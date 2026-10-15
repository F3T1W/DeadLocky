#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"

signing_identity="${DEADLOCKY_CODESIGN_IDENTITY:--}"
notary_profile="${DEADLOCKY_NOTARY_PROFILE:-}"
if [[ -n "$notary_profile" && "$signing_identity" == - ]]; then
    echo "Notarization requires DEADLOCKY_CODESIGN_IDENTITY (Developer ID Application)." >&2
    exit 1
fi

bash scripts/publish-native.sh
app="$project_root/artifacts/NativeAOT/DeadLocky.app"
version="$(plutil -extract CFBundleShortVersionString raw "$app/Contents/Info.plist")"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
filename="DeadLocky-$version-apple-silicon.dmg"
output_dir="$project_root/artifacts/Releases"
mkdir -p "$output_dir"
work_dir="$(mktemp -d "$project_root/artifacts/.DiskImage.XXXXXX")"
mounted=false
cleanup() {
    if [[ "$mounted" == true ]]; then
        hdiutil detach "$work_dir/Mount" >/dev/null || true
    fi
    rm -rf "$work_dir"
}
trap cleanup EXIT

mkdir "$work_dir/Contents" "$work_dir/Mount"
ditto "$app" "$work_dir/Contents/DeadLocky.app"
ln -s /Applications "$work_dir/Contents/Applications"
cp docs/Install.md "$work_dir/Contents/Install.md"
cp LICENSE "$work_dir/Contents/LICENSE"

hdiutil create -volname "DeadLocky $version" -srcfolder "$work_dir/Contents" \
    -fs HFS+ -format UDZO -imagekey zlib-level=9 "$work_dir/$filename"
if [[ "$signing_identity" != - ]]; then
    codesign --sign "$signing_identity" --timestamp "$work_dir/$filename"
fi
if [[ -n "$notary_profile" ]]; then
    xcrun notarytool submit "$work_dir/$filename" --keychain-profile "$notary_profile" --wait
    xcrun stapler staple "$work_dir/$filename"
    xcrun stapler validate "$work_dir/$filename"
fi

hdiutil verify "$work_dir/$filename"
hdiutil attach "$work_dir/$filename" -readonly -nobrowse -mountpoint "$work_dir/Mount"
mounted=true
codesign --verify --deep --strict "$work_dir/Mount/DeadLocky.app"
cmp "$app/Contents/MacOS/DeadLocky" "$work_dir/Mount/DeadLocky.app/Contents/MacOS/DeadLocky"
[[ "$(readlink "$work_dir/Mount/Applications")" == /Applications ]]
hdiutil detach "$work_dir/Mount"
mounted=false

mv -f "$work_dir/$filename" "$output_dir/$filename"
(
    cd "$output_dir"
    shasum -a 256 "$filename" > "$filename.sha256"
)
{
    echo "DeadLocky $version / Apple Silicon / macOS 14+ / NativeAOT"
    echo "Source commit: $(git rev-parse HEAD)"
    if [[ -n "$(git status --porcelain)" ]]; then
        echo "Source includes uncommitted changes. Commit and rebuild for a tagged release."
    fi
    echo "Built at: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "Signing: $signing_identity"
    if [[ -n "$notary_profile" ]]; then
        echo "Apple notarization: accepted and stapled"
    else
        echo "Apple notarization: not performed"
    fi
} > "$output_dir/$filename.build.txt"

echo "DMG: $output_dir/$filename"
echo "SHA-256: $output_dir/$filename.sha256"
