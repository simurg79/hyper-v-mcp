# Hyper-V MCP Server

A **Model Context Protocol (MCP)** server that provides AI agents with tools to create, manage, and execute commands on Hyper-V virtual machines. Designed for AI-driven end-to-end testing workflows where a clean Windows VM is needed to validate application deployment and configuration restoration.

## Purpose

This MCP server enables AI agents (Roo, Claude Desktop, GitHub Copilot, Cursor, etc.) to:

- **Create** Hyper-V VMs from base VHDX images using differencing disks
- **Bootstrap** VMs from bare metal to remote-shell-ready state automatically
- **Execute** commands and multi-line scripts inside guest VMs via PowerShell Direct
- **Transfer** files between host and guest in both directions
- **Snapshot** VM state via checkpoints for fast reset between test scenarios
- **Destroy** VMs and clean up all resources

## Architecture

```
┌──────────────┐     stdio      ┌──────────────────────┐    VMBus     ┌─────────────┐
│   AI Agent   │◄──────────────►│  Hyper-V MCP Server  │◄────────────►│  Guest VM   │
│ (Roo/Claude) │   JSON-RPC     │  (.NET 8, console)   │  PS Direct  │ (Windows 11)│
└──────────────┘                └──────────────────────┘              └─────────────┘
```

- **Transport**: stdio (standard MCP transport)
- **Execution channel**: PowerShell Direct over VMBus (primary), WinRM over HTTPS (fallback)
- **Target framework**: `net8.0-windows`

## MCP Tools

| Tool | Description | MVP Slice |
|------|-------------|-----------|
| `vm_echo` | Health check — verify server is working | 1 |
| `vm_create` | Create VM from diff VHDX + bootstrap to ready | 1 |
| `vm_run_command` | Execute single command on guest | 1 |
| `vm_copy_file` | Copy file/directory from host to guest | 1 |
| `vm_destroy` | Stop + remove VM + delete VHDX | 1 |
| `vm_list` | Query existing VMs by name pattern | 1 |
| `vm_status` | Non-blocking VM status query | 2 |
| `vm_get_file` | Retrieve file from guest to host | 2 |
| `vm_wait_ready` | Block until VM reaches target readiness state | 2 |
| `vm_run_script` | Execute multi-line script on guest | 2 |
| `vm_start` | Start a stopped VM | 2 |
| `vm_stop` | Stop a VM (graceful or force) | 2 |
| `vm_diag` | Diagnostic tool — reports execution context, privileges, and environment | 2 |
| `vm_pause` | Pause a running VM | 2 |
| `vm_resume` | Resume a paused VM | 2 |
| `vm_configure` | Modify VM settings: CPU, memory, network | 2 |
| `vm_restart` | Restart a VM | 2 |
| `vm_list_images` | List available base VHDX images | 2 |
| `vm_checkpoint` | Create, restore, or list checkpoints | 3 |
| `vm_cleanup_orphans` | Find and destroy orphaned VMs | 3 |
| `vm_os_install` | Install OS from ISO image — fully automated, single call | 3 |
| `vm_create_base_image` | Generalize an installed VM into a reusable base VHDX (sysprep + checkpoint-merge + copy) | 3 |

> **Note on `vm_create` performance and timeout.** `vm_create` verifies the base VHDX with SHA-256 before and after the differencing clone (≈ 2 s/GB per pass on a cold page cache). A persisted `<base>.vhdx.sha256` sidecar collapses the pre-hash to a stat-tuple match on subsequent runs. The default server-side request envelope is **120 s**; override via `HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS` (range 60–600). Pass `verifyBaseImageHash: false` per call to skip the hash check entirely (see the tool description for the trade-off). The full env-var reference was relocated to the operator-local roo-vault at `myplans/operational/environment-variables.md` (not tracked in this repo).

## Prerequisites

| Requirement | Detail |
|-------------|--------|
| Host OS | Windows 10/11 Pro, Enterprise, or Server with Hyper-V role enabled |
| Host PowerShell | PowerShell 7+ (`pwsh.exe`) preferred; automatically falls back to Windows PowerShell 5.1 (`powershell.exe`) if `pwsh.exe` is unavailable/not on `PATH` or if the `pwsh` Hyper-V probe/cmdlets fail (see Known Issues) |
| .NET SDK | .NET 8.0+ |
| Privilege | MCP server process must run elevated (admin) |
| Base VHDX | Pre-prepared Windows 11 image |

## MCP Configuration

```json
{
  "mcpServers": {
    "hyper-v": {
      "command": "dotnet",
      "args": ["run", "--project", "src/HyperV.Mcp.Server"],
      "env": {
        "HYPERV_MCP_BASE_VHDX": "C:\\HyperV\\Images\\windows-11-clean.vhdx",
        "HYPERV_MCP_DEFAULT_SWITCH": "Default Switch",
        "HYPERV_MCP_VM_USERNAME": "HyperVMCP",
        "HYPERV_MCP_VM_PASSWORD": "<initial-password>"
      }
    }
  }
}
```

## Project Structure

```
src/
├── HyperV.Mcp.Server/
│   ├── HyperV.Mcp.Server.csproj
│   ├── Program.cs
│   ├── Configuration/
│   │   ├── HostProfile.cs
│   │   ├── JsonOptions.cs
│   │   └── ServerOptions.cs
│   ├── Infrastructure/
│   │   ├── IPowerShellExecutor.cs / PowerShellExecutor.cs
│   │   ├── IHyperVManager.cs       / HyperVManager.cs
│   │   ├── ICommandExecutor.cs     / CommandExecutor.cs       ← inlines PS Direct script composition (Phase 1)
│   │   ├── IFileTransferService.cs / FileTransferService.cs   ← inlines PS Direct script composition (Phase 1)
│   │   ├── ICheckpointManager.cs   / CheckpointManager.cs
│   │   ├── ISessionStore.cs        / SessionStore.cs
│   │   ├── IConcurrencyGate.cs     / ConcurrencyGate.cs
│   │   ├── IHostResolver.cs        / HostResolver.cs
│   │   ├── IErrorMapper.cs         / ErrorMapper.cs
│   │   ├── IToolDispatcher.cs      / ToolDispatcher.cs
│   │   ├── CredentialResolver.cs
│   │   └── InputValidation.cs
│   ├── Models/
│   │   ├── ToolCatalog.cs
│   │   ├── McpToolResponse.cs
│   │   ├── ErrorCodes.cs
│   │   └── (CommandResult, FileTransferResult, CheckpointResult, OsInstallResult, ImageInfo, VmInfo)
│   └── Tools/
│       └── VmTools.cs              ← single consolidated `[McpServerToolType]` with all 22 implemented tool wrappers
│
tests/
├── HyperV.Mcp.Server.Tests/
│   ├── Integration/
│   ├── McpInterface/
│   ├── Operational/
│   ├── Remoting/
│   └── Runtime/
```

## Installing Windows from ISO (`vm_os_install`)

The `vm_os_install` tool creates a new VM and installs Windows 11 from an ISO image in a single call. It handles all orchestration automatically: VM creation, hardware configuration (Gen 2, TPM 2.0, Secure Boot, UEFI), disk partitioning via DISM, unattended answer file generation, installation monitoring, post-install bootstrap, and cleanup.

### Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | — | VM name (must be unique on host) |
| `isoPath` | string | Yes | — | Path to the Windows 11 ISO file on the host |
| `adminPassword` | string | Yes | — | Administrator password for the install |
| `hostId` | string | No | `null` (local) | Target Hyper-V host |
| `cpuCount` | int | No | `4` | Virtual processor count (min 2) |
| `memoryMB` | int | No | `8192` | Startup memory in MB (min 4096) |
| `diskSizeGB` | int | No | `127` | VHDX size in GB (min 64) |
| `switchName` | string | No | *(auto-resolved)* | Virtual switch name |
| `locale` | string | No | `en-US` | Installation locale |
| `windowsEdition` | string | No | `Windows 11 Pro` | Edition name matching the ISO's `install.wim` |
| `productKey` | string | No | `null` | Windows product key; uses a GVLK if omitted |
| `timeoutMinutes` | int | No | `60` | Maximum wait time for the entire operation |

### Example

```
Tool: vm_os_install
Arguments:
  name: "win11-test"
  isoPath: "C:\\ISOs\\Win11_24H2.iso"
  adminPassword: "P@ssw0rd!"
```

This creates a 4-vCPU, 8 GB RAM, 127 GB disk Windows 11 Pro VM, installs the OS unattended, bootstraps PowerShell Direct access, and returns the VM ready for `vm_run_command`.

### Important Notes

- **Execution time**: ~8 minutes on typical hardware. This is a long-running operation.
- **MCP client timeout**: Many MCP clients (including Roo Code) have a default tool call timeout of 60 seconds, which is far shorter than the ~8 minute installation. The tool will complete successfully on the server side regardless of client timeout. Configure your MCP client's timeout to ≥600 seconds if you want to receive the completion response. The same client-side cap applies to any tool whose server envelope exceeds 60 s — see the **Note on `vm_create` performance and timeout** above for `vm_create`'s specific knob.
- **Product keys**: When no `productKey` is provided, the server uses a well-known Generic Volume License Key (GVLK) for the selected edition. These are Microsoft-published KMS client setup keys that allow installation without activation.
- **Test VMs**: VMs created for live integration testing (e.g., `win11-mcp-test`) should be kept running. The single source of truth for lab credentials, env-var contract, VMs, and storage layout was relocated to the operator-local roo-vault at `myplans/operational/lab-environment.md` (not tracked in this repo). The test-suite README at [`tests/HyperV.Mcp.Server.Tests/README.md`](tests/HyperV.Mcp.Server.Tests/README.md) carries the same breadcrumb.

## Diagnostics

For diagnosing PowerShell-related issues (PS Direct / WinRM script generation, credential handling, autoload failures), the server supports an opt-in **script-dump** mode that writes the exact `.ps1` handed to `pwsh` (with credentials masked) to a directory of your choosing and preserves the `%TEMP%` original for manual rerun.

Enable on Windows (PowerShell):

```powershell
$env:HYPERV_MCP_DUMP_PS_SCRIPTS = "C:\hvmcp-debug"
```

**Activation rules (operator-facing summary):**

- The value is **trimmed** of surrounding whitespace before evaluation.
- **Disabled** (feature off, identical to `main`) when the trimmed value is empty, or one of `0`, `false`, `no`, `off` (case-insensitive). Any other non-empty value is treated as a directory path.
- **Absolute paths recommended.** Relative paths are resolved against the MCP server's current working directory at the moment of the call. UNC paths (`\\server\share\…`) are supported; the operator owns share reachability and credentials. Junctions and symlinks are followed normally.
- **Read per call**, not cached at startup — toggling the env var takes effect on the next tool call without restarting the server.
- **OS-install scripts (`vm_os_install`) are excluded from dumping in v1** because the v1 masker cannot redact their variable-backed credentials and unattended-XML password nodes. Setting the env var has no effect for that one code path.
- If the dump directory cannot be created, the server logs a Warning and behaves as if the feature were disabled for that call (the `%TEMP%` script is deleted as normal). If the directory exists but a write fails mid-run, the server logs a Warning and **preserves the `%TEMP%` script** so you can still rerun manually. Dump-side failures never affect the underlying tool call.

The full activation, masking, and security contract — including a recommended Windows ACL recipe for the dump directory — is maintained with the project's internal design notes. **Treat the dump directory as sensitive.** The `.gitignore` recommendation only applies if the dump directory is inside a repository checkout; for production diagnostic use, prefer a path **outside any repo** (e.g., `C:\hvmcp-debug` or `%TEMP%\hvmcp-debug`).

## Known Issues

| Issue | Detail | Workaround |
|-------|--------|------------|
| pwsh 7+ Hyper-V probe fails on Windows 11 26200+ | `Get-VM` throws "Value cannot be null" when spawned non-interactively in pwsh due to a WMI provider bug on recent Windows 11 Insider builds. The server detects this and falls back to `powershell.exe` 5.1 automatically. | No action needed — fallback is automatic. Tracked as #26. Will resolve when Microsoft fixes the WMI provider. |
| Smoke startup failure: `Get-VMHost` probe `CommandNotFoundException: 'Select-Object'` | The MCP server's startup Hyper-V probe runs `Get-VMHost \| Select-Object -ExpandProperty Name` inside the in-proc PowerShell 7 (Core) runspace. On a freshly-built server `bin/`, the post-build `StripBundledMicrosoftPowerShellModules` target in [`HyperV.Mcp.Server.csproj`](src/HyperV.Mcp.Server/HyperV.Mcp.Server.csproj) uses an over-broad `Microsoft.PowerShell.*` glob that removes `Microsoft.PowerShell.Utility` (which provides `Select-Object`) along with the intended `Microsoft.PowerShell.Security`. The Core runspace then cannot resolve `Select-Object` because the only remaining copy on disk is the `Desktop`-edition module under `C:\Windows\System32\WindowsPowerShell\v1.0\`, which Core correctly refuses to load. **This is not an OS-version issue** — it reproduces on any host where the server is launched against a freshly-built, stripped `bin/`. | **No end-user workaround.** The fix lives in the build target and is tracked in #66. (Developers can manually restore `Microsoft.PowerShell.Utility` to `bin\Debug\net8.0-windows\runtimes\win\lib\net8.0\Modules\` after each build, but this is fragile and not advisable.) |

> For operational failure modes (timeouts, partially-created resources, transient PowerShell faults), the troubleshooting guide was relocated to the operator-local roo-vault at `myplans/operational/troubleshooting.md` (not tracked in this repo). This table tracks build/OS-level known issues only.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) for the full license text.

Copyright 2026 simurg79
