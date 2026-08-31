#!/usr/bin/env bash
# Build a release zip and fold it into manifest.json.
#
# The same script the GitHub Actions workflow runs, so a release can be reproduced — and
# verified against a real Jellyfin — without pushing anything.
#
#   ./scripts/build-release.sh 1.0.0.0 https://github.com/OWNER/REPO/releases/download/v1.0.0.0
#   ./scripts/build-release.sh 1.0.0.0 http://localhost:8100        # local verification
#
# Produces  dist/dialogue-boost_<version>.zip  and updates  manifest.json  in place.

set -euo pipefail

readonly REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ASSEMBLY="Jellyfin.Plugin.DialogueBoost.dll"
readonly TFM="net9.0"

VERSION="${1:-}"
BASE_URL="${2:-}"
CHANGELOG="${3:-}"

[[ -n "$VERSION" ]] || { echo "usage: $0 <version> <base-url> [changelog]" >&2; exit 2; }
[[ -n "$BASE_URL" ]] || { echo "usage: $0 <version> <base-url> [changelog]" >&2; exit 2; }
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    echo "version must be four numbers, e.g. 1.0.0.0 — Jellyfin compares them as System.Version" >&2
    exit 2
}

say() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }

readonly ZIP_NAME="dialogue-boost_${VERSION}.zip"
readonly DIST="$REPO_ROOT/dist"
readonly STAGE="$DIST/stage"
readonly TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)"

# The version lives in four places and Jellyfin reads all four. build.yaml names the install
# directory, meta.json is what the loader logs, the manifest is what the catalogue compares
# against — a disagreement shows up as an update that installs and never applies — and the
# assembly's own version is what the dashboard's plugin list reports, at least on Jellyfin 12.2:
# a 1.1.0.0 install whose DLL said 1.0.0.0 was listed as 1.0.0.0.
say "Stamping version $VERSION"
sed -i "s/^version: .*/version: \"$VERSION\"/" "$REPO_ROOT/build.yaml"
python3 - "$REPO_ROOT/meta.json" "$VERSION" "$TIMESTAMP" <<'PY'
import json, sys
path, version, timestamp = sys.argv[1], sys.argv[2], sys.argv[3]
with open(path) as f:
    meta = json.load(f)
meta["version"] = version
meta["timestamp"] = timestamp
with open(path, "w") as f:
    json.dump(meta, f, indent=2)
    f.write("\n")
PY

say "Building Release"
dotnet build "$REPO_ROOT/Jellyfin.Plugin.DialogueBoost.csproj" -c Release --nologo -p:Version="$VERSION"

# Files sit at the root of the zip: Jellyfin extracts straight into plugins/<name>_<version>/,
# so a top-level folder in the archive becomes a nested directory the loader never looks in.
say "Packing $ZIP_NAME"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cp "$REPO_ROOT/bin/Release/$TFM/$ASSEMBLY" "$STAGE/"
cp "$REPO_ROOT/meta.json" "$STAGE/"
( cd "$STAGE" && zip -q -X -r "../$ZIP_NAME" . )
rm -rf "$STAGE"

# Jellyfin verifies the download with MD5 — Emby.Server.Implementations hashes the package and
# compares it to the manifest's `checksum`. A mismatch fails the install with no useful message.
CHECKSUM="$(md5sum "$DIST/$ZIP_NAME" | cut -d' ' -f1)"
say "md5 $CHECKSUM"

say "Updating manifest.json"
python3 - "$REPO_ROOT" "$VERSION" "$BASE_URL/$ZIP_NAME" "$CHECKSUM" "$TIMESTAMP" "$CHANGELOG" <<'PY'
import json, os, sys
root, version, source_url, checksum, timestamp, changelog = sys.argv[1:7]

with open(os.path.join(root, "meta.json")) as f:
    meta = json.load(f)

manifest_path = os.path.join(root, "manifest.json")
try:
    with open(manifest_path) as f:
        manifest = json.load(f)
except FileNotFoundError:
    manifest = []

package = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
if package is None:
    package = {"guid": meta["guid"], "versions": []}
    manifest.append(package)

# Descriptive fields follow meta.json so there is one place to edit them.
package.update({
    "name": meta["name"],
    "description": meta["description"],
    "overview": meta["overview"],
    "owner": meta["owner"],
    "category": meta["category"],
})

entry = {
    "version": version,
    "changelog": changelog or f"Release {version}",
    "targetAbi": meta["targetAbi"],
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp,
}

# Newest first, and one entry per version: re-running a release replaces rather than duplicates.
package["versions"] = [v for v in package.get("versions", []) if v.get("version") != version]
package["versions"].insert(0, entry)

with open(manifest_path, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print(f"manifest.json: {len(package['versions'])} version(s)")
PY

say "Done."
echo "  zip       $DIST/$ZIP_NAME"
echo "  md5       $CHECKSUM"
echo "  sourceUrl $BASE_URL/$ZIP_NAME"
