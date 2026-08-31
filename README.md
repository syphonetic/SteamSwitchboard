# SteamSwitchboard

<p align="center">
  <img src="src/SteamSwitchboard.App/Assets/Branding/SteamSwitchboard-logo-v1.png" alt="SteamSwitchboard switchboard-and-chat logo" width="220" />
</p>

SteamSwitchboard is a privacy-first Windows companion for people who use several Steam accounts. It gives every saved profile an independent official Steam web-chat session and provides an account-verified handoff for local Steam library applications.

![SteamSwitchboard account workspace](artifacts/ui-final.png)

> **Release status:** version 1.0.0 remains the immutable source-only release. Version 1.0.1 adds the protected Microsoft Artifact Signing pipeline for the first public Windows binary. Local packages remain unsigned development builds; only ZIPs attached by the protected GitHub release workflow are publisher-authenticated releases.

## What it solves

- **Conversations without account churn.** Every Switchboard profile receives a separate Microsoft Edge WebView2 profile, so its Steam web session, cookies, and cache remain isolated from the others. Up to 16 chats stay open concurrently by default; additional saved profiles reopen on selection without losing their persisted Steam sign-in. A memory-saving sleep option remains available in Settings.
- **Account-aware notifications.** Steam web notifications show the receiving profile nickname, linked Steam login, and Steam-provided title (normally the sender shown by Steam). On supported systems, a modern Windows alert opens the receiving chat and correlates its click to the matching live in-app notification through bounded opaque identifiers. Modern alerts and the bounded tray-alert fallback use the committed SteamSwitchboard artwork, while Steam's aggregate per-account unread-message state is mirrored as a numbered taskbar-icon badge and accessible window/account status. Clearing alert history does not falsely clear unread messages; opening the matching chat does. The fallback opens the account-labelled in-app center because legacy callbacks contain no alert identity. Message previews are off by default, Windows alerts have a master switch, and Settings includes a test alert, live delivery status, and a shortcut to Windows notification settings.
- **Safer account-aware launching.** The **Library** page discovers local Steam application manifests and asks for the **Required Steam account** before offering **Launch with Steam**. A manifest and local directory do not prove that an application is fully installed or immediately playable; Steam remains authoritative. Switchboard does not pretend that a web-chat profile changes Steam's one native desktop login. It repeatedly verifies a local Valve-signed `steam.exe`, one stable Steam process, and Steam's authoritative active SteamID; a mismatch opens a guided wait screen while you switch in Steam itself. Only after an exact match does it send Valve's `steam://run/<AppID>` request.
- **Capacity beyond Steam's small account cache.** Switchboard supports up to 512 saved profiles on one PC. A safety budget keeps at most 16 WebView2 chats live simultaneously; the least-recently-used hidden workspace closes and transparently reopens when a seventeenth profile is selected. These explicit bounds prevent a damaged state file from exhausting the machine.
- **Beginner-friendly operation.** Steam and local library entries are discovered automatically. A **profile nickname** is private, local, and editable; the linked **Steam login** is the exact native login used by the launch guard. Settings can safely relink it only to an account detected in Steam's local cache. Account removal includes a plain-language confirmation and local-session cleanup, and the shell remains usable while chats, launch checks, or Windows alerts initialise in the background.

## Quick start

1. From the GitHub Release, download `SteamSwitchboard-1.0.1-win-x64.zip` and its `.sha256` file, then verify the checksum before extracting it.
2. Run `SteamSwitchboard.exe`.
3. Choose **Add account**, enter a private profile nickname and the account's exact Steam login name, then sign in on the official Steam page shown inside the app.
4. Select any account to use its conversation workspace. Up to 16 open profiles can notify you in the background; additional saved profiles reopen when selected.
5. Open **Library**, choose the **Required Steam account**, and select **Launch with Steam** beside a game. If Steam is using another login, Switchboard opens Steam and waits while you switch there. It submits the launch only after an exact match; Steam still decides whether ownership, updates, anti-cheat, or another client condition allows the game to start.

To change a nickname later, select the account and use **Settings → Edit profile nickname**. If the profile was linked to the wrong native login, use **Relink Steam login** and choose one of the logins Switchboard safely detected from Steam. Relinking does not change the signed-in identity inside the web page.

Steam Guard and QR approval continue to work through Steam's own sign-in page. SteamSwitchboard never asks for or stores a password.

The profile nickname is convenience metadata, not proof of web identity. A permanent banner shows the expected Steam login; always confirm the account displayed by Steam's page before sending a message.

## The important Steam limitation

Valve supports using several accounts on one computer, but the native Steam desktop client permits only one active account at a time. SteamSwitchboard therefore does **not** claim to run several native Steam client sessions or several account-bound games simultaneously on one Windows desktop. It does not inject into Steam, copy authentication tokens, automate password entry, modify `loginusers.vdf`, bypass anti-cheat, or emulate the Steam protocol.

The safe product boundary is:

- up to 16 simultaneous, isolated Steam **web conversation sessions**, with as many as 512 saved profiles; and
- one native Steam desktop login at a time, with explicit verification and a guided account switch when needed.

This follows [Valve's published account-use rule](https://help.steampowered.com/en/faqs/view/71EA-CDCE-FB5C-82B3). Running games under truly concurrent accounts requires separate operating-system or physical/remote machine environments and is outside this project.

## Requirements

- 64-bit Windows 10 or Windows 11
- Steam desktop client for game launching
- Internet access for Steam web chat
- Microsoft Edge WebView2 Evergreen Runtime; it is present on most current Windows installations and can be obtained from [Microsoft's WebView2 download page](https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/)

The packaged build includes the .NET runtime and the Windows App SDK components it uses, so a separate .NET installation is not required for end users. Modern Windows app notifications additionally depend on an operating-system broker that is not present on every self-contained/unpackaged system; Switchboard detects that condition and uses its compatibility tray alert instead.

The unread taskbar badge uses Windows' native overlay surface. Windows may hide overlays when a small-icon taskbar mode is forced, and an already pinned shortcut may need to be unpinned and pinned again after replacing an older build so Explorer refreshes its cached executable artwork.

## Privacy and security

- Embedded documents are limited to the exact `steamcommunity.com/chat` and `steamcommunity.com/login` routes over default-port HTTPS. Child frames use the same policy.
- The app exposes no browser host objects or web messaging. Developer tools, script dialogs, context menus, password saving, autofill, downloads, camera, clipboard reads, screen capture, HTTP authentication, client certificates, and custom protocols are blocked. Exact-origin Steam notification permission is handled by the host, while a visible, user-initiated Steam microphone request may use WebView2's own prompt; neither decision is saved.
- Notification titles and bodies are treated as untrusted input, raw-size checked before sanitisation, stripped of control/bidirectional formatting characters, replacement-aware, globally and per-profile rate-contained, bounded in memory, and never written to state or diagnostics.
- Modern Windows alerts expire after 24 hours and on reboot. Turning alerts off, disabling previews, clearing history, or forgetting a profile first saves an opaque cleanup request, then removes the relevant Windows alerts through one ordered bounded queue. New modern deliveries wait behind the latest cleanup generation; a successful Windows removal clears only its matching receipt. Shutdown drains accepted work for up to five seconds, and startup retries any receipt left by an interruption or Windows failure. Windows history removal remains best effort outside the app's control. Preview text remains opt-in, and **Send test alert** verifies the path without requiring a real message.
- External HTTPS links clicked from a visible, fully loaded Steam page show the complete canonical destination before opening in the system browser. Startup/script-initiated external navigation is silently blocked.
- Profile names on disk are generated GUIDs rather than Steam login names.
- Steam metadata is read with strict path, size, nesting, and identity limits. Remote, linked, malformed, or overlarge metadata is ignored. The app never modifies Steam configuration.
- Choosing **Forget account** first saves a durable cleanup request, suppresses alerts, and commits the browser to a local blank document before clearing it. The local record is removed only after WebView2 accepts profile deletion; failed cleanup remains visible and retries next launch with no live workspace left behind.
- The application refuses elevation and a second concurrent instance.

Local data lives at `%LOCALAPPDATA%\SteamSwitchboard`. See [Privacy](docs/PRIVACY.md), [Security](SECURITY.md), and [Architecture](docs/ARCHITECTURE.md) for the complete boundary.

## Build and verify

Development requires the .NET 9 SDK on Windows.

```powershell
./scripts/verify.ps1
```

That command regenerates the icon, performs a signed-package/locked restore with NuGet auditing, verifies formatting, compiles Release with warnings and security diagnostics treated as errors, and runs the full test suite.

On an interactive Windows desktop with WebView2 installed, reproduce the two-account startup, forced timeout/fresh-session reconnect, input, HWND-layering, and monitor-boundary checks with disposable data:

```powershell
./scripts/test-ui-regression.ps1
```

To additionally submit and then clear a real Windows test alert through the same UI path:

```powershell
dotnet run --project tests/SteamSwitchboard.UiRegression -c Release -- --notification-smoke
```

For the extended dependency, secret, configuration, and static-analysis pass:

```powershell
./scripts/security-audit.ps1 -RequireExternalScanners
```

To create an unsigned self-contained development package:

```powershell
./scripts/package.ps1
```

The ZIP and SHA-256 checksum are written to `artifacts/release/`. Packaging requires a clean Git checkout, binds both first-party binaries to the complete source revision, includes the exact restored third-party license/notice texts, validates archive paths before extraction, runs adversarial validator fixtures, excludes debug/session data, and normalises ZIP order and timestamps. It rechecks the source revision and worktree immediately before publishing the result. The protected GitHub tag workflow signs only the two first-party binaries through Microsoft Artifact Signing, then independently rebuilds the unsigned baseline on a fresh non-signing runner before it validates the publisher and trusted timestamp, attests the result, and publishes it.

## Project map

- `src/SteamSwitchboard.App` — WPF application, WebView2 chat profiles, Steam discovery, and guarded launch flow
- `tests/SteamSwitchboard.Tests` — parser, persistence, policy, discovery, navigation, and account-transition tests
- `tests/SteamSwitchboard.UiRegression` — disposable real-window/WebView2 interaction and composed-layer regression harness
- `docs/ARCHITECTURE.md` — component and trust-boundary design
- `docs/VALIDATION.md` — automated and live validation evidence
- `docs/GITHUB_RELEASE.md` — beginner GitHub push, Actions build/scan, source-release, and signed-binary requirements
- `scripts/verify.ps1` — reproducible verification entry point
- `scripts/package.ps1` — self-contained Windows release packaging
- `scripts/prepare-signed-release.ps1` / `finalize-signed-release.ps1` — integrity-locked cloud-signing boundary

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the required local checks and project security boundaries. GitHub pushes and pull requests run the same Windows Release verification gate automatically.

GitHub Actions uses checksum-pinned Gitleaks plus pinned Semgrep and Trivy before the Windows Release build/test/package gate, then independently rebuilds the package and requires matching hashes. Protected version tags additionally invoke Microsoft Artifact Signing through GitHub OIDC, attest the signed assets, and publish only after an isolated least-privilege handoff. See the [GitHub release guide](docs/GITHUB_RELEASE.md) for setup and release steps.

SteamSwitchboard is unofficial and is not affiliated with or endorsed by Valve Corporation. Steam and the Steam logo are trademarks of Valve Corporation.
