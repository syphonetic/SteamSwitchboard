# Contributing to SteamSwitchboard

Thanks for helping improve SteamSwitchboard. Keep changes focused, privacy-preserving, and inside the documented Steam/WebView2 trust boundary.

## Before opening a pull request

1. Use Windows and the .NET 9.0.317 SDK.
2. Read `SECURITY.md`, `docs/ARCHITECTURE.md`, and `docs/PRIVACY.md` before changing authentication, browser, filesystem, launch, or packaging behavior.
3. Add regression tests for behavior changes and security fixes.
4. Run:

   ```powershell
   ./scripts/verify.ps1
   ./scripts/security-audit.ps1 -RequireExternalScanners
   ```

5. Do not include credentials, cookies, QR codes, personal Steam data, `BrowserData`, local logs, generated release ZIPs, or build output.

## Pull-request expectations

- Explain the user-facing outcome and any security or privacy impact.
- Keep Steam files and registry state read-only.
- Do not add password/cookie capture, authentication export/import, Steam injection, anti-cheat or licensing bypasses, or silent external navigation.
- Preserve fail-closed launch verification and durable profile cleanup.
- Keep the normal Release build warning-free.

Report suspected vulnerabilities through the private process described in `SECURITY.md`, not a public issue.
