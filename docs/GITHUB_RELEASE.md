# GitHub CI and signed release guide

SteamSwitchboard uses one `Verify` workflow for pull requests, pushes, and protected version tags. Ordinary runs have read-only repository access. A version tag can publish a Windows binary only after all source/dependency scanners, tests, and reproducible-package gates pass.

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). See the repository's [code signing policy](../CODE_SIGNING_POLICY.md).

## Protected release design

1. The approval-gated `sign-release` job rebuilds the unsigned ZIP twice and requires identical SHA-256 hashes.
2. It validates/extracts that exact candidate and records every payload path, length, and hash. Only `SteamSwitchboard.exe` and `SteamSwitchboard.dll` are uploaded as a one-day GitHub artifact.
3. SignPath's official immutable-pinned GitHub action submits that exact artifact. The SignPath connector verifies that it came from this repository and GitHub-hosted workflow, applies the checked-in metadata restrictions, and waits for the Foundation-required manual release approval.
4. The signer job accepts exactly two returned root files, requires intact Authenticode metadata from `SignPath Foundation`, proves their normalized PE contents still match the unsigned manifest, and merges them into the locked payload. It cannot write repository contents or Releases.
5. A fresh `validate-release` runner has no `release` environment or SignPath token. It independently rebuilds the source tag twice, regenerates its trusted manifest, treats the returned payload as untrusted, and permits only bounded Authenticode metadata to differ in the two first-party files.
6. Finalization requires valid same-certificate Authenticode signatures, the exact Foundation publisher, code-signing EKU, trusted RFC 3161 timestamps, and unchanged executable code/resources. GitHub then signs provenance attestations.
7. A separate `publish-release` job has release-write permission but no signing credential. It verifies exact filenames, checksum, workflow/source provenance, and hosted-runner policy before creating an immutable GitHub Release whose page links this policy.

Every referenced GitHub/SignPath action is pinned to an immutable commit. The certificate private key remains in SignPath's HSM. No PFX, personal signing certificate, or release-capable long-lived GitHub token belongs in this repository.

## Apply for free open-source signing

The project owner must submit the application at <https://signpath.org/apply>. Use the exact public details and project description in [SIGNPATH_ONBOARDING.md](SIGNPATH_ONBOARDING.md). Before submission:

1. Keep the repository public and enable multi-factor authentication on GitHub.
2. Confirm that the MIT license, application description, download page, privacy policy, uninstall instructions, team roles, and this code-signing policy are visible.
3. Disclose the clearly labelled unsigned `v1.0.0` prerelease candidate and its checksum-only trust boundary. Ask whether it satisfies the Foundation's existing-binary-release requirement; do not describe it as signed or GitHub-attested.
4. Accept that approval is discretionary, signing displays `SignPath Foundation` rather than a personal publisher name, and every production signature requires manual approval.

Do not create or publish the `v1.0.1` tag while the application is pending. Ordinary pull-request CI remains fully functional without SignPath configuration, while tag signing fails closed.

## Configure SignPath after approval

Use the exact organization and slugs assigned in the SignPath dashboard:

1. Install the SignPath GitHub App with access only to `syphonetic/SteamSwitchboard`.
2. Add the predefined `GitHub.com` trusted build system to the SignPath organization and link it to the SteamSwitchboard project.
3. Set the project repository URL to `https://github.com/syphonetic/SteamSwitchboard`.
4. Create or confirm a release signing policy that uses the Foundation certificate, requires origin verification, accepts only protected release refs from this repository, and requires one manual approval by `syphonetic`.
5. Create an artifact configuration from [.signpath/artifact-configuration.xml](../.signpath/artifact-configuration.xml). It signs exactly the two first-party PE files and enforces their product and file versions.
6. Create a dedicated CI user/API token with submitter permission only for this project and release policy. It must have no approver, configurator, certificate, organization-administrator, or GitHub release authority.

The official SignPath documentation describes the [GitHub trusted-build connector](https://docs.signpath.io/trusted-build-systems/github), [origin verification](https://docs.signpath.io/origin-verification/), and [artifact configuration](https://docs.signpath.io/artifact-configuration/).

## Configure the protected GitHub environment

The repository uses a GitHub environment named exactly `release`:

1. Keep at least one required reviewer. For a one-maintainer repository, keep “prevent self-review” off; turn it on after a second trusted maintainer is available.
2. Keep deployment branches/tags limited to selected tags matching `v*`.
3. Keep the active tag ruleset matching `refs/tags/v*` that blocks deletion and non-fast-forward updates. The workflow refuses an unprotected tag.
4. Keep `main` protected with `Source and dependency security gate` and `Windows Release gate` required before merging.
5. Keep release immutability enabled under **Settings → General → Releases**.

After SignPath approval, add these values to the `release` environment—not repository-wide configuration:

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | Dedicated least-privilege CI submitter token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Assigned organization UUID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Assigned project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Assigned release policy slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Assigned artifact-configuration slug |

With GitHub CLI authenticated to `syphonetic`, set the secret interactively so it never appears in shell history:

```powershell
gh secret set SIGNPATH_API_TOKEN --env release
gh variable set SIGNPATH_ORGANIZATION_ID --env release --body '<assigned-organization-uuid>'
gh variable set SIGNPATH_PROJECT_SLUG --env release --body '<assigned-project-slug>'
gh variable set SIGNPATH_SIGNING_POLICY_SLUG --env release --body '<assigned-release-policy-slug>'
gh variable set SIGNPATH_ARTIFACT_CONFIGURATION_SLUG --env release --body '<assigned-artifact-configuration-slug>'
```

Never paste the API token into a command argument, variable, source file, issue, pull request, workflow log, or release asset.

## Pull-request and release flow

For ordinary changes:

```powershell
git switch -c my-change
git add <reviewed-paths>
git commit -m 'Describe the change'
git push -u origin my-change
```

Open a pull request, review every changed path, and wait for both required gates. Merge only after they pass.

`v1.0.0` is the immutable initial tag and has one explicitly unsigned prerelease package. Do not move, delete, or recreate the tag, and do not replace its assets in place. Version `1.0.1` is the first signed-binary candidate. After the SignPath application is approved, all GitHub/SignPath configuration is independently checked, and the release-pipeline pull request is merged:

```powershell
git switch main
git pull --ff-only
$Version = '1.0.1'
$ProjectVersion = ([xml](Get-Content './src/SteamSwitchboard.App/SteamSwitchboard.App.csproj' -Raw)).Project.PropertyGroup.Version
if ($ProjectVersion -cne $Version) { throw 'Project version does not match the intended release.' }
git status --short
git tag -a "v$Version" -m "SteamSwitchboard $Version"
git push origin "v$Version"
```

Open **Actions → Verify**, select the tag run, and approve the GitHub `release` environment after confirming the commit, tag, and version. Then approve the matching request in SignPath after confirming the repository, commit, workflow, artifact configuration, and two filenames. The workflow publishes automatically only after independent validation succeeds.

A failed run never publishes an unsigned binary. Diagnose and rerun the same workflow when safe; never move a public version tag to different source. If SignPath's source/build policy forbids reruns, create a new version after fixing the cause.

## Independent release verification

Download the two assets from the Release page into an empty directory and verify checksum and GitHub provenance:

```powershell
$Version = '1.0.1'
$Archive = "SteamSwitchboard-$Version-win-x64.zip"
$Expected = ((Get-Content "$Archive.sha256" -Raw).Trim() -split '  ', 2)[0]
$Actual = (Get-FileHash $Archive -Algorithm SHA256).Hash
if (-not $Actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Release checksum mismatch.'
}

gh attestation verify $Archive `
  --repo syphonetic/SteamSwitchboard `
  --signer-workflow syphonetic/SteamSwitchboard/.github/workflows/verify.yml `
  --source-ref "refs/tags/v$Version" `
  --deny-self-hosted-runners
gh attestation verify "$Archive.sha256" `
  --repo syphonetic/SteamSwitchboard `
  --signer-workflow syphonetic/SteamSwitchboard/.github/workflows/verify.yml `
  --source-ref "refs/tags/v$Version" `
  --deny-self-hosted-runners
```

After extraction, inspect both first-party files:

```powershell
$Root = ".\SteamSwitchboard-$Version-win-x64"
$Signatures = @(
  Get-AuthenticodeSignature "$Root\SteamSwitchboard.exe"
  Get-AuthenticodeSignature "$Root\SteamSwitchboard.dll"
)
foreach ($Signature in $Signatures) {
  if ($Signature.Status -ne 'Valid' `
      -or $null -eq $Signature.TimeStamperCertificate `
      -or $Signature.SignerCertificate.GetNameInfo('SimpleName', $false) -cne 'SignPath Foundation') {
    throw 'Release signature validation failed.'
  }
}
$Signatures | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

Windows' Properties dialog must show the same Digital Signatures identity. A new validly signed application may still receive a SmartScreen reputation prompt initially; code signing establishes publisher identity and integrity but does not guarantee immediate reputation.

## Troubleshooting

- **Tag is unprotected:** restore the active `v*` tag ruleset; do not remove the workflow check.
- **SignPath configuration is missing:** wait for Foundation approval, then set the one secret and four environment variables exactly as assigned.
- **Origin verification fails:** confirm the SignPath GitHub App repository scope, predefined GitHub trusted-build link, exact repository URL, protected ref policy, and GitHub-hosted runners.
- **Signing request is awaiting approval:** inspect the source commit, workflow, artifact configuration, product/file-version parameters, and exact two-file artifact before approving it in SignPath.
- **Response shape or publisher fails:** do not loosen the importer. Confirm the artifact configuration signs exactly the two root files with the Foundation certificate.
- **Signature has no trusted timestamp or Windows trust fails:** do not publish; ask SignPath support to inspect the signing policy/certificate.
- **A release already exists for the tag:** inspect it instead of overwriting. Publish a new version for changed bytes.
- **No release appears after normal branch CI:** intentional. Only an annotated, protected `vMAJOR.MINOR.PATCH` tag can enter the signing environment.
