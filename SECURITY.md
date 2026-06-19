# Security Policy

## Supported versions

This project is in active preview. Security fixes are applied to the latest
release on the `main` branch. Older preview builds are not maintained.

| Version | Supported |
|---------|-----------|
| Latest `main` / latest release | :white_check_mark: |
| Older preview builds | :x: |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub
issues.**

Instead, use GitHub's private vulnerability reporting:

1. Go to the **Security** tab of this repository.
2. Click **Report a vulnerability** and provide the details below.

Please include, where possible:

- A description of the vulnerability and its impact.
- Steps to reproduce or a proof of concept.
- The affected version, commit, or configuration.
- Any suggested remediation.

You can expect an initial acknowledgement of your report and, once triaged, an
indication of next steps and expected timeline. Please give maintainers a
reasonable opportunity to address the issue before any public disclosure.

## Operational security notes

This MCP server runs **elevated (administrator)** and executes commands inside
guest VMs via PowerShell Direct. When operating it:

- Treat any configured credentials (e.g. `HYPERV_MCP_VM_PASSWORD`) as secrets;
  supply them via environment variables, never commit them.
- If you enable the diagnostic **script-dump** mode, treat the dump directory
  as sensitive and prefer a path **outside any repository checkout**.
- Restrict who can reach the server's stdio transport — it grants full control
  over VM creation, execution, and file transfer on the host.
