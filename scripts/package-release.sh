#!/usr/bin/env bash
# Builds the release artifacts for the Silksong Co-op mod.
#
# Usage:
#   ManagedDir="/path/to/Hollow Knight Silksong_Data/Managed" ./scripts/package-release.sh [output-dir]
#
# Produces in <output-dir> (default: dist/):
#   SilksongCoop_v<version>.zip              -- Windows, self-contained (bundles BepInEx 5.4.21 x64)
#   SilksongCoop_v<version>_plugin-only.zip  -- plugin DLLs only (Linux/macOS or existing BepInEx install)
#
# Optional environment variables:
#   BEPINEX_ZIP     Path to a local BepInEx_x64_5.4.21.0.zip (skips the download)
#   BEPINEX_SHA256  Expected SHA-256 of that zip; release builds should pin it
#   NUGET_CONFIG    Path to a NuGet.config used for restore (offline/local feeds)
#   SKIP_BUNDLE     If set to 1, only the plugin-only package is produced
#
# The version is read from src/SilksongCoop.Mod/SilksongCoop.Mod.csproj, so bump it there first.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-$ROOT/dist}"
BEPINEX_VERSION="5.4.21"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_x64_${BEPINEX_VERSION}.0.zip"

VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT/src/SilksongCoop.Mod/SilksongCoop.Mod.csproj" | head -1)"
if [ -z "$VERSION" ]; then
    echo "error: could not read <Version> from SilksongCoop.Mod.csproj" >&2
    exit 1
fi
if [ -z "${ManagedDir:-}" ]; then
    echo "error: ManagedDir is not set (path to the game's *_Data/Managed folder)" >&2
    exit 1
fi

BUILD_ARGS=(-c Release "-p:ManagedDir=$ManagedDir")
if [ -n "${NUGET_CONFIG:-}" ]; then
    BUILD_ARGS+=(--configfile "$NUGET_CONFIG")
fi

echo "==> Building $VERSION (Managed: $ManagedDir)"
dotnet build "$ROOT/src/SilksongCoop.Mod/SilksongCoop.Mod.csproj" "${BUILD_ARGS[@]}" -p:TreatWarningsAsErrors=true

MOD_BIN="$ROOT/src/SilksongCoop.Mod/bin/Release/netstandard2.1"
CORE_BIN="$ROOT/src/SilksongCoop.Core/bin/Release/netstandard2.1"
for f in "$MOD_BIN/SilksongCoop.Mod.dll" "$CORE_BIN/SilksongCoop.Core.dll"; do
    [ -f "$f" ] || { echo "error: missing build output $f" >&2; exit 1; }
done

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# Absolutize: later steps `cd` into staging directories, so a relative OUT_DIR
# would resolve against the wrong cwd and zip would fail.
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

# ---------------- plugin-only package ----------------
PLUGIN="$WORK/plugin"
mkdir -p "$PLUGIN/SilksongCoop"
cp "$MOD_BIN/SilksongCoop.Mod.dll" "$CORE_BIN/SilksongCoop.Core.dll" "$PLUGIN/SilksongCoop/"
cp "$ROOT/assets/com.silksong.coop.cfg" "$PLUGIN/"
cp "$ROOT/README.md" "$PLUGIN/"
cp "$ROOT/assets/INSTALL.txt" "$PLUGIN/"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$PLUGIN/"
for doc in README.ru.md CHANGELOG.md ROADMAP.md; do
    if [ -f "$ROOT/$doc" ]; then cp "$ROOT/$doc" "$PLUGIN/"; fi
done
if [ -d "$ROOT/docs" ]; then cp -r "$ROOT/docs" "$PLUGIN/"; fi
cp -r "$ROOT/LICENSES" "$PLUGIN/"
sed -i "s/__VERSION__/$VERSION/g" "$PLUGIN/INSTALL.txt"
PLUGIN_ZIP="$OUT_DIR/SilksongCoop_v${VERSION}_plugin-only.zip"
rm -f "$PLUGIN_ZIP"
(cd "$PLUGIN" && zip -qr "$PLUGIN_ZIP" .)
echo "  -> $PLUGIN_ZIP"

if [ "${SKIP_BUNDLE:-0}" = "1" ]; then
    echo "==> SKIP_BUNDLE=1 — skipping self-contained package"
    exit 0
fi

# ---------------- self-contained (Windows) package ----------------
BEPINEX_SRC="${BEPINEX_ZIP:-$WORK/bepinex.zip}"
if [ -z "${BEPINEX_ZIP:-}" ]; then
    echo "==> Downloading BepInEx $BEPINEX_VERSION"
    curl -sSL --fail --retry 3 -o "$BEPINEX_SRC" "$BEPINEX_URL"
fi

ACTUAL_BEPINEX_SHA="$(sha256sum "$BEPINEX_SRC" | awk '{print $1}')"
echo "    BepInEx SHA256: $ACTUAL_BEPINEX_SHA"
if [ -n "${BEPINEX_SHA256:-}" ]; then
    EXPECTED_BEPINEX_SHA="$(printf '%s' "$BEPINEX_SHA256" | tr '[:upper:]' '[:lower:]')"
    if [ "$ACTUAL_BEPINEX_SHA" != "$EXPECTED_BEPINEX_SHA" ]; then
        echo "error: BepInEx SHA256 mismatch (expected $EXPECTED_BEPINEX_SHA)" >&2
        exit 1
    fi
    echo "  [ok]   BepInEx SHA256 matches the pinned value"
else
    echo "  [warn] BepInEx SHA256 is not pinned; set BEPINEX_SHA256 for release builds"
fi

WIN="$WORK/win"
mkdir -p "$WIN"
(cd "$WIN" && unzip -q "$BEPINEX_SRC")
mkdir -p "$WIN/BepInEx/plugins/SilksongCoop" "$WIN/BepInEx/config"
cp "$MOD_BIN/SilksongCoop.Mod.dll" "$CORE_BIN/SilksongCoop.Core.dll" "$WIN/BepInEx/plugins/SilksongCoop/"
cp "$ROOT/assets/com.silksong.coop.cfg" "$WIN/BepInEx/config/"
cp "$ROOT/README.md" "$WIN/"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$WIN/"
for doc in README.ru.md CHANGELOG.md ROADMAP.md; do
    if [ -f "$ROOT/$doc" ]; then cp "$ROOT/$doc" "$WIN/"; fi
done
if [ -d "$ROOT/docs" ]; then cp -r "$ROOT/docs" "$WIN/"; fi
cp -r "$ROOT/LICENSES" "$WIN/"
FULL_ZIP="$OUT_DIR/SilksongCoop_v${VERSION}.zip"
rm -f "$FULL_ZIP"
(cd "$WIN" && zip -qr "$FULL_ZIP" .)
echo "  -> $FULL_ZIP"

echo "==> SHA256"
HASH_FILE="$OUT_DIR/SilksongCoop_v${VERSION}_SHA256SUMS.txt"
(cd "$OUT_DIR" && sha256sum "SilksongCoop_v${VERSION}.zip" "SilksongCoop_v${VERSION}_plugin-only.zip" > "$HASH_FILE")
cat "$HASH_FILE"

echo "==> Done"
