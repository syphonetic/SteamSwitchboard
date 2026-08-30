# Validation record

Validation is split into deterministic automated checks, adversarial release tests, static security analysis, live Windows checks, and three independent review passes. No personal Steam credential, QR code, cookie, message, or game launch was used during this hardening pass.

## Automated suite

The Release suite contains **131 passing tests** covering:

- VDF comments, escapes, bare tokens, malformed objects, duplicate keys, exact/over-limit tokens, file-size limits, depth limits, and cancellation;
- Unicode normalisation, control/bidirectional-character rejection, Steam-login syntax, duplicate login/profile IDs, and colour validation;
- JSON round trips, schema migration, duplicate/unknown-property rejection, linear semantic validation, duplicate/null IDs, 4 MiB/512-profile limits, unknown chat/play selections, durable deletion tombstones, and bounded corrupt-state retention;
- transaction rollback when account add/remove/rename persistence fails, independent play-account persistence, preservation of existing launch intent when adding profiles, explicit reselection after removing the play account, and friendly-label updates that leave Steam login identity unchanged;
- local path acceptance plus relative, UNC, device, reparse/dangling-link, sibling-prefix, missing-path, and application-data rejection;
- Steam library discovery, remote-library rejection, malformed manifests, filename/AppID mismatch, install-path traversal, and untrusted display-name sanitisation;
- valid individual SteamID64 parsing, duplicate login-name rejection, invalid cached identity rejection, persona sanitisation, signed-out/unknown fail-closed behaviour, and authoritative active-account matching;
- every launch-policy state, exact process arguments, temporally separated stable SteamID/process checks, unknown/mismatched accounts, untrusted executables, a changed process identity, and an executable that changes after assessment;
- exact Steam Chat/login navigation routes, host/path/port/userinfo/lookalike rejection, URI length limits, host-cancelled/stale navigation-completion suppression, direct-user external-link gating, visible user-initiated microphone policy, exact-origin notification acceptance, raw notification-size rejection, replacement/fallback lifecycle, hostile notification-text sanitisation, and untrusted-origin rejection;
- privacy-safe log records, bounded rotation, and single-instance mutual exclusion; and
- selection/removal regressions, cleanup marker lifecycle, corrupt-state warning retention, truthful open/sleep/dormant status wording, persisted-setting binding refresh, account-scoped unread state, preview redaction, bounded/non-persistent notification history, game filtering, WPF-dispatcher collection transitions, and physical window clamping for scaled, negative-coordinate, and already-visible monitor layouts.

The ordinary gate runs a disposable empty-cache signer-policy restore followed by normal signed-source/locked NuGet restore, formatting verification, deterministic Release compilation, warnings-as-errors, .NET security diagnostics as errors, and all tests.

The checked-in GitHub workflow adds a mandatory Ubuntu scanner job with pinned Semgrep and Trivy versions. The Windows build/test/package job declares that scanner job as a prerequisite, so a tag artifact is never uploaded from a workflow run whose dedicated source/dependency/secret/configuration gate failed.

## Automated security analysis

On 2026-08-30:

- `dotnet list package --vulnerable --include-transitive --format json`: **0 vulnerable package records**;
- Semgrep community C# + security-audit configuration: **180 rules, 63 files, 0 findings**;
- Trivy filesystem vulnerability/secret/misconfiguration scan at High/Critical: **0 vulnerabilities, 0 secrets, and no recognised configuration files**; and
- Release and test NuGet lock files: **0 known vulnerabilities**, with package-source mapping, repository-signature validation, and an exact pinned Microsoft author certificate for the one legacy package that predates repository countersigning.

The baseline all-rules .NET analyzer pass also informed hardening. Its non-security maintainability diagnostics are not conflated with vulnerability findings; the normal build is zero-warning and security-category diagnostics fail the build.

## Release-boundary tests

Every package build performs static validation before publication. The validator verifies one exact checksum/filename record, exact version/root, required application/offline-documentation/branding files, embedded icon, optional Authenticode policy, entry count and expansion size, and absence of PDBs, logs, state, or browser data. It holds the validated archive read-locked through extraction. Before extraction it rejects absolute/traversal paths, backslash ambiguity, alternate-data-stream syntax, reserved Windows device names, trailing-dot/space aliases, symbolic links, and case-insensitive collisions.

The packaging pipeline then mutates disposable archive copies and proves rejection of:

1. a sidecar naming the wrong archive;
2. `root/../escape.txt` traversal;
3. a recomputed-checksum archive containing `leaked.pdb`;
4. a case-colliding `readme.MD`;
5. a high-ratio compressed entry intended to amplify extraction resources; and
6. an unsigned archive when signature enforcement is requested.

Archive entries are sorted and use a fixed timestamp. Source file modification times therefore do not affect the ZIP. Public reproducibility still depends on using the same pinned SDK/runtime inputs and clean source snapshot.

## Live Windows QA

Test host:

- Windows 11 Home, 64-bit, build 26200;
- .NET SDK 9.0.317;
- Microsoft Edge WebView2 Evergreen Runtime; and
- a local multi-library Steam installation.

Observed during this pass:

- The installed `steam.exe` produced a valid Windows trust result and exact publisher name `Valve Corp.`; an unsigned fixture was rejected.
- The normal-privilege app started without clipping at 144-DPI, exposed the expected title and 48×48 associated icon, detected 58 installed applications, and settled into the beginner first-run state.
- The repeatable `scripts/test-ui-regression.ps1` harness created two disposable profiles, forced the selected profile's first browser initialization to time out, activated the visible Reconnect action, and proved a fresh session recovered. It also proved the shell enabled and accepted Chats/Settings navigation within one second, selected-before-background browser startup ordering, a physical window wholly inside its 3840×2088 work area, both profiles leaving the bounded starting state, and a **0.11%** near-white pixel ratio in the real composed account sidebar. That HWND-level pixel assertion detects the reported white WebView overpaint; all disposable browser data and processes were removed afterwards. The harness project is compiled by the normal/CI solution gate, while its composed-screen run is intentionally a documented interactive-Windows check rather than a headless CI step.
- The self-contained 1.0.0 ZIP passed checksum, pre-extraction path validation, version/icon/no-PDB/no-session-data assertions, and adversarial validator tests.
- A baseline packaged process remained healthy through startup, then closed normally. Its disposable extraction and test-data directories were removed, with zero processes remaining. The final feature package was not process-smoked on this host because an older user-owned SteamSwitchboard instance and its real `%LOCALAPPDATA%` state were deliberately left untouched; the exact final ZIP instead receives deterministic WPF rendering plus static and adversarial package validation.
- A final screenshot was captured from a disposable empty-state render with startup discovery disabled; it contains no account, installed-game, QR, conversation, or credential data.
- Earlier functional QA established distinct generated WebView2 profile directories, account switching between live controls, Steam sign-in rendering, wrong-native-account launch blocking, and profile deletion. The 1.0.0 hardening pass adds durable cleanup retry and stricter browser policy around that verified foundation.

Authenticated message sending and an actual game start remain owner acceptance checks because they would require personal credentials and change the user's Steam/game state.

## Independent security reviews and fixes

Three read-only reviews separately examined browser/session isolation, native/local-data boundaries, usability/accessibility, and release/supply-chain handling. Their release-blocking and important findings were reproduced and fixed as follows:

- child-frame and broad-domain trust → exact route/origin policy on main frames, every child frame, and document requests;
- persistent/overbroad browser permissions and native credential surfaces → deny-by-default, non-persistent permissions and explicit event cancellation;
- potentially surviving hidden microphone document → proven local-blank commit before reconnect, with fail-closed controller disposal;
- label/web-identity ambiguity → permanent host-owned expected-login banner and non-verifying status language;
- silent/live profile-deletion failure → persisted tombstone, immediate notification suppression/session detachment, failure-safe controller disposal, visible pending state, and startup retry;
- stale `MostRecent` launch authority and assess/start race → authoritative-only ActiveUser, duplicate-login rejection, stable SteamID/process identity, temporal checks, Authenticode publisher verification, and locked final recheck;
- notification duplicate/lifecycle/flood gaps and identity-free legacy balloon callbacks → tag replacement, correlated fallback, deferred click/close reporting, raw bounds, local/global circuit breakers, and generic notification-center activation instead of timer-slot account inference;
- UNC/reparse/traversal and unbounded Steam metadata → local-path policy plus parser/path/identity limits;
- duplicate state IDs, unbounded account activation, and quadratic validation → 512 saved-profile/16 live-session budgets, lazy LRU activation, and linear identity validation;
- persisted-setting/startup-event and constrained-layout defects → binding settle before event enablement, an interactive shell before browser startup, selected-visible/background-hidden WebView layout, bounded serial profile warm-up, per-monitor native bounds clamping, adaptive scrolling/resizing, truthful native states, and login-disambiguated accessibility names;
- cancelled/stale navigation, hidden-media, delayed-suspension, missing-runtime, and retry defects → navigation-ID correlation, media teardown on genuine failure, presentation-generation rechecks, account-scoped runtime/folder errors, idempotent event attachment, and fresh-session reconnect;
- clean-runner signer/test nondeterminism → exact legacy author certificate, empty-cache restore with metadata reset, and STA Dispatcher-hosted ViewModel initialization tests; and
- report-only release checks → enforced archive assertions, malicious fixtures, signed NuGet policy, static scanners, and deterministic ZIP metadata.

## Three final product-review passes

### Pass 1 — browser and identity boundary

Reviewed every browser event registration/detachment, main/child/document navigation decision, local media-reset commit, permission kind, notification origin/raw bound/replacement/lifecycle/quota, ambiguity-safe Windows activation, external launch path, live-session budget, workspace wording, and cleanup failure path. No host bridge, cookie access, unsafe scheme, saved permission, persisted message text, timer-slot account inference, silent live cleanup session, or identity-verification claim remains.

**Assessment:** happy with the browser/session security boundary as a world-class local companion.

### Pass 2 — native, parser, and persistence boundary

Reviewed all filesystem probes before use, Steam signer/process-ID/start-time/SteamID checks, temporal launch transition, AppID argument handling, VDF/JSON/profile limits, linear semantic identity validation, mutation rollback, diagnostic content, and recovery retention. Malformed, duplicated, remote, linked, changed, or unknown state fails closed without modifying Steam.

**Assessment:** happy with the native and local-data security boundary as a world-class local companion.

### Pass 3 — release, usability, and residual-risk honesty

Reviewed beginner flow, responsive browser startup, real composed HWND layering, constrained/high-DPI layout and monitor clamping, settings restoration, disambiguated accessibility labels, unsigned-build messaging, exact version propagation, clean-cache dependency trust, scanner results, ZIP construction, pre-extraction validation, adversarial fixtures, smoke-test boundary, cleanup, and documentation. The unsigned-development, 16-live-session, native-Steam, and web-identity limitations are prominent rather than hidden.

**Assessment:** happy with the release and user-trust boundary as a world-class development product.

## Owner acceptance checklist

Before public production distribution:

1. Authenticode-sign and timestamp `SteamSwitchboard.exe` and `SteamSwitchboard.dll` with the publisher certificate; run packaging with `-RequireSignature -ExpectedPublisher '<publisher>'`.
2. Wrap the complete payload in an authenticated installer/package (for example a signed MSIX), publish through a trusted HTTPS release channel, and verify immutable release metadata independently of the adjacent checksum.
3. Rebuild on the current supported .NET runtime/WebView2 patch and run `security-audit.ps1 -RequireExternalScanners`.
4. Add two disposable real accounts, confirm the host banner/login label against Steam's page, and send/receive one harmless message per account.
5. Start a free/test game with a matching native account, then repeat from a mismatch and confirm launch occurs only after Steam's active account changes.
6. Forget a disposable profile, restart, and confirm it requires fresh Steam authentication; repeat once with an intentionally interrupted cleanup to confirm retry.
7. Repeat smoke checks on the oldest supported Windows 10 build and a standard non-administrator Windows account.
