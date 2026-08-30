# Changelog

All notable changes are documented here.

## 1.0.0 — 2026-08-30

First GitHub-ready source release and self-contained Windows package.

### Product

- Added up to 512 isolated, persistent official Steam web-chat profiles, with a safe 16-workspace live budget and least-recently-used on-demand reopening beyond it.
- Added default always-connected startup within the live-workspace budget, with an optional memory-saving background-sleep setting.
- Added account-aware Windows and in-app chat notifications showing the receiving profile/login and Steam-provided notification title, with tagged replacement, native click/close lifecycle, correlated unread fallback, global/per-profile flood controls, a master Windows-alert switch, privacy-safe preview controls, and ambiguity-safe Windows-alert clicks that open the labelled notification center.
- Added editable friendly profile names without changing the immutable Steam login name used for native launch verification.
- Added installed-game discovery across Steam library folders.
- Added a saved, independent **Play account** drop-down plus account-aware launch verification, guided native Steam account switching, and automatic launch after the requested account is verified.
- Added explicit profile forgetting with browser-data clearing and profile deletion.
- Added an original SteamSwitchboard brand mark, responsive application icon set, high-DPI keyboard-accessible WPF interface, and beginner onboarding.

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

- Added 125 automated tests plus Semgrep, Trivy, and transitive dependency auditing.
- Enforced signed NuGet source policy, an isolated clean-cache signer check, lock files, warnings/security diagnostics as build errors, and zero-warning Release builds.
- Added a read-only GitHub Actions gate with pinned Semgrep and Trivy versions that must pass before Windows compilation, tests, packaging, or tag-artifact upload.
- Added deterministic ZIP metadata, pre-extraction traversal/link/resource/session-data checks, adversarial package-validator fixtures, and clean-install smoke assertions.
