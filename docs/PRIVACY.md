# Privacy

SteamSwitchboard is local-first. It has no telemetry, analytics endpoint, advertising SDK, cloud database, update beacon, or developer account service.

## Data stored locally

`%LOCALAPPDATA%\SteamSwitchboard\state.json` contains generated profile identifiers, user-chosen display labels, non-secret Steam login names used for native-account matching, selected chat/play profile identifiers, accent colours, timestamps, application settings, and any browser-profile deletion request that still needs to finish. It never contains a Steam password, Steam Guard code, cookie, QR secret, authentication token, sender name, or message text.

`%LOCALAPPDATA%\SteamSwitchboard\BrowserData` is managed by Microsoft Edge WebView2. Each GUID-named profile contains that workspace's Steam cookies, site storage, cache, and preferences. Password saving and general autofill are disabled, but a persistent authenticated web session necessarily stores Steam session data. Up to 16 profiles stay open by default so they can receive conversations and notifications; additional saved profiles retain their isolated session on disk and reopen when selected. Users can opt into background sleep in Settings to reduce memory use further.

The notification center holds at most 100 entries in process memory and clears them when Switchboard exits. The receiving account label/login and Steam-provided notification title are shown; the title normally names the sender but remains untrusted web text. Tagged replacements reuse one entry, and Steam receives click/close lifecycle only for the matching host action. A legacy Windows balloon click opens the generic labelled notification center and is not attributed to an account until you choose a specific in-app entry. Message previews are disabled by default, and Windows alerts can be disabled independently. Notification text is not written to state, logs, analytics, or another service.

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

The Switchboard display label is local metadata. SteamSwitchboard intentionally does not scrape the signed-in web identity or access cookies to bind that label to the page. The host-owned banner shows the expected login; the account shown by Steam's page is authoritative for messages.

## Removing data

Choose **Settings → Forget selected account**. Switchboard first saves a durable cleanup marker, suppresses that account's queued/history notifications, detaches its workspace, commits a local blank document, asks WebView2 to clear all profile data and delete the generated browser profile, and disposes the controller. It then removes local metadata. If deletion cannot be scheduled, the record remains visibly pending and retries at next startup instead of claiming success; no failed-cleanup workspace remains connected.

To remove everything manually, close SteamSwitchboard and delete `%LOCALAPPDATA%\SteamSwitchboard`. Backups, filesystem recovery tools, roaming/backup products, Steam, Microsoft, or network infrastructure may retain data outside the application's control.

## Local attacker boundary

Anyone who can execute software as the same Windows user can generally read or alter that user's WebView2 data, Steam metadata, registry values, and application files. Use a protected Windows account, lock the device when unattended, and do not copy `BrowserData` between users or machines. SteamSwitchboard refuses elevation so these local assets are never intentionally exposed through an administrator-level process.
