# Security policy

## Supported version

Security fixes are made against the latest source and the 1.0.x release line. Version 1.0.0 contains the complete August 2026 initial-release hardening pass.

## Reporting a vulnerability

Do not post Steam credentials, cookies, QR codes, WebView2 profile data, or `%LOCALAPPDATA%\SteamSwitchboard` contents in a public issue. If this project is hosted, use the host's private security-advisory channel. Include the affected version, minimum reproduction, expected impact, and privacy-safe diagnostics only.

## Threat model

Protected assets are Steam web sessions, the account identity shown to the user, local Switchboard metadata, the native account selected for a game launch, and release integrity.

Untrusted inputs include every web document/frame, URI, permission request, local Steam VDF/ACF file, registry path, configured executable path, persisted JSON file, archive entry, and user-facing name read from Steam. The app also assumes that another process may alter Steam files while they are being read.

The current Windows user and Windows kernel remain trust anchors. Software already running as the same user can read or alter that user's WebView2 data and can invoke Steam directly. SteamSwitchboard reduces confused-deputy and cross-user/shared-path risks; it is not a sandbox against same-user malware.

## Enforced boundaries

### Web sessions

- Each account receives a GUID-named WebView2 profile. Cookies and site data are not copied between profiles.
- Only default-port HTTPS documents at exact `steamcommunity.com/chat` and `steamcommunity.com/login` paths may render. Main-frame, child-frame, and document-resource paths are checked.
- Host objects, web messages, developer tools, script dialogs, context menus, downloads, custom URI schemes, HTTP authentication, client certificates, screen capture, camera, and clipboard reads are blocked.
- Microphone access can reach WebView2's native prompt only from the exact Steam origin, while the workspace is visible, after a user gesture. Decisions are never persisted.
- External HTTPS links require a direct user gesture and display the full canonical URL. Script-generated prompts are suppressed and non-HTTPS schemes are denied.
- Background workspaces are suspended and resumed on demand.

The configured workspace label is deliberately not presented as verified web identity. Steam's own page is authoritative, and a host-owned banner tells the user which login is expected.

### Native Steam and game launching

- Discovery accepts only absolute paths on local fixed/removable drives, rejects UNC/device paths and reparse points, and requires `steam.exe` to have a valid Windows trust result with Valve as publisher.
- A running client counts only when its final image path and Windows session match the validated executable.
- Launching requires Steam's `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser`; stale `MostRecent` cache data is never authoritative.
- Individual SteamID64 values and account names are validated before matching. Unknown, signed-out, malformed, or mismatched state fails closed.
- Account and executable checks are repeated. The executable is opened without write/delete sharing, revalidated while locked, and kept locked through process creation.
- AppIDs are unsigned integers passed via `ProcessStartInfo.ArgumentList`; no shell-concatenated game arguments are used.
- The app refuses to run elevated and permits only one instance in the current Windows session.

### Files and state

- VDF files have byte, character, token, nesting, and node limits; duplicate keys are rejected and parsing is cancellation-aware.
- Libraries/manifests must remain beneath validated local Steam paths. Manifest filename/AppID mismatches, path traversal, reserved devices, and linked install folders are rejected.
- State JSON has 4 MiB, depth, and element-count limits; duplicate and unknown properties are rejected before semantic validation of non-empty unique profile IDs, safe names, colours, paths, selections, and deletion markers.
- State saves use unique same-directory temporary files and replacement. UI account mutations roll back when persistence fails.
- Corrupt-state backups and crash logs have retention/size limits. Diagnostics contain only timestamp, exception type, and numeric error code.
- Forgetting an account persists a deletion tombstone before browser cleanup. Metadata remains until WebView2 accepts profile deletion, and incomplete deletion retries at startup without navigating the profile to Steam first.
- Diagnostic destinations are revalidated as local, non-linked paths immediately before every bounded privacy-safe write.

### Build and release

- NuGet sources are cleared and mapped to `nuget.org`; repository signatures, lock files, transitive vulnerability auditing, and low-severity audit reporting are enforced.
- Release builds use warnings-as-errors and promote .NET security diagnostics to errors.
- The security script runs the complete test suite, dependency audit, Semgrep rules, Trivy vulnerability/secret/configuration checks, and formatting validation.
- ZIP construction uses stable ordering/timestamps. Validation requires one exact hash/filename sidecar record, holds the archive read-locked through extraction, and rejects traversal, alternate separators, links, reserved devices, case-colliding names, expansion bombs, debug symbols, logs, state, and browser-profile data before extraction.
- Package validation is tested with malicious checksum-name, traversal, debug-data, duplicate-path, resource-amplification, and unsigned-required fixtures.

## Known residual risks

- The included 1.0.0 build is unsigned. Its SHA-256 sidecar detects accidental corruption only; an attacker who can replace both files can forge both. Wider public distribution requires trusted, timestamped signatures for first-party binaries plus an authenticated installer/package and trusted delivery channel.
- Steam exposes no supported host API in this design for binding a WebView session to the user-entered label. The permanent banner makes that limitation explicit; users must confirm Steam's displayed identity before sending.
- Native Steam still supports one active desktop account at a time. Switchboard guides and verifies a native account change but does not create concurrent native clients.
- No userspace check can make a mutable executable path perfectly atomic with Windows process creation against a kernel-level or same-user attacker. The lock-and-reverify window is intentionally narrow, and the app never elevates.
- WebView2, Steam, Windows, and the bundled .NET runtime require ongoing security updates. .NET 9 reaches end of support on 2026-11-10 under the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy); migrate public builds to .NET 10 LTS before then and rebuild promptly after security releases.

## Deliberately unsupported techniques

The project will not accept features that capture/export/import authentication cookies, store passwords or Steam Guard codes, inject into or impersonate Steam, modify `loginusers.vdf`, bypass anti-cheat/licensing/session controls, scrape private web identity into the host, or silently open untrusted navigation/downloads.
