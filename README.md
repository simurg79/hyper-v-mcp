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
| `vm_find_by_name` | Resolve VM IDs and states by exact name, including untagged VMs | 2 |
| `vm_status` | Non-blocking VM status query | 2 |
| `vm_get_file` | Retrieve file from guest to host | 2 |
| `vm_wait_ready` | Confirm fresh authenticated guest access for the resolved identity | 2 |
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

> **Note on `vm_create` performance and timeout.** `vm_create` verifies the base VHDX with SHA-256 before and after the differencing clone (≈ 2 s/GB per pass on a cold page cache). A persisted `<base>.vhdx.sha256` sidecar collapses the pre-hash to a stat-tuple match on subsequent runs. The default server-side request envelope is **120 s**; override via `HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS` (range 60–600). Pass `verifyBaseImageHash: false` per call to skip the hash check entirely (see the tool description for the trade-off).

### Guest-login readiness: `vm_wait_ready`

`vm_wait_ready` observes a supported Windows or Linux guest without changing VM configuration,
power state, accounts, or guest files. Success means **this call freshly authenticated the resolved
identity and positively completed a read-only guest operation**. Running state and a healthy
heartbeat are only prefilters, never proof of guest-login readiness. Existing host selection and
unsupported or undetermined guest-routing refusals remain authoritative; no transport is guessed.
Use `vm_status` when only power state is needed.

Inputs: required `vmId`, optional `hostId`, `username`, `password`, and `timeoutSeconds`.
Each supplied credential field takes precedence over its corresponding default,
`HYPERV_MCP_VM_USERNAME` or `HYPERV_MCP_VM_PASSWORD`. Without a usable pair the result is
`MISSING_CREDENTIALS`, not heartbeat-only success. Use the identity intended for subsequent guest work.
Credentials are not returned or durably stored by the readiness wait.

`timeoutSeconds` defaults to **300**, must be positive, and accepts **1** without raising it to a
larger minimum. Zero and negative values return `INVALID_PARAMETER`.
The single elapsed wait budget starts before concurrency waits and is checked before every new
functional step or retry. **It is not a hard whole-call deadline.** Host preparation, waiting for
access, an already-started operation, and required cleanup can delay the response beyond the budget.
Expiry does not interrupt an in-flight step; no next step starts after expiry. A final authenticated
confirmation begun with time remaining may succeed at or after expiry. Cleanup runs on every exit,
and its duration alone does not invalidate confirmation. Explicit caller cancellation still prevents
success, including when confirmation succeeds before cleanup finishes. Independent access limits and
concurrency refusals remain in force. Requested and effective budgets are reported in uncertain failures.

Success retains the existing VM-information result shape. It is **point-in-time evidence only**:
it does not reserve or return a session, guarantee the next command, another account's access,
desktop login, application or installation completion, network availability, or arbitrary privileged
commands. Handle each subsequent guest operation's own result. Ordinary command and transfer
credential-rejection and retry behavior is unchanged.

Failures retain the existing failure envelope without a successful readiness payload:

- **`READINESS_NOT_REACHED` — budget exhausted:** guest-login readiness could not be determined.
  Inspect the VM and last safe observation; retry if further boot progress may help.
- **Credential rejection observed:** the same uncertain code is returned if no later attempt confirms
  readiness. The message identifies the attempted username. **Verify credentials for the image, or
  wait and retry after a recent start**; an ambiguous rejection does not prove which cause applies.
- **`READINESS_NOT_REACHED` — observation failed:** guest-login readiness could not be reliably checked.
  The message distinguishes this from budget exhaustion, supplies a safe cause or states it is unknown,
  and identifies the VM. Correct an identified access problem before retrying.
- Missing VM, invalid input, unsupported host or guest, undetermined routing, and concurrency refusals
  retain their specific failure meanings. Caller cancellation retains the existing cancellation outcome.

None of the uncertain outcomes proves the guest will never become ready. Do not begin dependent work
on an uncertain result. Even valid credentials may require materially longer than the former
heartbeat-only wait and may consume the whole budget; no fixed boot duration or minimum delay is implied.

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

## Process Lifecycle and Exit Codes

The server is bound to the lifetime of its stdio peer: when the MCP client disconnects (stdin EOF) or the transport faults, the server stops the host and waits up to **5 seconds** for the host and its DI container to finish disposing. A surviving process would pin the PowerShell runspace and session state, so the next-spawned server cannot reach the rotated stdio pipes and every call fails with "Not connected" until the stale process is killed.

| Exit code | Meaning |
|-----------|---------|
| `0` | Shutdown and disposal completed within the grace period. |
| `3` | Disposal hung past the grace period, or shutdown failed — the process was force-exited. Supervisors and CI wrappers must treat `3` as an unclean stop, not a crash of the tool call that preceded it. |

Once the peer disconnects, the grace period is armed regardless of what else has already requested a stop; if a shutdown is already in progress by another route (e.g. Ctrl+C), that route keeps ownership of the stop reason but a hang still force-exits with `3`. The grace period is a fixed build-time constant — there is no environment variable or CLI flag to tune it.

The server does not require the launcher to pass `COMPUTERNAME`, `windir`, or `SystemRoot`: any that are missing are recovered from the operating system at startup. Startup is bounded at **120 seconds** from process launch; if it does not complete, a report naming the unfinished stage is written to stderr and the same detail is available in the `phase2Host` block of `vm_diag`. A timeout report does not mean the unfinished work was cancelled — recovery is a server restart.

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

## Installing an OS from ISO (`vm_os_install`)

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
- **Test VMs**: Configure lab VM identities, credentials, and storage privately. See the [test-suite README](tests/HyperV.Mcp.Server.Tests/README.md) for testing guidance.

### Installing Ubuntu Server 24.04

`vm_os_install` also installs Ubuntu Server 24.04 from a `casper`-based live-server ISO. Pass the initial login user via `guestUsername` (default `ubuntu`) and its password via `adminPassword`; the server creates a Generation 2 / Secure-Boot-off VM and drives cloud-init autoinstall. `windowsEdition` and `productKey` are ignored for this target.

**The ISO must be autoinstall-prepared.** A stock `ubuntu-24.04-live-server-amd64.iso` carries no `autoinstall` kernel boot token, so Ubuntu's subiquity installer stops at a "Confirmation is required to continue" prompt that nothing can answer. Prepare the media once, offline, with [`scripts/prepare-ubuntu-autoinstall-iso.py`](scripts/prepare-ubuntu-autoinstall-iso.py) and pass its output as `isoPath`:

```bash
python scripts/prepare-ubuntu-autoinstall-iso.py \
    --source C:\ISOs\ubuntu-24.04-live-server-amd64.iso \
    --output C:\ISOs\ubuntu-24.04-autoinstall-amd64.iso
```

The script needs `xorriso` (via WSL on Windows); it never modifies the source ISO. See its module docstring for the full contract.

#### `LINUX_PRECONDITION_UNMET` — media is not autoinstall-capable

The server inspects Ubuntu media **before** creating the VM, attaching media, or starting any wait. If the `autoinstall` boot token is absent — or its presence cannot be confirmed — the call fails within seconds with `LINUX_PRECONDITION_UNMET`, naming the supplied ISO path and pointing at the preparation script. No VM is created. This refusal is deliberately fail-closed and **is not bypassable with `skipPreflight: true`**. The fix is always the same: run the preparation script and retry with the prepared ISO.

#### `LINUX_INSTALL_TIMEOUT` — triage context

If an install still exceeds `timeoutMinutes`, the error message and its structured `details` carry two extra facts for triage without host access:

- the ISO path actually attached to the VM's primary DVD drive, read from the VM (never echoed back from the request), and
- the guest completion-channel state, as one of *available but no signal*, *unavailable*, or *undetermined*.

Either item is reported as undetermined rather than guessed when it cannot be read. Both survive credential sanitization.

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

**Treat the dump directory as sensitive and restrict its ACL to authorized operators.** The `.gitignore` recommendation only applies if the dump directory is inside a repository checkout; for production diagnostic use, prefer a path **outside any repo** (e.g., `C:\hvmcp-debug` or `%TEMP%\hvmcp-debug`).

## Known Issues

| Issue | Detail | Workaround |
|-------|--------|------------|
| pwsh 7+ Hyper-V probe fails on Windows 11 26200+ | `Get-VM` throws "Value cannot be null" when spawned non-interactively in pwsh due to a WMI provider bug on recent Windows 11 Insider builds. The server detects this and falls back to `powershell.exe` 5.1 automatically. | No action needed — fallback is automatic. Will resolve when Microsoft fixes the WMI provider. |

### Smoke startup failure: `Get-VMHost` probe `CommandNotFoundException: 'Select-Object'`

The MCP server's startup Hyper-V probe runs `Get-VMHost | Select-Object -ExpandProperty Name` inside the in-proc PowerShell 7 (Core) runspace.
On a freshly-built server `bin/`, the post-build `StripBundledMicrosoftPowerShellModules` target in [`HyperV.Mcp.Server.csproj`](src/HyperV.Mcp.Server/HyperV.Mcp.Server.csproj) uses an over-broad `Microsoft.PowerShell.*` glob that removes `Microsoft.PowerShell.Utility` (which provides `Select-Object`) along with the intended `Microsoft.PowerShell.Security`.
The Core runspace then cannot resolve `Select-Object` because the only remaining copy on disk is the `Desktop`-edition module under `C:\Windows\System32\WindowsPowerShell\v1.0\`, which Core correctly refuses to load.
**This is not an OS-version issue** — it reproduces on any host where the server is launched against a freshly-built, stripped `bin/`.

**No end-user workaround.** The fix lives in the build target. (Developers can manually restore `Microsoft.PowerShell.Utility` to `bin\Debug\net8.0-windows\runtimes\win\lib\net8.0\Modules\` after each build, but this is fragile and not advisable.)

## Documentation

- [Contributing and public release checks](CONTRIBUTING.md)
- [Test-suite guidance](tests/HyperV.Mcp.Server.Tests/README.md)
- [Security reporting](SECURITY.md)

## License

Private — All rights reserved.
