# Architecture

This is not a tour of the code — the code is commented, and the comments explain themselves. This
is the list of things that are true about Jellyfin and about ffmpeg that are **not obvious**, and
that this plugin's design is shaped around. Nearly every one was a bug before it was a rule.

If you are changing something here, read the section that covers it. Rediscovering any of these
costs time.

---

## What the plugin does

It writes an **additional audio track** beside each media file — a `.mka` sidecar carrying a
processed version of the source's audio — and leaves the source file untouched. Jellyfin presents
the sidecar as another selectable audio track on the same item.

```
Movie (2024)/
  Movie (2024).mkv                              ← never touched, never re-muxed
  Movie (2024).Dialogue Boost.default.mka       ← what this plugin writes
```

Five profiles decide what the processed audio is: **Dialogue Boost** (lifts the front centre, or
enhances speech on stereo), **Broadband Night Mode** (`dynaudnorm`), **Speech** (`speechnorm`),
**EBU R128** (`loudnorm`), and **Custom** (your own filter graph).

---

## Layout

```
Plugin.cs                     BasePlugin entry point; registers the config page
PluginServiceRegistrator.cs   DI registrations
EntryPoints/                  IHostedService — temp cleanup, missed daily run, config upgrade
Configuration/                PluginConfiguration + per-profile configs
  └── Web/src/                the config page's parts — config.html is ASSEMBLED from these
      ├── config.shell.html   the skeleton; every region is an @include
      ├── css/                tokens.css is the ONLY file that may name a colour
      ├── html/               the band, the tab strip, one file per panel
      └── js/                 theme probe, api, band, tabs, tree, coverage, profiles,
                              advanced, settings — one responsibility each
Integration/                  THE seam: every Jellyfin service interface, one adapter each
Analysis/                     ffprobe + MediaStream → AudioStreamInfo; channel-layout branching
Discovery/                    ItemAdded watcher — notices media added while the server runs
Selection/                    the chosen rows: the tree, paths, the store, run-time resolution
Processing/                   the item pipeline as named stages, ffmpeg command building
Output/                       sidecar naming, atomic write/publish, storage probe, the full sweep
State/                        SQLite record of processed items; WorkForecaster answers
                              "how much is left" without running anything
Api/                          REST surface for the page
ScheduledTasks/               library normalization + watched cleanup
build/                        WebAssets.targets — assembles the config page at build time
scripts/                      deploy helper and the verification harnesses
```

### The pipeline

`ScheduledTask | Api` → `ItemProcessor`, which is orchestration only:

```
ItemGate            may we, should we
CandidateStreams    which tracks qualify
  └─ per profile:
     SidecarBookkeeping        follow a rename, clear a stale marker
     FfmpegCommandBuilder      the spec
     SidecarWriter             encode to temp → verify → atomic rename
     ProcessingStateRepository record
     IMetadataRefresher        now, or a bounded batch after the loop
```

Each stage returns an **outcome**, not just a record: `Written`, `AlreadyDone`, `Skipped`, `Failed`.
A record cannot carry that — an item found up to date comes back with its *old* success flag — and
without the outcome a run that encoded nothing reports "Processed: 28".

An item the gate refuses is a `Skipped` with **no record**: counted, never written down, because
"already watched" is a fact about the account and stops being true on its own. Returning nothing for
it is how a run that declined 255 of 284 items reported *skipped 0*.

---

## Rules that are load-bearing

**Never modify source media.** Sidecars are additive files only. Source hash and mtime must be
identical before and after every run — this is a test, not an aspiration. A media library is often
also a seeding torrent tree; re-muxing a source file breaks it.

**Durability lives in `Integration/`.** `IEncoderTools`, `IMediaLibrary`, `IWatchedState`,
`IMetadataRefresher`, `IPlaybackSessions` — **no file outside that directory may name a Jellyfin
service interface.** If you need one, widen its adapter. `BaseItem` itself is fine to pass around;
it is the *query* surface that churns. The seam is also the only place a test can stand: the tests
project references `Jellyfin.Controller`, so a fake adapter is a class, not a mocking framework.

**Never assemble a command as a string.** `ProcessStartInfo.ArgumentList`, always. A filename may
contain a quote, a newline, or the sidecar's own name, and hand-written quoting loses to all three.

**Write nothing without probing first.** `Output/StorageProbe` checks the item's own folder before
an encode. Permissions on a media volume are **per-folder**, so a volume-level check is worthless —
and .NET reports "not mounted", "read-only" and "denied" identically.

**Modular files, single responsibility.** `ItemProcessor.ProcessItemAsync` was once ~280 lines
mixing idempotency, rename-following, dry-run, encoding and persistence. It is four named stages
now; nothing should grow back toward it.

---

## Things that are true about Jellyfin

### A sidecar renumbers the source's own streams

Jellyfin presents external audio as streams *of the item*, ahead of the embedded ones — a fresh
item's audio moved from container index 1,2 to 3,4 the moment its sidecar became visible. So the
**absolute stream index is not a property of the source**, and nothing durable may be keyed on it.
Use `AudioStreamInfo.AudioIndex`, the ordinal among the source's own audio tracks, which is also
what `-map 0:a:N` means.

Hashing the absolute index made the run *after* a completed one re-encode the entire library.

### The default-track flag is ignored; the filename is not

The container's `default` disposition is ignored outright on a single-stream external file. The
claim that actually starts playback on a track is Jellyfin's own `.default` filename token.

**At most one profile may write it.** Jellyfin takes the lowest external stream index, so with two
claimants the winner is whichever filename happens to sort first.
`PluginConfiguration.ClaimsDefaultTrack` is the one place that decides, resolving in the order
profiles are declared.

### A sidecar holds only the tracks it processed

Copying the source's original tracks into the sidecar bought duplicate rows in the audio menu and
3–5 GB per movie per profile. An original that was never copied is still played straight from the
source, so there is nothing to gain.

### A language code has one spelling, and it is not the one that arrived

Jellyfin reports a `ger`-tagged stream as `deu`, while ffprobe reads the raw tag. The two analyzer
paths therefore disagreed about the same file, and an exact-string rule skipped half a library.

Everything goes through `Analysis/LanguageCodes.Normalize` — both analyzer paths, both rule
comparisons, and the check that refuses a naming marker Jellyfin would read as a language.
**Never compare `AudioStreamInfo.Language` to a configured string directly.**

`und` is worth knowing about: it is ISO 639-2 for *undetermined*, it is what an untagged track
normalises to, and naming it in a rule is the only way to reach those tracks.

### Ask the watched state of a set once, not once per item

`IUserDataManager` answers per item, and each answer is a round trip — the config page's headline
number cost 3.2 s over 284 items. `WatchedItems.WatchedAmong` asks the library's query engine
instead: once per deciding account, same answer, 0.1 s. `ItemGate` still asks per item, which is
right — it has one.

### Refresh what the run wrote, after the loop — not through `QueueRefresh`

`IProviderManager.QueueRefresh` is not thread-safe under a burst; it can hand back a value it
refused to accept. A run refreshes what it wrote itself, bounded, and after the encode loop. Not
conditional on the run finishing, either: a track on disk that Jellyfin has not looked at is a track
nobody can select.

### When a task runs is Jellyfin's answer, not ours

Read and write the trigger through the task worker (`ScheduledTasks/DailySchedule`); never store a
schedule in the plugin configuration. `GetDefaultTriggers()` is consulted **only while a task has no
stored schedule**, so a bool there is a control that stops controlling the moment anyone touches the
dashboard.

### Nothing server-side remembers an audio-stream selection

Which track a viewer picked is a client-side preference. The plugin cannot read it, and cannot set
it — which is why the `.default` filename claim matters at all.

### The log's file sink prints a string property quoted

So a placeholder holding half a sentence — or a value the template already wraps in quotes — comes
out as `librar"y was"` or `'"Some Title"'`, and an empty one prints `""`. Use `{Name:l}` for those;
leave counts and bare paths alone, since the quotes are what delimits them. Read the line back off a
real log, never off the source.

---

## Things that are true about ffmpeg

### `pan` answers a channel the input does not have with silence

Silence, exit 0, no warning. Jellyfin reports a layout as `5.1` while ffmpeg decodes the same stream
as `5.1(side)`, so asking for `BL`/`BR` at an input holding `SL`/`SR` **emptied the surrounds of
every 5.1 sidecar** — −102 dB against a source carrying −41.

Build the graph from positional `c0..cN`, and require the layout's channel count to match the
stream.

### But a `pan` whose coefficients are all 1 is not a matrix

ffmpeg reads it as a channel **remap** and performs it by name — so a centre gain of exactly 0 dB is
the one shape of this graph that can still lose channels. There is nothing to scale at unity, so
build no `pan` at all.

### The layout name Jellyfin reports has its variant flattened off

`6.1(front)`, `6.1(back)`, `3.0(back)`, `6.0(front)`, `7.0(front)`, `7.1(wide)` and
`7.1(wide-side)` all arrive under their bare names — and `6.1(front)` keeps its **LFE** where `6.1`
keeps FC. A graph built on the bare name lifts the wrong channel.

`CandidateStreams` confirms against ffprobe the five names that hide a different shape — `3.0`,
`6.0`, `6.1`, `7.0`, `7.1` — and only those, because the 5.1/5.0/quad families hide nothing but
their own `(side)` twin and probing them would cost every library a rehash. One ffprobe per
affected item: 67 ms on a 518 MB source, against a run that spends minutes encoding.

### The channel count is the encoder's answer

`eac3` folds anything above 5.1 to 5.1 **without failing**, so a 7.1 source's bitrate would
otherwise be carried onto a 5.1 track. Above eac3's own list the codec is `aac`, which carries 6.1
as seven channels and 7.1 as eight; the height layouts aac refuses fall back to eac3's fold, which
is the only answer that cannot fail.

Every layout with a front centre is in the table — **3.0 upward, not 5.1 upward** — because a row
missing from it means a downmix to stereo. The three profiles that are not Dialogue Boost take the
same carrier through `ChannelLayoutDetector.CarrierFor`; asking aac for everything, which they used
to do, fails a 5.1.4 source outright with `Unsupported channel layout "5.1.4"`.

### A filter can change the channel count

`dialoguenhance` turns stereo into **3.0**. `BranchExecutionSpec.OutputChannels` is what answers
"how many channels come out", and it is measured, not assumed.

### The bitrate follows the source only where that means something

`Auto` matches the source's rate when the branch leaves the channel count alone. A branch that
downmixes — or a source whose rate is a fact about its *format* rather than its content — falls back
to the table. "Lossless" includes **uncompressed**: `pcm_s16le` is not `pcm`, and 6 × 48000 × 16 is
a real bitrate Jellyfin reports, which `Auto` once followed into a 4608 kbps sidecar.

### `-threads` changes nothing here

Measured at 1, 2 and 8: the encoded packet payload is sha256-identical, and so is the decoded audio
MD5. Every audio codec is single-threaded per stream and ffmpeg's working threads are structural.
`MaxConcurrentJobs` is the only real lever over a run's cost. Don't add a threads control back.

---

## Idempotency

Keyed `(ItemId, ProfileId)` → one sidecar path. `ParamsHash` covers profile parameters +
`ProcessLanguages` + `SetAsDefaultTrack` + the *resolved* default-track claim + the bitrate mode +
a signature of the source's audio track set, bitrates included. A mismatch triggers reprocessing;
publishing always overwrites the same path, never duplicates.

**Changing what goes into that string costs one re-encode of the library.** Pay it deliberately,
never discover it.

**Changing how an encode is *built* moves no hash on its own.** `ParamsHash` covers the settings and
the source; when the *construction* is what changed, every sidecar the old code wrote reads as
current forever. `ProcessingStateRepository.EncodeRevision` is the deliberate bump — pay it, or fix
a defect and leave it standing in the library. It covers the codec and the rate as well as the graph.

---

## Selection

**Selection is a scope, never a snapshot.** Persist only the rows the user chose and resolve their
members at run time. That single property is what covers new downloads automatically; materialising
descendant IDs re-creates the bug it was fixing.

**A chosen row is a path, and the plugin serves the tree it is a path through.** `Selection/`
answers what the page draws, from the same relation a run resolves, so a row covers exactly what it
displays. The page used to build its tree from Jellyfin's `/Items`, which answers a `ParentId` query
with its **merged presentation view** — one `Season 2` row listed eight episodes owned by eight
different season entities, while a scope resolved physically and covered one of them.

A `ScopePath` is the library id, then one segment per level: `i:<id>` for one entity,
`k:<kind>|<name>` for every sibling that presents as the same thing — which is how one row stands
for nine release folders and adopts the tenth.

Marks are then a prefix test, not a walk: *selected* when a stored path equals the row, *included*
when one is a prefix of it, *partially selected* when the row is a prefix of one. **Never decide
coverage by comparing a single path** — an item can sit under two rows; resolve the scopes and see
what they hold.

---

## The way out

Every granular delete — a profile, a watched item, one row — does not add up to *everything*.
`DeleteByProfile` deletes what a **record** names, and a record can be gone while its file is not.

So `Output/SidecarSweep` asks twice and takes the union: the two names each profile writes beside
**every item in every library** (one `readdir` per directory, not ten `stat`s an episode), and
whatever the records name — which is the only thing that finds a marker nobody uses any more. What
neither finds is left alone; **never delete by extension**, because a hand-made `.mka` beside a
source is a real thing in a real library.

`DELETE /Sidecars/All` clears both triggers and disables the plugin **first**: a library emptied this
afternoon with the daily run still scheduled is full again by morning.

---

## The configuration page

**`Configuration/Web/config.html` is generated — edit `Configuration/Web/src/`.** It is gitignored
and assembled by `build/WebAssets.targets`, which splices each `@include` marker and re-indents the
part to match. Jellyfin needs one file; that is a packaging constraint, not an editing one. The
whole page shares **one function scope**, so a helper written in one part is callable from every
other. `js/theme.js` is deliberately outside it, since it publishes CSS custom properties and
nothing else.

**Only the `data-role="page"` element survives.** Jellyfin's router grafts that one element into its
own container and discards the rest of the document — so everything the page needs, **the `<style>`
block included**, must sit *inside* it. CSS parked in `<head>` reaches nobody and warns nobody; the
page just renders with none of its own rules.

**Jellyfin has no CSS custom properties to inherit.** Its themes are class-based CSS and differ in
accent as well as ground. Use Jellyfin's own component classes — `emby-input`, `emby-select`,
`emby-checkbox`, `raised`, `button-submit`, `fieldDescription`, `inputLabel` — and let `js/theme.js`
probe the rendered theme. **No rule outside `css/tokens.css` may name a colour**; if one needs a
colour that is not a token, the token is missing. Do not restate Jellyfin's font or foreground
either.

**`emby-checkbox` has a required shape:**

```html
<label><input is="emby-checkbox" … /><span>text</span></label>
```

Its `attachedCallback` classes the first `<span>` in the parent and **throws without one** — and the
webcomponents polyfill then abandons the rest of its upgrade pass, so one bad label leaves every
later custom element on the page un-upgraded. Where the visible name lives outside the label, hide
the span rather than omitting it. A checkbox written into the DOM *after* that pass still upgrades,
so a generated list is fine.

**The page is 54em wide and that is Jellyfin's rule.** Its stylesheet carries a global
`form { max-width: 54em }` and the whole page lives in one form, so the content box is ~801 px at a
1100 px browser and ~801 px at 1920. Do not fight it — widen the content instead. A release filename
is **one unbreakable token** (a dot is not a break opportunity), so any cell holding one sets its
column's minimum to the longest name in the table. `overflow-wrap: anywhere` on that cell, never
`break-word` — only `anywhere` may shrink a box's intrinsic minimum.

**Live task progress needs no polling.** `window.TaskButton` starts a task, disables its button while
it runs, and opens Jellyfin's `ScheduledTasksInfo` socket stream; `Events.on(ApiClient, 'message', …)`
reads the same stream for anything the page draws itself. Guard for both being absent and degrade to
the numbers already on screen — never add a timer.

---

## Platforms

Everything in this document was measured on Linux, and Linux is the only platform this plugin has
ever run on. Nothing in it is *written* for Linux; these are the places where that could stop being
true, and the ones to check first if you port it.

- **Paths** go through `Path.Combine`. Nothing concatenates a separator.
- **Processes** start from `ProcessStartInfo.ArgumentList`, never a command string, so no quoting
  rule has to be right per platform. `ProcessRunner.ForLog` renders a quoted line for the log and
  nothing else reads it.
- **Publishing is a same-volume rename by construction.** `SidecarWriter` writes its temporary file
  as `<target>.tmp_<guid>.mka`, in the target's own directory, then `File.Move(overwrite: true)`.
  On Windows that call still fails when another process holds the destination open — a client
  streaming the very sidecar being replaced — where Linux allows it. The temporary file is removed,
  the item is reported failed, and the next run redoes it. This is the one known behavioural
  difference, and it is a retry rather than a corruption.
- **No native code ships.** The release zip is `Jellyfin.Plugin.DialogueBoost.dll` and `meta.json`,
  nothing else — confirmed by installing it from a repository URL and watching the plugin create
  its SQLite database anyway. `Microsoft.Data.Sqlite` is referenced for compilation and left out of
  the package; at runtime the plugin binds to the copy Jellyfin loads, which is the one built for
  the host. ffmpeg and ffprobe come from `IEncoderTools`, which is Jellyfin's configured encoder.
- **One platform-specific call exists**: `File.GetUnixFileMode`, in `StorageProbe.Mode`, behind
  `OperatingSystem.IsWindows()`. `/proc/self/mountinfo` is read only where it exists, and only to
  turn a refused write into *read-only mount* rather than *denied*; the probe itself decides by
  **writing a file**, so it answers correctly everywhere and simply loses one distinction.
- **`Path.GetInvalidFileNameChars()` is itself platform-specific**, and `SidecarNamer` refuses a
  naming marker by it. On Linux it returns `/` and NUL only, so a marker holding `:` or `?` is
  accepted there and produces a filename Windows will not take. That matters only for a library
  read from both.

CA1416, the platform-compatibility analyzer, is on and the build is clean. That is a real check
rather than a claim — removing the `IsWindows` guard above makes it fire:

```
warning CA1416: This call site is reachable on all platforms.
'File.GetUnixFileMode(string)' is unsupported on: 'windows'
```

It catches an unguarded API. It does not catch a behavioural difference like the rename above, and
it is no substitute for running the thing. Windows and macOS reports are wanted.

---

## Verification

Two harnesses answer for the audio, and both are re-runnable.

**`scripts/check-filter-graphs.sh`** parses the layout table straight out of
`Analysis/ChannelLayoutDetector.cs` and runs every row through real ffmpeg — the channels each
layout holds against `ffmpeg -layouts`, the graph built and run, and a per-channel level read back
off the output. The table cannot drift from the test, because the test reads the table.

**`scripts/make-format-fixtures.sh`** builds a 30-movie library of the codecs and layouts this plugin
has to decide differently about, every track a bed of tones falling 3.74 dB a channel — so a
dropped, silenced or swapped channel is visible in one `astats` pass rather than being something to
listen for.

```bash
sudo ./scripts/make-format-fixtures.sh          # ~1 min, default /srv/dbm2
```

Point a Jellyfin *movies* library at the result.

**`scripts/probe-config-page.py`** verifies the page in the client that will show it — it logs in,
lets Jellyfin's own router load the page, and reads the live DOM: whether the stylesheet reached the
browser at all, the band's numbers, every `--db-*` token as the theme resolved it, and uncaught
errors with their stacks.

```bash
JELLYFIN_USER=… JELLYFIN_PASS=… ./scripts/probe-config-page.py --shot out.png
```

Needs `geckodriver` and `python-selenium`. This matters because a standalone screenshot harness
loads the file as a whole document and **cannot see what the router does to it** — one rendered the
page exactly as designed while the deployed page was dropping its entire stylesheet.

---

## Build

```bash
dotnet build -c Release --no-restore
dotnet test Jellyfin.Plugin.DialogueBoost.Tests/Jellyfin.Plugin.DialogueBoost.Tests.csproj --no-restore
./scripts/deploy.sh
```

**A bare `dotnet test` in the repo root is a silent no-op** — it resolves the root `.csproj`, which
is not a test project: no output, exit 0, nothing run. Always name the test project, and read the
`Passed!` line rather than trusting the exit code.

Targets **`net9.0`** because Jellyfin 10.11 runs on .NET 9. Never bump the TFM to match a newer SDK.
If the build errors with `NETSDK1127` (missing targeting pack), install the pack rather than
retargeting.

Plugins load from **`/var/lib/jellyfin/plugins/`**. `config.html` is an embedded assembly resource,
so a browser hard-refresh (`Ctrl+Shift+R`) is required after any UI change.
