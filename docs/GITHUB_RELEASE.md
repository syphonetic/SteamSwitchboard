# GitHub CI and signed release guide

SteamSwitchboard uses one `Verify` workflow for pull requests, pushes, and protected version tags. Ordinary runs have read-only repository access. A version tag can publish a Windows binary only after all source/dependency scanners and the reproducible Windows package gate pass.

The release path is deliberately split:

1. The approval-gated `sign-release` job rebuilds the unsigned ZIP twice and requires identical SHA-256 hashes.
2. It validates/extracts that exact candidate and records every payload path, length, and hash.
3. GitHub OIDC signs in to Azure without a client secret. Microsoft Artifact Signing receives only the absolute paths to `SteamSwitchboard.exe` and `SteamSwitchboard.dll`, then the job exports only the signed payload.
4. A fresh `validate-release` runner has no Azure environment/identity. It independently rebuilds the protected source tag twice, regenerates the trusted manifest from its own reproducible archive, treats the signed payload as untrusted, and permits only Authenticode metadata to differ in the two first-party files.
5. Finalization requires valid same-certificate Authenticode signatures, the configured publisher, code-signing EKU, trusted RFC 3161 timestamps, and unchanged executable code/resources; GitHub then signs build-provenance attestations.
6. A separate `publish-release` job has GitHub release-write permission but no Azure identity. It verifies the handoff checksum and exact workflow/source provenance before creating the GitHub Release.

Every referenced GitHub/Azure action is pinned to an immutable commit. No PFX, certificate private key, Azure client secret, or long-lived GitHub token belongs in this repository.

## One-time Microsoft Artifact Signing setup

Microsoft Public Trust identity validation is an external prerequisite. Microsoft currently limits organization and individual eligibility by region, and public validation can take 1–20 business days or longer. Check the current [Artifact Signing quickstart and eligibility](https://learn.microsoft.com/azure/artifact-signing/quickstart) before paying for or creating resources.

1. In Azure, select the intended subscription and register `Microsoft.CodeSigning`.

   ```powershell
   az login
   az account set --subscription '<subscription-id>'
   az provider register --namespace Microsoft.CodeSigning
   az extension add --name artifact-signing
   ```

2. Create an Artifact Signing account in a supported region. Basic is sufficient unless the project's signing volume requires another tier.

   ```powershell
   az group create --name '<resource-group>' --location '<azure-region>'
   az artifact-signing create `
     --name '<globally-unique-account>' `
     --location '<azure-region>' `
     --resource-group '<resource-group>' `
     --sku Basic
   ```

3. In the Azure portal, assign your human account the required identity-verifier access, create a **Public** identity validation, and complete every email/document/identity step. Identity validation cannot be completed through the CLI. Wait until its state is **Completed**.

4. Create a **PublicTrust** certificate profile from the completed identity. Record its account name, profile name, regional endpoint, and exact certificate common name shown by the subject preview.

   ```powershell
   az artifact-signing certificate-profile create `
     --resource-group '<resource-group>' `
     --account-name '<artifact-signing-account>' `
     --name '<certificate-profile>' `
     --profile-type PublicTrust `
     --identity-validation-id '<completed-identity-validation-id>'
   ```

   Do not use `PrivateTrust` for a public download: other users' Windows installations will not trust it by default.

5. Create one dedicated Microsoft Entra application/service principal for this workflow. It receives no password or certificate credential.

   ```powershell
   $ApplicationId = az ad app create `
     --display-name 'SteamSwitchboard GitHub Artifact Signing' `
     --query appId `
     --output tsv
   $ServicePrincipalObjectId = az ad sp create `
     --id $ApplicationId `
     --query id `
     --output tsv
   $TenantId = az account show --query tenantId --output tsv
   $SubscriptionId = az account show --query id --output tsv
   ```

6. Federate that application to exactly the GitHub `release` environment. The subject is case-sensitive and must remain exactly as shown.

   ```powershell
   $FederatedCredential = @{
     name = 'SteamSwitchboard-GitHub-release'
     issuer = 'https://token.actions.githubusercontent.com'
     subject = 'repo:syphonetic/SteamSwitchboard:environment:release'
     description = 'Secretless signing from the protected SteamSwitchboard release environment'
     audiences = @('api://AzureADTokenExchange')
   } | ConvertTo-Json -Compress

   az ad app federated-credential create `
     --id $ApplicationId `
     --parameters $FederatedCredential
   ```

7. Give only that service principal the `Artifact Signing Certificate Profile Signer` role, scoped to one profile—not the subscription, resource group, or whole signing account.

   ```powershell
   $ProfileScope = "/subscriptions/$SubscriptionId/resourceGroups/<resource-group>/providers/Microsoft.CodeSigning/codeSigningAccounts/<artifact-signing-account>/certificateProfiles/<certificate-profile>"
   az role assignment create `
     --assignee-object-id $ServicePrincipalObjectId `
     --assignee-principal-type ServicePrincipal `
     --role 'Artifact Signing Certificate Profile Signer' `
     --scope $ProfileScope
   ```

Microsoft documents this least-privilege scope in its [Artifact Signing RBAC guide](https://learn.microsoft.com/azure/artifact-signing/tutorial-assign-roles).

## One-time GitHub protection setup

1. In **Settings → Environments**, create an environment named exactly `release`.
2. Add at least one required reviewer. Keep “prevent self-review” off if this is currently a one-maintainer repository; turn it on after a second trusted maintainer is available.
3. Limit deployment branches/tags to selected tags matching `v*`.
4. Create an active tag ruleset matching `refs/tags/v*` that blocks tag deletion and non-fast-forward updates. The workflow checks `github.ref_protected` and refuses an unprotected tag.
5. Protect `main` and require both `Source and dependency security gate` and `Windows Release gate` before merging. Do not permit pull requests to bypass those checks.
6. In **Settings → General → Releases**, enable **release immutability**. GitHub then locks a published release's tag and assets and generates a release attestation. This applies only to releases published after the setting is enabled.

Store these six non-secret Azure identifiers as **environment variables** on `release` (not repository-wide values):

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | Dedicated Entra application's client ID |
| `AZURE_TENANT_ID` | Azure directory/tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription containing Artifact Signing |
| `ARTIFACT_SIGNING_ENDPOINT` | Exact regional HTTPS endpoint, such as `https://eus.codesigning.azure.net` |
| `ARTIFACT_SIGNING_ACCOUNT` | Artifact Signing account name |
| `ARTIFACT_SIGNING_PROFILE` | PublicTrust certificate profile name |

Store `ARTIFACT_SIGNING_PUBLISHER` as a **repository variable** containing the exact certificate common name shown in the subject preview. The clean validation job intentionally does not reference the Azure-enabled `release` environment, so its OIDC token cannot match the signing federation; the non-secret publisher name is the only shared repository-level value.

With GitHub CLI authenticated to `syphonetic`, the variables can be added without exposing any secret:

```powershell
gh variable set AZURE_CLIENT_ID --env release --body $ApplicationId
gh variable set AZURE_TENANT_ID --env release --body $TenantId
gh variable set AZURE_SUBSCRIPTION_ID --env release --body $SubscriptionId
gh variable set ARTIFACT_SIGNING_ENDPOINT --env release --body 'https://<region-code>.codesigning.azure.net'
gh variable set ARTIFACT_SIGNING_ACCOUNT --env release --body '<artifact-signing-account>'
gh variable set ARTIFACT_SIGNING_PROFILE --env release --body '<certificate-profile>'
gh variable set ARTIFACT_SIGNING_PUBLISHER --body '<exact-certificate-common-name>'
```

Never add `AZURE_CLIENT_SECRET`; the workflow is intentionally designed to reject the need for one.

## Pull-request and release flow

For ordinary changes:

```powershell
git switch -c my-change
git add <reviewed-paths>
git commit -m 'Describe the change'
git push -u origin my-change
```

Open a pull request, review every changed path, and wait for both required gates. Merge only after they pass.

`v1.0.0` is already published as the immutable source-only initial tag. Do not move, delete, or recreate it. Version `1.0.1` is the first signed-binary candidate. After this release-pipeline change is merged and the Azure/GitHub setup above is complete:

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

Open **Actions → Verify**, select the tag run, and approve the `release` environment after confirming the commit/tag/version. The workflow then publishes the GitHub Release automatically. A failed run never uploads an unsigned binary as a release asset. Fix the cause and rerun the same workflow; never move a public version tag to different source.

## Independent release verification

Download the two assets from the Release page into an empty directory, then verify all three trust signals:

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
Get-AuthenticodeSignature ".\SteamSwitchboard-$Version-win-x64\SteamSwitchboard.exe" |
  Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
Get-AuthenticodeSignature ".\SteamSwitchboard-$Version-win-x64\SteamSwitchboard.dll" |
  Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

Both statuses must be `Valid`, both files must show the expected publisher, and both must have timestamp certificates. Windows' Properties dialog should show the same Digital Signatures identity.

## Troubleshooting

- **Release job says the tag is unprotected:** activate the `v*` tag ruleset; do not remove the check from the workflow.
- **Azure login has no matching federated identity:** verify the exact subject `repo:syphonetic/SteamSwitchboard:environment:release`, issuer, audience, client ID, and tenant ID.
- **Artifact Signing returns authorization denied:** assign the signer role to the service principal object ID at the exact certificate-profile scope and allow Azure RBAC propagation time.
- **Publisher mismatch:** use the certificate profile's exact common name, including punctuation and case, for `ARTIFACT_SIGNING_PUBLISHER`.
- **Signature has no timestamp:** do not publish. The workflow requires Microsoft's RFC 3161 timestamp and fails closed.
- **A release already exists for the tag:** inspect it rather than using overwrite/clobber. Publish a new version for changed bytes.
- **No release appears after normal branch CI:** intentional. Only an annotated, protected `vMAJOR.MINOR.PATCH` tag can enter the signing environment.
