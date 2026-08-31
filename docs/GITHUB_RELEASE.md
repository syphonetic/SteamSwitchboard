# GitHub CI and release guide

This repository's `Verify` workflow is the release gate. It runs on every pull request and push, including version-tag pushes. The workflow has read-only repository permission and does not publish a GitHub Release.

For every pull request and push, the dedicated Ubuntu scanner job runs checksum-pinned Gitleaks over complete Git history, then pinned Semgrep and Trivy. After that job succeeds, the Windows job:

1. Restores NuGet packages in locked mode.
2. Builds and tests in `Release` with warnings treated as errors.
3. Runs `scripts/security-audit.ps1`, including the repository's transitive NuGet vulnerability audit and the complete build/test gate.
4. Runs `scripts/package.ps1` twice around a clean build, requiring identical package hashes; each pass creates a runner-local self-contained Windows x64 package and runs the normal and adversarial package validators.

For a tag named `vMAJOR.MINOR.PATCH` (or a prerelease form such as `v1.2.3-beta.1`), the workflow also checks that the tag exactly matches the `<Version>` in `src/SteamSwitchboard.App/SteamSwitchboard.App.csproj`. The current workflow deliberately does not upload its unsigned Windows package. CI proves the source builds and packages reproducibly, then discards that runner-local candidate.

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

5. Open the repository's **Actions** tab on GitHub, select **Verify**, and wait for both **Source and dependency security gate** and **Windows Release gate** to finish successfully. You can also use **Run workflow** there to repeat the checks manually.

6. This release checkout already contains the annotated `v1.0.0` tag. Confirm that it names the same commit as `main`, then push the existing tag; do not try to create it again:

   ```powershell
   $HeadCommit = git rev-parse HEAD
   $TagCommit = git rev-list -n 1 v1.0.0
   if ($HeadCommit -cne $TagCommit) { throw 'v1.0.0 does not point to the current release commit.' }
   git push origin v1.0.0
   ```

7. Wait for both gates to pass again on the `v1.0.0` tag run. A malformed tag or tag/project-version mismatch fails that run.

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

For a public repository (or an eligible organization repository with GitHub Code Security), you can add GitHub's own CodeQL analysis without editing this workflow: open **Settings → Advanced Security**, find **CodeQL analysis**, choose **Set up**, and enable **Default**. GitHub currently supports no-build analysis for C#, so this complements the enforced Gitleaks/Semgrep/Trivy/NuGet gates without replacing the Windows build. See GitHub's official [CodeQL default-setup guide](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/configure-code-scanning).

After the first successful runs, use a branch ruleset or branch protection to require **Source and dependency security gate** and **Windows Release gate** before merging to `main`. Keep workflow permissions read-only unless a future, separately reviewed release-publishing design genuinely needs write access.

## Publish a source version

For the initial 1.0.0 release, the first-push instructions above already push the existing reviewed tag. For each later version:

1. Update `<Version>` in `src/SteamSwitchboard.App/SteamSwitchboard.App.csproj` to the intended semantic version, update any release notes, commit the change, and push it through the normal pull-request flow.
2. Wait for both gates on the `main` branch to pass.
3. From an up-to-date `main` branch, create and push a new annotated tag that exactly matches the project version:

   ```powershell
   Set-Location J:\tools\SteamSwitchboard
   git switch main
   git pull --ff-only
   $Version = '1.0.0'
   git tag -a "v$Version" -m "SteamSwitchboard $Version"
   git push origin "v$Version"
   ```

4. On GitHub, open **Actions** > **Verify**, select the run for the version tag, and wait for both gates to pass. A malformed tag or tag/project-version mismatch fails the run.

For 1.0.0 or a later version whose tag gate passed, open the repository's **Releases** page, choose **Draft a new release**, select the existing `vVERSION` tag, enter a title and release notes, and publish it without a Windows binary. GitHub automatically links source archives for the tagged source. GitHub documents [managing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository).

This safely publishes the source version while keeping the workflow token at `contents: read`. Do not attach the current unsigned ZIP to a public release.

## Required before a Windows binary release

The local/CI package is unsigned by design and exists only for validation. A SHA-256 sidecar detects corruption only when the sidecar itself is trusted; it cannot authenticate a publisher if an attacker replaces both files. A Git tag or GitHub Release also does not Authenticode-sign Windows binaries.

Before attaching a Windows ZIP to a public release:

- Obtain a trusted Windows Authenticode code-signing certificate, preferably protected by a hardware security module or a reputable cloud-signing service, and use a trusted timestamp service.
- Never commit a PFX file, private key, certificate password, signing token, or service credential to this repository.
- Sign the executable and first-party DLL before the ZIP and checksum are created, then run packaging with `-RequireSignature -ExpectedPublisher '<publisher>'` and independently validate the resulting package against the exact tagged source revision before upload.
- Treat a self-signed certificate as a development aid only; it is not automatically trusted on other users' computers.

The existing `package.ps1 -RequireSignature` option validates signatures; it does not create them. Adding signing requires a separately reviewed protected GitHub environment or signing service, least-privilege credentials, exact publisher enforcement, and a signed-package artifact step. Until that exists, the source release is publishable but the Windows ZIP is not a public release asset.

## Troubleshooting

- **The version-tag run fails before packaging:** make the tag text match the project's `<Version>` exactly. Publish a corrected version commit and tag instead of moving a release tag that users may already trust.
- **No Windows artifact appears:** this is intentional while the build is unsigned. The workflow compiles, tests, scans, packages twice, and compares hashes without uploading the candidate.
- **A scanner job cannot download its rules/database:** retry once in case the upstream registry had a transient outage. Do not bypass a repeatable scanner failure; inspect the job log and pinned scanner version. Local parity is available with `security-audit.ps1 -RequireExternalScanners` when both tools are installed.
- **Windows warns about a local candidate:** it is unsigned and must not be treated as the public binary release. Self-contained deployment removes the need for a separately installed .NET runtime; it does not establish publisher trust.
