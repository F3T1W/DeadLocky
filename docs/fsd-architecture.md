# FSD architecture

DeadLocky uses a single macOS executable with native AppKit controls. FSD layers are directories,
within one application project. Its `net10.0-macos` target includes AppKit; its `net10.0` target
compiles portable APIs and models for the separate test project.
Dependency direction is App → Pages → Features → Entities / Shared.
Features never import sibling features; Pages coordinates them through public view APIs.

The [Mermaid UML diagram](../README.md#architecture-and-checks) is maintained directly in the README.

## Responsibilities

- App owns application lifecycle, menus and dependency composition.
- Pages/Launcher owns screen layout and feature lifetimes.
- Features/PrepareGame owns selected directory, saved sign-in and installation state. Its service
  delegates authentication and download to the Windows Steam client configured by LaunchGame.
- Features/LaunchGame owns runtime preparation, configuration, launch, monitoring and prefix cleanup.
- Features/ManageMods owns the remote catalog adapter, protected and custom profile persistence,
  bounded archive extraction, VPK validation, resource conflicts, and journaled game deployment.
- Entities contains GameDirectory, SteamAccount, ModProfile and InstalledMod values.
- Shared contains generic directory selection and native presentation helpers.

The composition root injects delegates between features instead of creating cross-slice imports.
Controllers expose immutable states and action methods. Render displays state on the AppKit thread;
external I/O belongs in API adapters. Views unsubscribe and dispose their controllers with the window.
Cancellation must suppress stale results and preserve previously installed game/runtime data.

## Steam and installation

Windows Steam owns login, downloads, file validation and updates. Local text KeyValues metadata detects
remembered sign-in and completed installation; malformed or missing metadata is treated as unavailable.
Preparation also recognizes old completion receipts without downloading files itself. InstallationStore
persists directory preferences atomically and checks receipt paths, file sizes and modification times.
Steam credentials remain with Windows Steam. No QR authentication or Keychain token adapter remains.

The runtime installer validates Apple graphics, verifies downloaded component hashes, stages Wine and
Steam installation and marks readiness only after setup succeeds. Ownership markers prevent overwriting
unrelated destinations. A cross-process lock serializes setup. Cancellation and failures stop only the new
prefix and remove only owned destinations. The setup test creates an independent root and never saves
its returned configuration into the production launcher. Rosetta remains a shared system dependency.

## Game lifetime

IGameSession.Completion follows the game process, rather than Steam's initial command process.
Monitoring can attach to an existing matching game. Disposing monitoring never kills the game.
The saved exit preference controls Steam cleanup scoped to the configured prefix.

## Mod application

The composition root injects `ModWorkspace.ApplyAsync` into the launcher's pre-launch callback and
injects the launcher's game/Steam idle check into the mod workspace. Neither feature imports the other.
The launcher's existing start semaphore covers preparation and game startup. A workspace semaphore
and exclusive file lock serialize profile writes and deployment across windows and app instances.
The Pages layer forwards busy state and the active profile summary between the native feature views.

Profiles and installed package metadata use source-generated JSON. Packages remain in application support;
archives never extract directly into the game. VPK indexes validate entry bounds, referenced data and
resource paths. Each deployed file has a SHA-256 ownership entry. Staged directory swaps and atomic
configuration writes use a persisted recovery journal, with backup data retained until commit.
Recovery preserves newer Steam configuration and refuses changed or unmanaged deployment files.
The Vanilla profile removes only DeadLocky-owned files and its search path; it does not remove other
mod managers' files. Reapplying the selected profile before every Play handles ordinary Steam updates,
but a new game build can still change mod compatibility and needs real-game verification.

## NativeAOT and validation

Release builds enable PublishAot; Debug remains suitable for managed debugging. JSON contexts are
source-generated. AppKit callbacks use registered native types. SDK 27 currently needs NoDSymUtil for
NativeAOT bundles because its dSYM pipeline references a globalization dylib omitted from that bundle.

The portable test project references the application project's `net10.0` target. Tests cover controller cancellation,
remembered sign-in, game-session monitoring, prefix-scoped cleanup, installation transactions and verified
download reuse. Native UI checks and a full actual game session remain separate validation layers.
check-fsd.py checks layout and explicit namespace imports, not semantic architecture correctness.

References: [FSD layers](https://feature-sliced.design/docs/reference/layers),
[FSD public API](https://feature-sliced.design/docs/reference/public-api).

## App updates

`Features/UpdateApp` owns semantic version ordering, public GitHub release discovery,
verified DMG downloads and the macOS replacement helper. `Pages/Launcher` coordinates
its busy state with game preparation, launching and mod installation. The composition
root supplies the installed bundle path and version. Application termination waits for
update cancellation, and startup confirms a successful replacement after the main
window opens. App updates do not access the game deployment transaction or profiles.

## Problem reports

`Shared/Api/Diagnostics` provides the bounded asynchronous ILogger sink and redacted log snapshots. `Features/ReportProblem` owns field validation, ZIP allowlisting, the Apps Script HTTP adapter and the native report window. `App` wires the menu, bundle endpoint and shutdown; `Pages/Launcher` pauses update installation while a report is open. No feature imports another feature for logging. See [ProblemReports.md](ProblemReports.md).
