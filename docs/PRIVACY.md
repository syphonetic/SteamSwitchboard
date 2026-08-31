# Privacy

SteamSwitchboard is local-first. It has no telemetry, analytics endpoint, advertising SDK, cloud database, update beacon, or developer account service.

## Data stored locally

`%LOCALAPPDATA%\SteamSwitchboard\state.json` contains generated profile identifiers, user-chosen profile nicknames, non-secret linked Steam login names used for native-account matching, selected chat/required-launch profile identifiers, accent colours, timestamps, application settings, and any browser-profile deletion request that still needs to finish. It never contains a Steam password, Steam Guard code, cookie, QR secret, authentication token, sender name, or message text.

`%LOCALAPPDATA%\SteamSwitchboard\BrowserData` is managed by Microsoft Edge WebView2. Each GUID-named profile contains that workspace's Steam cookies, site storage, cache, and preferences. Password saving and general autofill are disabled, but a persistent authenticated web session necessarily stores Steam session data. Up to 16 profiles stay open by default so they can receive conversations and notifications; additional saved profiles retain their isolated session on disk and reopen when selected. Users can opt into background sleep in Settings to reduce memory use further.

The notification center holds at most 100 entries in process memory and clears them when Switchboard exits. The receiving profile nickname/login and Steam-provided notification title are shown; the title normally names the sender but remains untrusted web text. Tagged replacements reuse one entry, and Steam receives click/close lifecycle only for the matching host action. Message previews are disabled by default, and Windows alerts can be disabled independently.

When modern Windows alerts are supported, Switchboard submits the receiving profile/login, Steam title, and optional preview to Windows. Those alerts expire after 24 hours and on reboot. Turning Windows alerts off, turning previews off, clearing history, or forgetting a profile first persists an opaque cleanup generation, then requests removal through one bounded ordered queue. New modern delivery waits behind the latest cleanup barrier, and only its matching successful Windows removal clears the receipt. The receipt contains no message, sender, login, or profile nickname; a detached-profile receipt contains only the generated local GUID and remains bounded. Shutdown flushes state and drains accepted cleanup for up to five seconds, while startup retries unconfirmed work. Windows history cleanup remains best effort outside Switchboard's process. A modern click carries only bounded opaque profile/live-notification IDs and reports Steam's click lifecycle only when that exact in-memory entry still exists. When the modern broker is unavailable, a short-lived compatibility tray alert is used instead; because its click contains no alert identity, it opens the generic labelled notification center. Notification text is not written to Switchboard state, logs, analytics, or a developer service. **Send test alert** replaces its prior test entry and lets you verify the selected Windows path, while **Open Windows notification settings** exposes Windows' own delivery/history controls.

`%LOCALAPPDATA%\SteamSwitchboard\Logs` contains bounded privacy-minimised records: UTC timestamp, exception type, and numeric error code. Release and Debug builds use the same safe format. URLs, messages, stack traces, local account paths, credentials, and browser data are not logged.

The app requires these folders to remain on a local fixed/removable drive without filesystem links. This prevents its data paths from being silently redirected to a network share.

## Network connections

The embedded workspace renders only default-port HTTPS documents at exact `steamcommunity.com/chat` and `steamcommunity.com/login` routes. Steam-controlled scripts may load their normal static/network subresources; those requests are governed by Valve's privacy practices.

SteamSwitchboard itself sends nothing to a developer-controlled service. A direct user action can open an external default-port HTTPS URL in the system browser only after the complete canonical URL is shown for confirmation. Script-initiated external navigation, custom protocols, and downloads are blocked. Keeping profiles active does not add a developer connection; it keeps each official Steam Chat page running in its isolated WebView2 profile.

## Browser permissions

Camera, clipboard-read, screen-capture, client-certificate, browser-native authentication, and other unnecessary permissions are denied. Notification permission is allowed only for the exact Steam origin, intercepted by the host, and never saved. Web-controlled notification strings are raw-size bounded, sanitised, replacement-aware, and globally/per-profile rate-contained before display. Persisted permission grants from an older profile/runtime are reset before navigation. A microphone request can reach WebView2's own prompt only from the exact Steam origin while that account's workspace and the app window are visible and after a user gesture. Switching away commits a host-generated local blank document before the hidden chat can reconnect; if that proof fails, the WebView is disposed. Permission decisions are not saved to the profile.

## Local Steam information read

For discovery and launch safety, the app reads bounded copies of Steam library manifests, cached account/login/persona metadata, the current user's `ActiveUser` registry value, the exact Steam process image/session/ID/start time, and the Authenticode signer of `steam.exe`. These sources are never modified. `MostRecent` cache data does not authorize a launch.

## Identity notice

The profile nickname is editable local metadata. The linked Steam login is a non-secret native-account identifier used by the launch guard; it can be relinked only to a safe unique login detected from Steam's local cache. SteamSwitchboard intentionally does not scrape the signed-in web identity or access cookies to bind either field to the page. The host-owned banner shows the expected login; the account shown by Steam's page is authoritative for messages.

## Removing data

Choose **Settings → Forget selected account**. Switchboard first saves a durable cleanup marker, suppresses that account's queued/history notifications, detaches its workspace, commits a local blank document, asks WebView2 to clear all profile data and delete the generated browser profile, and disposes the controller. It then removes local metadata. If deletion cannot be scheduled, the record remains visibly pending and retries at next startup instead of claiming success; no failed-cleanup workspace remains connected.

To remove everything manually, close SteamSwitchboard and delete `%LOCALAPPDATA%\SteamSwitchboard`. Backups, filesystem recovery tools, roaming/backup products, Steam, Microsoft, or network infrastructure may retain data outside the application's control.

## Local attacker boundary

Anyone who can execute software as the same Windows user can generally read or alter that user's WebView2 data, Steam metadata, registry values, and application files. Use a protected Windows account, lock the device when unattended, and do not copy `BrowserData` between users or machines. SteamSwitchboard refuses elevation so these local assets are never intentionally exposed through an administrator-level process.
