# Build a release

Build on an Apple Silicon Mac with the Xcode version required by the pinned
macOS workload. Run `bash scripts/setup-macos.sh` on a new development machine.
The SDK and workload versions are recorded in `global.json`.

## Checks

```sh
python3 scripts/check-fsd.py
.dotnet/dotnet test tests/DeadLocky.UnitTests/DeadLocky.UnitTests.csproj
.dotnet/dotnet build DeadLocky.slnx --no-restore
.dotnet/dotnet format DeadLocky.slnx --no-restore --verify-no-changes
```

Run Rider Code → Inspect Code… → Whole solution, wait for ReSharper and Roslyn,
and resolve all findings. Confirm a completed result of zero issues.
Review dependency changes against `docs/ThirdPartyNotices.txt` before packaging.
Update both bundle version fields in `src/DeadLocky/Info.plist` for a new release.

Update assets must follow `DeadLocky-<version>-apple-silicon.dmg` with a semantic
version tag such as `v0.2.0`. Always increment the version. The updater uses GitHub's
asset digest, or the matching `.dmg.sha256` file if that digest is unavailable. It
includes published previews and ignores equal or older versions. Keep the bundle ID
unchanged to preserve macOS permissions and update identity checks. Developer ID
builds must retain the same signing team. The source PNG and icon packaging script
are described in `src/DeadLocky/App/Assets/README.md`.

## DMG

```sh
bash scripts/create-dmg.sh
```

This publishes a fresh NativeAOT app, verifies its arm64 executable and code
signature, and creates a compressed read-only image with an Applications shortcut,
installation instructions and the project license. Separate output and intermediate trees prevent
stale managed Release artifacts or cached bundle metadata from entering the image. It mounts the image,
checks the copied app and shortcut, then ejects it. Generated files stay ignored
under `artifacts/`.

Outputs are `artifacts/Releases/DeadLocky-<version>-apple-silicon.dmg`, a SHA-256
file and build metadata. The standalone app is `artifacts/NativeAOT/DeadLocky.app`.
The version comes from the built bundle. No game files, runtime downloads, profiles
or Steam data are copied into the image. The script does not upload a release.
Commit the final source and rebuild before tagging/publishing so build metadata
identifies the released commit without local changes.

Without signing configuration the app is ad hoc signed and the DMG is not
notarized. Treat this as a community preview; disclose the first-launch Gatekeeper
restriction. Do not present it as an Apple-verified release.

## Developer ID and notarization

For distribution with a verified developer identity, install your own
**Developer ID Application** certificate and private key in Keychain. Apple
Development certificates cannot be used for this distribution workflow.
Set the full identity name when building:

```sh
export DEADLOCKY_CODESIGN_IDENTITY='Developer ID Application: Your Name (TEAMID)'
bash scripts/create-dmg.sh
```

The .NET macOS SDK signs the app and nested native code with hardened runtime
and timestamps; the script also signs the DMG. To request notarization, first
store your credentials in Keychain using `xcrun notarytool store-credentials`.
Then set `DEADLOCKY_NOTARY_PROFILE` to that profile's name before running the
script. This explicitly enables upload of the built DMG to Apple's notary
service, waits for acceptance and staples/validates the ticket. Credentials and
private keys do not belong in the repository. This path requires a real Developer
ID identity and has not been validated by the ad hoc build.

See Apple's [notarization documentation](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution).

## Manual smoke test

Copy the app from the mounted image to Applications, eject the image and launch
that copy through Finder. Check Launcher and Mods, catalog loading and the setup
wizard on a clean account without an existing environment. Test Steam sign-in,
download, Play, Settings/microphone permission, profile application, a game session,
return to Vanilla and Emergency quit on the target runtime before claiming full
end-to-end compatibility. A local build, signature check and unit tests cover only
their respective layers.
