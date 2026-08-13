#!/usr/bin/env bash
# Build, package and register a plugin release.
#
#   ./build.sh 1.0.0.0 "Initial release."
#
# Produces posterManager_<version>.zip and adds a matching entry to manifest.json.
# Upload the zip as a GitHub Release asset tagged v<version>.
set -euo pipefail

VERSION="${1:?usage: ./build.sh <4-part-version> [changelog]}"
CHANGELOG="${2:-Release ${VERSION}.}"

REPO="malte9799/jellyfin"
PROJECT="Jellyfin.Plugin.PosterManager"
GUID="a058f2e6-e8e6-4b4f-a791-73aa6a1ecb62"
# Must match (or be below) the Jellyfin.Controller version in the csproj.
TARGET_ABI="10.11.10.0"
# Non-Jellyfin dependencies shipped beside the plugin assembly.
EXTRA=(HtmlAgilityPack.dll)

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: version must be 4-part, e.g. 1.0.0.0" >&2
  exit 1
fi

ZIP="posterManager_${VERSION}.zip"
rm -rf out "$ZIP"

# Keep the assembly version in step with the release version.
sed -i '' \
  -e "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>${VERSION}</AssemblyVersion>|" \
  -e "s|<FileVersion>.*</FileVersion>|<FileVersion>${VERSION}</FileVersion>|" \
  "${PROJECT}/${PROJECT}.csproj"

dotnet publish "$PROJECT" -c Release -o out

# Jellyfin expects the DLLs at the zip root, not inside a folder.
(cd out && zip -j "../${ZIP}" "${PROJECT}.dll" "${EXTRA[@]}")

CHECKSUM=$(md5 -q "$ZIP")
TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
SOURCE_URL="https://github.com/${REPO}/releases/download/v${VERSION}/${ZIP}"

# Prepend the new version (Jellyfin shows newest first), replacing any same-version entry.
tmp=$(mktemp)
jq --arg g "$GUID" \
   --arg v "$VERSION" \
   --arg c "$CHANGELOG" \
   --arg a "$TARGET_ABI" \
   --arg s "$SOURCE_URL" \
   --arg k "$CHECKSUM" \
   --arg t "$TIMESTAMP" \
   'map(if .guid == $g then
      .versions = ([{
        version: $v, changelog: $c, targetAbi: $a,
        sourceUrl: $s, checksum: $k, timestamp: $t
      }] + (.versions | map(select(.version != $v))))
    else . end)' \
   manifest.json > "$tmp" && mv "$tmp" manifest.json

echo
echo "built ${ZIP}  (md5 ${CHECKSUM})"
echo "next:"
echo "  gh release create v${VERSION} ${ZIP} --title v${VERSION} --notes \"${CHANGELOG}\""
echo "  git add -A && git commit -m \"release ${VERSION}\" && git push"
