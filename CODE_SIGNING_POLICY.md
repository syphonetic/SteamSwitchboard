# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

SteamSwitchboard is an MIT-licensed open-source project. Its official source repository is <https://github.com/syphonetic/SteamSwitchboard>, and its only official binary download location is <https://github.com/syphonetic/SteamSwitchboard/releases>. A valid public signature identifies the publisher as **SignPath Foundation**; it does not imply affiliation with or endorsement by Valve Corporation.

## Signed artifacts

The release policy permits Authenticode signing of exactly these two first-party files built from this repository:

- `SteamSwitchboard.exe`
- `SteamSwitchboard.dll`

Third-party runtime files are included under their own licenses and are never signed with the SignPath Foundation certificate by this project. The checked-in SignPath artifact configuration requires the product name `SteamSwitchboard`, the exact source-bound product version, and the release file version on both signed files.

## Release controls

A production signature can be requested only by the protected GitHub tag workflow after all of the following controls pass:

1. Full-history secret scanning, dependency auditing, static analysis, compilation with warnings treated as errors, automated tests, adversarial package tests, and byte-for-byte package reproducibility.
2. An annotated, immutable, protected `vMAJOR.MINOR.PATCH` tag whose version exactly matches the project.
3. Approval of GitHub's protected `release` environment.
4. SignPath origin verification against this repository and GitHub-hosted runners, followed by the SignPath Foundation approval required for each release.
5. Independent reconstruction of the unsigned package on a fresh runner that has no SignPath API token or release-environment access.
6. Proof that only bounded Authenticode metadata changed in the two permitted files, followed by Windows trust, publisher, code-signing EKU, shared-certificate, and trusted timestamp validation.
7. GitHub provenance attestation, checksum verification, and publication as a new immutable GitHub Release. Failed candidates and unsigned production candidates are never published by this workflow.

The `v1.0.0` GitHub prerelease is a separately disclosed unsigned evaluation candidate published before Foundation onboarding. Its release title, notes, asset label, and documentation identify it as unsigned; its checksum provides integrity but no publisher identity. It is not evidence of a SignPath signature or GitHub build-provenance attestation. Version `v1.0.1` and later production binaries must pass the protected signing workflow above.

The SignPath API token is an approval-gated GitHub environment secret available only to the signing job. That job has read-only source and Actions access, cannot write repository contents or Releases, and transfers only a bounded signed payload to the independent validator. No certificate private key, PFX, or personal signing certificate is stored in GitHub or this repository.

## Team roles

- Committer and author: [@syphonetic](https://github.com/syphonetic)
- Reviewer for contributions from non-committers and release-boundary changes: [@syphonetic](https://github.com/syphonetic)
- Signing approver: [@syphonetic](https://github.com/syphonetic)

The project currently has one maintainer. External contributions require maintainer review, and GitHub's required build/security gates apply to every pull request. Release-signing requests also require a distinct manual approval step. If another trusted maintainer joins, code review and signing approval will be separated between people. Every maintainer with repository or SignPath access must use multi-factor authentication.

## Privacy and system changes

SteamSwitchboard has no telemetry, analytics, advertising SDK, update beacon, cloud database, or developer-operated account service. It transfers no information to a developer-controlled network service. User-requested embedded Steam sessions communicate with Steam-operated HTTPS services, and user-approved external links communicate with the displayed destination. The complete data and network behavior is documented in the [privacy policy](docs/PRIVACY.md).

The application is portable and does not silently change Windows or Steam configuration. It stores isolated local browser sessions and settings under `%LOCALAPPDATA%\SteamSwitchboard`. Account data can be removed from inside the app. To uninstall completely, close the app, delete the extracted application folder, and optionally delete `%LOCALAPPDATA%\SteamSwitchboard` as described in the privacy policy.

## Verification and incident response

Users should verify the release checksum, GitHub provenance attestation, and both Authenticode signatures using the steps in the [release guide](docs/GITHUB_RELEASE.md). A valid signature must report `SignPath Foundation`, include a trusted timestamp, and correspond to the protected source tag.

Suspected compromise, policy violation, malware, or misuse of the signing identity must be reported through GitHub's private security-advisory channel and to SignPath when appropriate. The maintainer will suspend releases, investigate the source/build/signing path, cooperate with SignPath Foundation, and request revocation when a signed artifact or signing credential may be compromised. Existing tags and immutable releases will not be silently replaced.
