#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"

if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo "Build on an Apple Silicon Mac with Xcode installed." >&2
    exit 1
fi
if [[ ! -x .dotnet/dotnet ]]; then
    echo "Run bash scripts/setup-macos.sh first." >&2
    exit 1
fi

output_dir="$project_root/artifacts/NativeAOT"
mkdir -p "$output_dir"
build_dir="$(mktemp -d "$project_root/artifacts/.NativeBuild.XXXXXX")"
trap 'rm -rf "$build_dir"' EXIT

signing_identity="${DEADLOCKY_CODESIGN_IDENTITY:--}"
signing_options=("-p:CodesignKey=$signing_identity")
if [[ "$signing_identity" != - ]]; then
    if [[ "$signing_identity" != "Developer ID Application:"* ]]; then
        echo "Distribution requires a Developer ID Application identity, or '-' for an ad hoc build." >&2
        exit 1
    fi
    signing_options+=("-p:UseHardenedRuntime=true")
fi

# A fresh output tree prevents managed Release builds or old resources entering the DMG.
./.dotnet/dotnet publish src/DeadLocky/DeadLocky.csproj \
    -c Release -f net10.0-macos -r osx-arm64 \
    -p:PublishAot=true -p:CreatePackage=false -p:CopyDSYMToPublishDirectory=false \
    "-p:BaseOutputPath=$build_dir/bin/" \
    "${signing_options[@]}" -o "$build_dir/publish"

built_app="$build_dir/bin/Release/net10.0-macos/osx-arm64/DeadLocky.app"
executable="$built_app/Contents/MacOS/DeadLocky"
[[ -x "$executable" ]]
[[ "$(lipo -archs "$executable")" == arm64 ]]
[[ ! -e "$built_app/Contents/MonoBundle/libcoreclr.dylib" ]]
[[ -z "$(find "$built_app" -type f -name '*.dll' -print -quit)" ]]
[[ -n "$(plutil -extract NSMicrophoneUsageDescription raw "$built_app/Contents/Info.plist")" ]]
[[ -s "$built_app/Contents/Resources/ThirdPartyNotices.txt" ]]
codesign --verify --deep --strict "$built_app"

# Replace only this script's generated app; application data is outside the repository.
rm -rf "$output_dir/DeadLocky.app"
ditto "$built_app" "$output_dir/DeadLocky.app"
echo "NativeAOT app: $output_dir/DeadLocky.app"
