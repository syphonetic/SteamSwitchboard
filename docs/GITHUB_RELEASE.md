# GitHub CI and release guide

This repository's `Verify` workflow is the release gate. It runs on every pull request and push, including version-tag pushes. The workflow has read-only repository permission and does not publish a GitHub Release.

For every pull request and push, the dedicated Ubuntu scanner job runs pinned Semgrep and Trivy first. After that job succeeds, the Windows job:

1. Restores NuGet packages in locked mode.
2. Builds and tests in `Release` with warnings treated as errors.
3. Runs `scripts/security-audit.ps1`, including the repository's transitive NuGet vulnerability audit and the complete build/test gate.
4. Runs `scripts/package.ps1`, which publishes a self-contained Windows x64 package and runs the normal and adversarial package validators.

For a tag named `vMAJOR.MINOR.PATCH` (or a prerelease form such as `v1.2.3-beta.1`), the workflow also checks that the tag exactly matches the `<Version>` in `src/SteamSwitchboard.App/SteamSwitchboard.App.csproj`. It then uploads the validated ZIP and SHA-256 checksum as a workflow artifact retained for 30 days.

GitHub's hosted Windows runner has no normal interactive desktop, Steam session, or notification surface. CI compiles the real-window harness and runs all deterministic tests, scanners, and package checks, but the composed WebView/window check and the actual Windows-alert check remain local release acceptance steps:

```powershell
./scripts/test-ui-regression.ps1
dotnet run --project tests/SteamSwitchboard.UiRegression -c Release -- --notification-smoke
```

## First push to GitHub

These commands are instructions for your computer. The CI workflow does not create or configure a remote. This checkout is already a Git repository on the `main` branch; when this guide was added, it had no configured remote.

1. Sign in to GitHub and [create a new empty repository](https://docs.github.com/en/repositories/creating-and-managing-repositories/creating-a-new-repository). Do not initialize it with a README, license, or `.gitignore` when this local folder already contains those files.
2. Open PowerShell and inspect what will be pushed:

   ```powershell
   Set-Location J:\tools\SteamSwitchboard
   git status
   git branch --show-current
   git remote -v
   ```

   The branch should be `main`. Review every path reported by `git status`; do not stage unrelated or secret files.

3. If the intended local work is not committed yet, stage it, review it again, and commit it. `git add .` stages every non-ignored change, so list individual paths instead when only some changes belong in the commit.

   ```powershell
   git add .
   git status
   git commit -m "Prepare SteamSwitchboard for GitHub"
   ```

4. Replace `YOUR-NAME` with your GitHub user or organization name, add the new empty repository as `origin`, and push `main`:

   ```powershell
   git remote add origin https://github.com/YOUR-NAME/SteamSwitchboard.git
   git push -u origin main
   ```

5. Open the repository's **Actions** tab on GitHub, select **Verify**, and wait for both **Semgrep and Trivy security gate** and **Windows Release gate** to finish successfully. You can also use **Run workflow** there to repeat the checks manually.

If `git remote -v` already shows the correct `origin`, skip `git remote add`. If it shows a different destination, stop and verify which repository should receive the code before changing anything. GitHub also documents [remote repository management](https://docs.github.com/en/get-started/git-basics/managing-remote-repositories).

## Normal change and pull-request flow

Create a branch, commit the intended files, and push the branch:

```powershell
Set-Location J:\tools\SteamSwitchboard
git switch -c my-change
git add path\to\changed-file
git status
git commit -m "Describe the change"
git push -u origin my-change
```

On GitHub, choose **Compare & pull request**, review the file list, create the pull request, and wait for **Windows Release gate** to pass. See GitHub's [pull-request instructions](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/proposing-changes-to-your-work-with-pull-requests/creating-a-pull-request) if the prompt is not visible.

Repository administrators can optionally require **Windows Release gate** in the `main` branch protection rules. Do not select a similarly named check from an untrusted workflow.

## Optional GitHub-native CodeQL layer

For a public repository (or an eligible organization repository with GitHub Code Security), you can add GitHub's own CodeQL analysis without editing this workflow: open **Settings → Advanced Security**, find **CodeQL analysis**, choose **Set up**, and enable **Default**. GitHub currently supports no-build analysis for C#, so this complements the enforced Semgrep/Trivy/NuGet gates without replacing the Windows build. See GitHub's official [CodeQL default-setup guide](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/configure-code-scanning).

After the first successful runs, use a branch ruleset or branch protection to require **Semgrep and Trivy security gate** and **Windows Release gate** before merging to `main`. Keep workflow permissions read-only unless a future, separately reviewed release-publishing design genuinely needs write access.

## Create a version release

1. Update `<Version>` in `src/SteamSwitchboard.App/SteamSwitchboard.App.csproj` to the intended semantic version, update any release notes, commit the change, and push it through the normal pull-request flow.
2. Wait for the `main` branch's **Windows Release gate** run to pass.
3. From an up-to-date `main` branch, create and push an annotated tag that exactly matches the project version:

   ```powershell
   Set-Location J:\tools\SteamSwitchboard
   git switch main
   git pull --ff-only
   $Version = '1.0.0'
   git tag -a "v$Version" -m "SteamSwitchboard $Version"
   git push origin "v$Version"
   ```

4. On GitHub, open **Actions** > **Verify**, select the run for the version tag, and wait for **Windows Release gate** to pass. A malformed tag or a tag/project-version mismatch fails before the artifact is uploaded.
5. In that run's **Artifacts** section, download `SteamSwitchboard-VERSION-win-x64`. GitHub wraps workflow artifacts in a download archive; extract it to obtain these two release files:

   - `SteamSwitchboard-VERSION-win-x64.zip`
   - `SteamSwitchboard-VERSION-win-x64.zip.sha256`

6. Optionally verify the downloaded package from a repository checkout:

   ```powershell
   $Version = '1.0.0'
   $Zip = ".\SteamSwitchboard-$Version-win-x64.zip"
   $Checksum = "$Zip.sha256"
   Get-FileHash -LiteralPath $Zip -Algorithm SHA256
   Get-Content -LiteralPath $Checksum
   .\scripts\validate-package.ps1 `
     -ArchivePath $Zip `
     -ChecksumPath $Checksum `
     -ExpectedVersion $Version
   ```

   The hash printed by `Get-FileHash` must match the first value in the checksum file. The validator performs the repository's additional archive and binary checks.

7. Open the repository's **Releases** page, choose **Draft a new release**, select the existing `vVERSION` tag, enter a title and release notes, and upload the two extracted release files. Upload the inner product ZIP and its checksum, not GitHub's outer artifact-download archive. Mark prereleases appropriately, review the draft, and then choose **Publish release**. GitHub documents [downloading workflow artifacts](https://docs.github.com/en/actions/managing-workflow-runs/downloading-workflow-artifacts) and [managing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

The workflow deliberately stops at artifact creation. Manual release publication keeps its GitHub token at `contents: read` and prevents a successful tag build from automatically becoming a public release.

## Optional code signing and its limitations

The current package is unsigned by design. The SHA-256 checksum detects accidental corruption only when the checksum itself is trusted; it does not prove publisher identity if an attacker can replace both files. A Git tag or GitHub Release also does not Authenticode-sign the Windows executable, so Windows may show an unknown-publisher or reputation warning.

For a future signed release:

- Obtain a trusted Windows Authenticode code-signing certificate, preferably protected by a hardware security module or a reputable cloud-signing service, and use a trusted timestamp service.
- Never commit a PFX file, private key, certificate password, signing token, or service credential to this repository.
- Sign the published executable and relevant DLLs before the ZIP and checksum are created, then validate the signatures before upload.
- Treat a self-signed certificate as a development aid only; it is not automatically trusted on other users' computers.

The existing `package.ps1 -RequireSignature` option validates signatures; it does not create them. Enabling that option in the current unsigned workflow would make every package fail. Adding signing requires a separately reviewed packaging/workflow change, protected GitHub environment or secret configuration, and signing-service permissions. Those changes are intentionally outside this guide and the current read-only release workflow.

## Troubleshooting

- **The version-tag run fails before packaging:** make the tag text match the project's `<Version>` exactly. Publish a corrected version commit and tag instead of moving a release tag that users may already trust.
- **No artifact appears:** artifacts are uploaded only for tags beginning with `v`, after all security and package validation succeeds.
- **The artifact expired:** workflow artifacts are retained for 30 days. Public release assets remain available according to the repository's release and retention policies.
- **A scanner job cannot download its rules/database:** retry once in case the upstream registry had a transient outage. Do not bypass a repeatable scanner failure; inspect the job log and pinned scanner version. Local parity is available with `security-audit.ps1 -RequireExternalScanners` when both tools are installed.
- **Windows warns about the app:** the package is self-contained but unsigned. Self-contained deployment removes the need for a separately installed .NET runtime; it does not establish publisher trust.
