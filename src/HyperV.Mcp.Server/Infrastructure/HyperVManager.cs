using System.Diagnostics;
using System.Text.Json;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>Local-only VM lifecycle via PowerShell JSON scripts; WinRM is deferred.
/// vm_create atomically creates and starts the VM; bootstrap is deferred. vm_destroy uses hard power-off.
/// VM Notes carry "hyper-v-mcp:created={ISO8601};role=ephemeral".</summary>
public class HyperVManager : IHyperVManager
{
    private readonly IPowerShellExecutor _psExecutor;
    private readonly IHostResolver _hostResolver;
    private readonly ServerOptions _options;
    private readonly ILogger<HyperVManager> _logger;
    private readonly IIsoInspector _isoInspector;

    // Optional constructor dependencies preserve direct-construction fixtures; production DI supplies real singletons.
    private readonly IGuestOsClassifier _guestOsClassifier;
    private readonly IUbuntuAutoinstallOrchestrator _ubuntuOrchestrator;
    private readonly IGuestRoutingHintStore _guestRoutingHintStore;

    // optional in the constructor so direct-construction test fixtures keep compiling;
    // production DI supplies the real singletons.
    private readonly IBaseImageGeneralizationProbe _generalizationProbe;
    private readonly IUnattendSeeder _unattendSeeder;
    private readonly IGuestReadinessPoller _readinessPoller;
    private readonly IGuestOsProbe _readinessOsProbe;
    private readonly ReadinessAuthenticator _readinessAuthenticator;
    private readonly TimeProvider _readinessClock;
    private readonly Func<string, int, CancellationToken, Task<bool>> _readinessBannerProbe;

    /// <summary>
    /// A password-bearing first boot is slower than an unseeded one, so the readiness limit is
    /// never allowed below this floor even when the create budget is lower.
    /// </summary>
    internal const int MinimumReadinessLimitSeconds = 150;

    /// <summary>
    /// Slack over the readiness limit before the poll's own token fires, so the script reports its
    /// own DEADLINE verdict instead of being cancelled out from under it.
    /// </summary>
    private const int ReadinessCancellationGraceSeconds = 60;

    /// <summary>
    /// Default storage root when not configured via environment variable or host profile.
    /// </summary>
    private const string DefaultStorageRoot = @"C:\HyperVMCP\VMs";

    /// <summary>
    /// Secondary fallback only: names a numeric <c>State</c> that still arrives from a legacy or
    /// cached payload. Values are the measured <c>Microsoft.HyperV.PowerShell.VMState</c> ordinals,
    /// NOT the CIM <c>EnabledState</c> vocabulary the earlier table wrongly encoded.
    /// </summary>
    private static readonly Dictionary<int, string> LegacyNumericVmStateFallback = new()
    {
        { 2, "Running" },
        { 3, "Off" },
        { 4, "Stopping" },
        { 6, "Saved" },
        { 9, "Paused" },
        { 10, "Starting" },
        { 32773, "Saving" },
        { 32776, "Pausing" },
    };

    private readonly IFileSystemProbe _fileSystemProbe;
    private readonly IBaseImageHashCache? _baseImageHashCache;

    /// <summary>
    /// Default rollback budget (seconds). Overridable via
    /// <c>HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS</c>.
    /// </summary>
    private const int DefaultRollbackBudgetSeconds = 30;

    /// <summary>Environment variable for the rollback budget.</summary>
    private const string RollbackBudgetEnvVar = "HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS";

    /// <summary>Shared projection for Start/Stop/Restart/Pause/Resume/Configure/GetVmStatus; the helper supplies the pipe.
    /// Tests require the literal <c>MemoryStartup/1MB</c>. State names avoid private VMState ordinals, which have drifted.</summary>
    private const string VmInfoProjection =
        "Select-Object Id, Name, ProcessorCount, "
        + "@{N='State';E={[string]$_.State}}, "
        + "@{N='MemoryMB';E={$_.MemoryStartup/1MB}}, "
        + "@{N='UptimeSeconds';E={$_.Uptime.TotalSeconds}}";

    public HyperVManager(
        IPowerShellExecutor psExecutor,
        IHostResolver hostResolver,
        ServerOptions options,
        ILogger<HyperVManager> logger,
        IIsoInspector isoInspector,
        IFileSystemProbe? fileSystemProbe = null,
        IBaseImageHashCache? baseImageHashCache = null,
        IGuestOsClassifier? guestOsClassifier = null,
        IUbuntuAutoinstallOrchestrator? ubuntuOrchestrator = null,
        IGuestRoutingHintStore? guestRoutingHintStore = null,
        IBaseImageGeneralizationProbe? generalizationProbe = null,
        IUnattendSeeder? unattendSeeder = null,
        IGuestReadinessPoller? readinessPoller = null,
        IGuestOsProbe? guestOsProbe = null,
        IPowerShellHost? psHost = null,
        ISshExecClientFactory? sshExecClientFactory = null,
        TimeProvider? readinessClock = null,
        Func<string, int, CancellationToken, Task<bool>>? readinessBannerProbe = null)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isoInspector = isoInspector ?? throw new ArgumentNullException(nameof(isoInspector));
        _readinessClock = readinessClock ?? TimeProvider.System;
        _readinessOsProbe = guestOsProbe
            ?? new KvpGuestOsProbe(psExecutor, hostResolver, NullLogger<KvpGuestOsProbe>.Instance);
        _readinessAuthenticator = new ReadinessAuthenticator(
            psHost, sshExecClientFactory ?? new SshExecClientFactory(), logger);
        _readinessBannerProbe = readinessBannerProbe ?? KvpGuestOsProbe.RespondsWithSshBannerAsync;
        // default the classifier and Ubuntu orchestrator to production
        // implementations when not injected, so direct-construction test fixtures keep working.
        _guestOsClassifier = guestOsClassifier
            ?? new GuestOsClassifier(_isoInspector, NullLogger<GuestOsClassifier>.Instance);
        // Share the hint store so installer hints reach the router and SshSessionStore.
        // The isolated fallback preserves source compatibility but warns: silent isolation can hide broken routing in tests.
        if (guestRoutingHintStore is null)
        {
            _logger.LogWarning(
                "HyperVManager was constructed without a shared IGuestRoutingHintStore; install-time guest-routing hints will be recorded into an isolated store and will NOT reach GuestChannelRouter or SshSessionStore.");
        }
        _guestRoutingHintStore = guestRoutingHintStore ?? new GuestRoutingHintStore();
        _ubuntuOrchestrator = ubuntuOrchestrator
            ?? new UbuntuAutoinstallOrchestrator(
                _psExecutor,
                new KvpCompletionReader(_psExecutor, NullLogger<KvpCompletionReader>.Instance),
                _hostResolver,
                new SystemTempPathProvider(),
                _guestRoutingHintStore,
                new SeedMediaAuthor(
                    _psExecutor,
                    new OscdimgProbe(new SystemEnvironment()),
                    NullLogger<SeedMediaAuthor>.Instance),
                NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
        // The production fallback preserves existing constructor call sites; DI supplies the registered singleton.
        _fileSystemProbe = fileSystemProbe ?? new FileSystemProbe();
        // The optional cache preserves existing direct-construction fixtures.
        _baseImageHashCache = baseImageHashCache;
        _generalizationProbe = generalizationProbe
            ?? new BaseImageGeneralizationProbe(_psExecutor, NullLogger<BaseImageGeneralizationProbe>.Instance);
        _unattendSeeder = unattendSeeder
            ?? new UnattendSeeder(_psExecutor, NullLogger<UnattendSeeder>.Instance);
        _readinessPoller = readinessPoller
            ?? new GuestReadinessPoller(_psExecutor, NullLogger<GuestReadinessPoller>.Instance);

        _logger.LogInformation("Base VHDX mutation guard active (ADR-4 / ST-D6 + ST-D6a + VC-D8): ReadOnly attribute + force-recomputed post-create SHA-256.");
    }

    /// <inheritdoc />
    /// <remarks>Atomically creates a VM with a differencing disk and ownership Notes; autoStart defaults to false (Off).
    /// Full bootstrap (OsBooting → OsReady → ShellReady → NetworkReady → AppReady) is deferred; use vm_wait_ready or manual delays before commands.
    /// Start failure cleans up the VM and VHDX to prevent orphans.</remarks>
    public Task<VmInfo> CreateVmAsync(
        string hostId,
        string name,
        string? baseVhdxPath = null,
        int cpuCount = 2,
        long memoryMB = 4096,
        bool autoStart = false,
        bool verifyBaseImageHash = true,
        CancellationToken ct = default)
        => CreateVmAsync(hostId, name, baseVhdxPath, cpuCount, memoryMB, autoStart,
            verifyBaseImageHash, adminPassword: null, createTimeBudgetSeconds: 0, ct);

    /// <inheritdoc />
    public async Task<VmInfo> CreateVmAsync(
        string hostId,
        string name,
        string? baseVhdxPath,
        int cpuCount,
        long memoryMB,
        bool autoStart,
        bool verifyBaseImageHash,
        string? adminPassword,
        int createTimeBudgetSeconds,
        CancellationToken ct)
    {
        // Validated here rather than only at the dispatcher, so a direct IHyperVManager caller
        // cannot smuggle a whitespace-only or too-short password past the same rule.
        if (adminPassword is not null)
        {
            InputValidation.ValidateAdminPassword(adminPassword);
        }

        var passwordSupplied = adminPassword is not null;

        // Readiness starts at first power-on, so preflight, hashing, cloning and seeding use a separate pre-boot budget.
        // The caller token bounds both phases; ConfirmLoginReadyAsync gets a full readiness window. No-password behavior is unchanged.
        var callerCt = ct;
        using var preBootCts = passwordSupplied && createTimeBudgetSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (preBootCts is not null)
        {
            preBootCts.CancelAfter(TimeSpan.FromSeconds(createTimeBudgetSeconds));
            ct = preBootCts.Token;
        }

        // A password-bearing success is defined as login-ready, which cannot be confirmed on a
        // powered-off VM — so the caller's autoStart is overridden upward, never downward.
        var effectiveAutoStart = autoStart || passwordSupplied;

        // The primary script must NOT start the VM on the password path: the answer file is written
        // to the diff disk after the script returns and must be in place before first boot. The
        // start is re-issued once seeding succeeds, so effectiveAutoStart still holds.
        var startInPrimaryScript = effectiveAutoStart && !passwordSupplied;
        // Structured stage logs replace the env-gated trace. Serilog honors @ destructuring; the default console
        // formatter uses the literal property name @phaseData, which downstream queries must match.
        // Stopwatches stay unconditional so elapsedMs and per-stage durations are always populated.
        var __traceSw = Stopwatch.StartNew();
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
            "entry", __traceSw.ElapsedMilliseconds, name);

        // Default hashing uses the warm pre-hash and an unconditional post-create recompute to detect preserved-stat mutations;
        // a mismatch is BASE_IMAGE_MUTATED. Opting out skips both hashes and accepts a ReadOnly-attribute-only guard.
        var hostProfile = ResolveLocalHost(hostId);

        var baseVhdx = baseVhdxPath
            ?? Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX")
            ?? hostProfile.BaseVhdxPath;

        if (string.IsNullOrWhiteSpace(baseVhdx))
        {
            throw new InvalidOperationException(
                "No base VHDX path specified. Provide via parameter, HYPERV_MCP_BASE_VHDX environment variable, or host profile configuration.");
        }

        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;

        // Pre-compute host-side artifact paths so the rollback path can target them
        // even on cancellation (the PowerShell child may be killed before it can
        // surface its own copies of these strings).
        var vmDir = Path.Combine(storageRoot, name);
        var diffPath = Path.Combine(vmDir, $"{name}.vhdx");

        var escapedName = EscapePowerShellString(name);
        var escapedBaseVhdx = EscapePowerShellString(baseVhdx);
        var escapedStorageRoot = EscapePowerShellString(storageRoot);

        _logger.LogInformation("Creating VM '{VmName}' on host '{HostId}' with {CpuCount} CPUs, {MemoryMB}MB RAM",
            name, hostId, cpuCount, memoryMB);
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "validated", __traceSw.ElapsedMilliseconds, name,
            new { baseVhdx, storageRoot });

        // Probe before mutations or hashing to reject duplicates quickly with VM_ALREADY_EXISTS.
        // No artifacts exist and rollback is not armed, so a pre-existing VM cannot be deleted.
        if (await VmExistsOnHostAsync(name, ct).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                "duplicate-shortcircuit", __traceSw.ElapsedMilliseconds, name);
            _logger.LogInformation(
                "vm_create rejected duplicate name '{VmName}' on host '{HostId}' via pre-create probe (no artifacts created).",
                name, hostProfile.HostId);
            throw new VmAlreadyExistsException(hostProfile.HostId, name);
        }

        // Password requests require positively confirmed generalization before any artifacts exist.
        // Negative and indeterminate results both reject without rollback.
        if (passwordSupplied)
        {
            var generalizationState = await _generalizationProbe
                .ProbeAsync(baseVhdx, ct).ConfigureAwait(false);

            if (generalizationState != BaseImageGeneralizationState.Generalized)
            {
                var indeterminate = generalizationState == BaseImageGeneralizationState.Indeterminate;
                _logger.LogInformation(
                    "vm_create rejected password-bearing request for '{VmName}': base image generalization verdict was {Verdict} (no artifacts created).",
                    name, generalizationState);
                throw new BaseImageNotGeneralizedException(
                    indeterminate
                        ? "The base image could not be confirmed to be sysprepped/generalized, so the supplied administrator password cannot be applied. Retry with a base image you have confirmed is generalized, or create one with vm_create_base_image."
                        : "The base image is not sysprepped/generalized, so the supplied administrator password cannot be applied. Retry with a generalized base image, or create one with vm_create_base_image.",
                    indeterminate);
            }
        }

        // Warm stat-tuple hits avoid hashing; cold hashes cache for 24h (configurable) before the PowerShell pipeline.
        // Cancellation leaves the cache unpopulated for retry. Snapshot FileInfo primitives now: lazy reads could otherwise
        // capture post-create metadata at both endpoints and hide mutation.
        string? preHash = null;
        FileStatSnapshot? preStat = null;
        if (File.Exists(baseVhdx))
        {
            var fi = new FileInfo(baseVhdx);
            // Refresh before copying primitives so the snapshot uses one OS round-trip, not later lazy reads.
            fi.Refresh();
            preStat = new FileStatSnapshot(
                Length: fi.Length,
                LastWriteTimeUtc: fi.LastWriteTimeUtc,
                IsReadOnly: fi.IsReadOnly);
        }
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "pre-stat-snapshot", __traceSw.ElapsedMilliseconds, name,
            new { preStatCaptured = preStat is not null });
        try
        {
            // opt-out path: skip pre-hash entirely when caller asked us
            // to. Mutation guard reduces to the ReadOnly-attribute check (still
            // enforced inside the PowerShell BuildBaseVhdxGuardScript helper).
            if (_baseImageHashCache is not null && verifyBaseImageHash)
            {
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                    "pre-hash-start", __traceSw.ElapsedMilliseconds, name);
                var __preHashSw = Stopwatch.StartNew();
                // Race cancellation so this handler promptly surfaces -32001 while the lifetime-token hash continues.
                // Later callers share its per-path semaphore and can reuse the result.
                var cacheTask = _baseImageHashCache.GetOrComputeAsync(baseVhdx, ct);

                if (ct.CanBeCanceled)
                {
                    var completed = await Task.WhenAny(
                        cacheTask,
                        Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);

                    if (completed != cacheTask)
                    {
                        // Inbound CT fired; surface -32001 but DO NOT observe cacheTask
                        // (the detached compute remains in flight inside the cache).
                        ct.ThrowIfCancellationRequested();
                    }
                }

                preHash = await cacheTask.ConfigureAwait(false);
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                    "pre-hash-end", __traceSw.ElapsedMilliseconds, name,
                    new { preHashStageMs = __preHashSw.ElapsedMilliseconds, preHashLen = preHash?.Length ?? 0 });
            }
            else
            {
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                    "pre-hash-skipped", __traceSw.ElapsedMilliseconds, name,
                    new { cacheWired = _baseImageHashCache is not null, verifyBaseImageHash });
            }
            // Without a cache (legacy fixtures), hashing is skipped and the guard is ReadOnly-only;
            // PowerShell no longer calls Get-FileHash. Production DI always wires the cache.
        }
        catch (FileNotFoundException fnf)
        {
            throw new InvalidOperationException(
                $"Base VHDX not found at '{baseVhdx}'. Verify the path or HYPERV_MCP_BASE_VHDX configuration.", fnf);
        }

        // Hashing stays host-side; rollback uses a separate detached-token PowerShell call.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$name = '{escapedName}'
$baseVhdx = '{escapedBaseVhdx}'
$storageRoot = '{escapedStorageRoot}'
$memoryBytes = {memoryMB} * 1MB
$cpuCount = {cpuCount}
$autoStart = {(startInPrimaryScript ? "$true" : "$false")}

# WMI workaround : -ComputerName localhost avoids null-name WMI bug
$existing = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
if ($existing) {{
    throw ""VM with name '$name' already exists""
}}

$vmDir = Join-Path $storageRoot $name
$diffPath = Join-Path $vmDir ""$name.vhdx""

if (-not (Test-Path -LiteralPath $vmDir)) {{
    New-Item -ItemType Directory -Path $vmDir -Force | Out-Null
}}

{BuildBaseVhdxGuardScript(escapedBaseVhdx)}

# WMI workaround : -ComputerName localhost avoids null-name WMI bug
New-VHD -Path $diffPath -ParentPath $baseVhdx -Differencing -ComputerName localhost | Out-Null

New-VM -Name $name -Generation 2 -MemoryStartupBytes $memoryBytes -VHDPath $diffPath -ComputerName localhost | Out-Null
Set-VM -Name $name -ProcessorCount $cpuCount -ComputerName localhost
Set-VM -Name $name -Notes ""hyper-v-mcp:created=$(Get-Date -Format o);role=ephemeral"" -ComputerName localhost
if ($autoStart) {{ Start-VM -Name $name -ComputerName localhost }}

$vm = Get-VM -Name $name -ComputerName localhost
$vm | Select-Object Id, Name, State, ProcessorCount, @{{N='MemoryMB';E={{$_.MemoryStartup/1MB}}}}, @{{N='UptimeSeconds';E={{$_.Uptime.TotalSeconds}}}} | ConvertTo-Json
";

        // Update phase before each boundary so rollback reports the failure location in details.phase.
        string phase = "create";
        Exception? primaryFailure = null;
        PowerShellResult? primaryResult = null;
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "ps-exec-start", __traceSw.ElapsedMilliseconds, name,
            new { scriptLength = script.Length, timeoutSeconds = 600 });
        var __psSw = Stopwatch.StartNew();
        try
        {
            primaryResult = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 600, ct: ct).ConfigureAwait(false);
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                "ps-exec-end", __traceSw.ElapsedMilliseconds, name,
                new
                {
                    psStageMs = __psSw.ElapsedMilliseconds,
                    success = primaryResult.Success,
                    cancelled = primaryResult.Cancelled,
                    timedOut = primaryResult.TimedOut,
                    exitCode = primaryResult.ExitCode,
                    stdoutBytes = primaryResult.Stdout?.Length ?? 0,
                    stderrBytes = primaryResult.Stderr?.Length ?? 0,
                    psReportedDurationMs = primaryResult.DurationMs,
                });

            if (primaryResult.Cancelled)
            {
                primaryFailure = new OperationCanceledException("vm_create was cancelled by the caller.", ct);
            }
            else if (primaryResult.TimedOut)
            {
                primaryFailure = new TimeoutException(
                    $"vm_create exceeded the {primaryResult.DurationMs}ms PowerShell budget.");
            }
            else if (!primaryResult.Success)
            {
                primaryFailure = new InvalidOperationException(
                    $"vm_create PowerShell pipeline failed (exit code {primaryResult.ExitCode}): {primaryResult.Stderr}");
            }
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                "ps-exec-cancelled", __traceSw.ElapsedMilliseconds, name,
                new { psStageMs = __psSw.ElapsedMilliseconds });
            primaryFailure = oce;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                "ps-exec-threw", __traceSw.ElapsedMilliseconds, name,
                new { psStageMs = __psSw.ElapsedMilliseconds, exType = ex.GetType().Name, exMessage = ex.Message });
            primaryFailure = ex;
        }

        if (primaryFailure is null && primaryResult is not null)
        {
            // Always re-read post-create bytes: unchanged stat tuples can hide sparse overwrites, reset mtimes or storage bit flips.
            // Reusing the cached pre-hash would miss these mutations. Opting out leaves only the ReadOnly guard.
            try
            {
                if (verifyBaseImageHash && preHash is not null && _baseImageHashCache is not null)
                {
                    _logger.LogDebug(
                        "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                        "post-hash-start", __traceSw.ElapsedMilliseconds, name);
                    var __postHashSw = Stopwatch.StartNew();
                    var postHash = await _baseImageHashCache
                        .ForceRecomputeAsync(baseVhdx, ct)
                        .ConfigureAwait(false);
                    var __match = string.Equals(preHash, postHash, StringComparison.OrdinalIgnoreCase);
                    _logger.LogDebug(
                        "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                        "post-hash-end", __traceSw.ElapsedMilliseconds, name,
                        new { postHashStageMs = __postHashSw.ElapsedMilliseconds, match = __match });
                    if (!__match)
                    {
                        // Record mutation before throwing to delete the sidecar, evict the cache entry and expose lastMutationDetected.
                        // Recording is best-effort so failure cannot replace the rollback envelope.
                        try
                        {
                            _baseImageHashCache.RecordMutationDetected(
                                baseImagePath: baseVhdx,
                                vmName: name,
                                expectedSha256: preHash!,
                                actualSha256: postHash);
                        }
                        catch (Exception recordEx)
                        {
                            _logger.LogWarning(
                                recordEx,
                                "vm_create: RecordMutationDetected threw for {VmName}; continuing with BASE_IMAGE_MUTATED rollback.",
                                name);
                        }

                        primaryFailure = new InvalidOperationException(
                            $"BASE_IMAGE_MUTATED: Base VHDX was mutated during differencing disk creation! Pre={preHash} Post={postHash} Path={baseVhdx}");
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                        "post-hash-skipped", __traceSw.ElapsedMilliseconds, name,
                        new { verifyBaseImageHash, preHashSet = preHash is not null, cacheWired = _baseImageHashCache is not null });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
                    "post-hash-threw", __traceSw.ElapsedMilliseconds, name,
                    new { exType = ex.GetType().Name, exMessage = ex.Message });
                primaryFailure = ex;
            }
        }

        if (primaryFailure is null && primaryResult is not null)
        {
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                "success-parsing", __traceSw.ElapsedMilliseconds, name);
            var __parsed = ParseSingleVmInfo(primaryResult.Stdout ?? string.Empty, hostProfile.HostId);

            if (!passwordSupplied)
            {
                // The result must state whether a password was applied, including on the
                // no-password path.
                __parsed.PasswordApplied = false;
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                    "envelope-returned", __traceSw.ElapsedMilliseconds, name);
                return __parsed;
            }

            // Seed before first boot: only this window lets the guest consume the offline answer file.
            try
            {
                await _unattendSeeder
                    .SeedAsync(diffPath, name, adminPassword!, locale: "en-US", ct)
                    .ConfigureAwait(false);
                await StartVmForPasswordSeedingAsync(escapedName, name, ct).ConfigureAwait(false);
            }
            catch (Exception seedEx)
            {
                // A failure to seed or start is a genuinely-failed create: fall through to the
                // unchanged rollback.
                primaryFailure = seedEx;

                // Rollback is best-effort and may leave a residual VHDX behind. Seeding may already
                // have written the answer file, so it is removed BEFORE rollback — otherwise this
                // terminal outcome could leave a readable plaintext password artifact.
                var scrubbedOnFailure =
                    await StopThenScrubAsync(escapedName, name, diffPath).ConfigureAwait(false);
                if (!scrubbedOnFailure)
                {
                    // A silently-dropped scrub failure would report a plain create failure while a
                    // readable plaintext password artifact survives on disk; the caller must see it.
                    _logger.LogError(
                        "vm_create could not confirm removal of the administrator-password answer file for '{VmName}' after a failed seed/start.",
                        name);
                    primaryFailure = new InvalidOperationException(
                        $"VM '{name}' could not be created, and the answer file carrying the administrator password could not be confirmed removed from its disk.",
                        seedEx);
                }
            }

            if (primaryFailure is null)
            {
                // The window starts here — the guest has just powered on for the first time.
                await ConfirmLoginReadyAsync(
                    name, escapedName, diffPath, adminPassword!, createTimeBudgetSeconds, callerCt)
                    .ConfigureAwait(false);

                __parsed.PasswordApplied = true;
                __parsed.State = "Running";
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                    "envelope-returned", __traceSw.ElapsedMilliseconds, name);
                return __parsed;
            }
        }
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "failure-path-entry", __traceSw.ElapsedMilliseconds, name,
            new { failureType = primaryFailure?.GetType().Name, failureMessage = primaryFailure?.Message });

        // Infer ownership from stderr because the primary script is single-shot. A bare collision owns nothing;
        // New-VM/Set-VM/Start-VM errors imply VHDX ownership, but only Set-VM/Start-VM imply completed registration.
        // Never Remove-VM unless this call registered it.
        var isNameCollision = LooksLikeNameCollision(primaryFailure!, primaryResult);

        // A successful primary script followed by a failed host-side guard still owns the VM registration.
        // Rollback must remove it; treating this as an ambiguous script failure leaks the newly created VM.
        var primaryScriptSucceeded =
            primaryResult is not null
            && primaryResult.Success
            && !primaryResult.Cancelled
            && !primaryResult.TimedOut
            && string.IsNullOrWhiteSpace(primaryResult.Stderr);

        var created = InferCreatedArtifacts(
            primaryFailure!, primaryResult, diffPath, vmDir, isNameCollision,
            primaryScriptSucceeded);

        // A collision after the initial probe may belong to another client. Return VM_ALREADY_EXISTS, not COMMAND_FAILED,
        // and clean up only owned artifacts; never remove the colliding VM.
        if (isNameCollision)
        {
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                "duplicate-residual-race", __traceSw.ElapsedMilliseconds, name);
            _logger.LogDebug(
                primaryFailure,
                "Name-collision raw PS text suppressed from envelope for VM '{VmName}'.",
                name);

            if (created.VhdxPath is not null || created.VmRegistered || created.VmDirCreated)
            {
                // The residual race created at least the VHDX — clean it up so it
                // doesn't leak as an orphan. The colliding VM is NOT touched.
                _ = await RunCreateRollbackAsync(
                    name, vmDir, diffPath, primaryFailure!, ct, created).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "vm_create rollback skipped for {VmName}: no artifacts owned by this call (LF-D18 invariant; pre-existing VM preserved).",
                    name);
            }

            throw new VmAlreadyExistsException(hostProfile.HostId, name, primaryFailure!);
        }

        phase = ClassifyPhase(primaryFailure!, primaryResult);
        var rollback = await RunCreateRollbackAsync(
            name, vmDir, diffPath, primaryFailure!, ct, created).ConfigureAwait(false);

        // Log the original failure after rollback to preserve diagnostic ordering.
        _logger.LogError(primaryFailure, "vm_create failed for {VmName}", name);

        var errorCode = primaryFailure switch
        {
            OperationCanceledException => ErrorCodes.OperationCanceled,
            TimeoutException => ErrorCodes.CommandTimeout,
            _ => ErrorCodes.CommandFailed,
        };

        throw new VmCreateRollbackException(
            vmName: name,
            errorCode: errorCode,
            phase: phase,
            rollback: rollback,
            message: BuildCreateFailureSummary(primaryFailure!, primaryResult),
            innerException: primaryFailure);
    }

    /// <summary>
    /// starts the seeded VM. Separate from the primary create script because the
    /// in-script <c>Start-VM</c> is suppressed on the password path — the answer file must be in
    /// place before first boot.
    /// </summary>
    private async Task StartVmForPasswordSeedingAsync(string escapedName, string name, CancellationToken ct)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
Start-VM -Name '{escapedName}' -ComputerName localhost | Out-Null
Write-Output 'STARTED'
";
        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct).ConfigureAwait(false);
        // Exact terminal-token match: a substring test reads 'NOT_STARTED' as 'STARTED' and would
        // let a VM that never powered on proceed into the readiness poll.
        if (!result.Success
            || !string.Equals(
                BaseImageGeneralizationProbe.LastNonEmptyLine(result.Stdout), "STARTED",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"vm_create could not start VM '{name}' after seeding the administrator password.");
        }
    }

    /// <summary>
    /// runs the bounded readiness poll and guarantees the guest-delivery artifact is
    /// gone before ANY terminal outcome. On the preserve-on-failure path the VM is stopped first,
    /// because an offline re-mount requires the VM to be Off, and only then is the file deleted.
    /// </summary>
    private async Task ConfirmLoginReadyAsync(
        string name, string escapedName, string diffPath, string adminPassword,
        int createTimeBudgetSeconds, CancellationToken ct)
    {
        var readinessLimitSeconds = Math.Max(MinimumReadinessLimitSeconds, createTimeBudgetSeconds);

        // Fresh window measured from first power-on: the token handed in here is the caller's, not
        // the pre-boot budget's, so none of the create work already done eats into it.
        using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readinessCts.CancelAfter(TimeSpan.FromSeconds(readinessLimitSeconds + ReadinessCancellationGraceSeconds));

        GuestReadinessOutcome outcome;
        try
        {
            outcome = await _readinessPoller
                .WaitForLoginReadyAsync(name, adminPassword, readinessLimitSeconds, readinessCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Scrub before propagating cancellation so the plaintext answer file is not left in a running VM.
            // Cancellation remains the outcome, but an unconfirmed scrub must be logged.
            var scrubbedOnCancel = await StopThenScrubAsync(escapedName, name, diffPath).ConfigureAwait(false);
            if (!scrubbedOnCancel)
            {
                _logger.LogError(
                    "vm_create was cancelled for VM '{VmName}' and removal of the guest answer file from its disk could NOT be confirmed; the VM is preserved and the plaintext administrator password may still be readable inside it.",
                    name);
            }
            throw;
        }

        if (outcome == GuestReadinessOutcome.LoginReadyAndScrubbed)
        {
            return;
        }

        // An unconfirmed scrub may leave the plaintext answer file readable, so it MUST NOT be
        // reported as success even though the guest accepted the credential: the offline scrub
        // below is the second chance, and the call still fails.
        var scrubbed = await StopThenScrubAsync(escapedName, name, diffPath).ConfigureAwait(false);
        var reason = outcome switch
        {
            GuestReadinessOutcome.CredentialRejected
                => "the guest refused the supplied administrator password",
            GuestReadinessOutcome.LoginReadyScrubUnconfirmed
                => "removal of the answer file written into its disk could not be confirmed from inside the guest",
            GuestReadinessOutcome.PollFailed
                => "the readiness check could not be completed",
            _ => "the guest did not become login-ready within the supported readiness limit",
        };
        var message = scrubbed
            ? $"VM '{name}' was created but never confirmed login-ready because {reason}. The VM has been preserved and stopped so you can inspect or delete it."
            : $"VM '{name}' was created but never confirmed login-ready because {reason}, and the answer file written into its disk could not be confirmed removed. The VM has been preserved so you can inspect or delete it.";

        throw new ReadinessNotReachedException(name, message, scrubbed);
    }

    /// <summary>
    /// Stops the preserved VM (offline mount requires VM Off), then deletes the answer file from
    /// the differencing disk. Runs on <see cref="CancellationToken.None"/>: the scrub must still
    /// happen when the caller's token is already signalled.
    /// </summary>
    private async Task<bool> StopThenScrubAsync(string escapedName, string name, string diffPath)
    {
        try
        {
            var stopScript = $@"
$ErrorActionPreference = 'Continue'
Import-Module Hyper-V -ErrorAction SilentlyContinue
Stop-VM -Name '{escapedName}' -TurnOff -Force -ComputerName localhost -ErrorAction SilentlyContinue | Out-Null
$vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction SilentlyContinue
if ($vm -and $vm.State -eq 'Off') {{ Write-Output 'STOPPED' }} else {{ Write-Output 'NOT_STOPPED' }}
";
            var stopResult = await _psExecutor
                .ExecuteAsync(stopScript, timeoutSeconds: 120, ct: CancellationToken.None)
                .ConfigureAwait(false);
            // Exact terminal-token match: a substring test reads 'NOT_STOPPED' as 'STOPPED' and
            // would drive an offline mount against a still-running VM.
            if (!string.Equals(
                    BaseImageGeneralizationProbe.LastNonEmptyLine(stopResult.Stdout), "STOPPED",
                    StringComparison.Ordinal))
            {
                return false;
            }

            return await _unattendSeeder.ScrubOfflineAsync(diffPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "vm_create could not scrub the guest answer file for preserved VM '{VmName}': {ExType}",
                name, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Read-only Get-VM existence probe through the primary executor; a non-null result means present.
    /// Errors fail open to the primary pipeline, whose collision handling still limits rollback to owned artifacts.</summary>
    private async Task<bool> VmExistsOnHostAsync(string name, CancellationToken ct)
    {
        var escapedName = EscapePowerShellString(name);
        // -ComputerName localhost avoids the Win11 26200+ WMI null-name bug; caller cancellation applies before any artifacts exist.
        // Stop-on-error and explicit import distinguish module/host failures (inconclusive) from ItemNotFoundException (absent).
        // The sentinel differs from the residue probe's probe-failed marker so script recognizers can distinguish the probes.
        var probeScript = $@"
$ErrorActionPreference = 'Stop'
try {{
    Import-Module Hyper-V -ErrorAction Stop | Out-Null
    try {{
        $vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction Stop
        if ($vm) {{ 'present' }} else {{ 'absent' }}
    }} catch [Microsoft.HyperV.PowerShell.VirtualizationException] {{
        # Only recognized not-found errors mean absent; service/WMI/permission failures remain inconclusive.
        # The primary pipeline still handles collisions with owned-only rollback.
        if ($_.FullyQualifiedErrorId -match 'ItemNotFound|ObjectNotFound|VMNotFound') {{
            'absent'
        }} elseif ($_.CategoryInfo -and $_.CategoryInfo.Category -eq 'ObjectNotFound') {{
            'absent'
        }} else {{
            'inconclusive'
        }}
    }} catch {{
        if ($_.FullyQualifiedErrorId -match 'InvalidParameter|ItemNotFound|ObjectNotFound|VMNotFound') {{
            'absent'
        }} else {{
            'inconclusive'
        }}
    }}
}} catch {{
    'inconclusive'
}}
";
        try
        {
            var probe = await _psExecutor
                .ExecuteAsync(probeScript, timeoutSeconds: 30, ct: ct)
                .ConfigureAwait(false);

            var stdout = probe.Stdout?.Trim() ?? string.Empty;

            if (!probe.Success
                || string.IsNullOrWhiteSpace(stdout)
                || stdout.Equals("inconclusive", StringComparison.OrdinalIgnoreCase))
            {
                // Inconclusive probes defer to the primary pipeline and owned-only collision rollback, without claiming absence.
                _logger.LogDebug(
                    "vm_create LF-D19 probe inconclusive for '{VmName}'; falling through to primary pipeline. (exit={ExitCode}, stdout='{Stdout}')",
                    name, probe.ExitCode, stdout);
                return false;
            }

            return stdout.Equals("present", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "vm_create LF-D19 probe threw for '{VmName}'; falling through to primary pipeline.",
                name);
            return false;
        }
    }

    /// <summary>Ownership starts empty/false so rollback never removes a VM this call did not register.</summary>
    private sealed class CreatedArtifacts
    {
        /// <summary>Non-null when this call created the differencing VHDX at this path.</summary>
        public string? VhdxPath { get; set; }

        /// <summary>True only when this call successfully ran <c>New-VM</c>.</summary>
        public bool VmRegistered { get; set; }

        /// <summary>True only when this call created the per-VM directory.</summary>
        public bool VmDirCreated { get; set; }
    }

    /// <summary>Detects primary-pipeline name collisions case-insensitively, preferring stderr over the exception message.</summary>
    private static bool LooksLikeNameCollision(Exception failure, PowerShellResult? result)
    {
        if (failure is OperationCanceledException or TimeoutException)
            return false;

        var stderr = result?.Stderr ?? string.Empty;
        var msg = failure?.Message ?? string.Empty;

        // BASE_IMAGE_MUTATED is NOT a name collision — guard against the substring
        // false-positive (the message contains other text but not "already exists").
        if (msg.Contains("BASE_IMAGE_MUTATED", StringComparison.Ordinal))
            return false;

        // Require a VM-specific token with "already exists", matching ErrorMapper.IsNameCollisionMessage.
        // A generic file collision must remain COMMAND_FAILED with full rollback, not VM_ALREADY_EXISTS with owned-only cleanup.
        return HasVmAlreadyExistsSignal(stderr) || HasVmAlreadyExistsSignal(msg);
    }

    /// <summary>Matches "already exists" with VM with name, Get-VM or New-VM, as ErrorMapper does,
    /// so rollback and wire-envelope classification agree.</summary>
    private static bool HasVmAlreadyExistsSignal(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (!text.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return false;
        return text.Contains("VM with name", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Get-VM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("New-VM", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Infers owned artifacts from failure signals. Ambiguous non-collision failures retain rollback consideration;
    /// an internal pre-mutation collision probe owns nothing, so Remove-VM stays gated.</summary>
    private static CreatedArtifacts InferCreatedArtifacts(
        Exception failure,
        PowerShellResult? result,
        string diffPath,
        string vmDir,
        bool isNameCollision,
        bool primaryScriptSucceeded)
    {
        var created = new CreatedArtifacts();
        var stderr = result?.Stderr ?? string.Empty;

        // The initial probe returns upstream; an internal Get-VM collision owns nothing.
        // A New-VM collision owns the already-created VHDX and directory, never the foreign VM registration.
        // Cmdlet error records include New-VM; the internal probe's bare collision message does not.
        if (isNameCollision)
        {
            if (stderr.Contains("New-VM", StringComparison.OrdinalIgnoreCase))
            {
                // Post-New-VHD New-VM collision: clean up the owned VHDX + dir.
                // VmRegistered stays false so Remove-VM never touches the
                // colliding (foreign) VM.
                created.VhdxPath = diffPath;
                created.VmDirCreated = true;
            }
            // else: script-internal pre-mutator probe path — owns nothing.
            return created;
        }

        // If New-VM, Set-VM, or Start-VM appear in stderr the New-VHD step MUST
        // have already succeeded ⇒ this call owns the VHDX (and the per-VM
        // directory, which the script creates before New-VHD).
        var stderrMentionsRegisterOrConfigure =
            stderr.Contains("New-VM", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Set-VM", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Start-VM", StringComparison.OrdinalIgnoreCase);

        if (stderrMentionsRegisterOrConfigure)
        {
            created.VhdxPath = diffPath;
            created.VmDirCreated = true;

            // VmRegistered iff stderr mentions Set-VM / Start-VM (post-New-VM
            // phase). New-VM itself failing means the registration did NOT
            // complete — Remove-VM stays gated off.
            if (stderr.Contains("Set-VM", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("Start-VM", StringComparison.OrdinalIgnoreCase))
            {
                created.VmRegistered = true;
            }
        }
        else if (primaryScriptSucceeded)
        {
            // The primary script succeeded before a host-side guard failed, so this call owns the VM, VHDX and directory.
            // Rollback must Remove-VM or the newly registered VM leaks.
            created.VhdxPath = diffPath;
            created.VmDirCreated = true;
            created.VmRegistered = true;
        }
        else if (result is not null)
        {
            // An ambiguous script failure proves no completed registration: retain VHDX/directory cleanup but never remove a possibly foreign VM.
            // The residue probe still reports a same-name VM so operators retain diagnostic signal.
            created.VhdxPath = diffPath;
            created.VmDirCreated = true;
        }
        // No result means no primary execution was observed; empty ownership leaves only a no-op/file scan.

        return created;
    }

    /// <summary>
    /// (b)(c)(d): runs the rollback PowerShell script under a fresh, detached
    /// <see cref="CancellationTokenSource"/> so a cancelled inbound CT cannot abort
    /// it. The task is <c>await</c>ed before the error response is returned.
    /// </summary>
    private async Task<VmCreateRollbackInfo> RunCreateRollbackAsync(
        string name,
        string vmDir,
        string diffPath,
        Exception originalFailure,
        CancellationToken inboundCt = default,
        CreatedArtifacts? created = null)
    {
        var budgetSeconds = ResolveRollbackBudgetSeconds();
        var budget = TimeSpan.FromSeconds(budgetSeconds);

        // Missing ownership records retain legacy full ownership for backward compatibility.
        var ownsVm = created?.VmRegistered ?? true;
        var ownsVhdx = created?.VhdxPath is not null || created is null;
        var ownsVmDir = created?.VmDirCreated ?? true;

        _logger.LogInformation(
            "vm_create rollback starting for {VmName} (ownsVm={OwnsVm}, ownsVhdx={OwnsVhdx}, ownsVmDir={OwnsVmDir})",
            name, ownsVm, ownsVhdx, ownsVmDir);

        var escapedName = EscapePowerShellString(name);
        var escapedVmDir = EscapePowerShellString(vmDir);
        var escapedDiffPath = EscapePowerShellString(diffPath);

        // Gate cleanup by ownership so rollback never deletes another call's VM or files.
        var psOwnsVm = ownsVm ? "$true" : "$false";
        var psOwnsVhdx = ownsVhdx ? "$true" : "$false";
        var psOwnsVmDir = ownsVmDir ? "$true" : "$false";

        // Rollback is idempotent: SilentlyContinue and Test-Path guards tolerate absent artifacts.
        // Its JSON reports removed kinds and remaining artifacts.
        var rollbackScript = $@"
$ErrorActionPreference = 'SilentlyContinue'
$name = '{escapedName}'
$vmDir = '{escapedVmDir}'
$diffPath = '{escapedDiffPath}'
$ownsVm = {psOwnsVm}
$ownsVhdx = {psOwnsVhdx}
$ownsVmDir = {psOwnsVmDir}

$removed = New-Object System.Collections.ArrayList
$failed  = New-Object System.Collections.ArrayList

if ($ownsVm) {{
    try {{
        Import-Module Hyper-V -ErrorAction SilentlyContinue
        $existing = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
        if ($existing) {{
            try {{
                $existing | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue | Out-Null
                $vhds = (Get-VMHardDiskDrive -VM $existing -ErrorAction SilentlyContinue).Path
                Remove-VM -VM $existing -Force -ErrorAction SilentlyContinue
                $removed.Add(@{{ kind = 'vm'; path = $name }}) | Out-Null
                foreach ($v in $vhds) {{
                    if ($v -and (Test-Path -LiteralPath $v)) {{
                        try {{
                            Remove-Item -LiteralPath $v -Force -ErrorAction Stop
                            $removed.Add(@{{ kind = 'vhdx'; path = $v }}) | Out-Null
                        }} catch {{
                            $failed.Add(@{{ kind = 'vhdx'; path = $v; reason = $_.Exception.Message }}) | Out-Null
                        }}
                    }}
                }}
            }} catch {{
                $failed.Add(@{{ kind = 'vm'; path = $name; reason = $_.Exception.Message }}) | Out-Null
            }}
        }}
    }} catch {{
        # Module import / Get-VM probe failed — fall through to file cleanup.
    }}
}}

if ($ownsVhdx -and (Test-Path -LiteralPath $diffPath)) {{
    try {{
        Remove-Item -LiteralPath $diffPath -Force -ErrorAction Stop
        $removed.Add(@{{ kind = 'vhdx'; path = $diffPath }}) | Out-Null
    }} catch {{
        $failed.Add(@{{ kind = 'vhdx'; path = $diffPath; reason = $_.Exception.Message }}) | Out-Null
    }}
}}

if ($ownsVmDir -and (Test-Path -LiteralPath $vmDir)) {{
    $children = Get-ChildItem -LiteralPath $vmDir -Force -ErrorAction SilentlyContinue
    if (-not $children) {{
        try {{
            Remove-Item -LiteralPath $vmDir -Force -Recurse -ErrorAction Stop
            $removed.Add(@{{ kind = 'dir'; path = $vmDir }}) | Out-Null
        }} catch {{
            $failed.Add(@{{ kind = 'dir'; path = $vmDir; reason = $_.Exception.Message }}) | Out-Null
        }}
    }}
}}

$residual = New-Object System.Collections.ArrayList
# Report only this call's owned artifacts as residue; pre-existing files or VMs do not mean rollback failed.
if ($ownsVhdx -and (Test-Path -LiteralPath $diffPath)) {{ $residual.Add($diffPath) | Out-Null }}
if ($ownsVmDir -and (Test-Path -LiteralPath $vmDir))    {{ $residual.Add($vmDir)    | Out-Null }}
$stillRegistered = if ($ownsVm) {{ Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue }} else {{ $null }}
if ($stillRegistered) {{ $residual.Add(""vm:$name"") | Out-Null }}

[PSCustomObject]@{{
    removed  = $removed
    failed   = $failed
    residual = $residual
}} | ConvertTo-Json -Depth 5 -Compress
";

        var sw = Stopwatch.StartNew();
        // (b): fresh CTS, NOT linked to inbound CT.
        using var rollbackCts = new CancellationTokenSource(budget);
        PowerShellResult? rollbackResult = null;
        Exception? rollbackError = null;
        try
        {
            rollbackResult = await _psExecutor
                .ExecuteAsync(rollbackScript, timeoutSeconds: budgetSeconds + 5, ct: rollbackCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rollbackError = ex;
        }
        sw.Stop();

        var residualArtifacts = new List<string>();
        var succeeded = false;

        if (rollbackResult is { Success: true } &&
            !string.IsNullOrWhiteSpace(rollbackResult.Stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(rollbackResult.Stdout.Trim());
                var root = doc.RootElement;

                if (root.TryGetProperty("removed", out var removedArr) &&
                    removedArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in removedArr.EnumerateArray())
                    {
                        var kind = r.TryGetProperty("kind", out var k) ? k.GetString() : "?";
                        var path = r.TryGetProperty("path", out var p) ? p.GetString() : "?";
                        _logger.LogInformation("vm_create rollback removed {Kind} {Path}", kind, path);
                    }
                }

                if (root.TryGetProperty("failed", out var failedArr) &&
                    failedArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in failedArr.EnumerateArray())
                    {
                        var kind = f.TryGetProperty("kind", out var k) ? k.GetString() : "?";
                        var path = f.TryGetProperty("path", out var p) ? p.GetString() : "?";
                        var reason = f.TryGetProperty("reason", out var rs) ? rs.GetString() : "?";
                        _logger.LogWarning("vm_create rollback failed to remove {Kind} {Path}: {Reason}", kind, path, reason);
                    }
                }

                if (root.TryGetProperty("residual", out var residualArr) &&
                    residualArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rv in residualArr.EnumerateArray())
                    {
                        var s = rv.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            residualArtifacts.Add(s);
                    }
                }

                succeeded = residualArtifacts.Count == 0;
            }
            catch (Exception parseEx)
            {
                _logger.LogWarning(parseEx, "vm_create rollback output was not parseable JSON; treating as residual.");
                // only treat artifacts as residual when this call owned
                // them. Pre-existing-VM artifacts are not this call's residue.
                if (ownsVhdx) residualArtifacts.Add(diffPath);
                if (ownsVmDir) residualArtifacts.Add(vmDir);
            }
        }
        else if (rollbackResult is { TimedOut: true } || rollbackError is OperationCanceledException ||
                 (rollbackResult is not null && rollbackResult.Cancelled))
        {
            _logger.LogError(
                rollbackError,
                "vm_create rollback exceeded budget for {VmName}; diffPath={DiffPath}, vmDir={VmDir}, elapsedMs={ElapsedMs}, budgetMs={BudgetMs}",
                name, diffPath, vmDir, sw.ElapsedMilliseconds, (long)budget.TotalMilliseconds);
            if (ownsVhdx) residualArtifacts.Add(diffPath);
            if (ownsVmDir) residualArtifacts.Add(vmDir);
            // After rollback budget expiry, probe owned VM registration under the caller token.
            // Never report a foreign colliding VM as this call's residue.
            if (ownsVm)
            {
                await TryAppendRegisteredVmResidueAsync(name, residualArtifacts, inboundCt).ConfigureAwait(false);
            }
        }
        else
        {
            // Non-cancellation failure (PowerShell exited non-zero, or threw).
            _logger.LogWarning(
                rollbackError,
                "vm_create rollback PowerShell failed for {VmName}: stderr={Stderr}",
                name, rollbackResult?.Stderr);
            // Best-effort: assume the worst until proven otherwise via filesystem probe below.
            // gate on ownership — never report someone else's VHDX/dir.
            if (ownsVhdx && File.Exists(diffPath)) residualArtifacts.Add(diffPath);
            if (ownsVmDir && Directory.Exists(vmDir)) residualArtifacts.Add(vmDir);
            // After rollback PowerShell failure, probe owned VM registration under the caller token.
            if (ownsVm)
            {
                await TryAppendRegisteredVmResidueAsync(name, residualArtifacts, inboundCt).ConfigureAwait(false);
            }
        }

        // Prefer host-observed residue over PowerShell's report. Check both owned VHDX and directory:
        // extra Hyper-V files can leave the directory after disk removal. Never report another call's files as residue.
        if (ownsVhdx && File.Exists(diffPath) && !residualArtifacts.Contains(diffPath))
        {
            residualArtifacts.Add(diffPath);
            succeeded = false;
        }
        if (ownsVmDir && Directory.Exists(vmDir) && !residualArtifacts.Contains(vmDir))
        {
            residualArtifacts.Add(vmDir);
            succeeded = false;
        }

        var info = new VmCreateRollbackInfo
        {
            Performed = true,
            Succeeded = succeeded,
            ElapsedMs = sw.ElapsedMilliseconds,
            ResidualArtifacts = residualArtifacts.AsReadOnly(),
        };

        _logger.LogInformation(
            "vm_create rollback completed for {VmName} in {ElapsedMs}ms; residualArtifacts=[{Residual}]",
            name, info.ElapsedMs, string.Join(",", info.ResidualArtifacts));

        return info;
    }

    /// <summary> Classifies the phase enum (<c>create</c> | <c>register</c> | <c>configure</c>) from the failure /
    /// result pair. The current pipeline is single-script so phase boundaries are inferred from stderr signals; this
    /// keeps the contract honest without false precision. </summary>
    private static string ClassifyPhase(Exception failure, PowerShellResult? result)
    {
        var text = (result?.Stderr ?? string.Empty) + " " + (failure?.Message ?? string.Empty);
        if (text.Contains("Set-VM", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Start-VM", StringComparison.OrdinalIgnoreCase))
        {
            return "configure";
        }
        if (text.Contains("New-VM", StringComparison.OrdinalIgnoreCase))
        {
            return "register";
        }
        return "create";
    }

    private static string BuildCreateFailureSummary(Exception failure, PowerShellResult? result)
    {
        if (failure is OperationCanceledException)
            return "vm_create was cancelled before completion; rollback was performed.";
        if (failure is TimeoutException)
            return "vm_create exceeded its execution budget; rollback was performed.";
        if (!string.IsNullOrWhiteSpace(result?.Stderr))
            return $"vm_create failed: {result.Stderr.Trim()}";
        return $"vm_create failed: {failure.Message}";
    }

    private static int ResolveRollbackBudgetSeconds()
    {
        var raw = Environment.GetEnvironmentVariable(RollbackBudgetEnvVar);
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, out var seconds) &&
            seconds > 0)
        {
            return seconds;
        }
        return DefaultRollbackBudgetSeconds;
    }


    /// <summary>Snapshot metadata primitives eagerly: lazy FileInfo reads could capture post-create state
    /// for the pre-create snapshot and hide mutation.</summary>
    private readonly record struct FileStatSnapshot(
        long Length,
        DateTime LastWriteTimeUtc,
        bool IsReadOnly);

    /// <summary>Cross-checks registration after fallback rollback using the caller token; the rollback token has expired.
    /// Stop-on-error distinguishes present, absent and probe-failed:<reason> rather than treating failures as absence.
    /// Present adds vm:<vmName>; absent adds nothing. Failure, timeout, cancellation or unparseable output adds
    /// vm:<vmName>(probe-unknown) and a warning because registration may remain.</summary>
    private async Task TryAppendRegisteredVmResidueAsync(
        string vmName,
        List<string> residualArtifacts,
        CancellationToken inboundCt)
    {
        if (inboundCt.IsCancellationRequested)
        {
            _logger.LogWarning(
                "vm_create rollback: residual VM-registration visibility lost for {VmName} (inbound CT cancelled).",
                vmName);
            AppendProbeUnknownSentinel(vmName, residualArtifacts);
            return;
        }

        var escapedName = EscapePowerShellString(vmName);
        // Do not suppress Get-VM errors: Stop promotes non-terminating failures into the catch.
        // Only not-found means absent; module/RPC/permission failures yield probe-failed so the caller reports unknown residue.
        var probeScript = $@"
$ErrorActionPreference = 'Stop'
try {{
    Import-Module Hyper-V -ErrorAction Stop
    try {{
        Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction Stop | Out-Null
        'present'
    }} catch {{
        if ($_.Exception.Message -match '(?i)not\s*found') {{
            'absent'
        }} else {{
            throw
        }}
    }}
}} catch {{
    $msg = $_.Exception.Message -replace '[\r\n]+', ' '
    ""probe-failed:$msg""
}}
";

        try
        {
            var probe = await _psExecutor
                .ExecuteAsync(probeScript, timeoutSeconds: 15, ct: inboundCt)
                .ConfigureAwait(false);

            if (!probe.Success)
            {
                _logger.LogWarning(
                    "vm_create rollback: residual VM-registration visibility lost for {VmName} (probe exit {ExitCode}, stderr={Stderr}); appending probe-unknown sentinel (fail-closed).",
                    vmName, probe.ExitCode, probe.Stderr);
                AppendProbeUnknownSentinel(vmName, residualArtifacts);
                return;
            }

            var stdout = probe.Stdout?.Trim() ?? string.Empty;

            if (stdout.Equals("present", StringComparison.OrdinalIgnoreCase))
            {
                var residueToken = $"vm:{vmName}";
                if (!residualArtifacts.Contains(residueToken))
                {
                    residualArtifacts.Add(residueToken);
                }
            }
            else if (stdout.Equals("absent", StringComparison.OrdinalIgnoreCase))
            {
                // Authoritative no-residue; append nothing.
            }
            else
            {
                // 'probe-failed:<reason>' or unparseable output ⇒ fail closed.
                _logger.LogWarning(
                    "vm_create rollback: residual VM-registration visibility lost for {VmName} (probe returned unrecognized output '{Stdout}'); appending probe-unknown sentinel (fail-closed).",
                    vmName, stdout);
                AppendProbeUnknownSentinel(vmName, residualArtifacts);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "vm_create rollback: residual VM-registration visibility lost for {VmName} (inbound CT cancelled during probe); appending probe-unknown sentinel (fail-closed).",
                vmName);
            AppendProbeUnknownSentinel(vmName, residualArtifacts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "vm_create rollback: residual VM-registration visibility lost for {VmName} (executor threw); appending probe-unknown sentinel (fail-closed).",
                vmName);
            AppendProbeUnknownSentinel(vmName, residualArtifacts);
        }
    }

    private static void AppendProbeUnknownSentinel(string vmName, List<string> residualArtifacts)
    {
        var sentinel = $"vm:{vmName}(probe-unknown)";
        if (!residualArtifacts.Contains(sentinel))
        {
            residualArtifacts.Add(sentinel);
        }
    }

    /// <summary>Shared Get-VM/action/projection/JSON script for Start/Stop/Restart/Pause/Resume/Configure/GetVmStatus.
    /// Callers retain host resolution, error mapping and parsing. safeVmId must already pass InputValidation.ValidateVmId.
    /// An empty actionBlock is the read-only status sentinel and emits one harmless blank line.</summary>
    private async Task<PowerShellResult> RunSingleVmActionAsync(
        string safeVmId,
        string actionBlock,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
# WMI workaround : -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}
{actionBlock}
$vm | {VmInfoProjection} | ConvertTo-Json
";

        return await _psExecutor.ExecuteAsync(script, timeoutSeconds: timeoutSeconds, ct: ct);
    }

    /// <inheritdoc />
    public async Task<VmInfo> StartVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        var actionBlock = $@"if ($vm.State -ne 'Running') {{
    Start-VM -VM $vm
    $vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
}}";

        _logger.LogInformation("Starting VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// When force=true, uses Stop-VM -TurnOff (hard power-off, immediate).
    /// When force=false, uses Stop-VM -Force (graceful shutdown, no confirmation prompt).
    /// </remarks>
    public async Task<VmInfo> StopVmAsync(string hostId, string vmId, bool force = false,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // force=true uses -TurnOff for hard power-off.
        // force=false uses -Force to suppress confirmation but still attempts graceful shutdown.
        var stopCommand = force
            ? "$vm | Stop-VM -TurnOff -Force"
            : "$vm | Stop-VM -Force";

        var actionBlock = $@"if ($vm.State -ne 'Off') {{
    {stopCommand}
    $vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
}}";

        _logger.LogInformation("Stopping VM '{VmId}' on host '{HostId}' (force={Force})", safeVmId, hostId, force);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Restart is an atomic stop + start operation. Uses Stop-VM -Force for graceful shutdown
    /// followed by Start-VM. Re-fetches VM state after restart to return updated info.
    /// </remarks>
    public async Task<VmInfo> RestartVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // The mid-action Get-VM inside Start-VM -VM (...) plus the trailing
        // re-fetch is a deliberate double-fetch quirk preserved byte-for-byte.
        var actionBlock = $@"# Stop (graceful, no confirmation prompt) then start
$vm | Stop-VM -Force
Start-VM -VM (Get-VM -Id '{safeVmId}' -ComputerName localhost)
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost";

        _logger.LogInformation("Restarting VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc /> <remarks> Pauses a running VM into in-memory <c>Paused</c> (State 6). The VM must be in Running
    /// state. Returns updated VM info showing Paused state; a settle that reaches only Saved/Off, or times out, is
    /// surfaced as a caller-facing failure (never a success projection). </remarks>
    public async Task<VmInfo> PauseVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Keep the state-gate throw literal stable for error mapping (currently COMMAND_FAILED).
        // Pause uses RequestedState 9 from the parameter ValueMap, not return-value 32776 (rejected with 32775).
        // Settle on Get-VM.State only; CIM EnabledState is a different enum.
        var actionBlock = $@"if ($vm.State -ne 'Running') {{ throw ""Cannot pause VM in state '$($vm.State)'. VM must be Running to pause."" }}
$computerSystem = Get-CimInstance -Namespace root/virtualization/v2 -ClassName Msvm_ComputerSystem -Filter ""Name = '$($vm.Id)'""
$pauseReturn = Invoke-CimMethod -InputObject $computerSystem -MethodName RequestStateChange -Arguments @{{ RequestedState = [uint16]9 }}
if ($pauseReturn.ReturnValue -ne 0 -and $pauseReturn.ReturnValue -ne 4096) {{
    # Only codes confirmed from the concrete Msvm_ComputerSystem.RequestStateChange return ValueMap;
    # base-class CIM_EnabledLogicalElement codes are deliberately absent (decoding them would repeat
    # the ValueMap conflation that caused #291). Exact numeric lookup, never substring.
    $pauseReturnMeanings = @{{ 32775 = 'Invalid State Transition'; 32776 = 'Use of Timeout Parameter Not Supported' }}
    $pauseReturnCode = [int]$pauseReturn.ReturnValue
    $pauseReturnMeaning = $pauseReturnMeanings[$pauseReturnCode]
    if ($null -eq $pauseReturnMeaning) {{ $pauseReturnMeaning = 'Unrecognized return code' }}
    # Re-read before naming a state: during a concurrent shutdown the pre-request reading is
    # 'Running', the one state that implies nothing is wrong.
    $vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
    throw ""vm_pause RequestStateChange(9 = Quiesce) returned $pauseReturnCode ($pauseReturnMeaning); observed Get-VM state '$($vm.State)'.""
}}
# Conservative default so any unmodelled loop exit reports 'outcome unknown' rather than falsely
# asserting a conflict.
$pauseOutcome = '{PauseOutcomes.WaitExhausted}'
$settleDeadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $settleDeadline) {{
    # WMI workaround : -ComputerName localhost avoids null-name WMI bug
    $vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
    # Exact equality so 'Paused' never matches 'PausedCritical'.
    if ($vm.State -eq 'Paused') {{ $pauseOutcome = '{PauseOutcomes.SettledPaused}'; break }}
    # 'Saved' / 'Off' are failure terminals; transient 'Saving' / 'Pausing' keep polling.
    if ($vm.State -eq 'Saved' -or $vm.State -eq 'Off') {{ $pauseOutcome = '{PauseOutcomes.ConflictingTerminalState}'; break }}
    Start-Sleep -Milliseconds 250
}}
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
# The recorded exit reason classifies, never this state token: an exhausted wait and a genuine
# divergence were both observed live ending on 'Saved'. This read supplies the reported state only.
if ($pauseOutcome -eq '{PauseOutcomes.ConflictingTerminalState}') {{
    throw ""{PauseOutcomes.SentinelPrefix}{PauseOutcomes.ConflictingTerminalState} the VM reached a terminal state other than 'Paused'; observed state '$($vm.State)'. The pause cannot succeed as issued.""
}}
if ($pauseOutcome -ne '{PauseOutcomes.SettledPaused}') {{
    throw ""{PauseOutcomes.SentinelPrefix}{PauseOutcomes.WaitExhausted} the bounded wait elapsed without the VM settling; state observed when the wait was abandoned was '$($vm.State)'.""
}}
if ($vm.State -ne 'Paused') {{ throw ""vm_pause did not settle into 'Paused'; observed state '$($vm.State)'."" }}";

        _logger.LogInformation("Pausing VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        try
        {
            HandleError(result, hostProfile.HostId, safeVmId);
        }
        catch (InvalidOperationException ex)
        {
            var classified = ClassifyPauseFailure(ex);
            // Unclassified failures rethrow in place: `throw ex` would reset the stack trace of
            // exactly the unexpected pause failures that most need it.
            if (ReferenceEquals(classified, ex))
            {
                throw;
            }

            throw classified;
        }

        var pausedInfo = ParseSingleVmInfo(result.Stdout, hostProfile.HostId);

        // The in-script throw fires only on a real host, so a mocked executor returning a
        // non-Paused payload would otherwise leak a benign success.
        if (!string.Equals(pausedInfo.State, "Paused", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"vm_pause did not settle into 'Paused'; observed state '{pausedInfo.State}'.");
        }

        return pausedInfo;
    }

    /// <summary>Recognized settle sentinels become VmStateConflictException; absent or unknown tokens leave the exception unchanged.
    /// Search beyond the prefix: HandleError's preamble, PowerShell decoration and stderr may precede the sentinel.</summary>
    private static Exception ClassifyPauseFailure(InvalidOperationException failure)
    {
        var message = failure.Message ?? string.Empty;
        var sentinelIndex = message.IndexOf(PauseOutcomes.SentinelPrefix, StringComparison.Ordinal);
        if (sentinelIndex < 0)
        {
            return failure;
        }

        var tokenStart = sentinelIndex + PauseOutcomes.SentinelPrefix.Length;
        var outcome = message.Length > tokenStart
            ? new string(message.Skip(tokenStart).TakeWhile(character => character == '_' || char.IsLetter(character)).ToArray())
            : string.Empty;

        if (outcome != PauseOutcomes.ConflictingTerminalState && outcome != PauseOutcomes.WaitExhausted)
        {
            return failure;
        }

        return new VmStateConflictException(outcome, ExtractObservedState(message), message);
    }

    /// <summary>
    /// Lifts the single-quoted state token from the settle throw. Reads the LAST quoted token
    /// because surrounding failure text may quote earlier values.
    /// </summary>
    private static string ExtractObservedState(string message)
    {
        var closingQuote = message.LastIndexOf('\'');
        if (closingQuote <= 0)
        {
            return "Unknown";
        }
        var openingQuote = message.LastIndexOf('\'', closingQuote - 1);
        if (openingQuote < 0)
        {
            return "Unknown";
        }
        var token = message.Substring(openingQuote + 1, closingQuote - openingQuote - 1).Trim();
        return token.Length == 0 ? "Unknown" : token;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resumes a paused/suspended/saved VM using Resume-VM. The VM must be in Paused or Saved state.
    /// Returns updated VM info showing Running state.
    /// </remarks>
    public async Task<VmInfo> ResumeVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Resume-VM can return mid-transition (Starting), so poll a stable terminal state before projecting.
        // Keep the state-gate throw literal unchanged; its error mapping remains unresolved.
        var actionBlock = $@"if ($vm.State -ne 'Paused' -and $vm.State -ne 'PausedCritical' -and $vm.State -ne 'Saved') {{ throw ""Cannot resume VM in state '$($vm.State)'. VM must be Paused or Saved to resume."" }}
Resume-VM -VM $vm
$settleDeadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $settleDeadline) {{
    # WMI workaround : -ComputerName localhost avoids null-name WMI bug
    $vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
    # break only on stable terminal states; the transient 'Starting' keeps polling
    if ($vm.State -eq 'Running' -or $vm.State -eq 'Paused' -or $vm.State -eq 'PausedCritical' -or $vm.State -eq 'Saved' -or $vm.State -eq 'Off') {{ break }}
    Start-Sleep -Milliseconds 250
}}
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost";

        _logger.LogInformation("Resuming VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc /> <remarks> Modifies VM configuration. Supports updating CPU count via Set-VMProcessor and/or
    /// startup memory via Set-VMMemory. At least one of <paramref name="cpuCount"/> or <paramref name="memoryMB"/>
    /// must be supplied (the dispatcher enforces this). Returns updated VM info after applying the changes.
    /// </remarks>
    public async Task<VmInfo> ConfigureVmAsync(string hostId, string vmId, int? cpuCount, long? memoryMB, CancellationToken ct)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Build the conditional Set-VM* lines. Values are validated numerics so direct
        // interpolation is safe (no string injection vector).
        var setProcessorLine = cpuCount.HasValue
            ? $"Set-VMProcessor -VM $vm -Count {cpuCount.Value}"
            : "# cpuCount not provided";
        var setMemoryLine = memoryMB.HasValue
            ? $"Set-VMMemory -VM $vm -StartupBytes ({memoryMB.Value}MB)"
            : "# memoryMB not provided";

        var actionBlock = $@"{setProcessorLine}
{setMemoryLine}
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
# review: project MemoryStartup (configured) instead of MemoryAssigned,
# which Hyper-V reports as 0 for stopped VMs even after a successful Set-VMMemory.";

        _logger.LogInformation(
            "Configuring VM '{VmId}' on host '{HostId}' (cpuCount={CpuCount}, memoryMB={MemoryMB})",
            safeVmId, hostId, cpuCount, memoryMB);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>Hard power-off tolerates already-off VMs, then removes the VM and VHDXs.
    /// Best-effort recursive directory cleanup is restricted to StorageRoot/<vmName> so unmanaged VMs are unaffected.</remarks>
    public async Task DestroyVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Resolve the same storage root as creation/install (environment > profile > default)
        // so directory cleanup can require an exact StorageRoot/VM-name match.
        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;
        var escapedStorageRoot = EscapePowerShellString(storageRoot);

        // Collect disk paths before hard power-off/removal. Recursive cleanup can delete non-empty directories, so normalize paths,
        // compare case-insensitively and require a strict child of the managed root to block VM-name traversal.
        // Skip missing/unsafe paths; cleanup failure only warns because VM/disk removal already succeeded. Write-Output keeps warnings in captured stdout.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
# WMI workaround : -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

# Hard power-off (ignore errors if already off)
$vm | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue

$vhdPaths = @((Get-VMHardDiskDrive -VM $vm).Path)

$expectedStorageRoot = '{escapedStorageRoot}'
$vmName = $vm.Name
$expectedVmDir = Join-Path $expectedStorageRoot $vmName

Remove-VM -VM $vm -Force

foreach ($path in $vhdPaths) {{
    if ($path -and (Test-Path -LiteralPath $path)) {{
        Remove-Item -LiteralPath $path -Force
    }}
}}

# managed-path safety guard before per-VM directory removal
$resolvedExpected = [System.IO.Path]::GetFullPath($expectedVmDir)
$resolvedRoot = [System.IO.Path]::GetFullPath($expectedStorageRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$rootPrefix = $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedExpected.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {{
    Write-Output ""[WARN] Skipping per-VM directory cleanup: '$resolvedExpected' does not match expected managed path under '$resolvedRoot'.""
}} elseif (-not (Test-Path -LiteralPath $resolvedExpected)) {{
    Write-Output ""[WARN] Skipping per-VM directory cleanup: expected managed path '$resolvedExpected' does not exist.""
}} else {{
    try {{
        Remove-Item -LiteralPath $resolvedExpected -Recurse -Force -ErrorAction Stop
    }} catch {{
        Write-Output ""[WARN] Failed to remove per-VM directory '$resolvedExpected': $($_.Exception.Message)""
    }}
}}

Write-Output 'destroyed'
";

        _logger.LogInformation("Destroying VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        // Surface any [WARN] lines from the script's captured stdout into the standard
        // logger so directory-cleanup safety-skips and failures are observable to ops.
        if (!string.IsNullOrEmpty(result.Stdout))
        {
            foreach (var line in result.Stdout.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r').TrimStart();
                if (trimmed.StartsWith("[WARN]", StringComparison.Ordinal))
                {
                    _logger.LogWarning("DestroyVmAsync cleanup: {Message}", trimmed);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Filters by wildcard name (*filter*) and optionally the hyper-v-mcp tag.
    /// -ComputerName localhost avoids the non-interactive WMI null-name bug on Windows 11 26200+.</remarks>
    public async Task<IReadOnlyList<VmInfo>> ListVmsAsync(string hostId, string? nameFilter = null,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        string getVmCommand;
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            var escapedFilter = EscapePowerShellString(nameFilter);
            getVmCommand = $"$vms = Get-VM -Name '*{escapedFilter}*' -ComputerName localhost -ErrorAction SilentlyContinue";
        }
        else
        {
            // -ComputerName localhost avoids the Win11 26200+ WMI null-name bug in parameterless and wildcard Get-VM.
            getVmCommand = "$vms = Get-VM -Name '*' -ComputerName localhost";
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
{getVmCommand}
if (-not $vms) {{ $vms = @() }}
$tagged = @($vms | Where-Object {{ $_.Notes -like '*hyper-v-mcp:*' }})
if ($tagged.Count -eq 0) {{
    Write-Output '[]'
}} else {{
    $tagged | Select-Object Id, Name, State, ProcessorCount, @{{N='MemoryMB';E={{$_.MemoryStartup/1MB}}}}, @{{N='UptimeSeconds';E={{$_.Uptime.TotalSeconds}}}} | ConvertTo-Json
}}
";

        _logger.LogDebug("Listing VMs on host '{HostId}' with filter '{NameFilter}'", hostId, nameFilter);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct: ct);
        HandleError(result, hostProfile.HostId, vmId: null);

        return ParseVmInfoList(result.Stdout, hostProfile.HostId);
    }

    public async Task<IReadOnlyList<VmInfo>> FindVmsByNameAsync(
        string hostId, string name, bool caseSensitive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A non-blank VM name is required.", nameof(name));

        var hostProfile = ResolveLocalHost(hostId);
        const string script = """
            Import-Module Hyper-V -ErrorAction Stop
            ConvertTo-Json -InputObject @(Get-VM -Name '*' -ComputerName localhost -ErrorAction Stop |
                Select-Object Id, Name, @{N='State';E={[string]$_.State}})
            """;

        PowerShellResult result;
        try
        {
            result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            ct.ThrowIfCancellationRequested();
            throw new VmLookupFailedException();
        }

        ct.ThrowIfCancellationRequested();
        if (result.Cancelled)
            throw new OperationCanceledException(ct);
        if (!result.Success)
            throw new VmLookupFailedException(result.ExitCode, result.Stderr);

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new VmLookupFailedException(result.ExitCode, result.Stderr);

            var matches = new List<VmInfo>();
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("Id", out var vmId) || vmId.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(vmId.GetString())
                    || !row.TryGetProperty("Name", out var vmName) || vmName.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(vmName.GetString())
                    || !row.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.String)
                {
                    throw new VmLookupFailedException(result.ExitCode, result.Stderr);
                }

                if (string.Equals(vmName.GetString(), name, comparison))
                {
                    matches.Add(new VmInfo
                    {
                        VmId = vmId.GetString()!,
                        Name = vmName.GetString()!,
                        State = state.GetString()!,
                        HostId = hostProfile.HostId,
                    });
                }
            }

            ct.ThrowIfCancellationRequested();
            return matches;
        }
        catch (JsonException)
        {
            ct.ThrowIfCancellationRequested();
            throw new VmLookupFailedException(result.ExitCode, result.Stderr);
        }
    }

    /// <inheritdoc />
    public async Task<VmInfo> GetVmStatusAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // empty actionBlock sentinel — read-only projection, no state change,
        // no re-fetch. Emits one extra blank line in the script ( documented no-op).
        // Timeout stays at 30s per (NOT 120s).
        _logger.LogDebug("Getting status for VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock: "", timeoutSeconds: 30, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>Image directory precedence: HYPERV_MCP_IMAGE_DIR, profile BaseVhdxPath parent, then HYPERV_MCP_BASE_VHDX parent.
    /// Unconfigured returns success with Configured=false and no images. A missing configured path is INVALID_PARAMETER;
    /// ACL/IO enumeration failure is IO_ERROR. Get-VHD uses localhost to avoid the Win11 26200+ WMI null-name bug.</remarks>
    public async Task<ImageListResult> ListImagesAsync(string hostId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        var imageDir = Environment.GetEnvironmentVariable("HYPERV_MCP_IMAGE_DIR");
        if (string.IsNullOrWhiteSpace(imageDir) && !string.IsNullOrWhiteSpace(hostProfile.BaseVhdxPath))
        {
            imageDir = System.IO.Path.GetDirectoryName(hostProfile.BaseVhdxPath);
        }
        if (string.IsNullOrWhiteSpace(imageDir))
        {
            var envBaseVhdx = Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX");
            if (!string.IsNullOrWhiteSpace(envBaseVhdx))
            {
                imageDir = System.IO.Path.GetDirectoryName(envBaseVhdx);
            }
        }

        if (string.IsNullOrWhiteSpace(imageDir))
        {
            // unconfigured is a soft, successful state — NOT an error envelope.
            _logger.LogDebug(
                "ListImagesAsync: no image directory configured on host '{HostId}'; returning empty list (ST-D7).",
                hostId);
            return new ImageListResult
            {
                Images = System.Array.Empty<ImageInfo>(),
                Count = 0,
                Configured = false,
                ImageDir = null,
                Hint = "Set HYPERV_MCP_IMAGE_DIR, host-profile BaseVhdxPath, or HYPERV_MCP_BASE_VHDX to enable image enumeration.",
            };
        }

        // Do not use Directory.Exists: ACL denial also returns false and would become INVALID_PARAMETER instead of IO_ERROR.
        // A one-step enumeration probe preserves native exceptions: missing directory maps to INVALID_PARAMETER;
        // UnauthorizedAccessException/IOException map to IO_ERROR. The injected probe preserves the same envelope.
        try
        {
            _fileSystemProbe.ProbeDirectory(imageDir);
        }
        catch (System.IO.DirectoryNotFoundException)
        {
            // Configured path does not exist (or a parent component is missing).
            throw new ArgumentException(
                $"Configured image directory '{imageDir}' does not exist.",
                "imageDir");
        }
        catch (UnauthorizedAccessException ex)
        {
            // existing-but-unenumerable directory → IO_ERROR.
            throw new IoOperationFailedException(
                imageDir,
                $"Configured image directory '{imageDir}' is not accessible: {ex.Message}",
                ex);
        }
        catch (System.IO.IOException ex)
        {
            // filesystem-level failure on enumeration probe → IO_ERROR.
            throw new IoOperationFailedException(
                imageDir,
                $"Configured image directory '{imageDir}' could not be enumerated: {ex.Message}",
                ex);
        }

        var escapedImageDir = EscapePowerShellString(imageDir);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$imageDir = '{escapedImageDir}'
if (-not (Test-Path $imageDir)) {{
    Write-Output '[]'
    return
}}

$images = @()
Get-ChildItem -Path $imageDir -Filter *.vhdx | ForEach-Object {{
    try {{
        $vhd = Get-VHD -Path $_.FullName -ComputerName localhost -ErrorAction Stop
        $images += [PSCustomObject]@{{
            Name       = $_.BaseName
            Path       = $_.FullName
            SizeGB     = [math]::Round($vhd.FileSize / 1GB, 2)
            MaxSizeGB  = [math]::Round($vhd.Size / 1GB, 2)
            VhdType    = $vhd.VhdType.ToString()
            ParentPath = if ($vhd.ParentPath) {{ $vhd.ParentPath }} else {{ $null }}
        }}
    }} catch {{
        # Skip files that can't be inspected (e.g., locked or corrupt)
        $images += [PSCustomObject]@{{
            Name       = $_.BaseName
            Path       = $_.FullName
            SizeGB     = [math]::Round($_.Length / 1GB, 2)
            MaxSizeGB  = 0
            VhdType    = 'Unknown'
            ParentPath = $null
        }}
    }}
}}

if ($images.Count -eq 0) {{
    Write-Output '[]'
}} else {{
    $images | ConvertTo-Json
}}
";

        _logger.LogDebug("Listing base images on host '{HostId}' from directory '{ImageDir}'", hostId, imageDir);

        PowerShellResult result;
        try
        {
            result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.UnauthorizedAccessException uaEx)
        {
            // ACL failure on enumeration → IO_ERROR (configured but unreadable).
            throw new IoOperationFailedException(
                imageDir!,
                $"Cannot enumerate image directory '{imageDir}': access denied.",
                uaEx);
        }
        catch (System.IO.IOException ioEx)
        {
            // filesystem-level failure on enumeration → IO_ERROR.
            throw new IoOperationFailedException(
                imageDir!,
                $"Cannot enumerate image directory '{imageDir}': {ioEx.Message}",
                ioEx);
        }

        try
        {
            HandleError(result, hostProfile.HostId, vmId: null);
        }
        catch (InvalidOperationException ioEx)
        {
            // Promote PowerShell enumeration errors (access denial or locked VHDX) to IO_ERROR,
            // not generic COMMAND_FAILED, so callers can distinguish unreadable storage from a wrong path.
            var stderr = ioEx.Message ?? string.Empty;
            if (stderr.Contains("UnauthorizedAccessException", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("PermissionDenied", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("IOException", StringComparison.OrdinalIgnoreCase))
            {
                throw new IoOperationFailedException(
                    imageDir!,
                    $"Cannot enumerate image directory '{imageDir}': {stderr}",
                    ioEx);
            }

            throw;
        }

        var images = ParseImageInfoList(result.Stdout);
        return new ImageListResult
        {
            Images = images,
            Count = images.Count,
            Configured = true,
            ImageDir = imageDir,
            Hint = null,
        };
    }

    public Task<VmInfo> WaitForReadyAsync(string hostId, string vmId, int timeoutSeconds = 300,
        CancellationToken ct = default) =>
        WaitForReadyAsync(hostId, vmId, new ReadinessBudget(timeoutSeconds, _readinessClock), ct: ct);

    public async Task<VmInfo> WaitForReadyAsync(
        string hostId, string vmId, ReadinessBudget budget,
        string? username = null, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var hostProfile = ResolveLocalHost(hostId);
        var (resolvedUsername, resolvedPassword) = CredentialResolver.ResolveCredentials(username, password);
        var safeUsername = CredentialResolver.RedactPasswordRepresentations(resolvedUsername, resolvedPassword);
        string? lastAccessObservation = null;
        var heartbeatScript = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost -ErrorAction SilentlyContinue
if (-not $vm) {{ throw 'VM not found: {safeVmId}' }}
$hb = (Get-VMIntegrationService -VM $vm | Where-Object {{$_.Name -eq 'Heartbeat'}}).PrimaryStatusDescription
if ($hb -eq 'OK') {{ Write-Output 'HVMCP_HEARTBEAT_OK' }}
";

        try
        {
            while (true)
            {
                budget.Check("retry", safeVmId, ct);
                budget.Check("status", safeVmId, ct);
                var vmInfo = await GetVmStatusAsync(hostId, safeVmId, ct).ConfigureAwait(false);
                budget.Record("status-completed");
                ct.ThrowIfCancellationRequested();
                budget.LastObservation = $"VM state is '{vmInfo.State}'; guest login is unconfirmed. " + lastAccessObservation;

                if (string.Equals(vmInfo.State, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    budget.Check("heartbeat", safeVmId, ct);
                    var heartbeat = await _psExecutor.ExecuteAsync(heartbeatScript, ct: ct).ConfigureAwait(false);
                    budget.Record("heartbeat-completed");
                    ct.ThrowIfCancellationRequested();
                    HandleError(heartbeat, hostProfile.HostId, safeVmId);
                    if (TokenMatcher.ContainsToken(heartbeat.Stdout, "HVMCP_HEARTBEAT_OK"))
                    {
                        budget.LastObservation =
                            "Running with heartbeat OK; heartbeat does not establish guest-login readiness. " + lastAccessObservation;
                        var route = await ResolveReadinessRouteAsync(hostProfile, safeVmId, budget, ct).ConfigureAwait(false);
                        var verdict = route.GuestOs == GuestOsKind.Linux
                            ? await _readinessAuthenticator.ConfirmLinuxAsync(safeVmId, route.SshHost!, route.SshPort,
                                resolvedUsername, resolvedPassword, budget, ct).ConfigureAwait(false)
                            : await _readinessAuthenticator.ConfirmWindowsAsync(safeVmId, vmInfo.Name,
                                resolvedUsername, resolvedPassword, budget, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (verdict.Kind == ReadinessVerdictKind.Ready)
                            return vmInfo;

                        budget.LastObservation = verdict.CredentialRejected
                            ? $"Credential rejection observed for username '{safeUsername}'. Verify credentials for the image, " +
                                "or wait and retry after a recent start; neither cause has been established."
                            : verdict.Observation;
                        lastAccessObservation = budget.LastObservation;
                        if (verdict.Kind == ReadinessVerdictKind.Terminal)
                            throw budget.Failure(safeVmId,
                                budget.Remaining <= TimeSpan.Zero ? "wait budget exhausted" : "observation failed");
                    }
                    else
                    {
                        budget.LastObservation =
                            "VM is Running but heartbeat is not OK; guest login is unconfirmed. " + lastAccessObservation;
                    }
                }

                await budget.DelayAsync(TimeSpan.FromSeconds(3), "retry-delay", safeVmId, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception failure) when (failure is not ReadinessNotReachedException
            and not VmNotFoundException and not HostNotFoundException and not GuestRoutingUnavailableException
            and not SshSessionOpenException and not NotSupportedException and not ConcurrencyLimitException)
        {
            budget.LastObservation = "Observation failed; the cause is unknown. Check host and guest access configuration.";
            throw budget.Failure(safeVmId, budget.Remaining <= TimeSpan.Zero ? "wait budget exhausted" : "observation failed");
        }
    }

    private async Task<GuestRoutingHint> ResolveReadinessRouteAsync(
        HostProfile profile, string vmId, ReadinessBudget budget, CancellationToken ct)
    {
        budget.Check("classification", vmId, ct);
        if (!_guestRoutingHintStore.TryGet(profile.HostId, vmId, out var hint))
        {
            budget.Check("kvp", vmId, ct);
            hint = await _readinessOsProbe.ProbeAsync(profile.HostId, vmId, ct).ConfigureAwait(false);
            budget.Record("kvp-completed");
            if (hint is null)
            {
                budget.Check("endpoint", vmId, ct);
                var endpoint = await _readinessOsProbe.ResolveVmScopedSshEndpointAsync(profile.HostId, vmId, ct)
                    .ConfigureAwait(false);
                budget.Record("endpoint-completed");
                if (endpoint is not null)
                {
                    budget.Check("banner", vmId, ct);
                    var responded = await _readinessBannerProbe(endpoint.Value.SshHost, endpoint.Value.SshPort, ct)
                        .ConfigureAwait(false);
                    budget.Record("banner-completed");
                    if (responded)
                        hint = new GuestRoutingHint(GuestOsKind.Linux, endpoint.Value.SshHost, endpoint.Value.SshPort);
                }
                if (hint is null)
                {
                    await budget.DelayAsync(TimeSpan.FromMilliseconds(250), "classification-settle", vmId, ct)
                        .ConfigureAwait(false);
                    budget.Check("kvp-retry", vmId, ct);
                    hint = await _readinessOsProbe.ProbeAsync(profile.HostId, vmId, ct).ConfigureAwait(false);
                    budget.Record("kvp-retry-completed");
                }
            }
            if (hint is not null)
                _guestRoutingHintStore.Record(profile.HostId, vmId, hint);
        }

        budget.Check("routing", vmId, ct);
        if (hint is null)
            throw new GuestRoutingUnavailableException(vmId,
                $"The guest OS of VM '{vmId}' was not determined, so no transport was selected. " +
                "The command can succeed once that guest's operating system becomes determinable.");

        if (hint.GuestOs == GuestOsKind.Linux && hint.HasUsableSshEndpoint)
            return hint;
        if (hint.GuestOs == GuestOsKind.Linux || profile.IsLinuxGuest)
        {
            budget.Check("host-endpoint", vmId, ct);
            var endpoint = profile.IsLinuxGuest ? profile.ResolveSshEndpoint() : null;
            if (endpoint is not null)
                return new GuestRoutingHint(GuestOsKind.Linux, endpoint.Value.SshHost, endpoint.Value.SshPort);
            if (hint.GuestOs == GuestOsKind.Linux)
                throw new GuestRoutingUnavailableException(vmId,
                    $"The Linux guest '{vmId}' has no reachable SSH endpoint; guest routing failed closed.");
            throw new SshSessionOpenException(vmId,
                $"Failed to open an SSH session to the Linux guest '{vmId}': no reachable SSH endpoint is configured.");
        }
        return hint;
    }

    /// <inheritdoc />
    /// <remarks>Only orphan-candidate rows may be destroyed (dryRun=false); needs-attention is report-only.
    /// Live and untagged VMs are omitted. See VmInfo.Reason; power state and VM name never classify ownership.</remarks>
    public async Task<IReadOnlyList<VmInfo>> CleanupOrphansAsync(string hostId, bool dryRun = true,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Plain Notes parsing keeps classification identical across Windows and Linux/PowerShell-Direct; only orphan-candidates may be destroyed.
        var dryRunFlag = dryRun ? "$true" : "$false";
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$dryRun = {dryRunFlag}
$cutoffTime = (Get-Date).AddHours(-24)

# WMI workaround : -ComputerName localhost avoids null-name WMI bug
$allVms = Get-VM -Name '*' -ComputerName localhost -ErrorAction SilentlyContinue
if (-not $allVms) {{ $allVms = @() }}

$tagged = @($allVms | Where-Object {{ $_.Notes -like '*hyper-v-mcp:*' }})

$orphans = @()
foreach ($vm in $tagged) {{
    $reason = $null
    # Anchor the role key on BOTH segment boundaries (start-of-string or after a
    # ';'/whitespace delimiter) so a spoof segment like 'notrole=ephemeral;' cannot
    # satisfy the ephemeral predicate and become an orphan-candidate.
    $isEphemeral = $vm.Notes -match '(?:^|[\s;])role=ephemeral(?:$|[\s;])'
    $createdAt = $null
    # Anchor the created key on its left segment boundary too, and terminate the
    # value at end/';'/whitespace so no adjacent segment can spoof or glue onto it.
    if ($vm.Notes -match '(?:^|[\s;])hyper-v-mcp:created=([^\s;]+)') {{
        try {{
            $createdAt = [DateTimeOffset]::Parse($Matches[1])
        }} catch {{
            $createdAt = $null
        }}
    }}

    if ($isEphemeral -and $null -ne $createdAt -and $createdAt -lt $cutoffTime) {{
        $reason = 'orphan-candidate'
    }} elseif ($isEphemeral -and $null -ne $createdAt) {{
        # Ephemeral + within cutoff -> live, skip entirely.
        $reason = $null
    }} else {{
        # Fail-closed: owned but not a clean ephemeral+aged row -> report only.
        $reason = 'needs-attention'
    }}

    if ($null -ne $reason) {{
        # Only 'orphan-candidate' rows are eligible for destroy ( parity).
        if ($reason -eq 'orphan-candidate' -and -not $dryRun) {{
            # Destroy: stop + remove + cleanup VHDX (same as DestroyVmAsync)
            $vm | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue
            $vhdPaths = (Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue).Path
            Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue
            foreach ($path in $vhdPaths) {{
                if ($path -and (Test-Path $path)) {{
                    Remove-Item -Path $path -Force -ErrorAction SilentlyContinue
                }}
            }}
        }}
        $orphans += [PSCustomObject]@{{
            Id = $vm.Id
            Name = $vm.Name
            State = $vm.State
            ProcessorCount = $vm.ProcessorCount
            MemoryMB = $vm.MemoryStartup / 1MB
            UptimeSeconds = $vm.Uptime.TotalSeconds
            Reason = $reason
        }}
    }}
}}

# Handle null and empty orphan results explicitly; force array JSON even for one row (depth 4).
if ($null -eq $orphans -or @($orphans).Count -eq 0) {{
    Write-Output '[]'
}} else {{
    ConvertTo-Json -InputObject @($orphans) -Depth 4
}}
";

        _logger.LogInformation("Cleaning up orphaned VMs on host '{HostId}' (dryRun={DryRun})",
            hostId, dryRun);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 300, ct: ct);
        HandleError(result, hostProfile.HostId, vmId: null);

        // Filter degenerate empty rows only here so other VM parsers retain their behavior.
        var parsed = ParseVmInfoList(result.Stdout, hostProfile.HostId);
        return FilterEmptyOrphanRows(parsed, _logger, hostProfile.HostId, dryRun, result.Stdout);
    }

    /// <summary>Drops cleanup rows with both empty VmId and Name; logs one structured warning when any are dropped
    /// so PS-host regressions remain visible without changing the envelope.
    /// This is the only C# defense layer; ParseVmInfoList and MapJsonToVmInfo retain other callers' behavior.</summary>
    private static IReadOnlyList<VmInfo> FilterEmptyOrphanRows(
        IReadOnlyList<VmInfo> rows,
        ILogger<HyperVManager> logger,
        string hostId,
        bool dryRun,
        string stdoutPreview)
    {
        if (rows is null || rows.Count == 0)
        {
            return rows ?? Array.Empty<VmInfo>();
        }

        var kept = new List<VmInfo>(rows.Count);
        int dropped = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.VmId) && string.IsNullOrWhiteSpace(row.Name))
            {
                dropped++;
                continue;
            }
            kept.Add(row);
        }

        if (dropped > 0)
        {
            var preview = stdoutPreview ?? string.Empty;
            if (preview.Length > 512)
            {
                preview = preview.Substring(0, 512);
            }

            logger.LogWarning(
                "vm_cleanup_orphans: empty orphan rows filtered (hostId={HostId}, droppedCount={DroppedCount}, dryRun={DryRun}, stdoutPreview={StdoutPreview})",
                hostId, dropped, dryRun, preview);
        }

        return kept.AsReadOnly();
    }


    /// <summary>
    /// Autounattend.xml template for unattended Windows 11 installation.
    /// Placeholders: {locale}, {windowsEdition}, {adminPassword}, {productKeyElement}, {vmName}.
    /// </summary>
    private const string AutounattendTemplate = @"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
  <settings pass=""windowsPE"">
    <component name=""Microsoft-Windows-International-Core-WinPE""
               processorArchitecture=""amd64"" language=""neutral""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <SetupUILanguage>
        <UILanguage>{locale}</UILanguage>
      </SetupUILanguage>
      <InputLocale>{locale}</InputLocale>
      <SystemLocale>{locale}</SystemLocale>
      <UILanguage>{locale}</UILanguage>
      <UserLocale>{locale}</UserLocale>
    </component>
    <component name=""Microsoft-Windows-Setup""
               processorArchitecture=""amd64"" language=""neutral""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>1</Order>
          <Path>cmd.exe /c "">>X:\diskpart.txt (echo SELECT DISK=0&amp;echo CLEAN&amp;echo CONVERT GPT&amp;echo CREATE PARTITION EFI SIZE=300&amp;echo FORMAT QUICK FS=FAT32 LABEL=""System""&amp;echo ASSIGN LETTER=S&amp;echo CREATE PARTITION MSR SIZE=16&amp;echo CREATE PARTITION PRIMARY&amp;echo FORMAT QUICK FS=NTFS LABEL=""Windows""&amp;echo ASSIGN LETTER=W)""</Path>
          <Description>Write diskpart script</Description>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>2</Order>
          <Path>cmd.exe /c ""diskpart.exe /s X:\diskpart.txt >>X:\diskpart.log 2>&amp;1 || ( type X:\diskpart.log &amp; echo diskpart.exe encountered an error. &amp; pause &amp; exit /b 1 )""</Path>
          <Description>Partition disk</Description>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>3</Order>
          <Path>cmd.exe /c ""dism.exe /Apply-Image /ImageFile:D:\sources\install.wim /Name:""{windowsEdition}"" /ApplyDir:W:\ || dism.exe /Apply-Image /ImageFile:E:\sources\install.wim /Name:""{windowsEdition}"" /ApplyDir:W:\ || ( echo dism.exe encountered an error. &amp; pause &amp; exit /b 1 )""</Path>
          <Description>Apply Windows image with DISM</Description>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>4</Order>
          <Path>cmd.exe /c ""bcdboot.exe W:\Windows /s S: || ( echo bcdboot.exe encountered an error. &amp; pause &amp; exit /b 1 )""</Path>
          <Description>Configure boot</Description>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>5</Order>
          <Path>cmd.exe /c ""mkdir W:\Windows\Panther 2>nul &amp; ( copy D:\unattend.xml W:\Windows\Panther\unattend.xml || copy E:\unattend.xml W:\Windows\Panther\unattend.xml )""</Path>
          <Description>Copy unattend.xml for specialize/oobe passes</Description>
        </RunSynchronousCommand>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>6</Order>
          <Path>wpeutil.exe reboot</Path>
          <Description>Reboot into installed Windows</Description>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
  <settings pass=""oobeSystem"">
    <component name=""Microsoft-Windows-International-Core""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <InputLocale>{locale}</InputLocale>
      <SystemLocale>{locale}</SystemLocale>
      <UILanguage>{locale}</UILanguage>
      <UserLocale>{locale}</UserLocale>
    </component>
    <component name=""Microsoft-Windows-Shell-Setup""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>Administrator</Username>
        <Password>
          <Value>{adminPassword}</Value>
          <PlainText>true</PlainText>
        </Password>
        <LogonCount>3</LogonCount>
      </AutoLogon>
      <UserAccounts>
        <AdministratorPassword>
          <Value>{adminPassword}</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
      </UserAccounts>
      <ComputerName>{vmName}</ComputerName>
    </component>
  </settings>
  <settings pass=""specialize"">
    <component name=""Microsoft-Windows-Shell-Setup""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <ComputerName>{vmName}</ComputerName>
    </component>
    <component name=""Microsoft-Windows-Deployment""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>1</Order>
          <Path>net user administrator /active:yes</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
</unattend>";

    /// <summary>WinPE DISM copies this specialize/oobeSystem template to W:\Windows\Panther\unattend.xml.
    /// Exclude windowsPE: its complex cmd.exe paths cause answer-file parse errors. Every component needs publicKeyToken.
    /// Placeholders: {locale}, {adminPassword}, {vmName}.</summary>
    internal const string PantherUnattendTemplate = @"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
  <settings pass=""oobeSystem"">
    <component name=""Microsoft-Windows-International-Core""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <InputLocale>{locale}</InputLocale>
      <SystemLocale>{locale}</SystemLocale>
      <UILanguage>{locale}</UILanguage>
      <UserLocale>{locale}</UserLocale>
    </component>
    <component name=""Microsoft-Windows-Shell-Setup""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <AutoLogon>
        <Enabled>true</Enabled>
        <Username>Administrator</Username>
        <Password>
          <Value>{adminPassword}</Value>
          <PlainText>true</PlainText>
        </Password>
        <LogonCount>3</LogonCount>
      </AutoLogon>
      <UserAccounts>
        <AdministratorPassword>
          <Value>{adminPassword}</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
      </UserAccounts>
      <ComputerName>{vmName}</ComputerName>
    </component>
  </settings>
  <settings pass=""specialize"">
    <component name=""Microsoft-Windows-Shell-Setup""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <ComputerName>{vmName}</ComputerName>
    </component>
    <component name=""Microsoft-Windows-Deployment""
               processorArchitecture=""amd64"" language=""neutral""
               publicKeyToken=""31bf3856ad364e35"" versionScope=""nonSxS""
               xmlns:wcm=""http://schemas.microsoft.com/WMIConfig/2002/State"">
      <RunSynchronous>
        <RunSynchronousCommand wcm:action=""add"">
          <Order>1</Order>
          <Path>net user administrator /active:yes</Path>
        </RunSynchronousCommand>
      </RunSynchronous>
    </component>
  </settings>
</unattend>";

    /// <inheritdoc /> <remarks> Implements the 13-step ISO installation orchestration from design doc. Steps 1-8
    /// failure: full rollback (destroy VM + delete artifacts). Step 9 timeout: preserve VM, return INSTALL_TIMEOUT
    /// error with VM info. Steps 9-11 failure: preserve VM, return error with VM info + cleanup artifacts. </remarks>
    public async Task<OsInstallResult> OsInstallAsync(
        string hostId,
        string name,
        string isoPath,
        string adminPassword,
        int cpuCount = 4,
        long memoryMB = 8192,
        int diskSizeGB = 127,
        string? switchName = null,
        string locale = "en-US",
        string windowsEdition = "Windows 11 Pro",
        string? productKey = null,
        int timeoutMinutes = 60,
        bool skipPreflight = false,
        string? guestUsername = null,
        CancellationToken ct = default)
    {

        // (1) ISO existence — fail fast with a typed exception so ErrorMapper emits
        // ISO_NOT_FOUND. The PS script also defends against this, but doing it here
        // gives a structured error before any process spawn.
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
        {
            throw new IsoNotFoundException(isoPath ?? string.Empty);
        }

        // Classify media once before VM/job work; skipPreflight never bypasses this check.
        // Reuse the mounted Ubuntu GRUB configuration for the capability check rather than mounting twice.
        var classification = await _guestOsClassifier.ClassifyMediaAsync(isoPath, ct).ConfigureAwait(false);
        var installTarget = classification.Target;

        if (installTarget == InstallTarget.Unsupported)
        {
            _logger.LogWarning("vm_os_install rejected unsupported ISO '{IsoPath}'.", isoPath);
            throw new OsNotSupportedException(isoPath);
        }

        if (installTarget == InstallTarget.UbuntuServer2404)
        {
            // Reject unsupported autoinstall media before resource checks, request construction or orchestration; skipPreflight cannot bypass it.
            // Without the autoinstall kernel argument, stock media waits at subiquity's consent prompt for the whole timeout.
            var capability = AutoinstallCapabilityEvaluator.Evaluate(classification.GrubConfiguration);
            if (capability != AutoinstallCapability.Capable)
            {
                _logger.LogWarning(
                    "vm_os_install refused Ubuntu ISO '{IsoPath}': autoinstall capability was {Capability}.",
                    isoPath, capability);
                throw new LinuxPreconditionUnmetException(
                    AutoinstallCapabilityEvaluator.BuildRefusalMessage(capability, isoPath));
            }

            // Validate guestUsername early for Ubuntu; default to ubuntu. Windows ignores it.
            var resolvedGuestUsername = string.IsNullOrWhiteSpace(guestUsername)
                ? "ubuntu"
                : InputValidation.ValidateGuestUsername(guestUsername);

            // Ubuntu resource floors (1 vCPU / 2048 MB / 12 GB), still skipPreflight-gated (A.2).
            // The Windows-11 floors below do NOT apply to Ubuntu.
            if (!skipPreflight)
            {
                if (cpuCount < 1)
                {
                    throw new InsufficientResourcesException(
                        failedFloor: "cpuCount", minimum: 1, actual: cpuCount,
                        message: $"Ubuntu Server 24.04 requires minimum 1 vCPU (got {cpuCount}). Pass skipPreflight=true to bypass.");
                }
                if (memoryMB < 2048)
                {
                    throw new InsufficientResourcesException(
                        failedFloor: "memoryMB", minimum: 2048, actual: memoryMB,
                        message: $"Ubuntu Server 24.04 requires minimum 2048 MB RAM (got {memoryMB}). Pass skipPreflight=true to bypass.");
                }
                if (diskSizeGB < 12)
                {
                    throw new InsufficientResourcesException(
                        failedFloor: "diskSizeGB", minimum: 12, actual: diskSizeGB,
                        message: $"Ubuntu Server 24.04 requires minimum 12 GB disk (got {diskSizeGB}). Pass skipPreflight=true to bypass.");
                }
            }

            _logger.LogInformation(
                "Dispatching Ubuntu Server 24.04 autoinstall for VM '{VmName}' on host '{HostId}' from ISO '{IsoPath}'.",
                name, hostId, isoPath);

            var ubuntuRequest = new UbuntuInstallRequest
            {
                HostId = hostId,
                Name = name,
                IsoPath = isoPath,
                AdminPassword = adminPassword,
                GuestUsername = resolvedGuestUsername,
                CpuCount = cpuCount,
                MemoryMB = memoryMB,
                DiskSizeGB = diskSizeGB,
                SwitchName = switchName,
                Locale = locale,
                TimeoutMinutes = timeoutMinutes,
            };
            return await _ubuntuOrchestrator.InstallAsync(ubuntuRequest, ct).ConfigureAwait(false);
        }


        // (3) Resource-floor preflight — short-circuit on first failure.
        // Skipped entirely when skipPreflight=true. Caller-supplied values are surfaced
        // in the structured error so retries can adjust the right knob.
        if (!skipPreflight)
        {
            if (cpuCount < 2)
            {
                throw new InsufficientResourcesException(
                    failedFloor: "cpuCount",
                    minimum: 2,
                    actual: cpuCount,
                    message: $"Windows 11 requires minimum 2 vCPUs (got {cpuCount}). Pass skipPreflight=true to bypass.");
            }
            if (memoryMB < 4096)
            {
                throw new InsufficientResourcesException(
                    failedFloor: "memoryMB",
                    minimum: 4096,
                    actual: memoryMB,
                    message: $"Windows 11 requires minimum 4096 MB RAM (got {memoryMB}). Pass skipPreflight=true to bypass.");
            }
            if (diskSizeGB < 64)
            {
                throw new InsufficientResourcesException(
                    failedFloor: "diskSizeGB",
                    minimum: 64,
                    actual: diskSizeGB,
                    message: $"Windows 11 requires minimum 64 GB disk (got {diskSizeGB}). Pass skipPreflight=true to bypass.");
            }
        }

        var hostProfile = ResolveLocalHost(hostId);

        // Resolve storage root: env var > host profile config > default.
        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;

        // Resolve switch name: parameter > env var > host profile > "Default Switch".
        var resolvedSwitch = switchName
            ?? Environment.GetEnvironmentVariable("HYPERV_MCP_DEFAULT_SWITCH")
            ?? hostProfile.DefaultSwitch
            ?? "Default Switch";

        // Escape strings for PowerShell single-quoted interpolation.
        var escapedName = EscapePowerShellString(name);
        var escapedIsoPath = EscapePowerShellString(isoPath);
        var escapedAdminPassword = EscapePowerShellString(adminPassword);
        var escapedStorageRoot = EscapePowerShellString(storageRoot);
        var escapedSwitch = EscapePowerShellString(resolvedSwitch);
        var escapedLocale = EscapePowerShellString(locale);
        var escapedEdition = EscapePowerShellString(windowsEdition);

        // When no product key is supplied, published KMS client setup keys avoid Windows Setup prompts but do not activate Windows.
        // See https://learn.microsoft.com/windows-server/get-started/kms-client-activation-keys
        var genericKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Windows 11 Pro"] = "W269N-WFGWX-YVC9B-4J6C9-T83GX",
            ["Windows 11 Home"] = "TX9XD-98N7V-6WMQ6-BX7FG-H8Q99",
            ["Windows 11 Enterprise"] = "NPPR9-FWDCX-D2C8J-H872K-2YT43",
            ["Windows 11 Education"] = "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2",
            ["Windows 11 Pro for Workstations"] = "NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J",
        };

        string productKeyElement;
        if (!string.IsNullOrWhiteSpace(productKey))
        {
            productKeyElement = $"<ProductKey><Key>{System.Security.SecurityElement.Escape(productKey)}</Key></ProductKey>";
        }
        else if (genericKeys.TryGetValue(windowsEdition, out var genericKey))
        {
            productKeyElement = $"<ProductKey><Key>{genericKey}</Key></ProductKey>";
        }
        else
        {
            // Unknown edition — omit key element
            productKeyElement = $"<!-- No product key provided for edition: {windowsEdition} -->";
        }

        // Truncate VM name to 15 chars for NetBIOS computer name limit.
        var computerName = name.Length > 15 ? name.Substring(0, 15) : name;

        var autounattendXml = AutounattendTemplate
            .Replace("{locale}", System.Security.SecurityElement.Escape(locale))
            .Replace("{windowsEdition}", System.Security.SecurityElement.Escape(windowsEdition))
            .Replace("{adminPassword}", System.Security.SecurityElement.Escape(adminPassword))
            .Replace("{productKeyElement}", productKeyElement)
            .Replace("{vmName}", System.Security.SecurityElement.Escape(computerName));

        // Escape the XML for embedding in PowerShell here-string.
        var escapedAutounattendXml = EscapePowerShellString(autounattendXml);

        // Generate the stripped-down panther unattend.xml (specialize + oobeSystem only).
        // This file is written UTF-8 WITHOUT BOM — the Windows Setup specialize parser
        // rejects files with BOM. It must include publicKeyToken on all component elements.
        var pantherUnattendXml = PantherUnattendTemplate
            .Replace("{locale}", System.Security.SecurityElement.Escape(locale))
            .Replace("{adminPassword}", System.Security.SecurityElement.Escape(adminPassword))
            .Replace("{vmName}", System.Security.SecurityElement.Escape(computerName));
        var escapedPantherXml = EscapePowerShellString(pantherUnattendXml);

        // The IMAPI2 marshalling workaround MUST have a single definition; divergent copies have
        // silently broken the stream cast before.
        var autounattendImapi2Block = MediaAuthoringScripts.BuildImapi2AuthoringBlock(
            sourceDirExpression: "$autounattendDir",
            outputPathExpression: "$autounattendIsoPath",
            volumeLabel: "AUTOUNATTEND",
            fileSystemsToCreate: MediaAuthoringScripts.FileSystemsUdf,
            failureMessagePrefix: "Failed to create autounattend ISO");

        _logger.LogInformation(
            "Starting OS installation for VM '{VmName}' on host '{HostId}' from ISO '{IsoPath}' " +
            "with {CpuCount} CPUs, {MemoryMB}MB RAM, {DiskSizeGB}GB disk, timeout={TimeoutMinutes}min",
            name, hostId, isoPath, cpuCount, memoryMB, diskSizeGB, timeoutMinutes);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
Import-Module Hyper-V -ErrorAction Stop

$name = '{escapedName}'
$isoPath = '{escapedIsoPath}'
$adminPassword = '{escapedAdminPassword}'
$storageRoot = '{escapedStorageRoot}'
$switchName = '{escapedSwitch}'
$cpuCount = {cpuCount}
$memoryBytes = {memoryMB} * 1MB
$diskSizeBytes = {diskSizeGB} * 1GB
$timeoutMinutes = {timeoutMinutes}

$vmDir = Join-Path $storageRoot $name
$vhdxPath = Join-Path $vmDir ""$name.vhdx""
$autounattendGuid = [Guid]::NewGuid().ToString('N').Substring(0,8)
$autounattendDir = Join-Path $storageRoot ""autounattend\$autounattendGuid""
$autounattendIsoPath = Join-Path $autounattendDir 'autounattend.iso'
$warnings = @()
$vmCreated = $false

# Rollback function for pre-installation failures (Steps 1-8)
function Invoke-FullRollback {{
    try {{
        $created = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
        if ($created) {{
            $created | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue
            $vhds = (Get-VMHardDiskDrive -VM $created -ErrorAction SilentlyContinue).Path
            Remove-VM -VM $created -Force -ErrorAction SilentlyContinue
            foreach ($v in $vhds) {{ if ($v -and (Test-Path $v)) {{ Remove-Item $v -Force -ErrorAction SilentlyContinue }} }}
        }}
    }} catch {{ }}
    try {{
        if (Test-Path $vmDir) {{ Remove-Item -Recurse -Force $vmDir -ErrorAction SilentlyContinue }}
    }} catch {{ }}
    try {{
        if (Test-Path $autounattendDir) {{ Remove-Item -Recurse -Force $autounattendDir -ErrorAction SilentlyContinue }}
    }} catch {{ }}
}}

# Cleanup function for post-installation (temp artifacts only)
function Invoke-ArtifactCleanup {{
    try {{
        if (Test-Path $autounattendDir) {{
            Remove-Item -Recurse -Force $autounattendDir -ErrorAction Stop
        }}
    }} catch {{
        $script:warnings += ""Failed to clean up autounattend artifacts at $autounattendDir`: $($_.Exception.Message)""
    }}
}}

try {{
    # Resource floors run in C# for structured INSUFFICIENT_RESOURCES errors and honor skipPreflight.
    # The mandatory OS-family check rejects non-Windows media before this script runs.
    if (-not (Test-Path $isoPath)) {{
        [PSCustomObject]@{{
            success   = $false
            error     = ""ISO file not found: $isoPath""
            errorCode = 'ISO_NOT_FOUND'
        }} | ConvertTo-Json -Depth 5
        return
    }}

    $existing = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
    if ($existing) {{ throw ""VM with name '$name' already exists"" }}

    New-Item -ItemType Directory -Path $vmDir -Force | Out-Null
    New-VHD -Path $vhdxPath -SizeBytes $diskSizeBytes -Dynamic -ComputerName localhost | Out-Null

    New-VM -Name $name -Generation 2 -MemoryStartupBytes $memoryBytes `
           -VHDPath $vhdxPath -SwitchName $switchName -ComputerName localhost | Out-Null
    $vmCreated = $true

    Set-VMProcessor -VMName $name -Count $cpuCount -ComputerName localhost
    Set-VMFirmware -VMName $name -EnableSecureBoot On -ComputerName localhost
    Set-VMKeyProtector -VMName $name -NewLocalKeyProtector -ComputerName localhost
    Enable-VMTPM -VMName $name -ComputerName localhost
    Set-VM -Name $name -Notes ""hyper-v-mcp:created=$(Get-Date -Format o);type=iso-install;role=ephemeral"" `
           -ComputerName localhost

    New-Item -ItemType Directory -Path $autounattendDir -Force | Out-Null
    $xmlPath = Join-Path $autounattendDir 'Autounattend.xml'
    $xmlContent = '{escapedAutounattendXml}'
    $xmlContent | Out-File -FilePath $xmlPath -Encoding UTF8

    # Write stripped-down unattend.xml (specialize/oobe only) — UTF-8 WITHOUT BOM.
    # The Windows Setup specialize pass parser rejects files with BOM.
    $pantherXmlPath = Join-Path $autounattendDir 'unattend.xml'
    $pantherXml = '{escapedPantherXml}'
    [System.IO.File]::WriteAllText($pantherXmlPath, $pantherXml, [System.Text.UTF8Encoding]::new($false))

    $oscdimg = Get-Command oscdimg.exe -ErrorAction SilentlyContinue
    if ($oscdimg) {{
        & oscdimg.exe -u2 -udfver102 $autounattendDir $autounattendIsoPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {{ throw ""oscdimg.exe failed with exit code $LASTEXITCODE"" }}
    }} else {{
{autounattendImapi2Block}
    }}

    if (-not (Test-Path $autounattendIsoPath)) {{
        throw ""Autounattend ISO was not created at $autounattendIsoPath""
    }}

    Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation 1 `
                   -Path $isoPath -ComputerName localhost
    Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation 2 `
                   -Path $autounattendIsoPath -ComputerName localhost

    $dvd = Get-VMDvdDrive -VMName $name -ComputerName localhost |
           Where-Object {{ $_.ControllerLocation -eq 1 }}
    Set-VMFirmware -VMName $name -FirstBootDevice $dvd -ComputerName localhost

    Start-VM -Name $name -ComputerName localhost
    $installStartTime = Get-Date

    # Send keystrokes to bypass DVD boot prompt (Gen 2 UEFI).
    # TypeScancodes with raw make/break codes is the only reliable method;
    # TypeKey/PressKey return 32773 (invalid parameter) on modern Windows.
    try {{
        Start-Sleep -Seconds 3
        $vmWmi = Get-WmiObject -Namespace 'root\virtualization\v2' -Class 'Msvm_ComputerSystem' |
                 Where-Object {{ $_.ElementName -eq $name }}
        $keyboard = $vmWmi.GetRelated('Msvm_Keyboard')
        # Space key: 0x39 = make (press), 0xB9 = break (release)
        for ($i = 0; $i -lt 5; $i++) {{
            $keyboard.TypeScancodes(@(0x39, 0xB9)) | Out-Null
            Start-Sleep -Milliseconds 500
        }}
    }} catch {{
        Write-Warning ""Could not send boot keystrokes: $($_.Exception.Message)""
    }}

}} catch {{
    # Pre-installation failure (Steps 1-8): full rollback
    if ($vmCreated) {{
        Invoke-FullRollback
    }} else {{
        # Clean up just the artifacts (VHDX dir + autounattend dir)
        try {{ if (Test-Path $vmDir) {{ Remove-Item -Recurse -Force $vmDir -ErrorAction SilentlyContinue }} }} catch {{ }}
        try {{ if (Test-Path $autounattendDir) {{ Remove-Item -Recurse -Force $autounattendDir -ErrorAction SilentlyContinue }} }} catch {{ }}
    }}
    throw
}}

# Post-step-8: failures preserve VM (no full rollback)
$timeoutAt = $installStartTime.AddMinutes($timeoutMinutes)
$phase = 'installing'
$previousUptime = [TimeSpan]::Zero
$dvdsUnmounted = $false

$guestCredential = New-Object System.Management.Automation.PSCredential(
    'Administrator',
    (ConvertTo-SecureString $adminPassword -AsPlainText -Force)
)

while ((Get-Date) -lt $timeoutAt) {{
    $vm = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
    if (-not $vm) {{
        Start-Sleep -Seconds 15
        continue
    }}

    if ($vm.State -ne 'Running') {{
        Start-Sleep -Seconds 5
        continue
    }}

    # Detect reboot via uptime reset and unmount DVDs to prevent
    # Autounattend.xml from being re-read during specialize pass
    $currentUptime = $vm.Uptime
    if ($previousUptime.TotalSeconds -gt 60 -and $currentUptime.TotalSeconds -lt $previousUptime.TotalSeconds) {{
        if (-not $dvdsUnmounted) {{
            $dvdsUnmounted = $true
            try {{
                Get-VMDvdDrive -VMName $name -ComputerName localhost | ForEach-Object {{
                    Set-VMDvdDrive -VMName $name -ControllerNumber $_.ControllerNumber `
                                   -ControllerLocation $_.ControllerLocation -Path $null `
                                   -ComputerName localhost
                }}
                $hdd = Get-VMHardDiskDrive -VMName $name -ComputerName localhost | Select-Object -First 1
                if ($hdd) {{ Set-VMFirmware -VMName $name -FirstBootDevice $hdd -ComputerName localhost }}
            }} catch {{ }}
        }}
    }}
    if ($currentUptime) {{ $previousUptime = $currentUptime }}

    $hb = Get-VMIntegrationService -VM $vm -ErrorAction SilentlyContinue |
          Where-Object {{ $_.Name -eq 'Heartbeat' }}
    $heartbeat = if ($hb) {{ $hb.PrimaryStatusDescription }} else {{ 'NotAvailable' }}

    if ($heartbeat -eq 'OK') {{
        $phase = 'os-ready'
        try {{
            # Use PowerShell Direct via -VMId; the prior VirtualizationException investigation
            # traced failures in no-console PowerShell to module autoload behavior, mitigated
            # by explicit module imports rather than a WMI-specific workaround.
            $session = New-PSSession -VMId $vm.Id -Credential $guestCredential `
                                     -ErrorAction Stop
            Remove-PSSession $session -ErrorAction SilentlyContinue
            $phase = 'completed'
            break
        }} catch {{
            $phase = 'os-ready'
        }}
    }}

    Start-Sleep -Seconds 15
}}

$installDuration = ((Get-Date) - $installStartTime).TotalSeconds

if ($phase -ne 'completed') {{
    # Timeout: preserve VM, clean up artifacts, return timeout error
    Invoke-ArtifactCleanup
    $vm = Get-VM -Name $name -ComputerName localhost -ErrorAction SilentlyContinue
    [PSCustomObject]@{{
        success   = $false
        error     = ""Installation timed out after $timeoutMinutes minutes (last phase: $phase)""
        errorCode = 'INSTALL_TIMEOUT'
        data      = @{{
            vmId  = $vm.Id.ToString()
            name  = $name
            state = $vm.State.ToString()
            installationDurationSeconds = [int]$installDuration
            lastPhase = $phase
        }}
    }} | ConvertTo-Json -Depth 5
    return
}}

try {{
    Get-VMDvdDrive -VMName $name -ComputerName localhost | ForEach-Object {{
        Set-VMDvdDrive -VMName $name -ControllerNumber $_.ControllerNumber `
                       -ControllerLocation $_.ControllerLocation -Path $null `
                       -ComputerName localhost
    }}

    $hdd = Get-VMHardDiskDrive -VMName $name -ComputerName localhost | Select-Object -First 1
    if ($hdd) {{
        Set-VMFirmware -VMName $name -FirstBootDevice $hdd -ComputerName localhost
    }}
}} catch {{
    $warnings += ""Failed to unmount ISOs or set boot order: $($_.Exception.Message)""
}}

$bootstrapStartTime = Get-Date
$bootstrapSuccess = $false
$guestIp = $null

try {{
    # Wait for PS Direct session to be reliably available
    $bootstrapTimeout = $installStartTime.AddMinutes($timeoutMinutes)
    $session = $null
    while ((Get-Date) -lt $bootstrapTimeout) {{
        try {{
            $vm = Get-VM -Name $name -ComputerName localhost
            # Explicit Security and Hyper-V imports avoid misleading New-PSSession VirtualizationException from module autoload failures.
            # -VMId is only the addressing form, not a WMI workaround.
            $session = New-PSSession -VMId $vm.Id -Credential $guestCredential -ErrorAction Stop
            break
        }} catch {{
            Start-Sleep -Seconds 5
        }}
    }}

    if (-not $session) {{
        throw ""Failed to establish PS Direct session for bootstrap""
    }}

    # Run idempotent bootstrap steps inside guest
    $bootstrapResult = Invoke-Command -Session $session -ScriptBlock {{
        $ErrorActionPreference = 'Stop'

        Enable-PSRemoting -Force -SkipNetworkProfileCheck 2>&1 | Out-Null

        Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Force -Scope LocalMachine 2>&1 | Out-Null

        Set-Service -Name WinRM -StartupType Automatic 2>&1 | Out-Null
        Start-Service -Name WinRM -ErrorAction SilentlyContinue 2>&1 | Out-Null

        $rule = Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP' -ErrorAction SilentlyContinue
        if (-not $rule) {{
            New-NetFirewallRule -Name 'WINRM-HTTP-In-TCP' -DisplayName 'WinRM HTTP' `
                -Direction Inbound -Protocol TCP -LocalPort 5985 -Action Allow 2>&1 | Out-Null
        }}

        $ip = (Get-NetIPAddress -AddressFamily IPv4 |
               Where-Object {{ $_.InterfaceAlias -notlike '*Loopback*' -and $_.IPAddress -ne '127.0.0.1' }} |
               Select-Object -First 1).IPAddress

        @{{ success = $true; ip = $ip }}
    }} -ErrorAction Stop

    if ($bootstrapResult.ip) {{
        $guestIp = $bootstrapResult.ip
    }}

    Remove-PSSession $session -ErrorAction SilentlyContinue
    $bootstrapSuccess = $true
}} catch {{
    $bootstrapError = $_.Exception.Message
    # Preserve VM — do NOT rollback after installation
}}

$bootstrapDuration = ((Get-Date) - $bootstrapStartTime).TotalSeconds

Invoke-ArtifactCleanup

$vm = Get-VM -Name $name -ComputerName localhost

if (-not $bootstrapSuccess -and $bootstrapError) {{
    # Bootstrap failed — return error (preserve VM per rollback policy)
    [PSCustomObject]@{{
        success   = $false
        error     = ""Bootstrap failed: $bootstrapError""
        errorCode = 'INSTALL_FAILED'
        data      = @{{
            vmId  = $vm.Id.ToString()
            name  = $name
            state = $vm.State.ToString()
            installationDurationSeconds = [int]$installDuration
            bootstrapDurationSeconds = [int]$bootstrapDuration
            lastPhase = 'bootstrap-failed'
            warnings = @($warnings)
        }}
    }} | ConvertTo-Json -Depth 5
    return
}}

[PSCustomObject]@{{
    success                    = $true
    data = [PSCustomObject]@{{
        VmId                       = $vm.Id.ToString()
        Name                       = $vm.Name
        State                      = $vm.State.ToString()
        ProcessorCount             = $vm.ProcessorCount
        MemoryMB                   = [long]($vm.MemoryStartup / 1MB)
        InstallationDurationSeconds = [int]$installDuration
        BootstrapDurationSeconds   = [int]$bootstrapDuration
        TotalDurationSeconds       = [int]($installDuration + $bootstrapDuration)
        GuestIpAddress             = $guestIp
        Warnings                   = @($warnings)
    }}
}} | ConvertTo-Json -Depth 5
";

        // The overall timeout for the PS executor should exceed the installation timeout
        // to allow for VM creation + installation + bootstrap + cleanup.
        var psTimeoutSeconds = (timeoutMinutes + 10) * 60;

        // A crash can leave the executor's temporary script containing plaintext credentials (admin password/product key).
        // Track files before/after to provide secondary cleanup beyond the executor's finally block.
        var tempDir = Path.GetTempPath();
        var preExistingTempFiles = new HashSet<string>(
            Directory.GetFiles(tempDir, "hvmcp-*.ps1"), StringComparer.OrdinalIgnoreCase);

        PowerShellResult result;
        try
        {
            // OS-install scripts use a variable-backed credential pattern and embed
            // plaintext admin passwords in unattended XML; the v1 script-dump masker cannot
            // redact those, so dumping is disabled for this code path.
            result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: psTimeoutSeconds, ct: ct, allowDump: false);
        }
        finally
        {
            // Secondary cleanup: delete any hvmcp-*.ps1 temp files created during this execution
            // that were not cleaned up by PowerShellExecutor (e.g., due to process crash).
            try
            {
                foreach (var tempFile in Directory.GetFiles(tempDir, "hvmcp-*.ps1"))
                {
                    if (!preExistingTempFiles.Contains(tempFile))
                    {
                        try { File.Delete(tempFile); }
                        catch { /* best-effort secondary cleanup */ }
                    }
                }
            }
            catch { /* best-effort — don't mask the original exception */ }
        }

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            var trimmed = result.Stdout.Trim();
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);

            // Check if the script returned a success: false response (e.g., ISO_NOT_FOUND, INSTALL_TIMEOUT)
            if (doc.RootElement.TryGetProperty("success", out var successProp))
            {
                bool isSuccess;
                if (successProp.ValueKind == JsonValueKind.True)
                    isSuccess = true;
                else if (successProp.ValueKind == JsonValueKind.False)
                    isSuccess = false;
                else
                    isSuccess = successProp.GetBoolean();

                if (!isSuccess)
                {
                    var errorMsg = doc.RootElement.TryGetProperty("error", out var errProp)
                        ? errProp.GetString() ?? "Installation failed"
                        : "Installation failed";
                    var errorCode = doc.RootElement.TryGetProperty("errorCode", out var codeProp)
                        ? codeProp.GetString() ?? Models.ErrorCodes.InstallFailed
                        : Models.ErrorCodes.InstallFailed;

                    // Extract VM identifiers from the data envelope if present.
                    string? dataVmId = null;
                    string? dataVmName = null;
                    string? lastPhase = null;
                    if (doc.RootElement.TryGetProperty("data", out var dataForError))
                    {
                        if (dataForError.TryGetProperty("vmId", out var vid))
                            dataVmId = vid.GetString();
                        if (dataForError.TryGetProperty("name", out var vn))
                            dataVmName = vn.GetString();
                        if (dataForError.TryGetProperty("lastPhase", out var lp))
                            lastPhase = lp.GetString();
                    }

                    // Throw typed exceptions that ErrorMapper can map to the correct error codes.
                    // This preserves ISO-specific error codes through the entire chain.
                    switch (errorCode)
                    {
                        case Models.ErrorCodes.IsoNotFound:
                            throw new IsoNotFoundException(errorMsg);

                        case Models.ErrorCodes.InstallTimeout:
                            throw new InstallTimeoutException(
                                errorMsg, timeoutMinutes, lastPhase ?? "unknown",
                                dataVmId, dataVmName ?? name);

                        case Models.ErrorCodes.AutounattendFailed:
                            throw new AutounattendFailedException(errorMsg);

                        case Models.ErrorCodes.InstallFailed:
                        default:
                            throw new InstallFailedException(
                                errorMsg, dataVmId, dataVmName ?? name);
                    }
                }
            }

            // Parse the success data envelope
            if (doc.RootElement.TryGetProperty("data", out var dataProp))
            {
                return ParseOsInstallResult(dataProp);
            }
        }

        // If stdout is empty or unparseable, check for errors
        HandleError(result, hostProfile.HostId, vmId: null, vmName: name, isCreateOperation: true);

        // Shouldn't reach here, but if HandleError didn't throw:
        throw new InvalidOperationException("Unexpected: OS installation produced no parseable output.");
    }

    /// <summary>
    /// Parses the data portion of the OS install result JSON into <see cref="OsInstallResult"/>.
    /// </summary>
    private static OsInstallResult ParseOsInstallResult(JsonElement data)
    {
        var result = new OsInstallResult();

        if (data.TryGetProperty("VmId", out var vmIdProp))
            result.VmId = vmIdProp.GetString() ?? string.Empty;

        if (data.TryGetProperty("Name", out var nameProp))
            result.Name = nameProp.GetString() ?? string.Empty;

        if (data.TryGetProperty("State", out var stateProp))
        {
            if (stateProp.ValueKind == JsonValueKind.Number && stateProp.TryGetInt32(out var stateInt))
                result.State = LegacyNumericVmStateFallback.GetValueOrDefault(stateInt, $"Unknown({stateInt})");
            else
                result.State = stateProp.GetString() ?? "Unknown";
        }

        if (data.TryGetProperty("ProcessorCount", out var cpuProp) && cpuProp.TryGetInt32(out var cpu))
            result.ProcessorCount = cpu;

        if (data.TryGetProperty("MemoryMB", out var memProp) && memProp.TryGetInt64(out var mem))
            result.MemoryMB = mem;

        if (data.TryGetProperty("InstallationDurationSeconds", out var installDurProp) && installDurProp.TryGetInt32(out var installDur))
            result.InstallationDurationSeconds = installDur;

        if (data.TryGetProperty("BootstrapDurationSeconds", out var bootstrapDurProp) && bootstrapDurProp.TryGetInt32(out var bootstrapDur))
            result.BootstrapDurationSeconds = bootstrapDur;

        if (data.TryGetProperty("TotalDurationSeconds", out var totalDurProp) && totalDurProp.TryGetInt32(out var totalDur))
            result.TotalDurationSeconds = totalDur;

        if (data.TryGetProperty("GuestIpAddress", out var ipProp) && ipProp.ValueKind != JsonValueKind.Null)
            result.GuestIpAddress = ipProp.GetString();

        if (data.TryGetProperty("Warnings", out var warningsProp) && warningsProp.ValueKind == JsonValueKind.Array)
        {
            var warnings = new List<string>();
            foreach (var w in warningsProp.EnumerateArray())
            {
                var ws = w.GetString();
                if (!string.IsNullOrEmpty(ws))
                    warnings.Add(ws);
            }
            result.Warnings = warnings.AsReadOnly();
        }

        return result;
    }


    /// <summary>
    /// Resolves the host profile and enforces local-only constraint for Phase 1.
    /// Remote host support (WinRM) will be added in a future phase.
    /// </summary>
    private HostProfile ResolveLocalHost(string hostId)
    {
        var hostProfile = _hostResolver.ResolveRequired(hostId);

        if (!hostProfile.IsLocal)
        {
            throw new NotSupportedException(
                $"Remote host '{hostId}' is not supported in Phase 1. Only local host operations are available. " +
                "Remote host support via WinRM will be added in a future phase.");
        }

        return hostProfile;
    }

    /// <summary>Maps PowerShell errors to typed domain exceptions. During creation, missing paths usually mean a base VHDX
    /// or storage path, not a missing VM; only lookup operations map these patterns to VmNotFoundException.</summary>
    private static void HandleError(PowerShellResult result, string hostId, string? vmId,
        string? vmName = null, bool isCreateOperation = false)
    {
        if (result.Success)
            return;

        var errorText = result.Stderr;

        // Missing paths during creation refer to base VHDX/storage, not a VM.
        // Only existing-VM lookups may map them to VmNotFoundException.
        if (!isCreateOperation && ContainsAny(errorText, "not found", "does not exist", "could not find", "unable to find a virtual machine"))
        {
            // Prefer vmId when known; otherwise fall back to vmName so the error
            // envelope references a meaningful identifier instead of "unknown"
            // (e.g., GetPrimaryVhdxPathAsync resolves by name and has no vmId).
            throw new VmNotFoundException(hostId, vmId ?? vmName ?? "unknown");
        }

        if (ContainsAny(errorText, "already exists"))
        {
            throw new VmAlreadyExistsException(hostId, vmName ?? "unknown");
        }

        // Generic error for unrecognized failures.
        throw new InvalidOperationException(
            $"PowerShell execution failed (exit code {result.ExitCode}): {errorText}");
    }

    /// <summary>
    /// Parses a single VM JSON object from PowerShell's ConvertTo-Json output.
    /// </summary>
    private static VmInfo ParseSingleVmInfo(string json, string hostId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("PowerShell returned empty output when VM info was expected.");
        }

        var trimmed = json.Trim();

        using var doc = JsonDocument.Parse(trimmed);
        return MapJsonToVmInfo(doc.RootElement, hostId);
    }

    /// <summary>
    /// Parses a JSON array (or single object) of VMs from PowerShell's ConvertTo-Json output.
    /// PowerShell's ConvertTo-Json returns a single object (not array) when there is exactly one result,
    /// so this method handles both cases.
    /// </summary>
    private static IReadOnlyList<VmInfo> ParseVmInfoList(string json, string hostId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<VmInfo>();
        }

        var trimmed = json.Trim();

        // Empty JSON array.
        if (trimmed == "[]")
        {
            return Array.Empty<VmInfo>();
        }

        using var doc = JsonDocument.Parse(trimmed);

        // PowerShell ConvertTo-Json returns a single object when there's one item,
        // and an array when there are multiple items.
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var list = new List<VmInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                list.Add(MapJsonToVmInfo(element, hostId));
            }
            return list.AsReadOnly();
        }
        else
        {
            // Single object — wrap in a list.
            return new List<VmInfo> { MapJsonToVmInfo(doc.RootElement, hostId) }.AsReadOnly();
        }
    }

    /// <summary>
    /// Maps a JSON element (from PowerShell's ConvertTo-Json, which uses PascalCase) to <see cref="VmInfo"/>.
    /// Handles Hyper-V's integer-based State enum by mapping to human-readable string names.
    /// </summary>
    private static VmInfo MapJsonToVmInfo(JsonElement element, string hostId)
    {
        // PowerShell's ConvertTo-Json uses PascalCase property names.
        var vmId = element.TryGetProperty("Id", out var idProp)
            ? idProp.ToString()
            : string.Empty;

        var name = element.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? string.Empty
            : string.Empty;

        // The projection emits the state name; a number only survives in legacy/cached payloads.
        var state = "Unknown";
        if (element.TryGetProperty("State", out var stateProp))
        {
            if (stateProp.ValueKind == JsonValueKind.Number && stateProp.TryGetInt32(out var stateInt))
            {
                state = LegacyNumericVmStateFallback.GetValueOrDefault(stateInt, $"Unknown({stateInt})");
            }
            else if (stateProp.ValueKind == JsonValueKind.String)
            {
                state = stateProp.GetString() ?? "Unknown";
            }
        }

        var cpuCount = element.TryGetProperty("ProcessorCount", out var cpuProp) && cpuProp.TryGetInt32(out var cpu)
            ? cpu
            : 0;

        var memoryMB = element.TryGetProperty("MemoryMB", out var memProp) && memProp.TryGetInt64(out var mem)
            ? mem
            : 0L;

        var uptimeSeconds = element.TryGetProperty("UptimeSeconds", out var uptimeProp)
            ? (long)(uptimeProp.GetDouble())
            : 0L;

        // Optional classification reason emitted by vm_cleanup_orphans.
        // Absent for all other tools.
        string? reason = null;
        if (element.TryGetProperty("Reason", out var reasonProp) &&
            reasonProp.ValueKind == JsonValueKind.String)
        {
            var r = reasonProp.GetString();
            if (!string.IsNullOrWhiteSpace(r))
            {
                reason = r;
            }
        }

        return new VmInfo
        {
            VmId = vmId,
            Name = name,
            State = state,
            HostId = hostId,
            CpuCount = cpuCount,
            MemoryMB = memoryMB,
            UptimeSeconds = uptimeSeconds,
            Reason = reason,
        };
    }

    /// <summary>Enforces only the ReadOnly attribute; SHA-256 hashing belongs to IBaseImageHashCache outside PowerShell
    /// so pipeline cancellation cannot bypass the cache and hashing is shared per path/stat-tuple/TTL.</summary>
    private static string BuildBaseVhdxGuardScript(string escapedBaseVhdx)
    {
        return $@"
# (refined by ): Base VHDX mutation guard — ReadOnly enforcement only.
# SHA-256 pre/post hashing is owned by managed code (IBaseImageHashCache).
if (-not (Get-ItemProperty -LiteralPath '{escapedBaseVhdx}' -Name IsReadOnly).IsReadOnly) {{
    Set-ItemProperty -LiteralPath '{escapedBaseVhdx}' -Name IsReadOnly -Value $true
}}
";
    }

    /// <summary>
    /// Escapes single quotes in a string for safe embedding in PowerShell single-quoted strings.
    /// In PowerShell, single quotes inside single-quoted strings are escaped by doubling them.
    /// </summary>
    private static string EscapePowerShellString(string value)
    {
        return value.Replace("'", "''");
    }

    /// <summary>
    /// Checks if a string contains any of the specified substrings (case-insensitive).
    /// </summary>
    private static bool ContainsAny(string text, params string[] substrings)
    {
        foreach (var sub in substrings)
        {
            if (text.Contains(sub, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a JSON array (or single object) of image info from PowerShell's ConvertTo-Json output.
    /// </summary>
    private static IReadOnlyList<ImageInfo> ParseImageInfoList(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<ImageInfo>();

        var trimmed = json.Trim();
        if (trimmed == "[]")
            return Array.Empty<ImageInfo>();

        using var doc = JsonDocument.Parse(trimmed);

        var list = new List<ImageInfo>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                list.Add(MapJsonToImageInfo(element));
            }
        }
        else
        {
            // Single object — wrap in list (PowerShell ConvertTo-Json behavior).
            list.Add(MapJsonToImageInfo(doc.RootElement));
        }

        return list.AsReadOnly();
    }

    /// <summary>
    /// Maps a JSON element to <see cref="ImageInfo"/>.
    /// </summary>
    private static ImageInfo MapJsonToImageInfo(JsonElement element)
    {
        var name = element.TryGetProperty("Name", out var nameProp)
            ? nameProp.GetString() ?? string.Empty
            : string.Empty;

        var path = element.TryGetProperty("Path", out var pathProp)
            ? pathProp.GetString() ?? string.Empty
            : string.Empty;

        var sizeGB = element.TryGetProperty("SizeGB", out var sizeProp)
            ? sizeProp.GetDouble()
            : 0.0;

        var maxSizeGB = element.TryGetProperty("MaxSizeGB", out var maxSizeProp)
            ? maxSizeProp.GetDouble()
            : 0.0;

        var vhdType = element.TryGetProperty("VhdType", out var typeProp)
            ? typeProp.GetString() ?? "Unknown"
            : "Unknown";

        var parentPath = element.TryGetProperty("ParentPath", out var parentProp)
            && parentProp.ValueKind != JsonValueKind.Null
            ? parentProp.GetString()
            : null;

        return new ImageInfo
        {
            Name = name,
            Path = path,
            SizeGB = sizeGB,
            MaxSizeGB = maxSizeGB,
            VhdType = vhdType,
            ParentPath = parentPath,
        };
    }

    /// <inheritdoc /> <remarks> Returns the host-side absolute path to the VM's primary VHDX via
    /// <c>Get-VMHardDiskDrive | Select-Object -First 1</c>. Used by <c>vm_create_base_image</c> to locate the disk to
    /// copy. </remarks>
    public async Task<string> GetPrimaryVhdxPathAsync(string hostId, string vmName,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        if (string.IsNullOrWhiteSpace(vmName))
        {
            throw new ArgumentException("VM name cannot be empty.", nameof(vmName));
        }

        var escapedName = EscapePowerShellString(vmName);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround : -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction SilentlyContinue
if (-not $vm) {{ throw ""VM not found: {escapedName}"" }}

$hdd = Get-VMHardDiskDrive -VM $vm -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $hdd -or -not $hdd.Path) {{ throw ""VM '{escapedName}' has no attached VHDX."" }}

[PSCustomObject]@{{ Path = $hdd.Path }} | ConvertTo-Json -Compress
";

        _logger.LogDebug("Resolving primary VHDX path for VM '{VmName}' on host '{HostId}'", vmName, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 30, ct: ct);
        HandleError(result, hostProfile.HostId, vmId: null, vmName: vmName);

        var stdout = (result.Stdout ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(stdout))
        {
            throw new InvalidOperationException(
                $"PowerShell returned empty output when resolving primary VHDX path for VM '{vmName}'.");
        }

        using var doc = JsonDocument.Parse(stdout);
        if (doc.RootElement.TryGetProperty("Path", out var pathProp))
        {
            var path = pathProp.GetString();
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        throw new InvalidOperationException(
            $"Unable to parse primary VHDX path from PowerShell output for VM '{vmName}'.");
    }
}
