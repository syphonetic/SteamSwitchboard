# Architecture

## Product boundary

SteamSwitchboard is a local companion, not a replacement Steam client. It combines independent browser profiles for official Steam web chat with read-only Steam discovery and a normal native launch. It intentionally stops at Steam's one-active-desktop-account boundary and never changes native authentication state itself.

## Component map

```text
WPF shell (single instance, standard integrity)
├─ Account workspace
│  └─ SteamChatSession loaded on selection
│     └─ shared WebView2 user-data folder
│        └─ isolated account-{GUID} profile
├─ Games workspace
│  ├─ SteamInstallationService
│  │  └─ local path + reparse + Authenticode/Valve verification
│  ├─ SteamLibraryService
│  │  └─ bounded libraryfolders.vdf + appmanifest_*.acf parsing
│  └─ GameLaunchService
│     ├─ exact running image/session check
│     ├─ SteamClientAccountService
│     │  ├─ HKCU ActiveProcess\ActiveUser (authoritative)
│     │  └─ bounded loginusers.vdf (ID-to-login metadata only)
│     ├─ repeated stable account/executable checks
│     └─ locked steam.exe → -applaunch <AppId>
└─ Local state
   ├─ state.json atomic replacement
   ├─ durable browser-profile deletion tombstones
   ├─ bounded recovery copies / privacy-safe logs
   └─ GUID-named BrowserData profiles
```

## Conversation isolation and trust

All controls share one WebView2 user-data root to reuse the runtime, but every account receives a unique `ProfileName` derived only from its GUID. WebView2 isolates cookies, cache, site storage, permissions, and preferences per profile. Cold profiles are not warmed at startup; the selected workspace is created first and hidden workspaces are suspended.

The host never injects script, reads cookies, registers a host object, or enables web messaging. It observes navigation status and a numeric unread prefix in the document title only.

### Document policy

`about:blank` is accepted only as the exact initial browser page. Remote documents require default-port HTTPS, no URI user information, exact host `steamcommunity.com`, and a `/chat` or `/login` path boundary. The same decision function protects:

- top-level `NavigationStarting`;
- every `CoreWebView2Frame.NavigationStarting`, including nested frames; and
- every document-context `WebResourceRequested` event.

Static/CDN domains remain available as ordinary subresources but cannot become a trusted top-level or child document. Non-user-initiated external navigation is canceled silently. A direct user gesture may open one bounded, canonical external HTTPS URL after confirmation.

Permissions are deny-by-default and never saved. Only a user-initiated microphone request from the exact Steam origin while the selected workspace and window are visible can reach WebView2's own prompt. Screen capture, camera, clipboard read, HTTP authentication, client certificates, downloads, custom protocols, script dialogs, context menus, autofill, password storage, developer tools, host objects, and web messaging are denied or disabled.

### Identity semantics

The user-entered profile label and login name select local workspace and native-launch intent. They do not cryptographically identify the web account. A host-owned area above the WebView permanently shows the expected login and states that Steam's page is authoritative. The UI says “Steam page ready,” never “identity verified.”

## Native launch state machine

```text
Select account + installed AppID
          │
          ▼
Resolve absolute local steam.exe
          │
          ├─ remote/link/unsigned/non-Valve ──► block
          ▼
Match running image path + Windows session
          │
          ├─ not running ──► ask user to start Steam
          ▼
Read authoritative ActiveUser and map valid SteamID64
          │
          ├─ absent/unmatched/signed out ──► block as unknown
          ├─ different login ──────────────► guide user to switch in Steam
          ▼
Repeat account and executable checks
          │
          ├─ any change ──► block
          ▼
Open steam.exe read-only with write/delete sharing denied
          │
          ▼
Revalidate Authenticode publisher while locked
          │
          ▼
Recheck process, game, and ActiveUser while locked
          │
          ├─ any change ──► block
          │
          ▼
ProcessStartInfo.ArgumentList: -applaunch <uint AppId>
```

`MostRecent` remains useful for ordering cached metadata but is never a launch authority. The registry value is deliberately treated as unknown when absent rather than falling back to stale state.

## Untrusted local metadata

VDF parsing is bounded to 8 MiB/files and source characters, 1 MiB/tokens, 64 nesting levels, and 100,000 nodes. Duplicate keys, truncation, oversized input, and cancellation fail parsing without exposing attacker-controlled values in diagnostics.

Library roots must be absolute local fixed/removable-drive paths without reparse components. A manifest must be a regular local file named `appmanifest_<AppId>.acf`, its internal AppID must match, and `installdir` must be one safe leaf beneath that library's `steamapps\common`. Display names/persona names are length-limited and stripped of control/bidirectional formatting characters.

Directory enumeration and parsing occur inside exception boundaries. Results are staged per library and committed only after a complete bounded scan; stale overlapping refresh results are discarded by generation number.

## Persistence and deletion protocol

`StateStore` accepts at most 4 MiB, JSON depth 32, and 100,000 JSON elements. Case-insensitive duplicate properties and unknown properties are rejected. Before use, state is normalised and validates every account ID/name/colour, ID uniqueness, selected ID, configured path, and deletion tombstone. Invalid state is quarantined to at most three collision-resistant recovery files.

Writes serialise to a unique same-directory file with write-through semantics, flush, size-check, and replace the primary. Add/remove operations roll UI and model state back if persistence fails. A per-session named mutex prevents two GUI instances from overwriting each other's snapshots.

For account forgetting:

1. persist the profile GUID in `PendingBrowserProfileDeletionIds`;
2. clear all profile browsing-data kinds where possible;
3. call WebView2 `Profile.Delete()`, which marks/retries profile deletion;
4. dispose the controller; and
5. atomically remove account metadata and tombstone.

If steps 2–3 cannot schedule deletion, the account remains visible and pending. Startup retries every tombstone before opening normal chat workspaces; the cleanup-only WebView2 initialization does not navigate to Steam or another network page.

## Release design

One project version drives executable metadata, archive name, smoke test, and validation. NuGet restore clears inherited feeds, maps all packages to `nuget.org`, requires its repository signature, and uses lock files. The repository certificate fingerprint must be updated through a reviewed change when NuGet rotates it.

Packaging uses a private temporary publish tree, exclusive release lock, sorted entries, fixed ZIP timestamps, and a temporary archive/checksum pair. It validates required files, version, icon, signature policy, entry count/size, traversal, separators, links, reserved names, case collisions, and forbidden local/debug data before publishing. Adversarial fixture tests prove those checks fail closed.

The SHA-256 sidecar is an integrity convenience, not publisher authentication. A public release requires Authenticode signatures for first-party binaries, an authenticated installer/package, and a trusted distribution channel.
