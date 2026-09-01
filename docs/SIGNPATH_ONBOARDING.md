# SignPath Foundation onboarding

This page contains the non-secret information and exact post-approval configuration for SteamSwitchboard's free open-source code-signing application.

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

Version `v1.0.0` is an immutable source release. Version `v1.0.1` is the first Windows binary candidate and will not be published as a final binary unless the protected workflow receives a trusted signature. If SignPath requires an already downloadable unsigned binary rather than the source release and reproducible package evidence, ask the Foundation whether a clearly labelled, GitHub-attested release candidate is sufficient before publishing one.

Dependency disclosure for the Foundation review:

- First-party application source and build scripts are MIT-licensed and fully public.
- The .NET runtime and WebView2 SDK redistribution are included with their restored license/notice files; Steam's web application and WebView2 Evergreen browser runtime are not bundled.
- The self-contained package includes unmodified Windows App SDK platform runtime files under Microsoft's redistributable license. They are treated as Windows system libraries, remain unsigned by this project, and are listed under `THIRD-PARTY-LICENSES` in every package. Explicitly ask SignPath Foundation to confirm that these files fit its System Library exception before approval.

## SignPath project configuration after approval

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

Keep the existing protected environment named `release`. Add the following environment values only after SignPath approves and creates the project:

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | Dedicated least-privilege CI submitter token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization UUID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Assigned project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Assigned release policy slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Assigned artifact configuration slug |

Never put the API token in a repository variable, source file, issue, pull request, command transcript, or release asset. The workflow is intentionally fail-closed while any value is absent.

## First signing acceptance

Before tagging `v1.0.1`, confirm that the SignPath dashboard shows the expected repository, policy, artifact configuration, publisher, manual approver, and origin-verification restrictions. The first protected tag run must then pass every gate in [GITHUB_RELEASE.md](GITHUB_RELEASE.md). Download the result on a clean standard-user Windows VM and verify the checksum, GitHub attestations, both trusted timestamped signatures, displayed publisher, startup, notification path, account isolation, guarded launch, and complete removal.
