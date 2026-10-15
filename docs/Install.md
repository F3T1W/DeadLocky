# Install DeadLocky

Requirements: an Apple Silicon Mac (M1 or later), macOS 14 or later, an internet
connection, a Steam account with access to Deadlock, and Apple's GPTK evaluation
environment. The launcher includes its own native code; .NET and Xcode are not
required to run it. Game compatibility also depends on the chosen GPTK version.

1. Open the DeadLocky DMG.
2. Drag DeadLocky.app onto the Applications shortcut. Quit an existing copy before replacing it.
3. Eject the disk image and open DeadLocky from Applications.
4. Follow the environment setup wizard. Download GPTK directly from Apple and
   choose its DMG or mounted volume. Accept any Apple or Rosetta license yourself.
5. Sign in to Windows Steam, install Deadlock, then use Play in the launcher.

Ad hoc preview builds have no Developer ID signature or Apple notarization.
For those builds, macOS may block the first launch. If you trust the download, follow Apple's
instructions for opening a blocked app in System Settings → Privacy & Security:
https://support.apple.com/102445

Microphone access is optional and is requested under DeadLocky for Deadlock's
voice chat. Choose the permission yourself in the macOS dialog.

## Profiles and updates

Vanilla is always available. Create another profile under Mods to install mods.
Return to Vanilla and launch the game to remove DeadLocky's deployed mods before
uninstalling the launcher. Files installed by other tools remain outside its scope.

To update DeadLocky, quit it and replace the app in Applications. Your game,
runtime and profiles are stored outside the app, primarily under
~/Library/Application Support/DeadLocky. Replacing the app preserves them.

The DMG includes the launcher only. Steam, Deadlock, Wine, Apple's GPTK and mods
are separate downloads. DeadLocky is not affiliated with Valve or Apple. The
launcher source is MIT-licensed; bundled dependencies retain their own licenses
in DeadLocky.app/Contents/Resources/ThirdPartyNotices.txt.

A successful launcher startup does not prove every mod or game update works.
When reporting a problem, include the DeadLocky version, macOS version, runtime
version, selected profile and exact error. Do not attach Steam login data.
