using System.Security.Principal;
using System.Text.Json;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Dispatches MCP tool calls to the appropriate handler.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D1: Attribute-based tool registration.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
///
/// Design decisions:
/// - All 19 catalog tools are pre-registered at construction time.
///   This ensures GetRegisteredTools() and IsRegistered() reflect the full catalog
///   immediately, matching /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
/// - P0 tools (vm_echo, vm_create, vm_start, vm_stop, vm_destroy, vm_list,
///   vm_status, vm_run_command, vm_copy_file) have real implementations that
///   delegate to infrastructure services via DI.
///   See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
/// - P1 tools (vm_list_images, vm_run_script, vm_get_file, vm_restart,
///   vm_wait_ready, vm_checkpoint, vm_cleanup_orphans, vm_os_install) have
///   real handler implementations.
/// - P2 tools have stub handlers that throw NotImplementedException,
///   to be replaced as each tool is implemented in later subtasks.
/// - Unknown tool dispatch returns a TOOL_NOT_FOUND error response (MCP-D6),
///   never throwing an exception to the caller.
/// - CancellationToken is checked before handler invocation for fast-fail on
///   already-cancelled requests. See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
/// - Infrastructure services are injected via constructor DI rather than being
///   created internally, enabling testability and proper lifecycle management.
///   See /myplans/execution-plan.md — Stage 1.5: Refactor ToolDispatcher for DI.
///
/// Lock ordering (Issue 3 fix):
/// Per /myplans/operational/concurrency/concurrency-design.md — CC-D1 through CC-D7:
/// 1. Global slot (outermost) — always acquired first
/// 2. Per-host lock — for lifecycle operations that affect host-level resources
/// 3. Per-VM lock (innermost) — for operations that affect a specific VM
///
/// Lifecycle operations (create, start, stop, destroy, checkpoint): global + host + VM
/// Read-only operations (list, status): global only
/// Execution operations (run_command, copy_file): global + VM
/// Readiness polling (wait_ready): global + VM
/// </summary>
public class ToolDispatcher : IToolDispatcher
{
    private const int VmDiagPerTestTimeoutSeconds = 10;
    private const int VmDiagOutputPreviewLength = 500;
    private static readonly HashSet<string> AllowedCheckpointActions = new() { "create", "restore", "list", "delete" };

    // VC-D12 (Issue #170): default request timeout for `vm_create` — 120s,
    // env-overridable via `HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS` in the
    // inclusive range 60..600. Covers a ~30 GB base on a cold OS page cache
    // (~67 s post-hash + ~7 s PowerShell + ~46 s headroom). The PowerShell-
    // internal 600 s budget is unchanged; only the transport-level CTS is
    // widened to give synchronous SHA verification room to fit.
    internal const int VmCreateTimeoutSecondsDefault = 120;
    internal const int VmCreateTimeoutSecondsMin = 60;
    internal const int VmCreateTimeoutSecondsMax = 600;
    internal const string VmCreateTimeoutEnvVar = "HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS";

    private readonly Dictionary<string, Func<Dictionary<string, object?>, CancellationToken, Task<McpToolResponse>>> _handlers = new();
    private readonly IErrorMapper _errorMapper;
    private readonly IHyperVManager _hyperVManager;
    private readonly ICommandExecutor _commandExecutor;
    private readonly IFileTransferService _fileTransferService;
    private readonly ICheckpointManager _checkpointManager;
    private readonly IHostResolver _hostResolver;
    private readonly IConcurrencyGate _concurrencyGate;
    private readonly IPowerShellExecutor _psExecutor;
    private readonly IPowerShellDirectChannel _channel;
    private readonly IPowerShellHost? _psHost;
    private readonly IBaseImageHashCache? _baseImageHashCache; // Issue #169 / VC-D7
    private readonly ServerOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;

    /// <summary>
    /// Creates a new ToolDispatcher with all infrastructure services injected via DI.
    /// See /myplans/execution-plan.md — Stage 1.5: P0 Tool Handlers.
    /// Issue #52, ST-6: <see cref="IPowerShellDirectChannel"/> is injected so that
    /// <see cref="HandleDestroyAsync"/> can evict any persistent PSSession BEFORE
    /// the VM is destroyed (SM-D7). The dispatcher does NOT take a direct dependency
    /// on <c>ISessionStore</c> — the channel is the single facade.
    /// </summary>
    public ToolDispatcher(
        IHyperVManager hyperVManager,
        ICommandExecutor commandExecutor,
        IFileTransferService fileTransferService,
        ICheckpointManager checkpointManager,
        IHostResolver hostResolver,
        IErrorMapper errorMapper,
        IConcurrencyGate concurrencyGate,
        IPowerShellExecutor psExecutor,
        IPowerShellDirectChannel channel,
        ServerOptions options,
        IPowerShellHost? psHost = null,
        ILogger<ToolDispatcher>? logger = null,
        IBaseImageHashCache? baseImageHashCache = null)
    {
        _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
        _fileTransferService = fileTransferService ?? throw new ArgumentNullException(nameof(fileTransferService));
        _checkpointManager = checkpointManager ?? throw new ArgumentNullException(nameof(checkpointManager));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        // Issue #52 Phase 2 Gate 3 RC-1: optional IPowerShellHost dependency. When supplied
        // (production DI wires it; many unit-test fixtures still pass null), the VM-state
        // pre-flight in EnsureVmRunningAsync routes through the in-process PowerShell host
        // instead of HyperVManager.GetVmStatusAsync — which would otherwise drag the legacy
        // out-of-process PowerShellExecutor (writing hvmcp-*.ps1 temp scripts) onto every
        // guest tool call, violating the PSD-D6 single-facade rule.
        _psHost = psHost;
        // Issue #169 / VC-D7: optional reference to the base-image hash cache so
        // vm_diag can surface its warm-up status without breaking existing test
        // fixtures that construct ToolDispatcher positionally.
        _baseImageHashCache = baseImageHashCache;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // DIAG-D7 (#65): canonical structured-logging seam. ILogger<ToolDispatcher> is
        // resolved from DI in production (Generic Host's default AddLogging registers
        // the open-generic ILogger<T>). Fixtures that pass null get a NullLogger so
        // the pre-existing test surface keeps working without a forced re-wire.
        _logger = logger ?? NullLogger<ToolDispatcher>.Instance;
        RegisterAllCatalogTools();
    }

    /// <inheritdoc />
    public async Task<string> DispatchAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        // Fast-fail on already-cancelled tokens.
        // See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
        ct.ThrowIfCancellationRequested();

        if (!_handlers.TryGetValue(toolName, out var handler))
        {
            // Unknown tools produce a structured error response, not an exception (MCP-D6).
            var errorResponse = McpToolResponse.Fail(
                $"Tool '{toolName}' is not registered",
                RuntimeErrorCodes.ToolNotFound);
            return JsonSerializer.Serialize(errorResponse);
        }

        try
        {
            // Check cancellation again before invoking the handler.
            ct.ThrowIfCancellationRequested();

            var response = await handler(arguments, ct);
            return JsonSerializer.Serialize(response);
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation as-is per design contract.
            throw;
        }
        catch (Exception ex)
        {
            // MCP-D6: Exceptions caught and wrapped — never propagated as MCP protocol errors.
            var errorResponse = _errorMapper.MapException(ex);
            return JsonSerializer.Serialize(errorResponse);
        }
    }

    /// <summary>
    /// Check if a tool is registered in the dispatcher. Test-only seam exposed
    /// to the friend assembly (<c>HyperV.Mcp.Server.Tests</c>) via
    /// <c>InternalsVisibleTo</c>; not part of the public <see cref="IToolDispatcher"/> surface.
    /// </summary>
    internal bool IsRegistered(string toolName)
    {
        return _handlers.ContainsKey(toolName);
    }

    /// <summary>
    /// Get the list of all registered tool names. Test-only seam exposed
    /// to the friend assembly (<c>HyperV.Mcp.Server.Tests</c>) via
    /// <c>InternalsVisibleTo</c>; not part of the public <see cref="IToolDispatcher"/> surface.
    /// </summary>
    internal IReadOnlyList<string> GetRegisteredTools()
    {
        return _handlers.Keys.ToList().AsReadOnly();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tool Registration
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers all 22 catalog tools (from <see cref="Models.ToolCatalog.AllTools"/>)
    /// with their handler functions. All 22 tools have real handler implementations:
    /// <list type="bullet">
    ///   <item><description>P0 (10): vm_echo, vm_diag, vm_create, vm_start, vm_stop,
    ///     vm_destroy, vm_list, vm_status, vm_run_command, vm_copy_file</description></item>
    ///   <item><description>P1 (8): vm_list_images, vm_run_script, vm_get_file,
    ///     vm_restart, vm_wait_ready, vm_checkpoint, vm_cleanup_orphans,
    ///     vm_os_install</description></item>
    ///   <item><description>P2 (3): vm_pause, vm_resume, vm_create_base_image</description></item>
    ///   <item><description>Configuration (1): vm_configure</description></item>
    /// </list>
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog.
    /// </summary>
    private void RegisterAllCatalogTools()
    {
        // P0 tools with real handlers
        _handlers["vm_echo"] = HandleEchoAsync;
        _handlers["vm_diag"] = HandleDiagAsync;
        _handlers["vm_create"] = HandleCreateAsync;
        _handlers["vm_start"] = HandleStartAsync;
        _handlers["vm_stop"] = HandleStopAsync;
        _handlers["vm_destroy"] = HandleDestroyAsync;
        _handlers["vm_list"] = HandleListAsync;
        _handlers["vm_status"] = HandleStatusAsync;
        _handlers["vm_run_command"] = HandleRunCommandAsync;
        _handlers["vm_copy_file"] = HandleCopyFileAsync;

        // P1 tools with real handlers
        _handlers["vm_list_images"] = HandleListImagesAsync;
        _handlers["vm_run_script"] = HandleRunScriptAsync;
        _handlers["vm_get_file"] = HandleGetFileAsync;
        _handlers["vm_restart"] = HandleRestartAsync;

        // P1 tools — Batch 2
        _handlers["vm_wait_ready"] = HandleWaitReadyAsync;
        _handlers["vm_checkpoint"] = HandleCheckpointAsync;
        _handlers["vm_cleanup_orphans"] = HandleCleanupOrphansAsync;

        // P1 tools — ISO Installation
        _handlers["vm_os_install"] = HandleOsInstallAsync;

        // P2 tools — Pause/Resume
        _handlers["vm_pause"] = HandlePauseAsync;
        _handlers["vm_resume"] = HandleResumeAsync;

        // Configuration
        _handlers["vm_configure"] = HandleConfigureAsync;

        // Issue #51: vm_create_base_image (P2 Storage).
        _handlers["vm_create_base_image"] = HandleVmCreateBaseImageAsync;

    }

    // ═══════════════════════════════════════════════════════════════════
    // Argument Extraction Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts an optional string argument from the tool arguments dictionary.
    /// Handles both raw string values and JsonElement values from deserialization.
    /// </summary>
    private static string? GetStringArg(Dictionary<string, object?> args, string key)
    {
        args.TryGetValue(key, out var value);
        return value?.ToString();
    }

    /// <summary>
    /// Extracts a required string argument, throwing ArgumentException if missing or empty.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: INVALID_PARAMETER.
    /// </summary>
    private static string GetRequiredStringArg(Dictionary<string, object?> args, string key)
    {
        var value = GetStringArg(args, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Required parameter '{key}' is missing or empty.", key);
        return value;
    }

    /// <summary>
    /// Extracts an integer argument with a default value.
    /// Handles int, long, JsonElement, and string representations.
    /// </summary>
    private static int GetIntArg(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (args.TryGetValue(key, out var value) && value != null)
        {
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is JsonElement je && je.TryGetInt32(out var ji)) return ji;
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// Extracts a long argument with a default value.
    /// Handles long, int, JsonElement, and string representations.
    /// </summary>
    private static long GetLongArg(Dictionary<string, object?> args, string key, long defaultValue)
    {
        if (args.TryGetValue(key, out var value) && value != null)
        {
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is JsonElement je && je.TryGetInt64(out var jl)) return jl;
            if (long.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// Extracts an optional integer argument. Returns <c>null</c> when the key is absent
    /// or the value is JSON null. Handles int, long, JsonElement, and string representations.
    /// </summary>
    private static int? GetOptionalIntArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null)
            return null;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Null)
            return null;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is JsonElement je2 && je2.TryGetInt32(out var ji)) return ji;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        throw new ArgumentException($"Parameter '{key}' has invalid integer value '{value}'.", key);
    }

    /// <summary>
    /// Extracts an optional long argument. Returns <c>null</c> when the key is absent
    /// or the value is JSON null. Handles long, int, JsonElement, and string representations.
    /// </summary>
    private static long? GetOptionalLongArg(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null)
            return null;
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Null)
            return null;
        if (value is long l) return l;
        if (value is int i) return i;
        if (value is JsonElement je2 && je2.TryGetInt64(out var jl)) return jl;
        if (long.TryParse(value.ToString(), out var parsed)) return parsed;
        throw new ArgumentException($"Parameter '{key}' has invalid integer value '{value}'.", key);
    }

    /// <summary>
    /// Extracts a boolean argument with a default value.
    /// Handles bool, JsonElement (True/False), and string representations.
    /// </summary>
    private static bool GetBoolArg(Dictionary<string, object?> args, string key, bool defaultValue)
    {
        if (args.TryGetValue(key, out var value) && value != null)
        {
            if (value is bool b) return b;
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True) return true;
                if (je.ValueKind == JsonValueKind.False) return false;
            }
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        }
        return defaultValue;
    }

    /// <summary>
    /// Extracts a boolean argument strictly under MCP-D9 canonical-bool contract.
    /// </summary>
    /// <remarks>
    /// Accepts ONLY:
    /// <list type="bullet">
    ///   <item><description>Absent key → returns <paramref name="defaultValue"/>.</description></item>
    ///   <item><description>.NET <see cref="bool"/> value.</description></item>
    ///   <item><description><see cref="JsonElement"/> of kind <see cref="JsonValueKind.True"/> or <see cref="JsonValueKind.False"/>.</description></item>
    ///   <item><description><see cref="JsonElement"/> of kind <see cref="JsonValueKind.String"/> whose value is EXACTLY the ordinal lowercase string <c>"true"</c> or <c>"false"</c>.</description></item>
    ///   <item><description>Raw <see cref="string"/> equal (ordinal) to EXACTLY <c>"true"</c> or <c>"false"</c>.</description></item>
    /// </list>
    /// Rejects everything else — including <c>"True"</c>, <c>"FALSE"</c>, <c>" true "</c>,
    /// <c>"1"</c>, <c>"0"</c>, empty string, numbers, objects, and JSON null —
    /// by throwing <see cref="ArgumentException"/> with the offending parameter name.
    /// <see cref="ErrorMapper"/> maps that to <c>INVALID_PARAMETER</c>.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D9 / Error Code Taxonomy: INVALID_PARAMETER.
    /// </remarks>
    private static bool GetStrictBoolArg(Dictionary<string, object?> args, string key, bool defaultValue)
    {
        if (!args.TryGetValue(key, out var value))
            return defaultValue;

        if (value == null)
            throw new ArgumentException(
                $"Parameter '{key}' has invalid boolean value 'null'. Expected true or false.", key);

        if (value is bool b) return b;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
            if (je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString();
                if (string.Equals(s, "true", StringComparison.Ordinal)) return true;
                if (string.Equals(s, "false", StringComparison.Ordinal)) return false;
                throw new ArgumentException(
                    $"Parameter '{key}' has invalid boolean value '{s}'. Expected exact-lowercase 'true' or 'false'.", key);
            }
            throw new ArgumentException(
                $"Parameter '{key}' has invalid boolean value '{je}'. Expected true or false.", key);
        }

        if (value is string raw)
        {
            if (string.Equals(raw, "true", StringComparison.Ordinal)) return true;
            if (string.Equals(raw, "false", StringComparison.Ordinal)) return false;
            throw new ArgumentException(
                $"Parameter '{key}' has invalid boolean value '{raw}'. Expected exact-lowercase 'true' or 'false'.", key);
        }

        throw new ArgumentException(
            $"Parameter '{key}' has invalid boolean value '{value}'. Expected true or false.", key);
    }

    /// <summary>
    /// Shared precondition: ensures the target VM is in "Running" state before
    /// attempting any guest operation (command, script, file transfer).
    /// Throws <see cref="VmNotRunningException"/> if the VM is not running.
    /// See GitHub Issue #21.
    /// </summary>
    private async Task EnsureVmRunningAsync(string hostId, string vmId, CancellationToken ct)
    {
        // Issue #52 Phase 2 Gate 3 RC-1: prefer the in-process IPowerShellHost when it has
        // been injected. The pre-flight (Issue #21 — clearer "VM not running" errors before
        // attempting New-PSSession) is preserved; only the routing changes — we no longer
        // funnel every vm_run_command/vm_copy_file/vm_run_script/vm_get_file through the
        // legacy out-of-process PowerShellExecutor (PSD-D5/D6 single-facade rule).
        string state;
        if (_psHost is not null)
        {
            state = await _psHost.GetVmStateAsync(hostId, vmId, ct).ConfigureAwait(false);
        }
        else
        {
            // Backward-compat path for tests that have not been updated to inject IPowerShellHost.
            var vmInfo = await _hyperVManager.GetVmStatusAsync(hostId, vmId, ct).ConfigureAwait(false);
            state = vmInfo.State;
        }

        if (state != "Running")
        {
            throw new VmNotRunningException(hostId, vmId, state);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // P0 Tool Handlers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for vm_echo: echoes back the "message" argument.
    /// This is the simplest tool — a health check that bypasses all
    /// concurrency controls and host resolution.
    /// See /myplans/mcp-interface/mcp-interface-design.md — Complete Tool Catalog: vm_echo.
    /// </summary>
    private static Task<McpToolResponse> HandleEchoAsync(
        Dictionary<string, object?> arguments, CancellationToken ct)
    {
        arguments.TryGetValue("message", out var messageObj);
        var message = messageObj?.ToString() ?? "";

        var response = McpToolResponse.Ok(new { message });
        return Task.FromResult(response);
    }

    /// <summary>
    /// Handler for vm_diag: diagnostic tool that reports execution context and privileges.
    /// Reports both .NET process-level info and spawned PowerShell process info.
    /// Useful for troubleshooting permission and environment issues (e.g., why vm_create fails).
    /// </summary>
    private async Task<McpToolResponse> HandleDiagAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        // ── Part 0: Build / version metadata ──
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
        var buildTime = System.IO.File.GetLastWriteTimeUtc(assembly.Location);
        var psExePath = (_psExecutor as PowerShellExecutor)?.ExecutablePath ?? "unknown";

        // ── Part 1: .NET process-level diagnostics (no PowerShell needed) ──
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

        var dotnetDiag = new Dictionary<string, object?>
        {
            ["user"] = identity.Name,
            ["isAdmin"] = isAdmin,
            ["processId"] = Environment.ProcessId,
            ["processPath"] = Environment.ProcessPath,
            ["dotnetVersion"] = Environment.Version.ToString(),
            ["osVersion"] = Environment.OSVersion.ToString(),
            ["machineName"] = Environment.MachineName,
            ["is64BitProcess"] = Environment.Is64BitProcess,
            ["psExecutable"] = psExePath,
        };

        // ── Part 2: PowerShell process diagnostics ──
        Dictionary<string, object?>? psDiag = null;
        Dictionary<string, object?>? psRaw = null;
        string? psError = null;

        var psScript = @"
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$elevationType = if ($isAdmin) { 'Elevated' } else { 'Standard' }

$hyperVModule = Get-Module -ListAvailable -Name Hyper-V -ErrorAction SilentlyContinue
$getVmWorks = $false
$getVmError = ''
$vmCount = 0
try {
    $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop)
    $getVmWorks = $true
    $vmCount = @($vms).Count
} catch {
    $getVmError = $_.Exception.Message
}

[ordered]@{
    user            = $identity.Name
    isAdmin         = $isAdmin
    elevationType   = $elevationType
    psVersion       = $PSVersionTable.PSVersion.ToString()
    psEdition       = $PSVersionTable.PSEdition
    psExecutable    = (Get-Process -Id $PID).Path
    hyperVModule    = ($null -ne $hyperVModule)
    hyperVVersion   = if ($hyperVModule) { $hyperVModule.Version.ToString() } else { $null }
    getVmWorks      = $getVmWorks
    getVmError      = $getVmError
    vmCount         = $vmCount
} | ConvertTo-Json
";

        try
        {
            var psResult = await _psExecutor.ExecuteAsync(psScript, timeoutSeconds: 30, ct);

            // Always capture raw output for debugging
            var rawStdout = psResult.Stdout;
            var rawStderr = psResult.Stderr;

            // Capture raw output FIRST, before any deserialization attempt
            psRaw = new Dictionary<string, object?>
            {
                ["exitCode"] = psResult.ExitCode,
                ["stdoutLength"] = rawStdout?.Length ?? 0,
                ["stderrLength"] = rawStderr?.Length ?? 0,
                ["stdoutPreview"] = rawStdout?.Length > 0 ? rawStdout.Substring(0, Math.Min(rawStdout.Length, 500)) : "(empty)",
                ["stderrPreview"] = rawStderr?.Length > 0 ? rawStderr.Substring(0, Math.Min(rawStderr.Length, 500)) : "(empty)",
                ["timedOut"] = psResult.TimedOut,
                ["cancelled"] = psResult.Cancelled,
                ["durationMs"] = psResult.DurationMs,
                ["success"] = psResult.Success,
            };

            if (psResult.Success && !string.IsNullOrWhiteSpace(psResult.Stdout))
            {
                try
                {
                    psDiag = JsonSerializer.Deserialize<Dictionary<string, object?>>(psResult.Stdout.Trim());
                }
                catch (JsonException jex)
                {
                    psError = $"JSON parse failed: {jex.Message}";
                }
            }
            else
            {
                psError = !string.IsNullOrWhiteSpace(psResult.Stderr)
                    ? psResult.Stderr.Trim()
                    : $"PowerShell exited with code {psResult.ExitCode}";
            }
        }
        catch (Exception ex)
        {
            psError = ex.Message;
        }

        // ── Part 4: Targeted PowerShell tests ──
        var psTests = new List<Dictionary<string, object?>>();

        // Test 1: Basic output
        psTests.Add(await RunPsTestAsync("basic-output", "Write-Output 'test-ok'", ct));

        // Test 2: Hashtable to JSON
        psTests.Add(await RunPsTestAsync("hashtable-json", "@{name='test';value=42} | ConvertTo-Json", ct));

        // Test 3: Admin check only
        psTests.Add(await RunPsTestAsync("admin-check",
            "$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent()); Write-Output \"IsAdmin: $($p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))\"", ct));

        // Test 4: Get-VM (the critical test)
        psTests.Add(await RunPsTestAsync("get-vm", "try { $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop); Write-Output \"VMs: $(@($vms).Count)\" } catch { Write-Output \"GetVM-Error: $($_.Exception.Message)\" }", ct));

        // Test 4b: Get-VM WITH Import-Module (tests the fix)
        psTests.Add(await RunPsTestAsync("get-vm-with-import",
            "Import-Module Hyper-V -ErrorAction Stop; try { $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop); Write-Output \"VMs: $(@($vms).Count)\" } catch { Write-Output \"GetVM-Error: $($_.Exception.Message)\" }", ct));

        // Test 4c: Get-VM -Name with Import-Module (tests the create flow)
        psTests.Add(await RunPsTestAsync("get-vm-name-with-import",
            "Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name 'nonexistent-test' -ComputerName localhost -ErrorAction SilentlyContinue; Write-Output \"Found: $($vm -ne $null)\"", ct));

        // Test 4d: New-VHD availability with Import-Module (tests VHDX creation)
        psTests.Add(await RunPsTestAsync("new-vhd-check",
            "Import-Module Hyper-V -ErrorAction Stop; Write-Output \"New-VHD available: $(Get-Command New-VHD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)\"", ct));

        // Test 7: Multi-line script with file-based output capture (bypasses stdout issue)
        psTests.Add(await RunPsTestAsync("file-capture-test", @"
$outFile = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    ('hypervmcp-diag-' + [System.Guid]::NewGuid().ToString('N') + '.txt'))
'START' | Out-File $outFile -Encoding UTF8
try {
    Import-Module Hyper-V -ErrorAction Stop
    'IMPORT_OK' | Out-File $outFile -Append -Encoding UTF8

    $vm = Get-VM -Name 'nonexistent-diag' -ComputerName localhost -ErrorAction SilentlyContinue
    ""GET_VM_OK: Found=$($vm -ne $null)"" | Out-File $outFile -Append -Encoding UTF8
    Write-Output ""GET_VM_OK: Found=$($vm -ne $null)""

    $baseVhdx = 'C:\HyperVMCP\Images\base.vhdx'
    ""BASE_EXISTS: $(Test-Path $baseVhdx)"" | Out-File $outFile -Append -Encoding UTF8
    Write-Output ""BASE_EXISTS: $(Test-Path $baseVhdx)""
} catch {
    ""ERROR: $($_.Exception.Message)"" | Out-File $outFile -Append -Encoding UTF8
    Write-Output ""ERROR: $($_.Exception.Message)""
} finally {
    'END' | Out-File $outFile -Append -Encoding UTF8
    Write-Output 'END'
    Remove-Item $outFile -Force -ErrorAction SilentlyContinue
}
", ct));

        // Test 5: Hyper-V module check
        psTests.Add(await RunPsTestAsync("hyperv-module", "$m = Get-Module -ListAvailable -Name Hyper-V; Write-Output \"Module: $($m.Version)\"", ct));

        // Test 6: Multi-line script with Get-VM
        psTests.Add(await RunPsTestAsync("multiline-getvm", @"
$ErrorActionPreference = 'Stop'
$result = @{works = $true; error = ''}
try {
    $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop)
    $result['vmCount'] = @($vms).Count
} catch {
    $result['works'] = $false
    $result['error'] = $_.Exception.Message
}
$result | ConvertTo-Json
", ct));

        // ── Part 3: Environment variables ──
        var envDiag = new Dictionary<string, object?>
        {
            ["HYPERV_MCP_BASE_VHDX"] = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX"),
            ["HYPERV_MCP_STORAGE_ROOT"] = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT"),
        };

        // ── Part 5: Phase 2 in-process PowerShellHost diagnostics (Issue #52) ──
        // The legacy `powershell.*` block above probes via the OUT-of-proc executor,
        // which is unrelated to the Phase 2 in-proc host's actual init state. Surface
        // the in-proc host's cached init failure (if any) so live debugging can localize
        // why `vm_diag.powershell.hyperVModule = false` while child-process probes
        // succeed. All string fields are credential-redacted in GetInitDiagnostics().
        Dictionary<string, object?>? phase2Host = null;
        if (_psHost is not null)
        {
            try
            {
                var diag = _psHost.GetInitDiagnostics();
                phase2Host = new Dictionary<string, object?>
                {
                    ["initialized"] = diag.Initialized,
                    ["edition"] = diag.Edition?.ToString(),
                    ["lastInitError"] = diag.LastInitError,
                    ["lastInitErrorType"] = diag.LastInitErrorType,
                    ["lastInitErrorTrace"] = diag.LastInitErrorTrace,
                    ["psModulePath"] = diag.PsModulePath,
                    // RC-8: per-edition attempt detail (PS7 + PS5.1) so vm_diag
                    // surfaces WHICH edition failed at WHICH stage with the full
                    // inner-exception chain.
                    ["ps7Attempt"] = SerializeEditionAttempt(diag.Ps7Attempt),
                    ["ps51Attempt"] = SerializeEditionAttempt(diag.Ps51Attempt),
                };
            }
            catch (Exception ex)
            {
                // Diagnostic surface must never break vm_diag itself.
                phase2Host = new Dictionary<string, object?>
                {
                    ["initialized"] = false,
                    ["lastInitError"] = $"GetInitDiagnostics threw: {ex.GetType().FullName}: {ex.Message}",
                    ["lastInitErrorType"] = ex.GetType().FullName,
                };
            }
        }

        // ── Part 6: Issue #169 / VC-D7 — base-image hash cache warm-up status ──
        Dictionary<string, object?>? baseImageHashCacheDiag = null;
        if (_baseImageHashCache is not null)
        {
            try
            {
                var stats = _baseImageHashCache.Stats;
                var sidecarStats = _baseImageHashCache.SidecarStats;
                var report = _baseImageHashCache.LatestWarmUpReport;
                long? warmUpDurationMs = null;
                if (report is { CompletedAtUtc: { } completedAt })
                {
                    warmUpDurationMs = (long)(completedAt - report.StartedAtUtc).TotalMilliseconds;
                }

                // VC-D15: project the latest mutation record (if any) as a small
                // nested dictionary with ISO-8601 timestamp + offending hashes.
                Dictionary<string, object?>? lastMutationDetected = null;
                if (sidecarStats.LastMutationDetected is { } mut)
                {
                    lastMutationDetected = new Dictionary<string, object?>
                    {
                        ["baseVhdxPath"] = mut.BaseVhdxPath,
                        ["vmName"] = mut.VmName,
                        ["detectedAtUtc"] = mut.DetectedAtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        ["expectedSha256"] = mut.ExpectedSha256,
                        ["actualSha256"] = mut.ActualSha256,
                    };
                }

                baseImageHashCacheDiag = new Dictionary<string, object?>
                {
                    ["warmUpStatus"] = SerializeWarmUpStatus(report?.Status),
                    ["warmUpDurationMs"] = warmUpDurationMs,
                    ["warmUpStartedAt"] = report?.StartedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["warmUpCompletedAt"] = report?.CompletedAtUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["hits"] = stats.Hits,
                    ["misses"] = stats.Misses,
                    ["computes"] = stats.Computes,
                    ["entries"] = stats.Entries,
                    // VC-D14 / VC-D15 — sidecar persistence telemetry.
                    ["sidecarHits"] = sidecarStats.SidecarHits,
                    ["sidecarWrites"] = sidecarStats.SidecarWrites,
                    ["sidecarDiscards"] = sidecarStats.SidecarDiscards,
                    ["lastMutationDetected"] = lastMutationDetected,
                    ["warmUpPaths"] = report is null
                        ? Array.Empty<object>()
                        // 🟡 #1 (Issue #169 Gate 6): VC-D7 specifies "the 100 most
                        // recently warmed paths". The previous Take(100) returned
                        // the FIRST 100, biasing diagnostics toward boot-time
                        // entries and hiding recent activity. WarmAsync appends
                        // results in iteration order, so TakeLast(100) gives the
                        // tail (the most recent). For multi-cycle warm-ups the
                        // LatestWarmUpReport.Paths list is replaced wholesale,
                        // so TakeLast within a single cycle reliably surfaces
                        // the most-recent entries that diagnostic consumers care
                        // about.
                        : report.Paths
                            .TakeLast(100)
                            .Select(p => (object)new Dictionary<string, object?>
                            {
                                ["path"] = p.Path,
                                ["status"] = SerializeWarmUpPathStatus(p.Status),
                                ["sha256"] = p.Sha256,
                                ["elapsedMs"] = p.ElapsedMs,
                                ["errorCode"] = p.ErrorCode,
                                ["errorMessage"] = p.ErrorMessage,
                            })
                            .ToArray(),
                };
            }
            catch (Exception ex)
            {
                // Diagnostic surface must never break vm_diag itself.
                baseImageHashCacheDiag = new Dictionary<string, object?>
                {
                    ["warmUpStatus"] = "failed",
                    ["error"] = $"{ex.GetType().FullName}: {ex.Message}",
                };
            }
        }

        var result = new Dictionary<string, object?>
        {
            // DIAG-D2 / DIAG-D3 / DIAG-D6: bumped to "v12" per VC-D14 / VC-D15
            // (Issue #170 / post-hash sidecar persistence). The baseImageHashCache
            // block gains sidecarHits / sidecarWrites / sidecarDiscards /
            // lastMutationDetected fields; consumers gating on diagVersion can
            // use this as a deterministic capability marker. "v11" referenced the
            // VC-D7 warm-up surface; "v10" the pre-warm-up spill-file cohort.
            ["diagVersion"] = "v12",
            ["serverVersion"] = fileVersionInfo.FileVersion ?? "unknown",
            ["buildTimestamp"] = buildTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["dotnet"] = dotnetDiag,
            ["powershell"] = psDiag,
            ["powershellError"] = psError,
            ["powershellRaw"] = psRaw,
            ["psTests"] = psTests,
            ["environment"] = envDiag,
            ["phase2Host"] = phase2Host,
            ["baseImageHashCache"] = baseImageHashCacheDiag,
        };

        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// RC-8 (Issue #52 Phase 2 Gate 3 Loopback #4): flatten a
    /// <see cref="PowerShellEditionAttempt"/> snapshot into a JSON-serializable
    /// dictionary for inclusion in <c>vm_diag.phase2Host</c>. Returns <c>null</c>
    /// when the attempt itself is <c>null</c> (i.e. that edition was never
    /// entered).
    /// </summary>
    private static Dictionary<string, object?>? SerializeEditionAttempt(PowerShellEditionAttempt? attempt)
    {
        if (attempt is null) return null;
        return new Dictionary<string, object?>
        {
            ["attempted"] = attempt.Attempted,
            ["succeeded"] = attempt.Succeeded,
            ["failureStage"] = attempt.FailureStage,
            ["exceptionType"] = attempt.ExceptionType,
            ["exceptionMessage"] = attempt.ExceptionMessage,
            ["innerExceptionType"] = attempt.InnerExceptionType,
            ["innerExceptionMessage"] = attempt.InnerExceptionMessage,
            ["innerExceptionStackTrace"] = attempt.InnerExceptionStackTrace,
            ["fullExceptionToString"] = attempt.FullExceptionToString,
        };
    }

    /// <summary>
    /// VC-D7 (Issue #169 Gate 6 remediation): serialize <see cref="WarmUpStatus"/>
    /// as one of the 4 canonical kebab-case literals the design's §VC-D7 wire
    /// contract specifies for <c>vm_diag.baseImageHashCache.warmUpStatus</c>:
    /// <c>"not-started" | "in-progress" | "completed" | "cancelled"</c>.
    /// <para>
    /// The <see cref="WarmUpStatus"/> enum carries two additional internal-use
    /// values (<see cref="WarmUpStatus.Partial"/>, <see cref="WarmUpStatus.Failed"/>)
    /// that VC-D7 does NOT expose as wire literals. Mapping rationale:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="WarmUpStatus.Partial"/> ⇒ <c>"completed"</c>:
    ///   the warm-up cycle DID finish; per-path successes / failures are already
    ///   surfaced individually in <c>warmUpPaths[].status</c>. Reporting
    ///   <c>"completed"</c> at the cycle level matches the design's "either
    ///   completed or cancelled" terminal-state model.</description></item>
    ///   <item><description><see cref="WarmUpStatus.Failed"/> ⇒ <c>"cancelled"</c>:
    ///   the warm-up cycle did NOT reach completion. <c>"cancelled"</c> is the
    ///   closest canonical literal for "did not finish"; the operator can drill
    ///   into <c>warmUpPaths[]</c> to see the per-path <c>errorCode</c>/<c>errorMessage</c>.</description></item>
    ///   <item><description><c>null</c> ⇒ <c>"not-started"</c>: <see cref="IBaseImageHashCache.LatestWarmUpReport"/>
    ///   returns <see langword="null"/> until the first warm-up cycle is scheduled.</description></item>
    /// </list>
    /// </summary>
    private static string SerializeWarmUpStatus(WarmUpStatus? status) => status switch
    {
        null => "not-started",
        WarmUpStatus.NotStarted => "not-started",
        WarmUpStatus.InProgress => "in-progress",
        WarmUpStatus.Completed => "completed",
        WarmUpStatus.Partial => "completed", // see XML doc above for mapping rationale
        WarmUpStatus.Cancelled => "cancelled",
        WarmUpStatus.Failed => "cancelled",  // see XML doc above for mapping rationale
        _ => "not-started",
    };

    /// <summary>
    /// VC-D7: serialize <see cref="WarmUpPathStatus"/> as a stable kebab-case
    /// string for the <c>vm_diag.baseImageHashCache.paths[].status</c> field.
    /// The path-status set retains the richer
    /// <c>already-warm</c>/<c>warmed-fresh</c> distinction because per-path
    /// telemetry is operationally useful for distinguishing warm-on-init hits
    /// from fresh computes during cold-start triage. <see cref="WarmUpPathStatus.Succeeded"/>
    /// remains for any future call site that does not differentiate.
    /// </summary>
    private static string SerializeWarmUpPathStatus(WarmUpPathStatus status) => status switch
    {
        WarmUpPathStatus.Succeeded => "succeeded",
        WarmUpPathStatus.AlreadyWarm => "already-warm",
        WarmUpPathStatus.WarmedFresh => "warmed-fresh",
        WarmUpPathStatus.Failed => "failed",
        WarmUpPathStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    /// <summary>
    /// Handler for vm_create: creates a new VM from a base VHDX.
    /// Acquires global slot + per-host lock for lifecycle operations.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — VM creation with differencing VHDX.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_create needs Global+Host.
    /// </summary>
    private async Task<McpToolResponse> HandleCreateAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var name = GetRequiredStringArg(args, "name");
        InputValidation.ValidateVmName(name);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var baseVhdxPath = GetStringArg(args, "baseVhdxPath");
        var cpuCount = GetIntArg(args, "cpuCount", 2);
        var memoryMB = GetLongArg(args, "memoryMB", 4096);
        var autoStart = GetStrictBoolArg(args, "autoStart", false);
        // Issue #169 / VC-D6: per-call mutation-guard knob. Strict-bool parsing
        // (MCP-D9 / Issues #63, #71) — non-canonical values are rejected with
        // INVALID_PARAMETER rather than silently coerced. Default true preserves
        // ST-D6 / Issue #23 enforcement; false collapses the guard to
        // ReadOnly-attribute-only (operator-accepted ADR-4 trade-off documented
        // on the vm_create tool description).
        var verifyBaseImageHash = GetStrictBoolArg(args, "verifyBaseImageHash", true);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);

        // VC-D12 (Issue #170): wrap the CreateVmAsync invocation in a linked
        // CTS that fires after the resolved per-call timeout (default 120s,
        // env-overridable via HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS). The
        // synchronous pre/post SHA-256 verification (VC-D13) dominates this
        // budget on cold OS page caches; the sidecar (VC-D14) collapses it on
        // repeat calls. The PowerShell-internal 600s budget inside
        // HyperVManager.CreateVmAsync is unchanged — this is a transport-only
        // widening.
        var vmCreateTimeoutSeconds = ResolveVmCreateTimeoutSeconds();
        using var vmCreateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        vmCreateCts.CancelAfter(TimeSpan.FromSeconds(vmCreateTimeoutSeconds));

        var vmInfo = await _hyperVManager.CreateVmAsync(
            hostId, name, baseVhdxPath, cpuCount, memoryMB, autoStart,
            verifyBaseImageHash, vmCreateCts.Token);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// VC-D12 (Issue #170): resolve the per-call <c>vm_create</c> transport
    /// timeout. Reads <c>HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS</c>, validates
    /// inclusive range 60..600, falls back to 120 on missing / invalid /
    /// out-of-range (logs a warning for invalid). Pure helper — no side
    /// effects beyond logging.
    /// </summary>
    internal int ResolveVmCreateTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(VmCreateTimeoutEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return VmCreateTimeoutSecondsDefault;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            _logger.LogWarning(
                "{EnvVar}='{Raw}' is not a valid integer; falling back to default {DefaultSeconds}s — VC-D12.",
                VmCreateTimeoutEnvVar, raw, VmCreateTimeoutSecondsDefault);
            return VmCreateTimeoutSecondsDefault;
        }

        if (parsed < VmCreateTimeoutSecondsMin || parsed > VmCreateTimeoutSecondsMax)
        {
            _logger.LogWarning(
                "{EnvVar}={Parsed} is outside the inclusive range {Min}..{Max}; falling back to default {DefaultSeconds}s — VC-D12.",
                VmCreateTimeoutEnvVar, parsed, VmCreateTimeoutSecondsMin, VmCreateTimeoutSecondsMax, VmCreateTimeoutSecondsDefault);
            return VmCreateTimeoutSecondsDefault;
        }

        return parsed;
    }

    /// <summary>
    /// Handler for vm_start: starts a stopped VM.
    /// Issue 3 fix: Acquires global slot + per-host lock + per-VM lock for lifecycle operations.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_start.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_start needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleStartAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.StartVmAsync(hostId, vmId, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_stop: stops a running VM (graceful or forced).
    /// Issue 3 fix: Acquires global slot + per-host lock + per-VM lock for lifecycle operations.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_stop (graceful + force).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_stop needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleStopAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        // MCP-D9 (#63): strict boolean parsing — a non-canonical 'force' value
        // (e.g. "yse", 1.5, arbitrary object) is rejected with INVALID_PARAMETER
        // instead of being silently coerced to false.
        var force = GetStrictBoolArg(args, "force", false);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.StopVmAsync(hostId, vmId, force, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_destroy: destroys a VM (stop + remove + cleanup resources).
    /// Issue 3 fix: Acquires global slot + per-host lock + per-VM lock.
    /// Adding VM lock ensures destroy cannot start while a command is executing on the VM.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_destroy.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_destroy needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleDestroyAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct).ConfigureAwait(false);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct).ConfigureAwait(false);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct).ConfigureAwait(false);

        // SM-D7 (issue #52): evict any persistent PSSession for this (hostId, vmId)
        // BEFORE destroying the VM. Once the VM is gone the underlying PSSession
        // becomes orphaned in the host runspace, leaking resources. The channel call
        // is best-effort — failures must NOT block the destroy operation, which is
        // the user's primary intent. Eviction is performed inside the same per-VM
        // lock scope so no concurrent command/file-transfer can re-create the session
        // between eviction and destroy.
        try
        {
            await _channel.EvictSessionAsync(hostId, vmId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation must propagate — do not swallow.
            throw;
        }
        catch
        {
            // Best-effort eviction. Swallow — VM destroy is the priority.
            // No logger is currently injected into ToolDispatcher (see ST-7);
            // the channel itself redacts and logs internally.
        }

        await _hyperVManager.DestroyVmAsync(hostId, vmId, ct).ConfigureAwait(false);
        return McpToolResponse.Ok(new { vmId, destroyed = true });
    }

    /// <summary>
    /// Handler for vm_list: lists VMs on a host with optional name filtering.
    /// Acquires global slot only (read-only, no per-VM/host lock needed).
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_list.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_list needs Global only.
    /// </summary>
    private async Task<McpToolResponse> HandleListAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var nameFilter = GetStringArg(args, "nameFilter");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        var vms = await _hyperVManager.ListVmsAsync(hostId, nameFilter, ct);
        return McpToolResponse.Ok(new { vms, count = vms.Count });
    }

    /// <summary>
    /// Handler for vm_status: gets detailed status for a specific VM.
    /// Acquires global slot only (read-only operation).
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_status.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_status needs Global only.
    /// </summary>
    private async Task<McpToolResponse> HandleStatusAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        var vmInfo = await _hyperVManager.GetVmStatusAsync(hostId, vmId, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_run_command: executes a command on a guest VM.
    /// Acquires global slot + per-VM lock to serialize commands on the same VM's PSSession.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_run_command needs Global+VM.
    ///
    /// Issue 4 fix: Timed-out and cancelled commands now return success: false with
    /// appropriate error codes instead of wrapping in McpToolResponse.Ok().
    /// See /myplans/execution/commands/commands-design.md — CMD-D4.
    /// </summary>
    private async Task<McpToolResponse> HandleRunCommandAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var command = GetRequiredStringArg(args, "command");
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var shell = GetStringArg(args, "shell") ?? "cmd";
        var timeoutSeconds = GetIntArg(args, "timeoutSeconds", 30);
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        // Issue #21: Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _commandExecutor.ExecuteCommandAsync(hostId, vmId, command, shell, timeoutSeconds, username, password, ct);

        // Issue 4: Timed-out commands return success: false with COMMAND_TIMEOUT error code.
        // The design says timed-out commands should return success: false per CMD-D4 / ADR-9.
        if (result.TimedOut)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = $"Command timed out after {result.DurationMs}ms",
                ErrorCode = ErrorCodes.CommandTimeout,
                Data = result, // Include partial output per ADR-9
            };
        }

        // Cancelled commands also return success: false.
        if (result.Cancelled)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = "Command was cancelled",
                ErrorCode = ErrorCodes.CommandFailed,
                Data = result,
            };
        }

        // Review round 2 fix: Non-zero exit code must return success: false with COMMAND_FAILED.
        // Previously, commands that exited non-zero were returned as successful MCP responses
        // because only timeout and cancellation were handled as failures.
        // See /myplans/mcp-interface/mcp-interface-design.md — Error Code Taxonomy: COMMAND_FAILED.
        if (result.ExitCode != 0)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = $"Command failed with exit code {result.ExitCode}",
                ErrorCode = ErrorCodes.CommandFailed,
                Data = result,
            };
        }

        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Runs a simple PowerShell script and returns a dictionary with the results.
    /// Uses a per-test timeout and cancellation to prevent long-running diagnostics.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunPsTestAsync(string label, string script, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(VmDiagPerTestTimeoutSeconds));

            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: VmDiagPerTestTimeoutSeconds, timeoutCts.Token);
            return new Dictionary<string, object?>
            {
                ["label"] = label,
                ["exitCode"] = result.ExitCode,
                ["stdout"] = result.Stdout.Length > VmDiagOutputPreviewLength
                    ? result.Stdout.Substring(0, VmDiagOutputPreviewLength)
                    : result.Stdout,
                ["stderr"] = result.Stderr.Length > VmDiagOutputPreviewLength
                    ? result.Stderr.Substring(0, VmDiagOutputPreviewLength)
                    : result.Stderr,
                ["success"] = result.Success,
                ["durationMs"] = result.DurationMs,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate caller cancellation
        }
        catch (OperationCanceledException)
        {
            // Per-test timeout hit
            return new Dictionary<string, object?>
            {
                ["label"] = label,
                ["error"] = $"Diagnostic test timed out after {VmDiagPerTestTimeoutSeconds}s",
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["label"] = label,
                ["error"] = ex.Message,
            };
        }
    }

    /// <summary>
    /// Handler for vm_copy_file: copies a file or directory from host to guest VM.
    /// Acquires global slot + per-VM lock for file transfer serialization.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D1.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_copy_file needs Global+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleCopyFileAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var sourcePath = GetRequiredStringArg(args, "sourcePath");
        var destPath = GetRequiredStringArg(args, "destPath");
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        // MCP-D9 (#63): strict boolean parsing — non-canonical 'isDirectory'
        // values are rejected with INVALID_PARAMETER, matching the cure already
        // applied to vm_cleanup_orphans.dryRun and vm_create.autoStart.
        var isDirectory = GetStrictBoolArg(args, "isDirectory", false);
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        // Issue #38: Validate local source path before VM resolution so callers get
        // FILE_NOT_FOUND instead of VM_NOT_FOUND when both are invalid.
        // Note: This check applies to the local host only. Remote file transfer
        // is not currently supported (see myplans/execution/file-transfer/file-transfer-design.md).
        if (!isDirectory && !System.IO.File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Source file not found on host: {sourcePath}", sourcePath);
        if (isDirectory && !System.IO.Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException(
                $"Source directory not found on host: {sourcePath}");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        // Issue #21: Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _fileTransferService.CopyToGuestAsync(hostId, vmId, sourcePath, destPath, isDirectory, username, password, ct);
        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_list_images: lists available base VHDX images on a host.
    /// Acquires global slot only (read-only operation).
    /// See /myplans/vm-management/storage/storage-design.md — Base Image Enumeration.
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_list_images (P1).
    /// </summary>
    private async Task<McpToolResponse> HandleListImagesAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        // ST-D7: ListImagesAsync returns an envelope distinguishing "unconfigured"
        // (Configured=false, soft success) from "configured but enumeration failed"
        // (throws IoOperationFailedException → IO_ERROR) and "configured but path
        // missing" (throws ArgumentException → INVALID_PARAMETER).
        var result = await _hyperVManager.ListImagesAsync(hostId, ct);
        return McpToolResponse.Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // P1 Tool Handlers — Batch 1
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for vm_run_script: executes a multi-line script on a guest VM.
    /// Acquires global slot + per-VM lock to serialize scripts on the same VM's PSSession.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_run_script needs Global+VM.
    ///
    /// Timed-out, cancelled, and non-zero exit code scripts return success: false
    /// with appropriate error codes, same pattern as HandleRunCommandAsync.
    /// </summary>
    private async Task<McpToolResponse> HandleRunScriptAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var script = GetRequiredStringArg(args, "script");
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var shell = GetStringArg(args, "shell") ?? "powershell";
        var timeoutSeconds = GetIntArg(args, "timeoutSeconds", 60);
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        // Issue #21: Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _commandExecutor.ExecuteScriptAsync(hostId, vmId, script, shell, timeoutSeconds, username, password, ct);

        // Timed-out scripts return success: false with COMMAND_TIMEOUT error code.
        if (result.TimedOut)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = $"Script timed out after {result.DurationMs}ms",
                ErrorCode = ErrorCodes.CommandTimeout,
                Data = result,
            };
        }

        // Cancelled scripts return success: false.
        if (result.Cancelled)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = "Script was cancelled",
                ErrorCode = ErrorCodes.CommandFailed,
                Data = result,
            };
        }

        // Non-zero exit code returns success: false with COMMAND_FAILED.
        if (result.ExitCode != 0)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = $"Script failed with exit code {result.ExitCode}",
                ErrorCode = ErrorCodes.CommandFailed,
                Data = result,
            };
        }

        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_get_file: retrieves a file from guest VM to host.
    /// Acquires global slot + per-VM lock for file transfer serialization.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D2, FT-D3.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_get_file needs Global+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleGetFileAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var sourcePath = GetRequiredStringArg(args, "sourcePath");
        var destPath = GetRequiredStringArg(args, "destPath");
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        // Issue #21: Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _fileTransferService.CopyFromGuestAsync(hostId, vmId, sourcePath, destPath, username, password, ct);
        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_restart: restarts a VM (stop + start as atomic operation).
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle operation).
    /// See /myplans/vm-management/vm-management-design.md — Capability Matrix: vm_restart.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_restart needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleRestartAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.RestartVmAsync(hostId, vmId, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    // ═══════════════════════════════════════════════════════════════════
    // P2 Tool Handlers — Pause/Resume
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for vm_pause: pauses a running VM.
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle operation).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_pause needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandlePauseAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.PauseVmAsync(hostId, vmId, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_resume: resumes a paused VM.
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle operation).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_resume needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleResumeAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.ResumeVmAsync(hostId, vmId, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_configure: modifies VM CPU and/or memory configuration.
    /// At least one of <c>cpuCount</c> or <c>memoryMB</c> must be supplied; otherwise
    /// an <see cref="ArgumentException"/> is thrown and mapped to INVALID_PARAMETER.
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle/configuration operation).
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification.
    /// </summary>
    private async Task<McpToolResponse> HandleConfigureAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var cpuCount = GetOptionalIntArg(args, "cpuCount");
        var memoryMB = GetOptionalLongArg(args, "memoryMB");

        // Issue #56 review finding 2: Range-validate before dispatching to PowerShell so
        // callers receive stable INVALID_PARAMETER envelopes (via the ArgumentException
        // arm + SafeArgumentMessage) instead of opaque PowerShell errors. No upper bound
        // is enforced — host-specific limits remain PowerShell's responsibility.
        if (cpuCount.HasValue && cpuCount.Value < 1)
        {
            throw new ArgumentException("'cpuCount' must be a positive integer.", "cpuCount");
        }
        if (memoryMB.HasValue && memoryMB.Value < 1)
        {
            throw new ArgumentException("'memoryMB' must be a positive integer (MB).", "memoryMB");
        }

        if (!cpuCount.HasValue && !memoryMB.HasValue)
        {
            throw new ArgumentException(
                "At least one of 'cpuCount' or 'memoryMB' must be provided.",
                "cpuCount");
        }

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.ConfigureVmAsync(hostId, vmId, cpuCount, memoryMB, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    // ═══════════════════════════════════════════════════════════════════
    // P1 Tool Handlers — Batch 2
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for vm_wait_ready: polls until a VM reaches a ready state (Running + heartbeat OK).
    /// Acquires global slot + per-VM lock to prevent readiness polling from overlapping with
    /// same-VM mutations (start/stop/destroy).
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Readiness Probes.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_wait_ready needs Global+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleWaitReadyAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var timeoutSeconds = GetIntArg(args, "timeoutSeconds", 300);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        var vmInfo = await _hyperVManager.WaitForReadyAsync(hostId, vmId, timeoutSeconds, ct);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_checkpoint: manages checkpoint operations (create, restore, list, delete).
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle-grade operation).
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_checkpoint needs Global+Host+VM.
    /// </summary>
    private async Task<McpToolResponse> HandleCheckpointAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var action = GetRequiredStringArg(args, "action").ToLowerInvariant();
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var name = GetStringArg(args, "name");

        // Validate action is one of the allowed values
        if (!AllowedCheckpointActions.Contains(action))
        {
            throw new ArgumentException(
                $"Invalid checkpoint action '{action}'. Allowed values: create, restore, list, delete.",
                nameof(action));
        }

        // name is required for create, restore, delete
        if (action != "list" && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                $"Parameter 'name' is required for checkpoint action '{action}'.",
                "name");
        }

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);

        CheckpointResult result = action switch
        {
            "create" => await _checkpointManager.CreateCheckpointAsync(hostId, vmId, name!, ct),
            "restore" => await _checkpointManager.RestoreCheckpointAsync(hostId, vmId, name!, ct),
            "list" => await _checkpointManager.ListCheckpointsAsync(hostId, vmId, ct),
            "delete" => await _checkpointManager.DeleteCheckpointAsync(hostId, vmId, name!, ct),
            _ => throw new ArgumentException($"Unknown checkpoint action: {action}"),
        };

        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_cleanup_orphans: finds and optionally destroys orphaned VMs.
    /// Acquires global slot + per-host lock (affects host-level resources).
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Orphan Cleanup.
    /// </summary>
    private async Task<McpToolResponse> HandleCleanupOrphansAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        // Issue #57: Mirror the #56 cure (commit ce28d74) early-validation pattern so
        // bad arguments produce a structured INVALID_PARAMETER envelope instead of an
        // opaque "An error occurred invoking 'vm_cleanup_orphans'" SDK message.
        // GetStrictBoolArg throws ArgumentException with a ParamName for non-boolean
        // dryRun values; the dispatcher's outer try/catch + ErrorMapper.MapException
        // converts it to ErrorCodes.InvalidParameter via the SafeArgumentMessage path.
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        if (string.IsNullOrWhiteSpace(hostId))
        {
            throw new ArgumentException(
                "Parameter 'hostId' is missing and no DefaultHostId is configured.",
                "hostId");
        }
        var dryRun = GetStrictBoolArg(args, "dryRun", true);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);

        // Issue #57 (Gate 6 round 2): Let unknown manager exceptions propagate
        // unchanged to the dispatcher's outer catch (DispatchAsync, ~line 134),
        // which routes them through ErrorMapper.MapException. Unmapped types fall
        // into the generic sanitization arm (ErrorMapper.cs ~line 356) which
        // produces a populated INTERNAL_ERROR envelope with a redacted, human-
        // readable message — preserving the redaction invariant (MCP-D6).
        //
        // Mirrors the #56-cure HandleConfigureAsync (commit ce28d74) pattern: no
        // local rewrap; let the centralized ErrorMapper own the mapping.
        //
        // Defense-in-depth log: the raw exception (type + message + stack) is
        // emitted at error severity at the catch site BEFORE rethrowing, so
        // operators can see the root cause even though the client-facing
        // envelope is sanitized. Routed through the DI-injected
        // ILogger<ToolDispatcher> via LogError(...) (DIAG-D7 / PR #67); see
        // the adjacent catch-site comment block below for the full rationale,
        // including why the prior Console.Error fallback was replaced.
        //
        // Typed pass-through arms (OperationCanceledException, ArgumentException,
        // HostNotFoundException, InvalidOperationException) intentionally do NOT
        // log — each has a dedicated ErrorMapper branch and is already covered by
        // the outer dispatch flow; mirrors the #56-cure precedent.
        IReadOnlyList<VmInfo> orphans;
        try
        {
            orphans = await _hyperVManager.CleanupOrphansAsync(hostId, dryRun, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (HostNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // Already a well-mapped type (→ COMMAND_FAILED with forwarded message).
            throw;
        }
        catch (Exception ex)
        {
            // Issue #57 Gate 6 Finding 1: log the underlying failure with full
            // type + message + stack at error severity at the catch site so the
            // root cause is preserved server-side, even after sanitization
            // strips it from the client envelope.
            //
            // DIAG-D7 (#65): the previous Console.Error.WriteLine fallback bypassed
            // log severity filtering and any structured-logging sink. Now routed
            // through the DI-wired ILogger<ToolDispatcher> as a structured
            // LogError, preserving the redaction-then-rethrow ordering.
            try
            {
                _logger.LogError(
                    ex,
                    "vm_cleanup_orphans: underlying manager exception {ExceptionType}: {ExceptionMessage}",
                    ex.GetType().FullName,
                    ex.Message);
            }
            catch
            {
                // Logging must never mask the original failure.
            }

            // Issue #57 Gate 6 Finding 2: do NOT rewrap as InvalidOperationException
            // carrying the raw type+message — that bypasses ErrorMapper's generic
            // sanitization arm (ErrorMapper.cs ~line 356) and leaks raw payload.
            // Rethrow unchanged; the outer dispatch catch routes it through the
            // sanitized generic arm → populated INTERNAL_ERROR envelope.
            throw;
        }

        // LF-D10: 'unknown-age' rows are ALWAYS report-only (never destroyed),
        // even when dryRun=false. Therefore the response-level 'action' label
        // must reflect whether ANY row in the result is actually destroyable
        // ('orphan' reason). A response containing only 'unknown-age' rows with
        // dryRun=false would otherwise be mislabeled "destroyed", misreporting
        // what happened to API consumers.
        var anyDestroyed = !dryRun && orphans.Any(o => o.Reason == "orphan");
        return McpToolResponse.Ok(new
        {
            orphans,
            count = orphans.Count,
            dryRun,
            action = anyDestroyed ? "destroyed" : "detected",
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // P1 Tool Handlers — ISO Installation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for vm_os_install: installs Windows from an ISO image in a single call.
    /// Acquires global slot + per-host lock (lifecycle operation creating a new VM).
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D1, ISO-D2.
    /// See /myplans/operational/concurrency/concurrency-design.md — Operation Classification: vm_os_install needs Global+Host.
    /// </summary>
    private async Task<McpToolResponse> HandleOsInstallAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var name = GetRequiredStringArg(args, "name");
        InputValidation.ValidateVmName(name);
        var isoPath = GetRequiredStringArg(args, "isoPath");
        InputValidation.ValidateIsoPath(isoPath);
        var adminPassword = GetRequiredStringArg(args, "adminPassword");
        InputValidation.ValidateAdminPassword(adminPassword);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var cpuCount = GetIntArg(args, "cpuCount", 4);
        var memoryMB = GetLongArg(args, "memoryMB", 8192);
        var diskSizeGB = GetIntArg(args, "diskSizeGB", 127);
        var switchName = GetStringArg(args, "switchName");
        var locale = GetStringArg(args, "locale") ?? "en-US";
        var windowsEdition = GetStringArg(args, "windowsEdition") ?? "Windows 11 Pro";
        var productKey = GetStringArg(args, "productKey");
        var timeoutMinutes = GetIntArg(args, "timeoutMinutes", 60);
        // Issue #97 / ISO-D17: optional escape hatch for the C#-side resource-floor preflight.
        // Strict bool parsing — non-bool values are an INVALID_PARAMETER, not silently coerced.
        var skipPreflight = GetStrictBoolArg(args, "skipPreflight", false);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);

        var result = await _hyperVManager.OsInstallAsync(
            hostId, name, isoPath, adminPassword,
            cpuCount, memoryMB, diskSizeGB, switchName,
            locale, windowsEdition, productKey,
            timeoutMinutes, skipPreflight, ct);
        return McpToolResponse.Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // P2 Tool Handlers — vm_create_base_image (Issue #51)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Handler for <c>vm_create_base_image</c>: sysprep a Running VM, optionally
    /// merge checkpoints, wait for shutdown, then host-side copy the primary VHDX
    /// to the configured image directory as a generalized base image.
    /// <para>
    /// Orchestration steps (per IA-Gate 1b design, ISO-D18/D19/D20 + CP-D6):
    /// </para>
    /// <list type="number">
    ///   <item>Resolve VM by name; preflight <c>state == Running</c> → otherwise <c>VM_NOT_RUNNING</c>.</item>
    ///   <item>If <c>mergeCheckpoints</c>, call <see cref="ICheckpointManager.MergeAllAsync"/>;
    ///         <c>MERGE_NOT_SUPPORTED</c> / <c>CHECKPOINT_MERGE_FAILED</c> surface via typed exceptions.</item>
    ///   <item>Run <c>sysprep /generalize /oobe /shutdown /quiet</c> in-guest via
    ///         <see cref="IPowerShellDirectChannel.InvokeScriptAsync"/>.</item>
    ///   <item>Poll VM state until <c>Off</c> or <c>shutdownTimeoutSeconds</c> elapses;
    ///         on timeout → <c>SYSPREP_FAILED</c>.</item>
    ///   <item>Resolve primary VHDX path via <see cref="IHyperVManager.GetPrimaryVhdxPathAsync"/>.</item>
    ///   <item>Verify <see cref="ServerOptions.ImageDirectory"/> configured; otherwise → <c>IMAGE_COPY_FAILED</c>.</item>
    ///   <item>Host-side <see cref="System.IO.File.Copy(string, string, bool)"/> with <c>overwrite=false</c>;
    ///         IO failure → <c>IMAGE_COPY_FAILED</c>.</item>
    ///   <item>Return <see cref="ImageInfo"/> with <c>Generalized=true</c>.</item>
    /// </list>
    /// Concurrency: global + host + VM (lifecycle-grade).
    /// </summary>
    private async Task<McpToolResponse> HandleVmCreateBaseImageAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var vmName = GetRequiredStringArg(args, "vmName");
        InputValidation.ValidateVmName(vmName);
        var imageName = GetRequiredStringArg(args, "imageName");
        // Disallow path separators / traversal in image name — final file is
        // <imageName>.vhdx under ImageDirectory.
        if (imageName.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
            || imageName.Contains(".."))
        {
            throw new ArgumentException(
                "Parameter 'imageName' must not contain path separators, drive letters, or traversal sequences.",
                "imageName");
        }

        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var mergeCheckpoints = GetStrictBoolArg(args, "mergeCheckpoints", true);
        var shutdownTimeoutSeconds = GetIntArg(args, "shutdownTimeoutSeconds", 600);
        if (shutdownTimeoutSeconds <= 0)
        {
            throw new ArgumentException(
                "Parameter 'shutdownTimeoutSeconds' must be a positive integer.",
                "shutdownTimeoutSeconds");
        }
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct).ConfigureAwait(false);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct).ConfigureAwait(false);

        // ── Step 1: Resolve VM by name; preflight state == Running ──────────
        var allVms = await _hyperVManager.ListVmsAsync(hostId, vmName, ct).ConfigureAwait(false);
        var vm = allVms.FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));
        if (vm is null)
        {
            throw new VmNotFoundException(hostId, vmName);
        }

        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vm.VmId, vmLockTimeout, ct).ConfigureAwait(false);

        if (!string.Equals(vm.State, "Running", StringComparison.OrdinalIgnoreCase))
        {
            throw new VmNotRunningException(hostId, vm.VmId, vm.State);
        }

        // ── Step 2 (optional): Merge checkpoints ────────────────────────────
        MergeResult? mergeOutcome = null;
        if (mergeCheckpoints)
        {
            // MergeAllAsync throws MergeNotSupportedException / CheckpointMergeFailedException;
            // those propagate to ErrorMapper and yield the correct error codes.
            mergeOutcome = await _checkpointManager.MergeAllAsync(hostId, vm.VmId, ct).ConfigureAwait(false);
        }

        // ── Resolve credentials for in-guest sysprep ────────────────────────
        var (resolvedUser, resolvedPass) = CredentialResolver.ResolveCredentials(username, password);

        // ── Step 3: In-guest sysprep via IPowerShellDirectChannel ───────────
        // Single self-contained script — runs sysprep.exe synchronously, surfaces
        // any non-zero exit code as a remote throw which the channel surfaces as
        // an exception (mapped to SYSPREP_FAILED below).
        const string sysprepScript = @"
$ErrorActionPreference = 'Stop'
$sysprepPath = Join-Path $env:windir 'System32\Sysprep\sysprep.exe'
if (-not (Test-Path -LiteralPath $sysprepPath)) {
    throw ""sysprep.exe not found at $sysprepPath""
}
$p = Start-Process -FilePath $sysprepPath `
    -ArgumentList '/generalize','/oobe','/shutdown','/quiet' `
    -Wait -PassThru
if ($p.ExitCode -ne 0) {
    throw ""sysprep.exe exited with code $($p.ExitCode)""
}
'sysprep-invoked'
";

        PowerShellHostResult sysprepResult;
        try
        {
            sysprepResult = await _channel.InvokeScriptAsync(
                hostId,
                vm.VmId,
                resolvedUser,
                resolvedPass,
                sysprepScript,
                args: null,
                ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Sysprep itself crashed or the in-guest invocation failed before
            // shutdown was triggered. Distinguishable from "VM never reached Off"
            // (which is detected by the post-invoke polling loop below).
            throw new SysprepFailedException(hostId, vm.VmId,
                $"In-guest sysprep invocation failed for VM '{vmName}': {ex.Message}", ex);
        }

        // IA-Gate 6 R1 Finding 1: PowerShellHost.InvokeWithTimeoutAsync surfaces
        // terminating in-guest script errors as Success=false / ExitCode=1 rather
        // than as thrown exceptions. Must inspect the result and fail fast instead
        // of proceeding to the poll-for-Off loop, which would otherwise silently
        // misattribute the failure to a shutdown-timeout.
        if (!sysprepResult.Success)
        {
            var reason = string.IsNullOrWhiteSpace(sysprepResult.Stderr)
                ? $"exit code {sysprepResult.ExitCode?.ToString() ?? "n/a"}"
                : sysprepResult.Stderr.Trim();
            throw new SysprepFailedException(hostId, vm.VmId,
                $"In-guest sysprep reported failure for VM '{vmName}': {reason}");
        }

        // ── Step 4: Poll VM state until Off or timeout ──────────────────────
        // Check state immediately before the first delay so very short timeouts
        // are honored, and cap each delay to the remaining budget so the total
        // wait stays within shutdownTimeoutSeconds (±a single status RPC).
        var pollDeadline = DateTime.UtcNow.AddSeconds(shutdownTimeoutSeconds);
        var pollInterval = TimeSpan.FromSeconds(5);
        string lastObservedState = vm.State;
        bool reachedOff = false;
        while (true)
        {
            VmInfo current;
            try
            {
                current = await _hyperVManager.GetVmStatusAsync(hostId, vm.VmId, ct).ConfigureAwait(false);
            }
            catch (VmNotFoundException)
            {
                // VM removed mid-operation — sysprep cannot complete; surface as failure.
                throw new SysprepFailedException(hostId, vm.VmId,
                    $"VM '{vmName}' was removed during sysprep wait.");
            }
            lastObservedState = current.State;
            if (string.Equals(current.State, "Off", StringComparison.OrdinalIgnoreCase))
            {
                reachedOff = true;
                break;
            }
            var remaining = pollDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }
            var delay = remaining < pollInterval ? remaining : pollInterval;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        if (!reachedOff)
        {
            throw new SysprepFailedException(hostId, vm.VmId,
                $"VM '{vmName}' did not reach 'Off' state within {shutdownTimeoutSeconds} seconds after sysprep was invoked (last state: {lastObservedState}).");
        }

        // ── Step 5: Resolve primary VHDX path ───────────────────────────────
        string sourceVhdx;
        try
        {
            sourceVhdx = await _hyperVManager.GetPrimaryVhdxPathAsync(hostId, vmName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ImageCopyFailedException(
                $"Failed to resolve primary VHDX path for VM '{vmName}': {ex.Message}", ex);
        }

        // ── Step 6: Verify ImageDirectory configured ────────────────────────
        var imageDir = _options.ImageDirectory;
        if (string.IsNullOrWhiteSpace(imageDir))
        {
            throw new ImageCopyFailedException(
                "ServerOptions.ImageDirectory is not configured. Set HyperVMcp:ImageDirectory in configuration to enable vm_create_base_image (ISO-D20).");
        }

        if (!System.IO.Directory.Exists(imageDir))
        {
            throw new ImageCopyFailedException(
                $"Configured image directory '{imageDir}' does not exist.",
                sourcePath: sourceVhdx,
                destinationPath: null);
        }

        var destPath = System.IO.Path.Combine(imageDir, imageName + ".vhdx");

        // ── Step 7: Host-side File.Copy (NOT IFileTransferService — ISO-D18) ─
        try
        {
            // overwrite: false — refuse to clobber an existing base image.
            System.IO.File.Copy(sourceVhdx, destPath, overwrite: false);
        }
        catch (System.IO.IOException ex) when (System.IO.File.Exists(destPath))
        {
            throw new ImageCopyFailedException(
                $"Destination image file already exists at '{destPath}'. Choose a different imageName or remove the existing file.",
                ex, sourcePath: sourceVhdx, destinationPath: destPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ImageCopyFailedException(
                $"Failed to copy VHDX '{sourceVhdx}' to '{destPath}': {ex.Message}",
                ex, sourcePath: sourceVhdx, destinationPath: destPath);
        }

        // ── Step 8: Return ImageInfo with Generalized=true ──────────────────
        // IA-Gate 6 R1 Finding 2: ImageInfo carries the Generalized marker directly
        // (matching the documented public contract). Provenance extras
        // (sourceVm*, mergedCheckpointCount, checkpointsMerged) live on an envelope
        // around the ImageInfo rather than as sibling fields of an anonymous object.
        var info = new ImageInfo
        {
            Name = imageName,
            Path = destPath,
            VhdType = "Unknown", // not inspected on this code path
            Generalized = true,
        };

        return McpToolResponse.Ok(new
        {
            image = info,
            sourceVmName = vmName,
            sourceVmId = vm.VmId,
            mergedCheckpointCount = mergeOutcome?.MergedCount ?? 0,
            checkpointsMerged = mergeCheckpoints,
        });
    }
}
