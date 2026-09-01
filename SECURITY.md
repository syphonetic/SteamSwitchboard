# Security policy

## Supported version

Security fixes are made against the latest source and the 1.0.x release line. Version 1.0.0 contains the complete August 2026 initial-release hardening pass.

## Reporting a vulnerability

Do not post Steam credentials, cookies, QR codes, WebView2 profile data, or `%LOCALAPPDATA%\SteamSwitchboard` contents in a public issue. If this project is hosted, use the host's private security-advisory channel. Include the affected version, minimum reproduction, expected impact, and privacy-safe diagnostics only.

## Threat model

Protected assets are Steam web sessions, the account identity shown to the user, local Switchboard metadata, the native account selected for a game launch, and release integrity.

Untrusted inputs include every web document/frame, URI, permission request, notification title/body, local Steam VDF/ACF file, registry path, configured executable path, persisted JSON file, archive entry, and user-facing name read from Steam. The app also assumes that another process may alter Steam files while they are being read.

The current Windows user and Windows kernel remain trust anchors. Software already running as the same user can read or alter that user's WebView2 data and can invoke Steam directly. SteamSwitchboard reduces confused-deputy and cross-user/shared-path risks; it is not a sandbox against same-user malware.

## Enforced boundaries

### Web sessions

- Each account receives a GUID-named WebView2 profile. Cookies and site data are not copied between profiles.
- Only default-port HTTPS documents at exact `steamcommunity.com/chat` and `steamcommunity.com/login` paths may render. Main-frame, child-frame, and document-resource paths are checked.
- Host objects, web messages, developer tools, script dialogs, context menus, downloads, custom URI schemes, HTTP authentication, client certificates, screen capture, camera, and clipboard reads are blocked.
- Microphone access can reach WebView2's native prompt only from the exact Steam origin, while the workspace is visible, after a user gesture. Persisted grants are reset before navigation and decisions are never saved. Switching workspaces must commit a local blank document before reconnecting; failure disposes the browser controller instead of leaving a hidden media document.
- Notification permission is granted only to the exact Steam origin. Native notification events are host-handled; raw input is bounded before sanitisation, title/body text is stripped of control/bidirectional characters, tagged replacements and click/close lifecycle are preserved, unread fallbacks are correlated, and per-profile/global circuit breakers contain floods. Queues are bounded and content is never persisted or logged by Switchboard; Windows may retain submitted alert text in its own notification history until removal or expiry. Modern Windows activation accepts only a bounded `open` action plus optional exact-format profile and live-notification GUIDs; activation reports a click only when both opaque IDs still match one in-memory entry. Replacement tags are opaque hashes, alerts expire after 24 hours and on reboot, and removal is grouped per profile. Delivery enablement is sticky inside the service, while one bounded single-reader command queue orders show, disable, preview redaction, clear, forget, and test operations. Privacy actions persist a bounded opaque cleanup generation before removal; modern submission executes inside an atomic privacy gate, compatibility queues are synchronously hidden and reject stale generations, stale completions cannot clear newer intent, shutdown drains accepted work for a bounded interval, and startup retries any unconfirmed global or detached-profile receipt. The self-contained build probes modern support on a worker thread and treats missing broker/runtime COM classes as recoverable. A bounded, exception-contained tray alert is used otherwise. Because that legacy callback carries no alert identity, it opens the account-labelled notification center rather than guessing a profile or message.
- External HTTPS links require a direct user gesture from a visible, fully loaded allowed Steam document and display the full canonical URL. Startup/script-generated prompts are suppressed and non-HTTPS schemes are denied.
- Up to 16 workspaces remain active by default so ordinary account switching preserves state and notification delivery. Additional saved profiles retain their isolated on-disk session and reopen on selection; an explicit memory-saving setting suspends hidden workspaces. Hidden profiles cannot request new microphone access.
- Workspace visibility is tracked independently from WebView layout state: a background controller may remain layout-connected, but it is non-interactive and cannot satisfy the visible-workspace microphone policy. The native browser surface stays hidden until an allowed Steam navigation succeeds, and a bounded browser-start failure is contained to that workspace while the trusted WPF shell remains interactive.
- Host-cancelled and stale navigation completions cannot replace the active document's state. A genuine failed navigation tears down any requested microphone document before recovery, and delayed background suspension rechecks a presentation generation before it can affect a newly visible workspace.
- Browser creation and permission reset share one bounded startup budget. Missing runtimes, inaccessible profile folders, timeouts, and browser-process failures become account-scoped recovery panels; Reconnect disposes the failed controller and creates a fresh isolated session instead of reawaiting cached work.

The editable profile nickname is deliberately not presented as verified web identity. Steam's own page is authoritative, and a host-owned banner tells the user which Steam login is expected.

### Native Steam and game launching

- Discovery accepts only absolute paths on local fixed/removable drives, rejects UNC/device paths and reparse points, requires the expected Steam installation layout and Valve file metadata, and requires `steam.exe` to have a valid Windows trust result with Valve as publisher and whole-chain online revocation checking.
- A running client counts only when one process's final image path, Windows session, process ID, and start time match the validated executable across every check.
- Launching requires Steam's `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser`; stale `MostRecent` cache data is never authoritative.
- Individual SteamID64 values and account names are validated before matching; duplicate login-name mappings are rejected. Unknown, signed-out, malformed, or mismatched state fails closed.
- Account, SteamID, process identity, and executable checks are temporally separated and repeated. The executable is opened without write/delete sharing, revalidated while locked, and kept locked through process creation.
- AppIDs are unsigned integers embedded only in Valve's documented `steam://run/<AppID>` request and passed as one `ProcessStartInfo.ArgumentList` item to the already locked, verified `steam.exe`; no shell-concatenated game arguments are used.
- The app refuses to run elevated and permits only one instance in the current Windows session.

### Files and state

- VDF files have byte, character, token, nesting, and node limits; duplicate keys are rejected and parsing is cancellation-aware.
- Libraries/manifests must remain beneath validated local Steam paths. Manifest filename/AppID mismatches, path traversal, reserved devices, and linked install folders are rejected. Global manifest-count, installed-game-count, and cumulative-byte budgets apply across all library folders rather than resetting per folder.
- State JSON has 4 MiB, depth, element-count, 512-profile, and 512 detached-notification-cleanup limits; duplicate and unknown properties are rejected before linear-time semantic validation of non-empty unique profile IDs/logins, safe names, colours, paths, selections, deletion markers, and opaque cleanup generations.
- Profile-nickname edits, native-login relinks, and required-launch-account selection are persisted transactionally; relinks accept only safe unique logins detected from local Steam metadata, and message/notification-title content is never included in state.
- State saves use unique same-directory temporary files and replacement. UI account mutations roll back when persistence fails.
- Corrupt-state backups and crash logs have retention/size limits. Diagnostics contain only timestamp, exception type, and numeric error code.
- Forgetting an account persists a deletion tombstone before browser cleanup, immediately suppresses its notifications, detaches it from the UI, and disposes the controller even if cleanup fails. Metadata remains until WebView2 accepts profile deletion, and incomplete deletion retries at startup without navigating the profile to Steam first.
- Diagnostic destinations are revalidated as local, non-linked paths immediately before every bounded privacy-safe write.

### Build and release

- NuGet sources are cleared and mapped to `nuget.org`; repository signatures, one exact legacy Microsoft author certificate, lock files, transitive vulnerability auditing, and low-severity audit reporting are enforced. A disposable empty-cache restore is part of release validation so cached packages cannot hide signer-policy failures.
- Release builds use warnings-as-errors and promote .NET security diagnostics to errors.
- The security script runs the complete test suite, dependency audit, Semgrep rules, Trivy vulnerability/secret/configuration checks, and formatting validation.
- GitHub Actions verifies a pinned Gitleaks binary checksum, scans complete Git history, and installs pinned Semgrep and Trivy versions in a mandatory read-only job; the Windows build/test/package job does not start until that scanner gate succeeds.
- ZIP construction requires one clean Git source snapshot, includes the applicable restored third-party license/notice texts, and uses stable ordering/timestamps. Validation requires both first-party product versions to contain the exact complete source revision, requires one exact hash/filename sidecar record, holds the archive read-locked through extraction, and rejects traversal, alternate separators, links, reserved devices, case-colliding names, expansion bombs, debug symbols, logs, state, and browser-profile data before extraction. Packaging rechecks both `HEAD` and worktree cleanliness before replacing the release output.
- Package validation is tested with malicious checksum-name, traversal, debug-data, duplicate-path, resource-amplification, and unsigned-required fixtures.
- Protected version tags run a separate SignPath Foundation signing job only after both source-security and Windows release gates pass. The approval-gated `release` environment supplies one least-privilege SignPath CI submitter token restricted to the project and release policy; the certificate key remains in SignPath's HSM, and no PFX, private key, personal certificate, or release-capable long-lived GitHub token exists in the repository.
- The unsigned package is reproduced byte-for-byte before signing. An integrity manifest permits only the executable and first-party application DLL to change, and PE-level content hashes prove their code/resources remain identical after excluding only the checksum field, certificate-directory entry, bounded alignment padding, and appended certificate table that Authenticode may add. Finalization then requires one expected publisher, one signer certificate, code-signing EKU, RFC 3161 timestamp EKU, valid Windows trust, exact source/version binding, and unchanged runtime/document inventory.
- The signing job has read-only repository and Actions permission. It uploads exactly the two integrity-locked first-party binaries for SignPath origin verification, waits for the required signing approval, structurally validates the bounded response, and transfers only the reconstructed signed payload. A fresh job with no `release` environment or SignPath token independently rebuilds the protected source tag twice, regenerates the manifest from its own reproducible unsigned archive, imports the payload as untrusted data, proves Authenticode-only mutation, finalizes the ZIP, attests it, and transfers it through an immutable short-retention workflow artifact. A separate publication job rechecks exact filenames, checksum, source ref/digest, hosted-runner provenance, and signer workflow before creating a GitHub Release.
- Repository release immutability locks each published release's assets and associated tag and adds GitHub's release attestation; changed bytes require a new version rather than an in-place replacement.

## Known residual risks

- Locally generated builds remain unsigned and their adjacent SHA-256 sidecars provide integrity, not publisher identity. Only an asset published by the protected GitHub workflow after SignPath Foundation Authenticode signing and GitHub provenance attestation is an official binary release. The SignPath open-source project, origin verification, release policy, artifact configuration, and CI submitter must be approved and activated before the first such release can run.
- Steam exposes no supported host API in this design for binding a WebView session to the user-entered label. The permanent banner makes that limitation explicit; users must confirm Steam's displayed identity before sending.
- Native Steam still supports one active desktop account at a time. Switchboard guides and verifies a native account change but does not create concurrent native clients.
- Modern `AppNotificationManager` delivery in a self-contained unpackaged app depends on Windows' optional App SDK Singleton broker. Switchboard probes support and falls back to a legacy tray alert when unavailable. Windows or Do not disturb can still suppress either path; the in-app notification center remains authoritative and Settings exposes a test alert and Windows settings shortcut.
- Saved profiles are capped at 512 and live WebView2 workspaces at 16 to prevent local resource exhaustion. Profiles beyond the live budget reopen on selection and cannot notify while closed.
- No userspace check can make a mutable executable path perfectly atomic with Windows process creation against a kernel-level or same-user attacker. The lock-and-reverify window is intentionally narrow, and the app never elevates.
- WebView2, Steam, Windows, and the bundled .NET runtime require ongoing security updates. .NET 9 reaches end of support on 2026-11-10 under the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy); migrate public builds to .NET 10 LTS before then and rebuild promptly after security releases.

## Deliberately unsupported techniques

The project will not accept features that capture/export/import authentication cookies, store passwords or Steam Guard codes, inject into or impersonate Steam, modify `loginusers.vdf`, bypass anti-cheat/licensing/session controls, scrape private web identity into the host, or silently open untrusted navigation/downloads.
