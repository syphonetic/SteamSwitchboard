# SignPath Foundation reapplication record

**Status:** SignPath Foundation declined the initial application on 2026-09-03 because SteamSwitchboard had not yet established sufficient public adoption and independent visibility. No SignPath project, certificate access, API token, or signing configuration was issued. The v1.0.0 prerelease remains unsigned.

This page preserves the non-secret application information and the exact post-approval configuration for a possible future reapplication. It must not be read as evidence that SignPath approved, endorsed, or signed SteamSwitchboard.

## Application information

- Project: `SteamSwitchboard`
- Repository: <https://github.com/syphonetic/SteamSwitchboard>
- Downloads/releases: <https://github.com/syphonetic/SteamSwitchboard/releases>
- License: [MIT](../LICENSE)
- Code signing policy: [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md)
- Privacy policy: [docs/PRIVACY.md](PRIVACY.md)
- Maintainer: `syphonetic`

Suggested project description:

> SteamSwitchboard is a privacy-first Windows companion for people who use several Steam accounts. It keeps independent official Steam web-chat sessions in isolated WebView2 profiles, provides account-aware Windows notifications, and verifies the active native Steam account before handing a local game launch to Valve's client. It does not collect passwords or cookies, inject into Steam, modify Steam configuration, bypass licensing or anti-cheat, or operate a developer-controlled backend.

Version `v1.0.0` remains an immutable annotated tag. Its [GitHub Release](https://github.com/syphonetic/SteamSwitchboard/releases/tag/v1.0.0) contains a clearly labelled unsigned Windows prerelease candidate built from exact commit `6dc7560c89e1d14cca7f16b815bde2aedf16a026`, plus a SHA-256 sidecar for `a654d76c8506238c9b5c96e4effcf2d288db3f82afd4dd1128f07655880c9c26`. The release explicitly states that the checksum provides integrity rather than publisher identity and that the package has no Authenticode or GitHub provenance attestation. Version `v1.0.1` remains reserved for the first publisher-authenticated production binary and will not be published unless the protected workflow receives a trusted signature.

Dependency disclosure for a future Foundation review:

- First-party application source and build scripts are MIT-licensed and fully public.
- The .NET runtime and WebView2 SDK redistribution are included with their restored license/notice files; Steam's web application and WebView2 Evergreen browser runtime are not bundled.
- The self-contained package includes unmodified Windows App SDK platform runtime files under Microsoft's redistributable license. They are treated as Windows system libraries, remain unsigned by this project, and are listed under `THIRD-PARTY-LICENSES` in every package. A future reapplication must explicitly ask SignPath Foundation to confirm that these files fit its System Library exception before approval.

## SignPath project configuration after a future approval

Use the values assigned by SignPath rather than guessing them. The recommended configuration is:

- Trusted build system: predefined `GitHub.com`, linked to this project
- Repository: `https://github.com/syphonetic/SteamSwitchboard`
- Allowed release refs: protected version tags created from `main`
- Signing policy: release signing with origin verification and one manual approval
- Artifact configuration: upload [.signpath/artifact-configuration.xml](../.signpath/artifact-configuration.xml)
- Signed files: exactly `SteamSwitchboard.exe` and `SteamSwitchboard.dll`
- Expected certificate publisher: `SignPath Foundation`
- Submitter: a dedicated CI user/token restricted to this project and release policy
- Approver: `syphonetic`

Install the SignPath GitHub App only for `syphonetic/SteamSwitchboard`, link the predefined GitHub trusted build system, and enable origin verification. Do not grant the CI token configuration, approval, or administrative permissions.

## GitHub release-environment configuration

Keep the existing protected environment named `release` empty. Add the following environment values only if SignPath approves a future reapplication and creates the project:

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | Dedicated least-privilege CI submitter token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization UUID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Assigned project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Assigned release policy slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Assigned artifact configuration slug |

Never put the API token in a repository variable, source file, issue, pull request, command transcript, or release asset. The workflow is intentionally fail-closed while any value is absent.

## First signing acceptance

Do not tag `v1.0.1` while the integration is inactive. After a future approval, confirm that the SignPath dashboard shows the expected repository, policy, artifact configuration, publisher, manual approver, and origin-verification restrictions. The first protected tag run must then pass every gate in [GITHUB_RELEASE.md](GITHUB_RELEASE.md). Download the result on a clean standard-user Windows VM and verify the checksum, GitHub attestations, both trusted timestamped signatures, displayed publisher, startup, notification path, account isolation, guarded launch, and complete removal.
