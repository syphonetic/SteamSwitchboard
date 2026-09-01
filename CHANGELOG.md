# Changelog

All notable changes are documented here.

## 1.0.1 — 2026-09-01

First trusted Windows binary release candidate.

### Release security

- Added a protected, tag-only SignPath Foundation pipeline for qualifying open-source releases; the Foundation certificate remains HSM-held, and no certificate private key, PFX, personal certificate, or release-capable long-lived GitHub token is stored in the repository.
- Isolated the signing job from GitHub release-write permission, pinned every GitHub/SignPath action to an immutable commit, limited the approval-gated API token to one project/release policy, and enabled SignPath origin verification over the exact GitHub-hosted build artifact.
- Preserved byte-for-byte reproducibility proof on the unsigned candidate before RFC 3161 timestamped signing, then independently rebuilt the trusted baseline on a fresh non-signing runner and allowed only `SteamSwitchboard.exe` and `SteamSwitchboard.dll` to change.
- Added an integrity manifest, strict signing-staging path and inventory validation, PE-level Authenticode content hashing that excludes only permitted signature metadata, same-certificate/publisher/EKU enforcement, trusted timestamp enforcement, signed-package revalidation, malicious staging fixtures, and atomic final package replacement.
- Added GitHub build-provenance attestations, an immutable one-day signed-candidate handoff, independent checksum and provenance verification in the publication job, and automatic release creation only from an annotated protected version tag after every existing security/build gate passes.

### Distribution

- Added the SignPath Foundation code-signing policy, exact two-file artifact configuration, application/onboarding record, and GitHub release-environment setup instructions for maintainers.
- Kept the `v1.0.0` tag immutable and published one exact-tag Windows package as an explicitly unsigned prerelease candidate for evaluation and Foundation eligibility review; `v1.0.1` remains the first version eligible for a signed production binary after SignPath approves and activates the open-source project.

## 1.0.0 — 2026-08-31

First GitHub-ready source release and self-contained Windows package. The exact-tag package was later attached to a GitHub prerelease with prominent unknown-publisher and checksum-only warnings while Foundation onboarding was pending.

### Product

- Added up to 512 isolated, persistent official Steam web-chat profiles, with a safe 16-workspace live budget and least-recently-used on-demand reopening beyond it.
- Added default always-connected startup within the live-workspace budget, with an optional memory-saving background-sleep setting.
- Added account-aware modern Windows alerts with a compatibility tray fallback, receiving-profile/login/sender labelling, generated SteamSwitchboard artwork on modern and fallback alerts, a numbered native taskbar badge driven by bounded aggregate Steam unread-message state, read-state clearing, unread-aware window/account/notification accessibility labels, notification-scoped opaque activation, opaque tagged replacement, 24-hour/reboot expiry, matching native click/close lifecycle, correlated unread fallback, global/per-profile flood controls, a sticky master switch, an ordered bounded command queue, generation-keyed delivery barriers, durable cleanup receipts with startup replay, bounded shutdown draining, privacy-safe preview controls, a delivery-status panel, a real replaceable test alert, and a Windows-settings shortcut.
- Added editable private profile nicknames, clear nickname-versus-Steam-login language, and safe relinking to a native login detected from Steam's local account cache.
- Added bounded local Steam application-manifest discovery across library folders, with truthful copy that leaves install readiness and actual startup to Steam.
- Added a saved **Required Steam account** selector, truthful **Launch with Steam** actions, guided native Steam account switching, cancellation immediately before native process invocation, and Valve's official `steam://run/<AppID>` handoff only after the required login is verified.
- Added explicit profile forgetting with browser-data clearing and profile deletion.
- Added an original SteamSwitchboard brand mark and used it consistently in the executable, title bar, dialogs, product header, and About panel, plus a high-contrast selected-profile marker, screen-reader current-page state, a responsive high-DPI keyboard-accessible WPF interface, and beginner onboarding.
- Kept the full shell interactive while isolated chat browsers, notification support, and native launch checks start; bounded every browser startup attempt; removed a re-entrant two-way profile-selection binding; prevented hidden WebView surfaces from painting across the sidebar; and clamped the native window to the active monitor at per-monitor DPI.
- Prevented first-run Steam login redirects from being mistaken for user-clicked external links; external-browser confirmation is now available only from a visible, fully loaded embedded Steam page.

### Security and privacy

- Restricted main documents and child frames to exact Steam Chat/sign-in routes; blocked custom protocols, native credential dialogs, client certificates, screen capture, downloads, camera, clipboard reads, and persistent permission grants.
- Restricted notification permission and event handling to the exact Steam origin; sanitised and bounded all web-controlled notification text and kept message content out of persistent state and logs.
- Added a permanent host-owned workspace/login banner without claiming the configured label verifies Steam's signed-in web identity.
- Added strict VDF/JSON size, depth, token, node, path, identity, duplicate, and unknown-property limits.
- Rejected remote and reparse-point paths for Steam libraries, manifests, executable discovery, application state, logs, and browser data.
- Added whole-chain revocation-aware Authenticode publisher verification plus expected file metadata/layout checks for `steam.exe`, exact process-image/session/start-time identity, authoritative SteamID resolution, duplicate-login rejection, temporally separated transition checks, and a final account/executable check while the executable is locked.
- Made account forgetting durable, immediately alert-suppressed and session-quiesced, network-silent during retry, and visibly pending until WebView2 accepts profile deletion.
- Added single-instance and non-elevated execution guards, bounded privacy-safe diagnostics, transactional state mutations, and stale-refresh suppression.

### Quality and release

- Added 173 automated tests, a repeatable composed-window WPF/WebView2 regression harness, an opt-in isolated real Windows-alert smoke path, plus Semgrep, Trivy, full-history secret scanning, and transitive dependency auditing.
- Enforced signed NuGet source policy, an isolated clean-cache signer check, lock files, warnings/security diagnostics as build errors, and zero-warning Release builds.
- Added a read-only GitHub Actions gate with checksum-pinned full-history Gitleaks plus pinned Semgrep and Trivy versions that must pass before Windows compilation, tests, reproducibility comparison, and packaging; unsigned tag builds are deliberately not uploaded.
- Added clean-source and exact full-revision binding for both first-party binaries, restored third-party license/notice collection, deterministic ZIP metadata, pre-extraction traversal/link/resource/session-data checks, adversarial package-validator fixtures, final source revalidation, and clean-install smoke assertions.
- Kept icon, validation, and deterministic packaging scripts compatible with built-in Windows PowerShell 5.1 while preserving path-containment and filesystem-link checks.
