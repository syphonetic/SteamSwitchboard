# SteamSwitchboard

SteamSwitchboard is a privacy-first Windows companion for people who use several Steam accounts. It keeps an independent official Steam web-chat session for every profile and provides an account-verified launcher for installed games.

![SteamSwitchboard account workspace](artifacts/ui-final.png)

> **Initial release:** version 1.0.0 is the first public source release. The included Windows build is unsigned; its checksum detects accidental corruption but does not authenticate the publisher. Sign the first-party binaries and distribute them through an authenticated installer/package before wider public distribution.

## What it solves

- **Conversations without account churn.** Every Switchboard profile receives a separate Microsoft Edge WebView2 profile, so its Steam web session, cookies, and cache remain isolated from the others. Profiles are opened on demand and resume when selected.
- **Safer account-aware launching.** Switchboard discovers installed Steam libraries and labels every action `Play as <account>`. Before launching, it repeatedly verifies a local Valve-signed `steam.exe`, the exact running image, and Steam's authoritative active-account value—including a final check after locking the executable. If anything is unknown or changes, launch stays blocked.
- **No application account cap.** Profiles use generated identifiers and a normal collection—there is no fixed account limit in Switchboard. Practical capacity depends on available memory, disk space, and Steam's own services.
- **Beginner-friendly operation.** Steam is detected automatically, installed games are discovered automatically, and account removal includes a plain-language confirmation and local-session cleanup.

## Quick start

1. Extract the downloaded `SteamSwitchboard-1.0.0-win-x64.zip`.
2. Run `SteamSwitchboard.exe`.
3. Choose **Add account**, enter a friendly label and the account's Steam login name, then sign in on the official Steam page shown inside the app.
4. Select any account to use its conversation workspace.
5. Open **Games**, select the account on the left, and choose **Play as…** beside a game. If Steam is using another account, switch accounts in Steam; Switchboard waits and verifies before starting the game.

Steam Guard and QR approval continue to work through Steam's own sign-in page. SteamSwitchboard never asks for or stores a password.

The Switchboard label is a convenience label, not proof of the web identity. A permanent banner shows the expected login name; always confirm the account displayed by Steam's page before sending a message.

## The important Steam limitation

Valve supports using several accounts on one computer, but the native Steam desktop client permits only one active account at a time. SteamSwitchboard therefore does **not** claim to run several native Steam client sessions or several account-bound games simultaneously on one Windows desktop. It does not inject into Steam, copy authentication tokens, automate password entry, modify `loginusers.vdf`, bypass anti-cheat, or emulate the Steam protocol.

The safe product boundary is:

- simultaneous, isolated Steam **web conversation sessions**; and
- one native Steam **play account** at a time, with explicit verification and a guided account switch when needed.

This follows [Valve's published account-use rule](https://help.steampowered.com/en/faqs/view/71EA-CDCE-FB5C-82B3). Running games under truly concurrent accounts requires separate operating-system or physical/remote machine environments and is outside this project.

## Requirements

- 64-bit Windows 10 or Windows 11
- Steam desktop client for game launching
- Internet access for Steam web chat
- Microsoft Edge WebView2 Evergreen Runtime; it is present on most current Windows installations and can be obtained from [Microsoft's WebView2 download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/)

The packaged build includes the .NET runtime, so a separate .NET installation is not required for end users.

## Privacy and security

- Embedded documents are limited to the exact `steamcommunity.com/chat` and `steamcommunity.com/login` routes over default-port HTTPS. Child frames use the same policy.
- The app exposes no browser host objects or web messaging. Developer tools, script dialogs, context menus, password saving, autofill, downloads, camera, clipboard reads, screen capture, HTTP authentication, client certificates, and custom protocols are blocked. A visible, user-initiated Steam microphone request may use WebView2's own prompt and is never saved.
- User-initiated external HTTPS links show the complete canonical destination before opening in the system browser. Script-initiated external navigation is silently blocked.
- Profile names on disk are generated GUIDs rather than Steam login names.
- Steam metadata is read with strict path, size, nesting, and identity limits. Remote, linked, malformed, or overlarge metadata is ignored. The app never modifies Steam configuration.
- Choosing **Forget account** first saves a durable cleanup request. The local record is removed only after WebView2 accepts profile deletion; failed cleanup remains visible and retries next launch.
- The application refuses elevation and a second concurrent instance.

Local data lives at `%LOCALAPPDATA%\SteamSwitchboard`. See [Privacy](docs/PRIVACY.md), [Security](SECURITY.md), and [Architecture](docs/ARCHITECTURE.md) for the complete boundary.

## Build and verify

Development requires the .NET 9 SDK on Windows.

```powershell
./scripts/verify.ps1
```

That command regenerates the icon, performs a signed-package/locked restore with NuGet auditing, verifies formatting, compiles Release with warnings and security diagnostics treated as errors, and runs the full test suite.

For the extended dependency, secret, configuration, and static-analysis pass:

```powershell
./scripts/security-audit.ps1 -RequireExternalScanners
```

To create the self-contained Windows package:

```powershell
./scripts/package.ps1
```

The ZIP and SHA-256 checksum are written to `artifacts/release/`. Packaging validates archive paths before extraction, runs adversarial validator fixtures, excludes debug/session data, and normalises ZIP order and timestamps. Pass `-RequireSignature` only in a release environment where first-party binaries have been Authenticode-signed.

## Project map

- `src/SteamSwitchboard.App` — WPF application, WebView2 chat profiles, Steam discovery, and guarded launch flow
- `tests/SteamSwitchboard.Tests` — parser, persistence, policy, discovery, navigation, and account-transition tests
- `docs/ARCHITECTURE.md` — component and trust-boundary design
- `docs/VALIDATION.md` — automated and live validation evidence
- `scripts/verify.ps1` — reproducible verification entry point
- `scripts/package.ps1` — self-contained Windows release packaging

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the required local checks and project security boundaries. GitHub pushes and pull requests run the same Windows Release verification gate automatically.

SteamSwitchboard is unofficial and is not affiliated with or endorsed by Valve Corporation. Steam and the Steam logo are trademarks of Valve Corporation.
