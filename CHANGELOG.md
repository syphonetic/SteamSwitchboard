# Changelog

All notable changes are documented here.

## 1.0.0 — 2026-08-31

First GitHub-ready source release and self-contained Windows package.

### Product

- Added up to 512 isolated, persistent official Steam web-chat profiles, with a safe 16-workspace live budget and least-recently-used on-demand reopening beyond it.
- Added default always-connected startup within the live-workspace budget, with an optional memory-saving background-sleep setting.
- Added account-aware modern Windows alerts with a compatibility tray fallback, receiving-profile/login/sender labelling, notification-scoped opaque activation, opaque tagged replacement, 24-hour/reboot expiry, matching native click/close lifecycle, correlated unread fallback, global/per-profile flood controls, a sticky master switch, an ordered bounded command queue, generation-keyed delivery barriers, durable cleanup receipts with startup replay, bounded shutdown draining, privacy-safe preview controls, a delivery-status panel, a real replaceable test alert, and a Windows-settings shortcut.
- Added editable private profile nicknames, clear nickname-versus-Steam-login language, and safe relinking to a native login detected from Steam's local account cache.
- Added bounded local Steam application-manifest discovery across library folders, with truthful copy that leaves install readiness and actual startup to Steam.
- Added a saved **Required Steam account** selector, truthful **Launch with Steam** actions, guided native Steam account switching, cancellation immediately before native process invocation, and Valve's official `steam://run/<AppID>` handoff only after the required login is verified.
- Added explicit profile forgetting with browser-data clearing and profile deletion.
- Added an original SteamSwitchboard brand mark and used it consistently in the executable, title bar, dialogs, product header, and About panel, plus a high-contrast selected-profile marker, screen-reader current-page state, a responsive high-DPI keyboard-accessible WPF interface, and beginner onboarding.
- Kept the full shell interactive while isolated chat browsers, notification support, and native launch checks start; bounded every browser startup attempt; removed a re-entrant two-way profile-selection binding; prevented hidden WebView surfaces from painting across the sidebar; and clamped the native window to the active monitor at per-monitor DPI.

### Security and privacy

- Restricted main documents and child frames to exact Steam Chat/sign-in routes; blocked custom protocols, native credential dialogs, client certificates, screen capture, downloads, camera, clipboard reads, and persistent permission grants.
- Restricted notification permission and event handling to the exact Steam origin; sanitised and bounded all web-controlled notification text and kept message content out of persistent state and logs.
- Added a permanent host-owned workspace/login banner without claiming the configured label verifies Steam's signed-in web identity.
- Added strict VDF/JSON size, depth, token, node, path, identity, duplicate, and unknown-property limits.
- Rejected remote and reparse-point paths for Steam libraries, manifests, executable discovery, application state, logs, and browser data.
- Added Authenticode publisher verification for `steam.exe`, exact process-image/session/start-time identity, authoritative SteamID resolution, duplicate-login rejection, temporally separated transition checks, and a final account/executable check while the executable is locked.
- Made account forgetting durable, immediately alert-suppressed and session-quiesced, network-silent during retry, and visibly pending until WebView2 accepts profile deletion.
- Added single-instance and non-elevated execution guards, bounded privacy-safe diagnostics, transactional state mutations, and stale-refresh suppression.

### Quality and release

- Added 155 automated tests, a repeatable composed-window WPF/WebView2 regression harness, an opt-in real Windows-alert smoke path, plus Semgrep, Trivy, and transitive dependency auditing.
- Enforced signed NuGet source policy, an isolated clean-cache signer check, lock files, warnings/security diagnostics as build errors, and zero-warning Release builds.
- Added a read-only GitHub Actions gate with pinned Semgrep and Trivy versions that must pass before Windows compilation, tests, packaging, or tag-artifact upload.
- Added deterministic ZIP metadata, pre-extraction traversal/link/resource/session-data checks, adversarial package-validator fixtures, and clean-install smoke assertions.
- Kept icon, validation, and deterministic packaging scripts compatible with built-in Windows PowerShell 5.1 while preserving path-containment and filesystem-link checks.
