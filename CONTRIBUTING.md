# Contributing

**How much attention to expect.** This plugin is finished for what it set out to do, and it is
maintained in spare time. Issues are read. Pull requests are read, and merged when they are clearly
right. Both can sit for a while — if something has gone quiet it is time, not disinterest, and a
ping on the thread is welcome. Nothing here needs permission to fork.

## Reporting something

The useful bug report for this plugin is short and specific:

- Jellyfin server version, plugin version, and OS.
- What you configured — the profile, the languages, and whether accounts are chosen under
  *Watched by*. Most surprising behaviour is one of those three.
- The relevant lines from the Jellyfin log. The plugin logs under `DialogueBoost`, names the file
  it was working on, and names the reason when it declines an item — that reason is usually the
  whole answer.
- For anything about the audio itself: the source's codec and channel layout (`ffprobe` on the
  source file), and what the resulting track sounded like or measured as.

"It didn't process anything" is nearly always one of: no libraries selected, no track matching
*Process languages*, or the item already being up to date. The **Overview** tab says which.

**If your server is not Linux, please say so even when nothing is wrong.** The plugin has only run
on Linux; Windows and macOS are untested, and [README's Platforms
section](README.md#platforms) says what is expected to carry over and what is already known not to.
A report that it simply worked is as useful here as a bug.

## Changing something

```bash
dotnet build -c Release
dotnet test Jellyfin.Plugin.DialogueBoost.Tests/Jellyfin.Plugin.DialogueBoost.Tests.csproj
./scripts/deploy.sh          # installs to /var/lib/jellyfin and restarts
```

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) is the file to read first. It is not a tour of the
code — it is the list of things that are true about Jellyfin and ffmpeg and are not obvious, each
one of which was a bug before it was a rule. Most of them will cost you a day if you rediscover
them.

A few of those rules are load-bearing enough to state here:

- **Never modify source media.** Sidecars are additive files. Source hash and mtime must be
  identical before and after every run. There is a test for it; keep it that way.
- **Jellyfin service interfaces live in `Integration/` only.** That is the seam where a Jellyfin
  breaking change lands, and it is the only reason the test suite can run without a server. If you
  need a new one, widen its adapter rather than reaching around it.
- **`Configuration/Web/config.html` is generated.** Edit `Configuration/Web/src/`; the build
  assembles it.
- **Changing how an encode is built does not invalidate anything on its own.** If your change means
  existing sidecars are now wrong, bump `ProcessingStateRepository.EncodeRevision` in the same
  commit, or the library keeps the old ones forever.

Two harnesses check the parts a unit test cannot. `scripts/check-filter-graphs.sh` runs every
channel layout through real ffmpeg and reads the per-channel levels back. `scripts/make-format-fixtures.sh`
builds a library of the codecs and layouts the plugin has to decide differently about, every track a
bed of tones so a dropped or swapped channel is visible in one pass rather than something to listen
for. If you touch the filter graphs or the layout table, run both.

## Scope

Things likely to be merged: correctness fixes, a Jellyfin compatibility fix, a channel layout that
is handled wrongly, a clearer log line.

Things likely to be discussed first: new profiles, new configuration surface, anything that changes
what an existing install already wrote to disk. Every setting is one more thing that has to keep
working, and this plugin deliberately has fewer than it could.
