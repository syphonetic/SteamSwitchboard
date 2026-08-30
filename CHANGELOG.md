# Changelog

All notable changes are documented here.

## 1.0.0 — 2026-08-30

First GitHub-ready source release and self-contained Windows package.

### Product

- Added isolated, persistent official Steam web-chat profiles with no fixed application account cap.
- Added installed-game discovery across Steam library folders.
- Added account-aware launch verification and guided native Steam account switching.
- Added explicit profile forgetting with browser-data clearing and profile deletion.
- Added high-DPI, keyboard-accessible WPF interface and beginner onboarding.

### Security and privacy

- Restricted main documents and child frames to exact Steam Chat/sign-in routes; blocked custom protocols, native credential dialogs, client certificates, screen capture, downloads, camera, clipboard reads, and persistent permission grants.
- Added a permanent host-owned workspace/login banner without claiming the configured label verifies Steam's signed-in web identity.
- Added strict VDF/JSON size, depth, token, node, path, identity, duplicate, and unknown-property limits.
- Rejected remote and reparse-point paths for Steam libraries, manifests, executable discovery, application state, logs, and browser data.
- Added Authenticode publisher verification for `steam.exe`, exact process-image/session matching, authoritative active-account resolution, repeated transition checks, and a final account/executable check while the executable is locked.
- Made account forgetting durable, network-silent during retry, and visibly pending until WebView2 accepts profile deletion.
- Added single-instance and non-elevated execution guards, bounded privacy-safe diagnostics, transactional state mutations, and stale-refresh suppression.

### Quality and release

- Added 96 automated tests plus Semgrep, Trivy, and transitive dependency auditing.
- Enforced signed NuGet source policy, lock files, warnings/security diagnostics as build errors, and zero-warning Release builds.
- Added deterministic ZIP metadata, pre-extraction traversal/link/resource/session-data checks, adversarial package-validator fixtures, and clean-install smoke assertions.
