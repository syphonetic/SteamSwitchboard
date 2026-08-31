# Architecture

## Product boundary

SteamSwitchboard is a local companion, not a replacement Steam client. It combines independent browser profiles for official Steam web chat with read-only Steam discovery and a normal native launch. It intentionally stops at Steam's one-active-desktop-account boundary and never changes native authentication state itself.

## Component map

```text
WPF shell (single instance, standard integrity)
├─ Account workspace
│  ├─ one isolated SteamChatSession per open account (maximum 16 live)
│  │  └─ shared WebView2 user-data folder
│  │     └─ isolated account-{GUID} profile
│  └─ notification router
│     ├─ exact-origin Web Notification metadata
│     ├─ unread-title fallback
│     ├─ bounded in-memory account/title history
│     └─ modern Windows app alert → bounded tray fallback
├─ Library workspace
│  ├─ SteamInstallationService
│  │  └─ local path + reparse + Authenticode/Valve verification
│  ├─ SteamLibraryService
│  │  └─ bounded libraryfolders.vdf + appmanifest_*.acf parsing
│  └─ GameLaunchService
│     ├─ exact running image/session check
│     ├─ SteamClientAccountService
│     │  ├─ HKCU ActiveProcess\ActiveUser (authoritative)
│     │  └─ bounded loginusers.vdf (ID-to-login metadata only)
│     ├─ repeated stable SteamID/process/executable checks
│     └─ locked steam.exe → steam://run/<AppId>
└─ Local state
   ├─ state.json atomic replacement
   ├─ durable browser-profile deletion tombstones
   ├─ bounded recovery copies / privacy-safe logs
   └─ GUID-named BrowserData profiles
```

## Conversation isolation and trust

All controls share one WebView2 user-data root to reuse the runtime, but every account receives a unique `ProfileName` derived only from its GUID. WebView2 isolates cookies, cache, site storage, permissions, and preferences per profile. Durable deletion tombstones are processed before any normal workspace; among non-deleting profiles, the selected workspace is created first and background profiles are then warmed serially up to a 16-session live budget. Selecting a profile beyond that budget disposes the least-recently-used hidden controller and reopens the requested profile from its isolated persistent data. Up to 512 profiles can be saved. Users can explicitly enable background sleep to reduce memory use; that setting trades immediate notifications for lower resource use.

Startup restores flat selected-profile presentation fields before account-selection events are enabled, then renders an enabled shell before game discovery or any WebView2 controller is created. Sidebar selection is synchronized explicitly rather than through a re-entrant two-way WPF selection binding. Game discovery continues independently. The selected session remains layout-connected while its browser surface stays hidden behind a WPF-owned loading/error panel; background sessions use WPF `Hidden`, not `Collapsed`, so their controllers can initialise at a stable size without painting. Profiles warm one at a time with dispatcher yields, and browser creation plus permission reset share a 15-second startup budget. A failed initialization or browser process is replaced with a fresh session when the user chooses Reconnect. Browser delay or failure therefore affects only that workspace, never navigation, settings, account management, or window input. Native window bounds are recalculated from the current monitor's physical work area and per-monitor DPI at source creation, load, and DPI changes.

The host never injects script, reads cookies, registers a host object, or enables web messaging. It observes navigation status, WebView2's native non-persistent notification event, and a numeric unread prefix in the document title only. An unrecognised hidden-page title is treated as unknown and preserves the local unread state; only an explicit bounded numeric prefix or the host's positive knowledge that the workspace is visible may change or clear it. Notification title/body strings remain untrusted: exact-origin policy, constant-time raw-length rejection before sanitisation, Unicode control/bidirectional removal, fixed display limits, tagged replacement, deferred native click/close reporting, correlated unread fallback, bounded history, per-profile/global circuit breakers, and non-persistence by Switchboard apply before display. Modern Windows alerts separate profile/login identity, Steam title, and optional preview; use the bounded generated-brand PNG as an app-logo override only after local-drive, strict-descendant, reparse-point, PNG-signature, exact reviewed-hash, and size checks; and hold a read/no-write-delete lease until notification service disposal. They use opaque bounded tags/groups, expire after 24 hours and on reboot, and accept only a bounded activation action plus optional exact profile and live-notification GUIDs. The compatibility tray path loads the same compiled multi-size ICO resource rather than inheriting a host-process icon. The live WPF window also applies the reviewed logo bitmap directly, avoiding dependence on a stale shell icon for its taskbar representation. A native WPF taskbar overlay renders Steam's bounded aggregate per-account unread-message state (`1`–`99` visually, with `99+` in accessible text), and clears synchronously only with message read state; clearing replaceable notification rows does not clear it. Dynamic window, account-row, and notification-button names expose the corresponding message/alert status to UI Automation. No message or identity is encoded in the overlay. A click lifecycle is reported only when both IDs still match one in-memory entry. Sticky service enablement plus a bounded single-reader command queue preserve causal order across delivery, disable, preview redaction, clear, forget, and test operations. Each privacy action first persists a bounded global or detached-profile cleanup marker with a fresh opaque generation. A shared generation barrier coalesces equal work, forces new modern delivery behind every newer generation, and allows only the matching successful operation to clear its receipt. The close path flushes state, submits a final non-cancelled retry, and drains accepted commands for up to five seconds; startup replays anything still unconfirmed. Together these prevent a stale submission from restoring alerts or silently surviving interrupted cleanup; Windows history removal itself remains best effort. The app-local Windows App SDK loads on a worker thread only when alerts are enabled or history cleanup is requested; unsupported/missing Singleton broker state fails into an exception-contained tray path without blocking WPF. Legacy balloon callbacks carry no immutable notification identifier, so they can open only the generic labelled notification center.

### Document policy

`about:blank` is accepted only as the exact local bootstrap/privacy-reset page. When a visible workspace has requested microphone access, switching away must complete a host-generated `NavigateToString` blank document before Steam Chat may reconnect; a timeout or navigation failure disposes the controller. Remote documents require default-port HTTPS, no URI user information, exact host `steamcommunity.com`, and a `/chat` or `/login` path boundary. The same decision function protects:

- top-level `NavigationStarting`;
- every `CoreWebView2Frame.NavigationStarting`, including nested frames; and
- every document-context `WebResourceRequested` event.

Static/CDN domains remain available as ordinary subresources but cannot become a trusted top-level or child document. Non-user-initiated external navigation is canceled silently. A direct user gesture may open one bounded, canonical external HTTPS URL after confirmation.

Permissions are deny-by-default and never saved. Notification permission is allowed only for the exact Steam origin and is handled by the native host; untrusted origins are discarded. Only a user-initiated microphone request from the exact Steam origin while the selected workspace and window are visible can reach WebView2's own prompt. Screen capture, camera, clipboard read, HTTP authentication, client certificates, downloads, custom protocols, script dialogs, context menus, autofill, password storage, developer tools, host objects, and web messaging are denied or disabled.

### Identity semantics

The user-entered **profile nickname** selects a local workspace and is editable private metadata. The linked **Steam login name** is the exact native-login identifier used by the launch guard. Neither cryptographically identifies the account signed into the web page. A host-owned area above the WebView permanently shows both and states that Steam's page is authoritative. The connection status reports a loaded workspace rather than claiming verified identity. A login can be relinked transactionally only to a unique safe login read from the local Steam account cache; relinking never manipulates Steam authentication or the web profile.

## Native launch state machine

```text
Select required Steam login + local library AppID
          │
          ▼
Resolve absolute local steam.exe
          │
          ├─ remote/link/unsigned/non-Valve ──► block
          ▼
Match one running image path + Windows session + process ID/start time
          │
          ├─ not running ──► ask user to start Steam
          ▼
Read authoritative ActiveUser and map one valid, non-ambiguous SteamID64
          │
          ├─ absent/unmatched/signed out ──► block as unknown
          ├─ different login ──────────────► guide user to switch in Steam
          ▼
Wait, then repeat SteamID, process, account, and executable checks
          │
          ├─ any change ──► block
          ▼
Open steam.exe read-only with write/delete sharing denied
          │
          ▼
Revalidate Authenticode publisher while locked
          │
          ▼
Wait, then recheck process identity, game, SteamID, and ActiveUser while locked
          │
          ├─ any change ──► block
          │
          ▼
Cancellation check immediately before native invocation
          │
          ▼
ProcessStartInfo.ArgumentList: steam://run/<uint AppId>
```

`MostRecent` remains useful for ordering cached metadata but is never a launch authority. The registry value is deliberately treated as unknown when absent rather than falling back to stale state. Duplicate case-insensitive login names in `loginusers.vdf` invalidate the complete account mapping. Every successful evaluation must return the same SteamID and exact process identity as the prior evaluation. Signature/process/account checks execute off the WPF thread and are serialized, so a slow trust check cannot freeze the interface or race a second launch. Cancellation is checked throughout verification and immediately before process creation. A successful result means only that the verified request was handed to Steam; Steam still controls install readiness, ownership, updates, anti-cheat, and actual process startup.

## Untrusted local metadata

VDF parsing is bounded to 8 MiB/files and source characters, 1 MiB/tokens, 64 nesting levels, and 100,000 nodes. Duplicate keys, truncation, oversized input, and cancellation fail parsing without exposing attacker-controlled values in diagnostics.

Library roots must be absolute local fixed/removable-drive paths without reparse components. A manifest must be a regular local file named `appmanifest_<AppId>.acf`, its internal AppID must match, and `installdir` must be one safe leaf beneath that library's `steamapps\common`. Display names/persona names are length-limited and stripped of control/bidirectional formatting characters.

Directory enumeration and parsing occur inside exception boundaries. Results are staged per library and committed only after a complete bounded scan; stale overlapping refresh results are discarded by generation number.

## Persistence and deletion protocol

`StateStore` accepts at most 4 MiB, JSON depth 32, 100,000 JSON elements, 512 account profiles, and 512 detached Windows-cleanup account IDs. Case-insensitive duplicate properties and unknown properties are rejected. Before use, a linear pass normalises and validates every account ID/login/name/colour, identity uniqueness, selected-chat ID, selected-play ID, configured path, deletion tombstone, and opaque cleanup generation. Invalid state is quarantined to at most three collision-resistant recovery files. Notification content is never part of persisted state.

Writes serialise to a unique same-directory file with write-through semantics, flush, size-check, and replace the primary. Add/remove operations roll UI and model state back if persistence fails. A per-session named mutex prevents two GUI instances from overwriting each other's snapshots.

For account forgetting:

1. persist the profile GUID in `PendingBrowserProfileDeletionIds`;
2. suppress and close its notification lifecycle, detach the controller from the host, and commit a local blank document;
3. clear all profile browsing-data kinds where possible;
4. call WebView2 `Profile.Delete()`, which marks/retries profile deletion;
5. dispose the controller in a `finally` path; and
6. atomically remove account metadata and tombstone.

If steps 3–4 cannot schedule deletion, the account remains visible and pending but no authenticated controller remains live. Startup retries every tombstone before opening normal chat workspaces; the cleanup-only WebView2 initialization does not navigate to Steam or another network page.

## Release design

One project version drives executable metadata, archive name, smoke test, and validation. NuGet restore clears inherited feeds, maps all packages to `nuget.org`, requires its repository signature, and uses lock files. A locked legacy Microsoft dependency that predates repository countersigning is accepted only from its exact pinned Microsoft author certificate. Certificate fingerprints must be updated through a reviewed change when a signer rotates.

Packaging uses a private temporary publish tree, exclusive release lock, sorted entries, fixed ZIP timestamps, and a temporary archive/checksum pair. It validates required files, version, icon, signature policy, entry count/size, traversal, separators, links, reserved names, case collisions, and forbidden local/debug data before publishing. Adversarial fixture tests prove those checks fail closed.

The SHA-256 sidecar is an integrity convenience, not publisher authentication. A public release requires Authenticode signatures for first-party binaries, an authenticated installer/package, and a trusted distribution channel.
