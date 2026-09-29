using System.Security.Principal;
using System.Text.Json;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>Registers the full catalog at construction so discovery is immediately complete; constructor DI owns service lifetimes.
/// Unknown tools return TOOL_NOT_FOUND; pre-cancelled requests fail before invocation. P0/P1 handlers delegate to services; P2 stubs are deferred.
/// Lock order: global, host, VM. Lifecycle uses all three; read-only uses global; execution and readiness use global + VM.</summary>
public class ToolDispatcher : IToolDispatcher
{
    private const int VmDiagPerTestTimeoutSeconds = 10;
    private const int VmDiagOutputPreviewLength = 500;
    private static readonly HashSet<string> AllowedCheckpointActions = new() { "create", "restore", "list", "delete" };

    // The 120s request budget fits a cold ~30GB base (~67s hash + ~7s PowerShell + ~46s headroom).
    // HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS accepts 60–600s; the inner PowerShell budget remains 600s.
    internal const int VmCreateTimeoutSecondsDefault = 120;
    internal const int VmCreateTimeoutSecondsMin = 60;
    internal const int VmCreateTimeoutSecondsMax = 600;
    internal const string VmCreateTimeoutEnvVar = "HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS";

    /// <summary>
    /// Extra transport slack on the password path so the inner readiness window and its own
    /// grace expire before the transport deadline does, keeping the preserve-the-VM outcome
    /// reachable instead of collapsing into cancellation rollback.
    /// </summary>
    internal const int PasswordPathTransportSlackSeconds = 120;

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

    /// <summary>
    /// Optional so existing positional fixtures keep compiling; production DI supplies the shared
    /// singleton, which is what makes the vm_destroy hint eviction effective.
    /// </summary>
    private readonly IGuestRoutingHintStore? _guestRoutingHintStore;
    private readonly IPowerShellHost? _psHost;
    private readonly IBaseImageHashCache? _baseImageHashCache;
    private readonly ServerOptions _options;
    private readonly ILogger<ToolDispatcher> _logger;
    private readonly TimeProvider _readinessClock;

    /// <summary>Inject the channel facade, not ISessionStore, so destroy can evict persistent sessions before removing the VM.</summary>
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
        IBaseImageHashCache? baseImageHashCache = null,
        IGuestRoutingHintStore? guestRoutingHintStore = null,
        TimeProvider? readinessClock = null)
    {
        _readinessClock = readinessClock ?? TimeProvider.System;
        _guestRoutingHintStore = guestRoutingHintStore;
        _hyperVManager = hyperVManager ?? throw new ArgumentNullException(nameof(hyperVManager));
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
        _fileTransferService = fileTransferService ?? throw new ArgumentNullException(nameof(fileTransferService));
        _checkpointManager = checkpointManager ?? throw new ArgumentNullException(nameof(checkpointManager));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        // Production injects the in-process host for VM-state preflight so guest calls do not spawn legacy PowerShell scripts.
        // The dependency remains optional for existing fixtures.
        _psHost = psHost;
        // optional reference to the base-image hash cache so
        // vm_diag can surface its warm-up status without breaking existing test
        // fixtures that construct ToolDispatcher positionally.
        _baseImageHashCache = baseImageHashCache;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // Production DI supplies structured logging; NullLogger preserves fixtures without a logger.
        _logger = logger ?? NullLogger<ToolDispatcher>.Instance;
        RegisterAllCatalogTools();
    }

    /// <inheritdoc />
    public async Task<string> DispatchAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!_handlers.TryGetValue(toolName, out var handler))
        {
            // Unknown tools produce a structured error response, not an exception.
            var errorResponse = McpToolResponse.Fail(
                $"Tool '{toolName}' is not registered",
                RuntimeErrorCodes.ToolNotFound);
            return JsonSerializer.Serialize(errorResponse);
        }

        try
        {
            ct.ThrowIfCancellationRequested();

            var response = await handler(arguments, ct);
            return JsonSerializer.Serialize(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exceptions caught and wrapped — never propagated as MCP protocol errors.
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


    private void RegisterAllCatalogTools()
    {
        _handlers["vm_echo"] = HandleEchoAsync;
        _handlers["vm_diag"] = HandleDiagAsync;
        _handlers["vm_create"] = HandleCreateAsync;
        _handlers["vm_start"] = HandleStartAsync;
        _handlers["vm_stop"] = HandleStopAsync;
        _handlers["vm_destroy"] = HandleDestroyAsync;
        _handlers["vm_list"] = HandleListAsync;
        _handlers["vm_find_by_name"] = HandleFindByNameAsync;
        _handlers["vm_status"] = HandleStatusAsync;
        _handlers["vm_run_command"] = HandleRunCommandAsync;
        _handlers["vm_copy_file"] = HandleCopyFileAsync;

        _handlers["vm_list_images"] = HandleListImagesAsync;
        _handlers["vm_run_script"] = HandleRunScriptAsync;
        _handlers["vm_get_file"] = HandleGetFileAsync;
        _handlers["vm_restart"] = HandleRestartAsync;

        _handlers["vm_wait_ready"] = HandleWaitReadyAsync;
        _handlers["vm_checkpoint"] = HandleCheckpointAsync;
        _handlers["vm_cleanup_orphans"] = HandleCleanupOrphansAsync;

        _handlers["vm_os_install"] = HandleOsInstallAsync;

        _handlers["vm_pause"] = HandlePauseAsync;
        _handlers["vm_resume"] = HandleResumeAsync;

        _handlers["vm_configure"] = HandleConfigureAsync;

        // vm_create_base_image (P2 Storage).
        _handlers["vm_create_base_image"] = HandleVmCreateBaseImageAsync;

    }


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

    /// <summary>Absent keys use defaultValue. Accept bool/JSON booleans and raw/JSON strings exactly equal to ordinal lowercase true or false.
    /// Reject all other values (including null, numbers, objects, empty strings, other casing and whitespace) with a named ArgumentException,
    /// which ErrorMapper maps to INVALID_PARAMETER.</summary>
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

    /// <summary> Shared precondition: ensures the target VM is in "Running" state before attempting any guest
    /// operation (command, script, file transfer). Throws <see cref="VmNotRunningException"/> if the VM is not
    /// running. See GitHub. </summary>
    private async Task EnsureVmRunningAsync(string hostId, string vmId, CancellationToken ct)
    {
        // In-process preflight preserves clear VM-not-running errors without spawning legacy PowerShell on every guest call.
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

    // P0 Tool Handlers

    /// <summary>
    /// Handler for vm_echo: echoes back the "message" argument.
    /// This is the simplest tool — a health check that bypasses all
    /// concurrency controls and host resolution.
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
    /// Reports both.NET process-level info and spawned PowerShell process info.
    /// Useful for troubleshooting permission and environment issues (e.g., why vm_create fails).
    /// </summary>
    private async Task<McpToolResponse> HandleDiagAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
        var buildTime = System.IO.File.GetLastWriteTimeUtc(assembly.Location);
        var psExePath = (_psExecutor as PowerShellExecutor)?.ExecutablePath ?? "unknown";

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

        var psTests = new List<Dictionary<string, object?>>();

        psTests.Add(await RunPsTestAsync("basic-output", "Write-Output 'test-ok'", ct));

        psTests.Add(await RunPsTestAsync("hashtable-json", "@{name='test';value=42} | ConvertTo-Json", ct));

        psTests.Add(await RunPsTestAsync("admin-check",
            "$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent()); Write-Output \"IsAdmin: $($p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))\"", ct));

        psTests.Add(await RunPsTestAsync("get-vm", "try { $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop); Write-Output \"VMs: $(@($vms).Count)\" } catch { Write-Output \"GetVM-Error: $($_.Exception.Message)\" }", ct));

        psTests.Add(await RunPsTestAsync("get-vm-with-import",
            "Import-Module Hyper-V -ErrorAction Stop; try { $vms = @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop); Write-Output \"VMs: $(@($vms).Count)\" } catch { Write-Output \"GetVM-Error: $($_.Exception.Message)\" }", ct));

        psTests.Add(await RunPsTestAsync("get-vm-name-with-import",
            "Import-Module Hyper-V -ErrorAction Stop; $vm = Get-VM -Name 'nonexistent-test' -ComputerName localhost -ErrorAction SilentlyContinue; Write-Output \"Found: $($vm -ne $null)\"", ct));

        psTests.Add(await RunPsTestAsync("new-vhd-check",
            "Import-Module Hyper-V -ErrorAction Stop; Write-Output \"New-VHD available: $(Get-Command New-VHD -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)\"", ct));

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

        psTests.Add(await RunPsTestAsync("hyperv-module", "$m = Get-Module -ListAvailable -Name Hyper-V; Write-Output \"Module: $($m.Version)\"", ct));

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

        var envDiag = new Dictionary<string, object?>
        {
            ["HYPERV_MCP_BASE_VHDX"] = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX"),
            ["HYPERV_MCP_STORAGE_ROOT"] = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT"),
        };

        // Child-process diagnostics cannot show the in-process host's initialization state.
        // Expose its cached failure to explain divergent probe results; GetInitDiagnostics redacts all string fields.
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
                    ["startupState"] = diag.StartupState,
                    ["startupDetail"] = diag.StartupDetail,
                    ["startupElapsedSeconds"] = diag.StartupElapsedSeconds,
                    ["startupProgress"] = diag.StartupProgress,
                    ["childIdentity"] = diag.ChildIdentity,
                    // per-edition attempt detail (PS7 + PS5.1) so vm_diag
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

                // project the latest mutation record (if any) as a small
                // nested dictionary with timestamp + offending hashes.
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
                    // sidecar persistence telemetry.
                    ["sidecarHits"] = sidecarStats.SidecarHits,
                    ["sidecarWrites"] = sidecarStats.SidecarWrites,
                    ["sidecarDiscards"] = sidecarStats.SidecarDiscards,
                    ["lastMutationDetected"] = lastMutationDetected,
                    ["warmUpPaths"] = report is null
                        ? Array.Empty<object>()
                        // WarmAsync appends in order, so TakeLast shows the 100 most recent paths rather than boot-time entries.
                        // Each cycle replaces the report, keeping this ordering local to that cycle.
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
            // diagVersion is a capability marker: v12 adds sidecar hit/write/discard and mutation telemetry;
            // v11 introduced warm-up status, v10 spill-file diagnostics.
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

    /// <summary> (Phase 2 Gate 3 Loopback #4): flatten a <see cref="PowerShellEditionAttempt"/> snapshot into a
    /// JSON-serializable dictionary for inclusion in <c>vm_diag.phase2Host</c>. Returns <c>null</c> when the attempt
    /// itself is <c>null</c> (i.e. that edition was never entered). </summary>
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

    /// <summary>Wire states are not-started, in-progress, completed and cancelled. Null means not-started.
    /// Partial maps to completed because the cycle finished; Failed maps to cancelled because it did not.
    /// Per-path status/errorCode/errorMessage retain individual failures.</summary>
    private static string SerializeWarmUpStatus(WarmUpStatus? status) => status switch
    {
        null => "not-started",
        WarmUpStatus.NotStarted => "not-started",
        WarmUpStatus.InProgress => "in-progress",
        WarmUpStatus.Completed => "completed",
        WarmUpStatus.Partial => "completed", // see XML doc above for mapping rationale
        WarmUpStatus.Cancelled => "cancelled",
        WarmUpStatus.Failed => "cancelled", // see XML doc above for mapping rationale
        _ => "not-started",
    };

    /// <summary>Stable per-path kebab-case states retain already-warm versus warmed-fresh for cold-start triage.
    /// Succeeded remains for callers that do not distinguish cache hits from fresh computes.</summary>
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
        // Strict booleans reject non-canonical input with INVALID_PARAMETER. Default hashing detects mutation;
        // opting out accepts a ReadOnly-only guard, as the tool description warns.
        var verifyBaseImageHash = GetStrictBoolArg(args, "verifyBaseImageHash", true);
        // Optional; when absent the pre-existing no-password contract applies unchanged. Never
        // logged, never stored — it flows per call only.
        var adminPassword = GetStringArg(args, "adminPassword");
        // Present-but-unusable MUST fail fast rather than silently degrade to the no-password path
        // or fail late after artifacts exist.
        if (adminPassword is not null)
        {
            InputValidation.ValidateAdminPassword(adminPassword);
        }

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);

        // The linked request timeout defaults to 120s and is environment-overridable to accommodate cold SHA-256 verification.
        // Sidecars reduce repeat-call cost; the inner PowerShell budget stays 600s.
        var vmCreateTimeoutSeconds = ResolveVmCreateTimeoutSeconds();
        using var vmCreateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Password-path transport time must cover pre-boot work plus a full readiness window, not their maximum.
        // Otherwise creation consumes readiness time and turns a preserve-VM result into cancellation rollback.
        var transportDeadlineSeconds = string.IsNullOrEmpty(adminPassword)
            ? vmCreateTimeoutSeconds
            : vmCreateTimeoutSeconds
              + Math.Max(HyperVManager.MinimumReadinessLimitSeconds, vmCreateTimeoutSeconds)
              + PasswordPathTransportSlackSeconds;
        vmCreateCts.CancelAfter(TimeSpan.FromSeconds(transportDeadlineSeconds));

        var vmInfo = string.IsNullOrEmpty(adminPassword)
            ? await _hyperVManager.CreateVmAsync(
                hostId, name, baseVhdxPath, cpuCount, memoryMB, autoStart,
                verifyBaseImageHash, vmCreateCts.Token)
            : await _hyperVManager.CreateVmAsync(
                hostId, name, baseVhdxPath, cpuCount, memoryMB, autoStart,
                verifyBaseImageHash, adminPassword, vmCreateTimeoutSeconds, vmCreateCts.Token);
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary> resolve the per-call <c>vm_create</c> transport timeout. Reads
    /// <c>HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS</c>, validates inclusive range 60..600, falls back to 120 on missing /
    /// invalid / out-of-range (logs a warning for invalid). Pure helper — no side effects beyond logging. </summary>
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
    /// fix: Acquires global slot + per-host lock + per-VM lock for lifecycle operations.
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
    /// fix: Acquires global slot + per-host lock + per-VM lock for lifecycle operations.
    /// </summary>
    private async Task<McpToolResponse> HandleStopAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        // (#63): strict boolean parsing — a non-canonical 'force' value
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
    /// fix: Acquires global slot + per-host lock + per-VM lock.
    /// Adding VM lock ensures destroy cannot start while a command is executing on the VM.
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

        // Evict sessions before destroy, under the same VM lock, so concurrent calls cannot recreate them or leave orphaned runspaces.
        // Eviction is best-effort: failures must not block the requested VM destruction.
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
            // Eviction is best-effort; the channel redacts and logs failures internally, and VM destruction takes priority.
        }

        await _hyperVManager.DestroyVmAsync(hostId, vmId, ct).ConfigureAwait(false);

        // A destroyed-then-recreated VM id would otherwise inherit the destroyed guest's OS
        // classification and be routed over a transport the new guest does not speak.
        (_channel as GuestChannelRouter)?.ForgetGuestClassification(hostId, vmId);
        _guestRoutingHintStore?.Remove(hostId, vmId);

        return McpToolResponse.Ok(new { vmId, destroyed = true });
    }

    /// <summary>
    /// Handler for vm_list: lists VMs on a host with optional name filtering.
    /// Acquires global slot only (read-only, no per-VM/host lock needed).
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

    private async Task<McpToolResponse> HandleFindByNameAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var name = GetRequiredStringArg(args, "name");
        var caseSensitive = GetStrictBoolArg(args, "caseSensitive", false);

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        var vms = await _hyperVManager.FindVmsByNameAsync(hostId, name, caseSensitive, ct);
        var matches = vms.Select(vm => new { vmId = vm.VmId, name = vm.Name, state = vm.State }).ToArray();
        return McpToolResponse.Ok(new { matches, count = matches.Length });
    }

    /// <summary>
    /// Handler for vm_status: gets detailed status for a specific VM.
    /// Acquires global slot only (read-only operation).
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

    /// <summary> Handler for vm_run_command: executes a command on a guest VM. Acquires global slot + per-VM lock to
    /// serialize commands on the same VM's PSSession. fix: Timed-out and cancelled commands now return success: false
    /// with appropriate error codes instead of wrapping in McpToolResponse.Ok. </summary>
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

        // Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _commandExecutor.ExecuteCommandAsync(hostId, vmId, command, shell, timeoutSeconds, username, password, ct);

        if (result.TimedOut)
        {
            return new McpToolResponse
            {
                Success = false,
                Error = $"Command timed out after {result.DurationMs}ms",
                ErrorCode = ErrorCodes.CommandTimeout,
                Data = result, // Include partial output
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

        // Non-zero exits must fail too, not just timeouts and cancellation.
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
    /// </summary>
    private async Task<McpToolResponse> HandleCopyFileAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var sourcePath = GetRequiredStringArg(args, "sourcePath");
        var destPath = GetRequiredStringArg(args, "destPath");
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        // (#63): strict boolean parsing — non-canonical 'isDirectory'
        // values are rejected with INVALID_PARAMETER, matching the cure already
        // applied to vm_cleanup_orphans.dryRun and vm_create.autoStart.
        var isDirectory = GetStrictBoolArg(args, "isDirectory", false);
        var username = GetStringArg(args, "username");
        var password = GetStringArg(args, "password");

        // Validate the local source first so invalid source and VM together produce FILE_NOT_FOUND, not VM_NOT_FOUND.
        // Remote file transfer is unsupported.
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

        // Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _fileTransferService.CopyToGuestAsync(hostId, vmId, sourcePath, destPath, isDirectory, username, password, ct);
        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_list_images: lists available base VHDX images on a host.
    /// Acquires global slot only (read-only operation).
    /// </summary>
    private async Task<McpToolResponse> HandleListImagesAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);

        // Unconfigured images return soft success (Configured=false); unreadable storage throws IO_ERROR,
        // while a missing configured path throws INVALID_PARAMETER.
        var result = await _hyperVManager.ListImagesAsync(hostId, ct);
        return McpToolResponse.Ok(result);
    }

    // P1 Tool Handlers — Batch 1

    /// <summary> Handler for vm_run_script: executes a multi-line script on a guest VM. Acquires global slot + per-VM
    /// lock to serialize scripts on the same VM's PSSession. Timed-out, cancelled, and non-zero exit code scripts
    /// return success: false with appropriate error codes, same pattern as HandleRunCommandAsync. </summary>
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

        // Check VM state before attempting session acquisition.
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

        // Check VM state before attempting session acquisition.
        await EnsureVmRunningAsync(hostId, vmId, ct);

        var result = await _fileTransferService.CopyFromGuestAsync(hostId, vmId, sourcePath, destPath, username, password, ct);
        return McpToolResponse.Ok(result);
    }

    /// <summary>
    /// Handler for vm_restart: restarts a VM (stop + start as atomic operation).
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle operation).
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

    // P2 Tool Handlers — Pause/Resume

    /// <summary>
    /// Handler for vm_pause: pauses a running VM.
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle operation).
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

    /// <summary> Handler for vm_configure: modifies VM CPU and/or memory configuration. At least one of
    /// <c>cpuCount</c> or <c>memoryMB</c> must be supplied; otherwise an <see cref="ArgumentException"/> is thrown
    /// and mapped to INVALID_PARAMETER. Acquires global slot + per-host lock + per-VM lock (lifecycle/configuration
    /// operation). </summary>
    private async Task<McpToolResponse> HandleConfigureAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;

        var cpuCount = GetOptionalIntArg(args, "cpuCount");
        var memoryMB = GetOptionalLongArg(args, "memoryMB");

        // Validate ranges before PowerShell to return stable INVALID_PARAMETER envelopes, not opaque script errors.
        // Host-specific upper limits remain PowerShell's responsibility.
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

    // P1 Tool Handlers — Batch 2

    private async Task<McpToolResponse> HandleWaitReadyAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        var budget = new ReadinessBudget(GetIntArg(args, "timeoutSeconds", 300), _readinessClock);
        var rawVmId = GetRequiredStringArg(args, "vmId");
        var vmId = InputValidation.ValidateVmId(rawVmId);
        var hostId = GetStringArg(args, "hostId") ?? _options.DefaultHostId;
        var (username, password) = CredentialResolver.ResolveCredentials(
            GetStringArg(args, "username"), GetStringArg(args, "password"));

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        var vmLockTimeout = TimeSpan.FromSeconds(_options.VmLockTimeoutSeconds);
        budget.Check("global-slot", vmId, ct);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        budget.Record("global-slot-acquired");
        budget.Check("vm-lock", vmId, ct);
        using var vmLock = await _concurrencyGate.AcquireVmLockAsync(hostId, vmId, vmLockTimeout, ct);
        budget.Record("vm-lock-acquired");
        budget.Check("manager-entry", vmId, ct);

        var vmInfo = await _hyperVManager.WaitForReadyAsync(hostId, vmId, budget, username, password, ct);
        ct.ThrowIfCancellationRequested();
        return McpToolResponse.Ok(vmInfo);
    }

    /// <summary>
    /// Handler for vm_checkpoint: manages checkpoint operations (create, restore, list, delete).
    /// Acquires global slot + per-host lock + per-VM lock (lifecycle-grade operation).
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
    /// </summary>
    private async Task<McpToolResponse> HandleCleanupOrphansAsync(
        Dictionary<string, object?> args, CancellationToken ct)
    {
        // Validate early so bad arguments become INVALID_PARAMETER through ErrorMapper, not opaque SDK errors.
        // Strict boolean failures carry the parameter name for SafeArgumentMessage.
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

        // Rethrow unchanged so centralized ErrorMapper sanitizes unknown failures into INTERNAL_ERROR.
        // Log raw type/message/stack through the structured logger first so operators retain the cause.
        // Known typed exceptions need no extra log; their mapper branches already handle them.
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
            // Structured error logging preserves type/message/stack before client-envelope sanitization.
            // Unlike Console.Error, it respects severity filters and configured sinks; keep redaction before rethrow.
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

            // Do not rewrap raw type/message in InvalidOperationException: that bypasses generic sanitization.
            // Rethrow unchanged for the sanitized INTERNAL_ERROR envelope.
            throw;
        }

        // Only orphan-candidates may be destroyed; needs-attention stays report-only.
        // Label the action by actual destruction so a report-only non-dry-run result is not mislabeled.
        var anyDestroyed = !dryRun && orphans.Any(o => o.Reason == "orphan-candidate");
        return McpToolResponse.Ok(new
        {
            orphans,
            count = orphans.Count,
            dryRun,
            action = anyDestroyed ? "destroyed" : "detected",
        });
    }

    // P1 Tool Handlers — ISO Installation

    /// <summary>
    /// Handler for vm_os_install: installs Windows from an ISO image in a single call.
    /// Acquires global slot + per-host lock (lifecycle operation creating a new VM).
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
        // optional escape hatch for the C#-side resource-floor preflight.
        // Strict bool parsing — non-bool values are an INVALID_PARAMETER, not silently coerced.
        var skipPreflight = GetStrictBoolArg(args, "skipPreflight", false);
        // optional initial Ubuntu login user (Windows ignores it). Its
        // charset validation runs inside OsInstallAsync only on the Ubuntu branch.
        var guestUsername = GetStringArg(args, "guestUsername");

        var queueTimeout = TimeSpan.FromSeconds(_options.QueueTimeoutSeconds);
        using var globalSlot = await _concurrencyGate.AcquireGlobalSlotAsync(queueTimeout, ct);
        using var hostLock = await _concurrencyGate.AcquireHostLockAsync(hostId, queueTimeout, ct);

        var result = await _hyperVManager.OsInstallAsync(
            hostId, name, isoPath, adminPassword,
            cpuCount, memoryMB, diskSizeGB, switchName,
            locale, windowsEdition, productKey,
            timeoutMinutes, skipPreflight, guestUsername, ct);
        return McpToolResponse.Ok(result);
    }

    // P2 Tool Handlers — vm_create_base_image

    /// <summary>Under global/host/VM locks, require Running (otherwise VM_NOT_RUNNING), optionally merge checkpoints
    /// (MERGE_NOT_SUPPORTED/CHECKPOINT_MERGE_FAILED), then run sysprep /generalize /oobe /shutdown /quiet through the guest channel.
    /// Wait for Off within shutdownTimeoutSeconds (otherwise SYSPREP_FAILED); resolve the primary VHDX and copy host-side without overwrite.
    /// Missing ImageDirectory or copy failure yields IMAGE_COPY_FAILED; return ImageInfo with Generalized=true.</summary>
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

        MergeResult? mergeOutcome = null;
        if (mergeCheckpoints)
        {
            // MergeAllAsync throws MergeNotSupportedException / CheckpointMergeFailedException;
            // those propagate to ErrorMapper and yield the correct error codes.
            mergeOutcome = await _checkpointManager.MergeAllAsync(hostId, vm.VmId, ct).ConfigureAwait(false);
        }

        var (resolvedUser, resolvedPass) = CredentialResolver.ResolveCredentials(username, password);

        // The guest script surfaces non-zero sysprep exits as throws mapped to SYSPREP_FAILED.
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

        // Terminating guest errors can return Success=false/ExitCode=1 instead of throwing.
        // Check now or the Off-state poll would misreport a sysprep failure as shutdown timeout.
        if (!sysprepResult.Success)
        {
            var reason = string.IsNullOrWhiteSpace(sysprepResult.Stderr)
                ? $"exit code {sysprepResult.ExitCode?.ToString() ?? "n/a"}"
                : sysprepResult.Stderr.Trim();
            throw new SysprepFailedException(hostId, vm.VmId,
                $"In-guest sysprep reported failure for VM '{vmName}': {reason}");
        }

        // Check before delaying and cap delays to the remaining budget so short timeouts are honored,
        // within one status RPC of shutdownTimeoutSeconds.
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

        // Copy host-side, not through IFileTransferService.
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

        // Keep Generalized on ImageInfo for the public contract; provenance (sourceVm*, mergedCheckpointCount,
        // checkpointsMerged) belongs on its envelope, not as anonymous-object siblings.
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
