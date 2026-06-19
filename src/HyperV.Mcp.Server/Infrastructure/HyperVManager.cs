using System.Diagnostics;
using System.Text.Json;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Manages Hyper-V virtual machines via PowerShell cmdlets executed through <see cref="IPowerShellExecutor"/>.
/// See /myplans/vm-management/lifecycle/lifecycle-design.md — VM lifecycle operations.
///
/// Phase 1 implementation: local host only. Remote hosts (WinRM) will be added in a future phase.
/// Each method composes a PowerShell script that outputs JSON, then parses the result into <see cref="VmInfo"/>.
///
/// Design decisions:
/// - LF-D1: vm_create performs creation + start as single atomic operation (bootstrap deferred to Stage 1.4).
///   See /myplans/vm-management/lifecycle/lifecycle-design.md
/// - LF-D3: vm_destroy performs hard power-off (Stop-VM -TurnOff), not graceful shutdown.
///   See /myplans/vm-management/lifecycle/lifecycle-design.md
/// - LF-D4: Tag VMs with "hyper-v-mcp:created={ISO8601}" in Hyper-V Notes field.
///   See /myplans/vm-management/lifecycle/lifecycle-design.md
/// </summary>
public class HyperVManager : IHyperVManager
{
    private readonly IPowerShellExecutor _psExecutor;
    private readonly IHostResolver _hostResolver;
    private readonly ServerOptions _options;
    private readonly ILogger<HyperVManager> _logger;
    private readonly IIsoInspector _isoInspector;

    /// <summary>
    /// Default storage root when not configured via environment variable or host profile.
    /// </summary>
    private const string DefaultStorageRoot = @"C:\HyperVMCP\VMs";

    /// <summary>
    /// Hyper-V VM state enum values mapped to human-readable names.
    /// See https://learn.microsoft.com/en-us/dotnet/api/microsoft.hyperv.powershell.vmstate
    /// </summary>
    private static readonly Dictionary<int, string> VmStateMap = new()
    {
        { 1, "Other" },
        { 2, "Running" },
        { 3, "Off" },
        { 4, "Stopping" },
        { 5, "Saved" },
        { 6, "Paused" },
        { 7, "Starting" },
        { 8, "Reset" },
        { 9, "Saving" },
        { 10, "PausedCritical" },
        { 11, "SavedCritical" },
        { 12, "FastSaved" },
        { 13, "FastSavedCritical" },
    };

    private readonly IFileSystemProbe _fileSystemProbe;
    private readonly IBaseImageHashCache? _baseImageHashCache;

    /// <summary>
    /// Default LF-D17 rollback budget (seconds). Overridable via
    /// <c>HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS</c>.
    /// </summary>
    private const int DefaultRollbackBudgetSeconds = 30;

    /// <summary>Environment variable for the LF-D17 rollback budget.</summary>
    private const string RollbackBudgetEnvVar = "HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS";

    /// <summary>
    /// PB-D2: Shared <c>Select-Object</c> projection used by the seven VM lifecycle
    /// methods (Start/Stop/Restart/Pause/Resume/Configure/GetVmStatus). The string is
    /// the pipe right-hand side only (no leading <c>|</c>); the helper template in
    /// <see cref="RunSingleVmActionAsync"/> supplies the pipe. The literal
    /// <c>MemoryStartup/1MB</c> is load-bearing — multiple tests grep for it.
    /// </summary>
    private const string VmInfoProjection =
        "Select-Object Id, Name, State, ProcessorCount, "
        + "@{N='MemoryMB';E={$_.MemoryStartup/1MB}}, "
        + "@{N='UptimeSeconds';E={$_.Uptime.TotalSeconds}}";

    public HyperVManager(
        IPowerShellExecutor psExecutor,
        IHostResolver hostResolver,
        ServerOptions options,
        ILogger<HyperVManager> logger,
        IIsoInspector isoInspector,
        IFileSystemProbe? fileSystemProbe = null,
        IBaseImageHashCache? baseImageHashCache = null)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isoInspector = isoInspector ?? throw new ArgumentNullException(nameof(isoInspector));
        // Issue #73: filesystem-probe seam. Defaults to the production implementation
        // so existing call sites (tests, harnesses) need not be threaded with a
        // new dependency. DI resolves the registered singleton in production.
        _fileSystemProbe = fileSystemProbe ?? new FileSystemProbe();
        // Issue #164 / ST-D6a: pre-hash cache for base VHDX mutation guard.
        // Optional in the constructor so existing test fixtures need not pass it;
        // when null, CreateVmAsync degrades to per-call SHA-256 inside the host
        // process (still off the PowerShell pipeline) using a transient cache.
        _baseImageHashCache = baseImageHashCache;

        _logger.LogInformation("Base VHDX mutation guard active (ADR-4 / ST-D6 + ST-D6a + VC-D8): ReadOnly attribute + force-recomputed post-create SHA-256.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// LF-D1: Creates VM + attaches differencing VHDX as single atomic operation.
    /// When autoStart is true, the VM is started after creation;
    /// when false (default), it remains in Off state.
    /// LF-D4: Tags VM with "hyper-v-mcp:created={ISO8601}" in Notes field.
    ///
    /// Phase 1 limitation: CreateVmAsync creates (and optionally starts) the VM but
    /// does NOT perform the full bootstrap state machine (OsBooting → OsReady →
    /// ShellReady → NetworkReady → AppReady). The bootstrap will be completed in a
    /// follow-up phase. Callers should use vm_wait_ready (P1 tool) or add manual
    /// delays before running commands on freshly created VMs.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Bootstrap State Machine.
    ///
    /// Issue 5: Basic rollback added — if Start-VM fails after VM creation, the VM
    /// and its VHDX are cleaned up to avoid orphaned resources.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md
    /// </remarks>
    public async Task<VmInfo> CreateVmAsync(
        string hostId,
        string name,
        string? baseVhdxPath = null,
        int cpuCount = 2,
        long memoryMB = 4096,
        bool autoStart = false,
        bool verifyBaseImageHash = true,
        CancellationToken ct = default)
    {
        // ── PA-D2: structured-logging stage markers (replaces the retired
        //    HYPERV_MCP_TRACE_VM_CREATE env-gated `[vm_create-trace]` stderr
        //    path). Each former __trace(...) call site now emits a single
        //    LogDebug message under one of two templates:
        //      (A) "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}"
        //      (B) "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}"
        //    Template (B) uses the `@` destructuring hint (honored by sinks
        //    such as Serilog; treated as a literal property-name prefix by the
        //    default Microsoft.Extensions.Logging console formatter, which
        //    surfaces the structured property as `@phaseData`). Downstream log
        //    queries should match on that exact property name.
        //    The single total-elapsed Stopwatch and the per-phase stopwatches
        //    are kept unconditional (cheap) so elapsedMs / *StageMs fields are
        //    always populated in the structured payload.
        var __traceSw = Stopwatch.StartNew();
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
            "entry", __traceSw.ElapsedMilliseconds, name);

        // VC-D6 / VC-D8 (Issue #169 Gate 6 remediation, Option D′ hybrid):
        // - Default (verifyBaseImageHash:true): pre-hash via the warm-on-init
        //   cache + post-create force-recompute (unconditional). Mismatch ⇒
        //   BASE_IMAGE_MUTATED. Closes the Gate 6 finding #1 gap where the
        //   cached SHA-256 had become a stored value rather than an enforced
        //   check (re-served as the post-hash, defeating ST-D6 against
        //   preserved-stat mutations — Rows 1/4/5 of the §ADR-4 Threat Model).
        // - Opt-out (verifyBaseImageHash:false): skip both the pre-hash and the
        //   post-create recompute; mutation guard collapses to ReadOnly-attribute
        //   only. Operator-accepted ADR-4 trade-off.
        var hostProfile = ResolveLocalHost(hostId);

        // Resolve base VHDX path: parameter > env var > host profile config.
        var baseVhdx = baseVhdxPath
            ?? Environment.GetEnvironmentVariable("HYPERV_MCP_BASE_VHDX")
            ?? hostProfile.BaseVhdxPath;

        if (string.IsNullOrWhiteSpace(baseVhdx))
        {
            throw new InvalidOperationException(
                "No base VHDX path specified. Provide via parameter, HYPERV_MCP_BASE_VHDX environment variable, or host profile configuration.");
        }

        // Resolve storage root: env var > host profile config > default.
        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;

        // Pre-compute host-side artifact paths so the rollback path can target them
        // even on cancellation (the PowerShell child may be killed before it can
        // surface its own copies of these strings).
        var vmDir = Path.Combine(storageRoot, name);
        var diffPath = Path.Combine(vmDir, $"{name}.vhdx");

        // Escape single quotes in strings for PowerShell literal embedding.
        var escapedName = EscapePowerShellString(name);
        var escapedBaseVhdx = EscapePowerShellString(baseVhdx);
        var escapedStorageRoot = EscapePowerShellString(storageRoot);

        _logger.LogInformation("Creating VM '{VmName}' on host '{HostId}' with {CpuCount} CPUs, {MemoryMB}MB RAM",
            name, hostId, cpuCount, memoryMB);
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "validated", __traceSw.ElapsedMilliseconds, name,
            new { baseVhdx, storageRoot });

        // ── Issue #203 / VC-DUP-D1 / LF-D19: pre-create existence probe ──────
        // MUST run BEFORE any state-mutating call (warm-hash compute is read-only
        // so we run it after the probe to fail fast on the dominant duplicate-name
        // case). A non-null Get-VM result short-circuits with the contractually
        // pinned VM_ALREADY_EXISTS envelope. No artifacts have been created →
        // there is no LF-D17 rollback to arm → the pre-existing VM cannot be
        // deleted by our cleanup path.
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

        // ── ST-D6a: host-side pre-hash via the cache ─────────────────────────
        // Cheap stat-tuple lookup short-circuits SHA-256 on the warm path. Cold
        // path computes once and caches for 24h (configurable). Hashing happens
        // BEFORE the PowerShell pipeline so it is not subject to inbound CT
        // mid-cancellation (the cache observes the CT but the cancelled task
        // simply doesn't populate the cache; subsequent calls will retry).
        // ── 🔴 Bug-fix (Issue #164 loop-back): eagerly snapshot the pre-create
        // stat tuple. `FileInfo` reads its properties lazily on first access and
        // caches them, so capturing the `FileInfo` object alone is unsafe: by the
        // time the post-create comparison reads `preStat.Length` etc., the OS
        // metadata can have moved and BOTH endpoints end up reading post-state
        // (silently masking a real mutation). Read the primitives NOW, before the
        // PowerShell pipeline runs, into a readonly record struct.
        string? preHash = null;
        FileStatSnapshot? preStat = null;
        if (File.Exists(baseVhdx))
        {
            var fi = new FileInfo(baseVhdx);
            // Eagerly materialize each property into a local primitive. Touching
            // FileInfo.Refresh() before reading guarantees a single OS round-trip
            // for this snapshot; subsequent reads of the locals are pure memory.
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
            // VC-D6 opt-out path: skip pre-hash entirely when caller asked us
            // to. Mutation guard reduces to the ReadOnly-attribute check (still
            // enforced inside the PowerShell BuildBaseVhdxGuardScript helper).
            if (_baseImageHashCache is not null && verifyBaseImageHash)
            {
                _logger.LogDebug(
                    "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                    "pre-hash-start", __traceSw.ElapsedMilliseconds, name);
                var __preHashSw = Stopwatch.StartNew();
                // VC-D5 (Shape B): the cache's GetOrComputeAsync internally swaps the
                // inbound CT for a lifetime CTS inside the gate-holder, so the actual
                // SHA-256 compute is NOT killed by an inbound MCP timeout. We still
                // want THIS handler to surface -32001 promptly on inbound cancellation
                // — so we race the cache task against Task.Delay(Infinite, ct) via
                // Task.WhenAny. On inbound cancellation we throw OperationCanceledException
                // and let the detached compute continue for the benefit of subsequent
                // callers (who will join the same per-path semaphore and observe a hit).
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
            // When no cache is wired (e.g., legacy test fixtures), we skip the
            // pre-hash entirely. The PowerShell side no longer performs Get-FileHash,
            // so the mutation guard collapses to "ReadOnly attribute only" in this
            // degraded mode. Production code paths always wire the cache via DI.
        }
        catch (FileNotFoundException fnf)
        {
            throw new InvalidOperationException(
                $"Base VHDX not found at '{baseVhdx}'. Verify the path or HYPERV_MCP_BASE_VHDX configuration.", fnf);
        }

        // Compose the create script (LF-D1, LF-D4, LF-D7). The script no longer
        // performs SHA-256 — that is owned host-side by the cache per ST-D6a.
        // The script also no longer carries an inline catch-block: rollback is now
        // a separate detached-CTS PowerShell call per LF-D17.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$name = '{escapedName}'
$baseVhdx = '{escapedBaseVhdx}'
$storageRoot = '{escapedStorageRoot}'
$memoryBytes = {memoryMB} * 1MB
$cpuCount = {cpuCount}
$autoStart = {(autoStart ? "$true" : "$false")}

# Check if VM already exists
# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
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

# Create differencing VHDX
# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
New-VHD -Path $diffPath -ParentPath $baseVhdx -Differencing -ComputerName localhost | Out-Null

# Create Generation 2 VM
New-VM -Name $name -Generation 2 -MemoryStartupBytes $memoryBytes -VHDPath $diffPath -ComputerName localhost | Out-Null
Set-VM -Name $name -ProcessorCount $cpuCount -ComputerName localhost
Set-VM -Name $name -Notes ""hyper-v-mcp:created=$(Get-Date -Format o)"" -ComputerName localhost
if ($autoStart) {{ Start-VM -Name $name -ComputerName localhost }}

$vm = Get-VM -Name $name -ComputerName localhost
$vm | Select-Object Id, Name, State, ProcessorCount, @{{N='MemoryMB';E={{$_.MemoryStartup/1MB}}}}, @{{N='UptimeSeconds';E={{$_.Uptime.TotalSeconds}}}} | ConvertTo-Json
";

        // ── Execute the primary pipeline under the inbound CT ─────────────────
        // Phase tracks where the failure occurred for the LF-D17 envelope.
        // It is updated optimistically before each phase boundary; rollback uses
        // the final value when reporting `details.phase`.
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
            // VC-D8 (Issue #169 Gate 6 remediation, ADR-4 / ST-D6): on the
            // default path the post-create SHA-256 MUST be force-recomputed
            // unconditionally — NOT gated on a stat-tuple "did it move?" check.
            // The previous tupleMoved-gated branch defeated ST-D6 against
            // preserved-stat mutations (Rows 1/4/5 of the §ADR-4 Threat Model:
            // sparse in-place overwrite, touch -t mtime reset, silent
            // storage-layer bit flip), because the cached pre-hash would also
            // be re-served as the post-hash whenever the cheap stat tuple
            // matched. Force-recompute via IBaseImageHashCache.ForceRecomputeAsync
            // bypasses the stat-tuple short-circuit and re-reads the bytes from
            // disk. The opt-out path (verifyBaseImageHash:false) skips this
            // block entirely; mutation guard there is ReadOnly-attribute-only.
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
                        // VC-D15: record the mutation event before throwing so
                        // the sidecar is deleted, the cache entry is evicted,
                        // and vm_diag surfaces a `lastMutationDetected` entry.
                        // Best-effort — any failure inside the cache helper is
                        // swallowed there so the rollback envelope is preserved.
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
            // Full success — return parsed VmInfo.
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                "success-parsing", __traceSw.ElapsedMilliseconds, name);
            var __parsed = ParseSingleVmInfo(primaryResult.Stdout ?? string.Empty, hostProfile.HostId);
            _logger.LogDebug(
                "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName}",
                "envelope-returned", __traceSw.ElapsedMilliseconds, name);
            return __parsed;
        }
        _logger.LogDebug(
            "vm_create stage {stage} elapsedMs={elapsedMs} vm={vmName} data={@phaseData}",
            "failure-path-entry", __traceSw.ElapsedMilliseconds, name,
            new { failureType = primaryFailure?.GetType().Name, failureMessage = primaryFailure?.Message });

        // ── Issue #203 / VC-DUP-D3: per-artifact ownership inference ─────────
        // The primary script is single-shot (probe → New-VHD → New-VM → Set-VM →
        // optional Start-VM). We cannot directly observe which step failed, so
        // ownership is inferred from PS stderr signals:
        //   - "already exists" with no other progress signal ⇒ collision detected
        //     at the script's internal Get-VM probe (residual race vs. our LF-D19
        //     probe). Nothing was created → CreatedArtifacts is fully empty.
        //   - "New-VM" / "Set-VM" / "Start-VM" mentioned in stderr ⇒ New-VHD must
        //     have succeeded and the VHDX is owned. VmRegistered is true only when
        //     the failure happened AFTER New-VM completed (Set-VM / Start-VM
        //     phase).
        // This is the LF-D18 / VC-DUP-D3 "did this call create it?" invariant:
        // Remove-VM MUST NOT run unless created.VmRegistered == true.
        var isNameCollision = LooksLikeNameCollision(primaryFailure!, primaryResult);

        // IA-Gate 6 fix (post-success host-side failure): distinguish the
        // ambiguous branch in InferCreatedArtifacts by signalling whether the
        // PowerShell primary script itself completed successfully (exit 0,
        // not cancelled, not timed out, empty stderr) before a post-success
        // host-side check (e.g., the BASE_IMAGE_MUTATED post-hash recompute,
        // or any future host-side guard fired after the script returned) set
        // primaryFailure. In that case New-VM / Set-VM / Start-VM all ran to
        // completion ⇒ this invocation OWNS the VM registration, and the
        // rollback MUST run Remove-VM. Pre-fix conservative behaviour kept
        // VmRegistered=false and leaked the newly-registered VM.
        var primaryScriptSucceeded =
            primaryResult is not null
            && primaryResult.Success
            && !primaryResult.Cancelled
            && !primaryResult.TimedOut
            && string.IsNullOrWhiteSpace(primaryResult.Stderr);

        var created = InferCreatedArtifacts(
            primaryFailure!, primaryResult, diffPath, vmDir, isNameCollision,
            primaryScriptSucceeded);

        // ── Issue #203 / VC-DUP-D4: residual-race name-collision branch ──────
        // The script's internal Get-VM threw "already exists" — either a TOCTOU
        // race vs. our LF-D19 probe, or parallel vm_create from another client.
        // Map to VM_ALREADY_EXISTS (NOT COMMAND_FAILED) and clean up ONLY the
        // owned VHDX. Remove-VM is gated off by created.VmRegistered == false,
        // so the colliding VM (which belongs to a different invocation) is
        // never touched.
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

        // ── LF-D17: cancellation-safe, awaited rollback (non-collision path) ─
        phase = ClassifyPhase(primaryFailure!, primaryResult);
        var rollback = await RunCreateRollbackAsync(
            name, vmDir, diffPath, primaryFailure!, ct, created).ConfigureAwait(false);

        // Original failure log AFTER rollback so the order in stderr matches LF-D17.
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
    /// Issue #203 / VC-DUP-D1 / LF-D19: pre-create existence probe.
    /// Runs <c>Get-VM -Name $name -ComputerName localhost</c> via the same
    /// PowerShell executor used by the primary pipeline. A non-null result ⇒
    /// the VM already exists on this host. Errors are treated as "absent" so
    /// the probe never false-positives — an actual collision will surface again
    /// inside the primary pipeline and be caught by the VC-DUP-D4 residual-race
    /// branch with full owned-only rollback semantics.
    /// </summary>
    private async Task<bool> VmExistsOnHostAsync(string name, CancellationToken ct)
    {
        var escapedName = EscapePowerShellString(name);
        // LF-D7: -ComputerName localhost avoids the Win11 26200+ WMI null-name bug.
        // Probe is read-only; cancellation is observed under the inbound CT (no
        // rollback to bypass — nothing has been created).
        // IA-Gate 10 / Copilot review fix: probe now runs under
        // $ErrorActionPreference='Stop' with an explicit Import-Module and a
        // try/catch that maps cmdlet/module failures to a distinct
        // 'inconclusive' sentinel. Previously the probe used SilentlyContinue
        // + a swallowed Get-VM -ErrorAction SilentlyContinue and ALWAYS emitted
        // 'present'/'absent', which meant a Hyper-V module load failure or PS
        // host glitch silently printed 'absent' — bypassing the duplicate-name
        // guard. The sentinel is intentionally distinct from the
        // residue-probe's 'probe-failed:' marker so test-side script
        // recognisers continue to identify this as the LF-D19 probe.
        // Get-VM returns a non-terminating ItemNotFoundException when the VM
        // is genuinely absent — we want that to surface as 'absent', NOT as
        // an inconclusive error. The catch therefore only treats failures
        // OTHER than ItemNotFoundException as inconclusive.
        var probeScript = $@"
$ErrorActionPreference = 'Stop'
try {{
    Import-Module Hyper-V -ErrorAction Stop | Out-Null
    try {{
        $vm = Get-VM -Name '{escapedName}' -ComputerName localhost -ErrorAction Stop
        if ($vm) {{ 'present' }} else {{ 'absent' }}
    }} catch [Microsoft.HyperV.PowerShell.VirtualizationException] {{
        # IA-Gate 6 fix: the typed Hyper-V exception catch is restricted to
        # actual not-found outcomes. Previously this branch mapped EVERY
        # VirtualizationException to 'absent', which silently swallowed
        # service-unavailable / WMI / permission failures and bypassed the
        # duplicate-name guard. We now inspect the same FullyQualifiedErrorId
        # markers as the generic catch below and fall through to
        # 'inconclusive' for anything that is not a recognised not-found
        # category — the LF-D19 caller treats 'inconclusive' as fail-open into
        # the primary pipeline + VC-DUP-D4 residual-race branch (defence-in-
        # depth preserved without false-claiming 'absent').
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
                // Probe inconclusive (module load failure, PS host glitch,
                // permissions, non-success exit). Fall through to the primary
                // pipeline + VC-DUP-D4 residual-race branch — same defence-in-
                // depth contract as before, just no longer claiming 'absent'.
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

    /// <summary>
    /// Issue #203 / VC-DUP-D3 / LF-D18: per-invocation artifact ownership record.
    /// Each field starts empty/false; the rollback path consults this record so
    /// that <c>Remove-VM</c> is NEVER called against a VM this invocation did not
    /// register (the data-loss-prevention invariant from the duplicate-name
    /// component design).
    /// </summary>
    private sealed class CreatedArtifacts
    {
        /// <summary>Non-null when this call created the differencing VHDX at this path.</summary>
        public string? VhdxPath { get; set; }

        /// <summary>True only when this call successfully ran <c>New-VM</c>.</summary>
        public bool VmRegistered { get; set; }

        /// <summary>True only when this call created the per-VM directory.</summary>
        public bool VmDirCreated { get; set; }
    }

    /// <summary>
    /// Issue #203 / VC-DUP-D4: returns true when the primary-pipeline failure
    /// looks like a name collision surfaced by <c>New-VM</c> (or by the script's
    /// own internal probe). Pattern derived from the design's "Name-Collision
    /// Detection Rules": case-insensitive <c>already exists</c> substring match
    /// against either stderr (preferred) or the exception message.
    /// </summary>
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

        // Issue #203 / IA-Gate 6 fix: require VM-specific co-occurrence with
        // "already exists" to keep classification consistent with
        // ErrorMapper.IsNameCollisionMessage. Generic "already exists" text
        // (e.g. ImageCopyFailedException's "Destination image file already
        // exists at '<path>'.") MUST NOT be misclassified as a VM-name
        // collision — that path is COMMAND_FAILED with full LF-D17 rollback,
        // not VM_ALREADY_EXISTS with owned-only cleanup.
        return HasVmAlreadyExistsSignal(stderr) || HasVmAlreadyExistsSignal(msg);
    }

    /// <summary>
    /// Issue #203 / IA-Gate 6: returns true when <paramref name="text"/> contains
    /// "already exists" co-occurring with a VM-specific token (<c>VM with name</c>,
    /// <c>Get-VM</c>, or <c>New-VM</c>). Mirrors
    /// <see cref="ErrorMapper.IsNameCollisionMessage(string?)"/> so the rollback
    /// classification path and the wire-envelope mapping path agree on what
    /// counts as a VM name collision.
    /// </summary>
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

    /// <summary>
    /// Issue #203 / VC-DUP-D3: infers which artifacts the current invocation
    /// actually created from the primary-pipeline failure signals. Conservative
    /// by design — when stderr/message signals are ambiguous, the result
    /// preserves the prior LF-D17 behaviour (full rollback consideration) by
    /// marking the post-New-VHD/New-VM artifacts as owned. The duplicate-name
    /// path is the only one where we MUST be tight: a script-internal probe
    /// throw produces an empty CreatedArtifacts so <c>Remove-VM</c> stays gated.
    /// </summary>
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

        // VC-DUP-D3 / LF-D18 / IA-Gate 6 fix: name-collision classification has
        // THREE sub-cases that must be distinguished — they have different
        // ownership profiles:
        //
        //   1. Pre-mutator path (LF-D19 probe hit): handled upstream — never
        //      reaches this method.
        //   2. Script-internal pre-mutator probe (in-script Get-VM check before
        //      New-VHD throws "already exists"): owns NOTHING. Empty record so
        //      the rollback is a no-op for the colliding VM and its disks.
        //   3. Post-New-VHD New-VM collision (residual race where the script's
        //      probe missed the racer but New-VM itself collided): New-VHD
        //      already succeeded, so this call OWNS the differencing VHDX and
        //      the per-VM directory. The colliding VM registration belongs to
        //      a different invocation, so VmRegistered stays false (Remove-VM
        //      is gated off; data-loss invariant from VC-DUP-D3 / LF-D18).
        //
        // Disambiguation: PowerShell error records for the New-VM cmdlet carry
        // the literal "New-VM" token in stderr (CategoryInfo / FullyQualifiedErrorId
        // include the cmdlet name). The script's internal Get-VM probe throws a
        // bare "VM with name '...' already exists" with no New-VM mention.
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
            // IA-Gate 6 fix (Case 2): the primary PowerShell script ran to
            // completion (exit 0, not cancelled, not timed out, no stderr) —
            // which means New-VHD, New-VM, Set-VM, and (optionally) Start-VM
            // all succeeded — and a SUBSEQUENT host-side post-success check
            // then threw, setting primaryFailure. The classic example is the
            // BASE_IMAGE_MUTATED guard that runs after the primary script
            // returns and re-hashes the base VHDX (lines ~371-433 of this
            // file). Any future host-side post-success guard funnels here too.
            //
            // In this case THIS invocation registered the VM. Ownership is
            // therefore full: VHDX + per-VM dir + VM registration. The
            // rollback MUST run Remove-VM or the newly-created VM leaks
            // (originally reported as the IA-Gate 6 regression on PR #210).
            created.VhdxPath = diffPath;
            created.VmDirCreated = true;
            created.VmRegistered = true;
        }
        else if (result is not null)
        {
            // IA-Gate 6 fix (Case 1): the primary script reported failure
            // (non-success exit, cancellation, timeout, or non-empty stderr
            // without a recognised cmdlet token) and the classifier could not
            // pinpoint a phase. We keep the conservative ownership profile —
            // VHDX + dir only, VmRegistered=false — because the script's own
            // failure means we cannot safely assume New-VM completed, and an
            // over-aggressive Remove-VM here could destroy a foreign VM that
            // happens to share the name (data-loss invariant from VC-DUP-D3 /
            // LF-D18). The residue probe inside the rollback script still
            // emits a vm:<name> residual entry when a foreign VM with the
            // same name is detected, so operators are not silently denied
            // diagnostic signal.
            created.VhdxPath = diffPath;
            created.VmDirCreated = true;
        }
        // else (Case 3): result is null ⇒ no primary execution was observed
        // (the executor itself threw before the script ran, or before a
        // result was assigned). Nothing to clean up. Empty CreatedArtifacts
        // means RunCreateRollbackAsync sees no owned artifacts and the
        // rollback is a no-op / file-only scan.

        return created;
    }

    /// <summary>
    /// LF-D17 (b)(c)(d): runs the rollback PowerShell script under a fresh, detached
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

        // Issue #203 / VC-DUP-D3 / LF-D18: ownership defaults to "this call owns
        // everything" when no record was supplied — preserves the LF-D17
        // pre-#203 behaviour for legacy call sites (none exist now, but the
        // contract is defense-in-depth).
        var ownsVm = created?.VmRegistered ?? true;
        var ownsVhdx = created?.VhdxPath is not null || created is null;
        var ownsVmDir = created?.VmDirCreated ?? true;

        _logger.LogInformation(
            "vm_create rollback starting for {VmName} (ownsVm={OwnsVm}, ownsVhdx={OwnsVhdx}, ownsVmDir={OwnsVmDir})",
            name, ownsVm, ownsVhdx, ownsVmDir);

        var escapedName = EscapePowerShellString(name);
        var escapedVmDir = EscapePowerShellString(vmDir);
        var escapedDiffPath = EscapePowerShellString(diffPath);

        // VC-DUP-D3 / LF-D18: Remove-VM (and VHDX/dir removal) are gated by the
        // ownership flags so the rollback NEVER touches an artifact this call did
        // not create. The pre-existing VM in the duplicate-name path is preserved
        // because ownsVm is $false ⇒ the Remove-VM block is skipped entirely.
        var psOwnsVm = ownsVm ? "$true" : "$false";
        var psOwnsVhdx = ownsVhdx ? "$true" : "$false";
        var psOwnsVmDir = ownsVmDir ? "$true" : "$false";

        // LF-D17 (d): idempotent, standalone rollback script. Every step is
        // SilentlyContinue + Test-Path-guarded; safe to run when nothing was created.
        // Output: a single JSON line listing the kinds successfully removed and any
        // residual artifacts still present at completion.
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
# VC-DUP-D3 / LF-D18: only report artifacts as residual if THIS call owned them.
# A pre-existing VHDX/dir/VM left by a prior successful call is NOT this call's
# residue — reporting it would mislead operators into thinking the rollback
# failed.
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
        // LF-D17 (b): fresh CTS, NOT linked to inbound CT.
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
                // VC-DUP-D3: only treat artifacts as residual when this call owned
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
            // 🟡 #3: budget-exceeded fallback omits registered-VM residue. Try a
            // defensive Get-VM under the inbound CT only (rollback CTS is dead).
            // VC-DUP-D3: only probe when this call owned the VM registration —
            // otherwise the colliding VM (owned by someone else) would surface
            // as residue and mislead operators.
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
            // VC-DUP-D3: gate on ownership — never report someone else's VHDX/dir.
            if (ownsVhdx && File.Exists(diffPath)) residualArtifacts.Add(diffPath);
            if (ownsVmDir && Directory.Exists(vmDir)) residualArtifacts.Add(vmDir);
            // 🟡 #3: rollback-PowerShell-failure fallback omits registered-VM
            // residue. Try a defensive Get-VM under the inbound CT only.
            // VC-DUP-D3: only when this call owned the VM registration.
            if (ownsVm)
            {
                await TryAppendRegisteredVmResidueAsync(name, residualArtifacts, inboundCt).ConfigureAwait(false);
            }
        }

        // Defensive filesystem cross-check: even if the script reported success,
        // confirm via the host-side filesystem (this is the same probe tests use
        // for AC#2). If the PS-reported and host-observed views disagree, prefer
        // the host-observed view so the envelope is authoritative.
        // 🟡 #2 (LF-D17 envelope completeness): cross-check BOTH the differencing
        // VHDX path AND the per-VM directory. The directory can survive a partial
        // rollback (e.g. extra files dropped by Hyper-V) even when the diff VHDX
        // was removed.
        // VC-DUP-D3 / LF-D18: only flag as residual when this call owned the
        // artifact. Pre-existing files left by a prior successful invocation
        // are NOT this call's residue and must not be reported as such.
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

    /// <summary>
    /// Classifies the LF-D17 phase enum (<c>create</c> | <c>register</c> |
    /// <c>configure</c>) from the failure / result pair. The current pipeline is
    /// single-script so phase boundaries are inferred from stderr signals; this
    /// keeps the contract honest without false precision.
    /// </summary>
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

    // VC-D8 (Issue #169 Gate 6): the prior local ForceRecomputeAsync helper
    // (which simply re-called cache.GetOrComputeAsync after a stat-tuple-moved
    // gate) was removed. It is replaced by IBaseImageHashCache.ForceRecomputeAsync
    // which unconditionally bypasses the stat short-circuit and re-reads bytes
    // from disk — necessary to detect preserved-stat mutations on the default
    // verifyBaseImageHash:true path. See VC-D8 / §ADR-4 Threat Model.

    /// <summary>
    /// Eagerly-snapshotted file metadata used by the ST-D6a mutation guard.
    /// Holding the raw <see cref="FileInfo"/> would be unsafe: its properties are
    /// resolved lazily and cached on first read, so a captured pre-create
    /// <c>FileInfo</c> would read POST-create state when compared, silently
    /// masking real mutation. This struct stores the primitives directly.
    /// </summary>
    private readonly record struct FileStatSnapshot(
        long Length,
        DateTime LastWriteTimeUtc,
        bool IsReadOnly);

    /// <summary>
    /// 🟡 #3 (LF-D17 envelope completeness): defensive host-side cross-check that
    /// the VM is no longer registered after a fallback rollback path. Runs under
    /// the inbound CT only — the rollback budget CTS is already cancelled here,
    /// so we cannot reuse it.
    ///
    /// FAIL-CLOSED CONTRACT (post-#164 review fix):
    /// The probe distinguishes three outcomes — <c>present</c>, <c>absent</c>,
    /// and <c>probe-failed:&lt;reason&gt;</c>. The PowerShell snippet uses
    /// <c>$ErrorActionPreference='Stop'</c> inside a <c>try { ... } catch { ... }</c>
    /// so any module-import failure, non-terminating cmdlet error, or other
    /// PowerShell-side problem surfaces as <c>probe-failed:&lt;reason&gt;</c>
    /// rather than silently falling through to a misleading <c>absent</c>.
    ///
    /// Host-side mapping:
    /// <list type="bullet">
    /// <item><description><c>present</c> ⇒ append <c>vm:&lt;vmName&gt;</c>.</description></item>
    /// <item><description><c>absent</c> ⇒ append nothing (authoritative no-residue).</description></item>
    /// <item><description>Any other outcome (executor failure, timeout, cancellation,
    /// <c>probe-failed:*</c>, unparseable stdout) ⇒ append the sentinel
    /// <c>vm:&lt;vmName&gt;(probe-unknown)</c> and emit a Warning. We
    /// conservatively assume the VM MIGHT still be registered rather than
    /// silently asserting it isn't.</description></item>
    /// </list>
    /// </summary>
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
        // Strict error handling: any failure inside the try block (module import,
        // Get-VM non-terminating error promoted by ErrorActionPreference='Stop',
        // etc.) is caught and surfaced as 'probe-failed:<reason>' so the host
        // can treat it as fail-closed rather than as 'absent'.
        // Fail-closed probe: the script-level Stop preference governs error
        // handling. Get-VM is invoked WITHOUT any per-call suppression (no
        // -ErrorAction SilentlyContinue/Ignore, no 2>$null redirect) so any
        // non-terminating error is promoted to a terminating exception. We
        // then distinguish the specific "VM not registered" outcome (message
        // matches /not\s*found/) — which maps to 'absent' — from every other
        // failure mode (module load, RPC, permissions, etc.) which maps to
        // 'probe-failed:<reason>' so the host appends the probe-unknown
        // sentinel instead of silently asserting no residue.
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

    /// <summary>
    /// PB-D1 / PB-D3: Shared PowerShell skeleton for the seven VM lifecycle methods
    /// (Start/Stop/Restart/Pause/Resume/Configure/GetVmStatus). Builds the standard
    /// <c>Get-VM</c> precondition + per-method <paramref name="actionBlock"/> +
    /// <see cref="VmInfoProjection"/> + <c>ConvertTo-Json</c> script and executes it.
    /// </summary>
    /// <remarks>
    /// Host resolution and error mapping stay in the caller per PB-D5; this helper
    /// returns the raw <see cref="PowerShellResult"/> so the caller can invoke
    /// <c>HandleError</c> and <c>ParseSingleVmInfo</c> with its existing
    /// <c>hostProfile</c>. The <paramref name="safeVmId"/> argument MUST already have
    /// been validated by <see cref="InputValidation.ValidateVmId(string)"/>.
    /// The <paramref name="actionBlock"/> sentinel for "no action" is the empty
    /// string (used by <c>GetVmStatusAsync</c> per PB-D7.g), which produces exactly
    /// one extra blank line in the emitted script — a PowerShell no-op (PB-I8).
    /// </remarks>
    private async Task<PowerShellResult> RunSingleVmActionAsync(
        string safeVmId,
        string actionBlock,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
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

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        var actionBlock = $@"if ($vm.State -ne 'Running') {{
    Start-VM -VM $vm
    # Re-fetch to get updated state
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

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // LF-D3: force=true uses -TurnOff for hard power-off.
        // force=false uses -Force to suppress confirmation but still attempts graceful shutdown.
        var stopCommand = force
            ? "$vm | Stop-VM -TurnOff -Force"
            : "$vm | Stop-VM -Force";

        var actionBlock = $@"if ($vm.State -ne 'Off') {{
    {stopCommand}
    # Re-fetch to get updated state
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

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // PB-Q1: The mid-action Get-VM inside Start-VM -VM (...) plus the trailing
        // re-fetch is a deliberate double-fetch quirk preserved byte-for-byte.
        var actionBlock = $@"# Stop (graceful, no confirmation prompt) then start
$vm | Stop-VM -Force
Start-VM -VM (Get-VM -Id '{safeVmId}' -ComputerName localhost)
# Re-fetch to get updated state
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost";

        _logger.LogInformation("Restarting VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Pauses a running VM using Suspend-VM. The VM must be in Running state.
    /// Returns updated VM info showing Paused state.
    /// </remarks>
    public async Task<VmInfo> PauseVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // The state-gate throw literal is byte-preserved — ErrorMapper substring-matches it
        // upstream to produce INVALID_PARAMETER (Issue #126).
        var actionBlock = $@"if ($vm.State -ne 'Running') {{ throw ""Cannot pause VM in state '$($vm.State)'. VM must be Running to pause."" }}
Suspend-VM -VM $vm
# Re-fetch to get updated state
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost";

        _logger.LogInformation("Pausing VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resumes a paused/suspended/saved VM using Resume-VM. The VM must be in Paused or Saved state.
    /// Returns updated VM info showing Running state.
    /// </remarks>
    public async Task<VmInfo> ResumeVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // The state-gate throw literal is byte-preserved — ErrorMapper substring-matches it
        // upstream to produce INVALID_PARAMETER.
        var actionBlock = $@"if ($vm.State -ne 'Paused' -and $vm.State -ne 'PausedCritical' -and $vm.State -ne 'Saved') {{ throw ""Cannot resume VM in state '$($vm.State)'. VM must be Paused or Saved to resume."" }}
Resume-VM -VM $vm
# Re-fetch to get updated state
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost";

        _logger.LogInformation("Resuming VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Modifies VM configuration. Supports updating CPU count via Set-VMProcessor and/or
    /// startup memory via Set-VMMemory. At least one of <paramref name="cpuCount"/> or
    /// <paramref name="memoryMB"/> must be supplied (the dispatcher enforces this).
    /// Returns updated VM info after applying the changes.
    /// </remarks>
    public async Task<VmInfo> ConfigureVmAsync(string hostId, string vmId, int? cpuCount, long? memoryMB, CancellationToken ct)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Build the conditional Set-VM* lines. Values are validated numerics so direct
        // interpolation is safe (no string injection vector).
        var setProcessorLine = cpuCount.HasValue
            ? $"Set-VMProcessor -VM $vm -Count {cpuCount.Value}"
            : "# cpuCount not provided";
        var setMemoryLine = memoryMB.HasValue
            ? $"Set-VMMemory -VM $vm -StartupBytes ({memoryMB.Value}MB)"
            : "# memoryMB not provided";

        // PB-D7.f: The two Issue #56 rationale comment lines are emitted between the
        // re-fetch and the projection — preserved byte-for-byte from the pre-refactor
        // script. They are PowerShell no-ops but count toward the byte-identity invariant.
        var actionBlock = $@"{setProcessorLine}
{setMemoryLine}
# Re-fetch to get updated configuration
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
# Issue #56 review: project MemoryStartup (configured) instead of MemoryAssigned,
# which Hyper-V reports as 0 for stopped VMs even after a successful Set-VMMemory.";

        _logger.LogInformation(
            "Configuring VM '{VmId}' on host '{HostId}' (cpuCount={CpuCount}, memoryMB={MemoryMB})",
            safeVmId, hostId, cpuCount, memoryMB);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// LF-D3: Performs hard power-off (Stop-VM -TurnOff), then removes the VM and cleans up VHDX files.
    /// Uses -ErrorAction SilentlyContinue on Stop-VM to handle VMs that are already off.
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md
    ///
    /// Issue #25: After VHDX cleanup, also removes the now-empty per-VM directory
    /// (e.g. C:\HyperVMCP\VMs\&lt;vmName&gt;\) recursively so the storage root does not
    /// accumulate empty stub directories. Uses best-effort delete that is constrained
    /// to the MCP-managed storage root (StorageRoot/&lt;vmName&gt;) so VMs created outside
    /// our provisioning are never affected. See in-script safety guard.
    /// </remarks>
    public async Task DestroyVmAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Issue #25 (Gate 6 finding #1): Resolve the expected MCP-managed storage root
        // using the same resolution order as CreateVmAsync / OS install: env var >
        // host profile > project default. The PowerShell script will only delete a
        // per-VM directory if it matches "<storageRoot>/<vmName>" exactly.
        var storageRoot = Environment.GetEnvironmentVariable("HYPERV_MCP_STORAGE_ROOT")
            ?? hostProfile.StorageRoot
            ?? DefaultStorageRoot;
        var escapedStorageRoot = EscapePowerShellString(storageRoot);

        // LF-D3: Hard power-off, not graceful shutdown.
        // Collect VHDX paths before removing the VM so we can clean up storage.
        //
        // Issue #25 (Gate 6 finding #1): After VHDX deletion, also remove the per-VM
        // directory recursively, but ONLY when it lives strictly under the MCP-managed
        // storage root. Both paths are normalized via [System.IO.Path]::GetFullPath
        // (which collapses any ".." / "." segments and trailing separators) and
        // compared case-insensitively. If the resolved expected path is not a child of
        // the resolved storage root, or the directory does not exist, the script skips
        // deletion and emits a [WARN] line on stdout. This protects against a
        // Hyper-V-reported $vm.Name containing path-traversal characters that could
        // escape the storage root.
        //
        // Note: Remove-Item -Recurse -Force will delete non-empty contents too. The
        // directory is expected to be empty after VHDX deletion above, but this
        // operation is NOT restricted to empty directories — the safety guard above
        // is what prevents collateral damage to non-managed paths.
        //
        // Design choice: best-effort delete with a [WARN] line on stdout (NOT a hard
        // error). Rationale: the VM has already been removed and VHDXs deleted;
        // failing to remove the now-empty stub directory should not turn a successful
        // destroy into a failure. We use Write-Output "[WARN] ..." (instead of
        // Write-Warning) so the message reliably appears in the executor's captured
        // stdout regardless of stream-merging behavior.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

# Hard power-off (ignore errors if already off)
$vm | Stop-VM -TurnOff -Force -ErrorAction SilentlyContinue

# Collect VHDX paths before removal
$vhdPaths = @((Get-VMHardDiskDrive -VM $vm).Path)

# Compute expected per-VM directory under the MCP-managed storage root
$expectedStorageRoot = '{escapedStorageRoot}'
$vmName = $vm.Name
$expectedVmDir = Join-Path $expectedStorageRoot $vmName

# Remove the VM
Remove-VM -VM $vm -Force

# Clean up VHDX files
foreach ($path in $vhdPaths) {{
    if ($path -and (Test-Path -LiteralPath $path)) {{
        Remove-Item -LiteralPath $path -Force
    }}
}}

# Issue #25: managed-path safety guard before per-VM directory removal
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
    /// <remarks>
    /// Returns VMs filtered by name pattern (wildcard match) and optionally by hyper-v-mcp tag.
    /// When nameFilter is provided, uses Get-VM -Name "*filter*" for wildcard matching.
    ///
    /// WMI workaround: All Get-VM calls use -ComputerName localhost to avoid the
    /// "Value cannot be null. Parameter name: name" WMI provider bug on newer
    /// Windows 11 builds (26200+) when Hyper-V cmdlets are invoked from non-interactive
    /// PowerShell sessions. See /myplans/vm-management/lifecycle/lifecycle-design.md — LF-D7.
    /// </remarks>
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
            // Issue #8 + WMI workaround: Parameterless Get-VM and Get-VM -Name '*' both fail
            // in the MCP server's spawned PowerShell process with "Value cannot be null.
            // Parameter name: name" due to a WMI provider bug on Windows 11 build 26200+.
            // Adding -ComputerName localhost forces the WMI provider through a code path
            // that does not trigger the null-name bug.
            getVmCommand = "$vms = Get-VM -Name '*' -ComputerName localhost";
        }

        // Filter to VMs tagged with hyper-v-mcp in Notes, then output as JSON array.
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

    /// <inheritdoc />
    public async Task<VmInfo> GetVmStatusAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // PB-D7.g: empty actionBlock sentinel — read-only projection, no state change,
        // no re-fetch. Emits one extra blank line in the script (PB-I8 documented no-op).
        // Timeout stays at 30s per PB-D8 (NOT 120s).
        _logger.LogDebug("Getting status for VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await RunSingleVmActionAsync(safeVmId, actionBlock: "", timeoutSeconds: 30, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists available base VHDX images from the configured image directory.
    /// See /myplans/vm-management/storage/storage-design.md — Base Image Enumeration.
    /// See /myplans/vm-management/storage/storage-design.md — ST-D7 (empty-config behavior).
    ///
    /// Resolution order (ST-D7):
    ///   1. <c>HYPERV_MCP_IMAGE_DIR</c> environment variable.
    ///   2. Host-profile <c>BaseVhdxPath</c> parent directory.
    ///   3. <c>HYPERV_MCP_BASE_VHDX</c> environment variable parent directory.
    ///   4. Unconfigured → returns successful empty list with <c>Configured=false</c>.
    ///
    /// Error mapping:
    ///   - Configured directory does not exist → <see cref="ArgumentException"/> → INVALID_PARAMETER.
    ///   - Configured directory exists but enumeration fails (ACL/IO) →
    ///     <see cref="IoOperationFailedException"/> → IO_ERROR.
    ///
    /// WMI workaround (LF-D7): Get-VHD uses -ComputerName localhost to avoid
    /// the "Value cannot be null" WMI provider bug on Windows 11 build 26200+.
    /// </remarks>
    public async Task<ImageListResult> ListImagesAsync(string hostId, CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // ST-D7 resolution order:
        //   (1) HYPERV_MCP_IMAGE_DIR
        //   (2) host-profile BaseVhdxPath parent
        //   (3) HYPERV_MCP_BASE_VHDX parent
        //   (4) unconfigured
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
            // ST-D7: unconfigured is a soft, successful state — NOT an error envelope.
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

        // ST-D7: a configured-but-missing directory is an operator-supplied path bug
        // (INVALID_PARAMETER); a configured-but-unauthorized/IO-failing directory is
        // IO_ERROR (Code Review Gate 6 Blocker #1, #54).
        //
        // PR #67 review (copilot-pull-request-reviewer, comment 3179029483):
        // Do NOT pre-check with `Directory.Exists` / `DirectoryInfo.Exists` — both
        // return `false` for ACL-denied paths, which would misclassify an
        // IO/permission failure as INVALID_PARAMETER. Rely on a single probe via
        // `EnumerateFileSystemEntries(...).GetEnumerator().MoveNext()` and let the
        // catch arms classify: DirectoryNotFoundException → missing
        // (INVALID_PARAMETER), UnauthorizedAccessException/IOException → IO_ERROR.
        // The probe is one-step (cheap on the happy path).
        // Issue #73: probe via injected IFileSystemProbe seam. The seam preserves
        // native exception fidelity (DirectoryNotFoundException /
        // UnauthorizedAccessException / IOException) so the catch arms below —
        // and the downstream ErrorMapper envelope shape — remain unchanged.
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
            // ST-D7: existing-but-unenumerable directory → IO_ERROR.
            throw new IoOperationFailedException(
                imageDir,
                $"Configured image directory '{imageDir}' is not accessible: {ex.Message}",
                ex);
        }
        catch (System.IO.IOException ex)
        {
            // ST-D7: filesystem-level failure on enumeration probe → IO_ERROR.
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
            // ST-D7: ACL failure on enumeration → IO_ERROR (configured but unreadable).
            throw new IoOperationFailedException(
                imageDir!,
                $"Cannot enumerate image directory '{imageDir}': access denied.",
                uaEx);
        }
        catch (System.IO.IOException ioEx)
        {
            // ST-D7: filesystem-level failure on enumeration → IO_ERROR.
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
            // ST-D7: PS-side enumeration failures (e.g. Get-ChildItem access denied,
            // VHDX read-locked) materialize as non-zero exit codes / stderr; promote
            // these from the generic COMMAND_FAILED → IO_ERROR so the operator can
            // distinguish "enumeration failed" from "wrong path".
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

    /// <inheritdoc />
    /// <remarks>
    /// Polling-based readiness wait. Polls every 3 seconds up to timeoutSeconds.
    /// VM is considered ready when:
    ///   1. VM state is Running (State == 2)
    ///   2. Heartbeat integration service reports "OK"
    /// See /myplans/vm-management/lifecycle/lifecycle-design.md — Readiness Probes.
    /// </remarks>
    public async Task<VmInfo> WaitForReadyAsync(string hostId, string vmId, int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Issue 2: Validate vmId is a GUID to prevent PowerShell injection.
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // PowerShell script that polls VM state and heartbeat in a loop.
        // Returns VM info JSON when ready, or throws on timeout.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$vmId = '{safeVmId}'
$timeoutSec = {timeoutSeconds}
$pollIntervalSec = 3
$deadline = (Get-Date).AddSeconds($timeoutSec)

while ((Get-Date) -lt $deadline) {{
    # WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
    $vm = Get-VM -Id $vmId -ComputerName localhost -ErrorAction SilentlyContinue
    if (-not $vm) {{ throw ""VM not found: $vmId"" }}

    if ($vm.State -eq 'Running') {{
        # Check heartbeat integration service
        $hb = (Get-VMIntegrationService -VM $vm | Where-Object {{$_.Name -eq 'Heartbeat'}}).PrimaryStatusDescription
        if ($hb -eq 'OK') {{
            # VM is ready — return info
            $vm | Select-Object Id, Name, State, ProcessorCount, @{{N='MemoryMB';E={{$_.MemoryStartup/1MB}}}}, @{{N='UptimeSeconds';E={{$_.Uptime.TotalSeconds}}}} | ConvertTo-Json
            return
        }}
    }}

    Start-Sleep -Seconds $pollIntervalSec
}}

throw ""Timed out waiting for VM '$vmId' to become ready after $timeoutSec seconds""
";

        _logger.LogInformation("Waiting for VM '{VmId}' to become ready on host '{HostId}' (timeout={TimeoutSeconds}s)",
            safeVmId, hostId, timeoutSeconds);

        if (timeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                "Timeout must be zero (no timeout) or a positive number of seconds.");
        }

        var executorTimeoutSeconds = timeoutSeconds == 0 ? 0 : checked(timeoutSeconds + 30);

        try
        {
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: executorTimeoutSeconds, ct: ct);
            HandleError(result, hostProfile.HostId, safeVmId);

            return ParseSingleVmInfo(result.Stdout, hostProfile.HostId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Timed out waiting for VM"))
        {
            throw new TimeoutException(
                $"VM '{safeVmId}' did not become ready within {timeoutSeconds} seconds.", ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Lists all VMs tagged with <c>hyper-v-mcp:</c> in Notes and classifies each into one of:
    /// <list type="bullet">
    ///   <item><c>orphan</c> — creation timestamp parses AND is older than the 24h cutoff. Destroyed when <paramref name="dryRun"/> is false.</item>
    ///   <item><c>unknown-age</c> — tagged but creation timestamp is missing or unparseable. <b>Reported but never auto-destroyed</b>, regardless of <paramref name="dryRun"/> (fail-closed).</item>
    ///   <item><c>live</c> — within the cutoff. Not returned.</item>
    /// </list>
    /// Power state is <b>not</b> an input to the predicate — see decision LF-D10
    /// in <c>myplans/vm-management/lifecycle/lifecycle-design.md</c>
    /// (fix for <see href="https://github.com/simurg79/hyper-v-mcp-server/issues/93">issue #93</see>).
    /// </remarks>
    public async Task<IReadOnlyList<VmInfo>> CleanupOrphansAsync(string hostId, bool dryRun = true,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);

        // Build a PowerShell script that:
        // 1. Lists all VMs tagged with hyper-v-mcp:
        // 2. Parses the creation timestamp from Notes (age-only predicate; LF-D10)
        // 3. Classifies as 'orphan' (parseable + old) or 'unknown-age' (parse failure)
        // 4. Destroys ONLY 'orphan' rows when -not $dryRun (unknown-age never auto-destroyed)
        // 5. Returns both kinds of rows with a 'Reason' field
        var dryRunFlag = dryRun ? "$true" : "$false";
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

$dryRun = {dryRunFlag}
$cutoffTime = (Get-Date).AddHours(-24)

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$allVms = Get-VM -Name '*' -ComputerName localhost -ErrorAction SilentlyContinue
if (-not $allVms) {{ $allVms = @() }}

# Filter to VMs tagged with hyper-v-mcp
$tagged = @($allVms | Where-Object {{ $_.Notes -like '*hyper-v-mcp:*' }})

$orphans = @()
foreach ($vm in $tagged) {{
    # LF-D10: age-only predicate. Power state is NOT an input.
    # Three buckets: orphan (parseable + old), unknown-age (parse fail), live (skipped).
    $reason = $null
    # LF-D10: regex excludes both whitespace AND ';' so trailing tag segments
    # like ';type=iso-install' don't get glued onto the timestamp capture and
    # cause [DateTimeOffset]::Parse to spuriously fail (which would fail-closed
    # every iso-installed VM into 'unknown-age' forever).
    if ($vm.Notes -match 'hyper-v-mcp:created=([^\s;]+)') {{
        $createdAt = $null
        try {{
            $createdAt = [DateTimeOffset]::Parse($Matches[1])
        }} catch {{
            $createdAt = $null
        }}
        if ($null -eq $createdAt) {{
            # Fail-closed: tagged but unparseable timestamp -> report only, never destroy.
            $reason = 'unknown-age'
        }} elseif ($createdAt -lt $cutoffTime) {{
            $reason = 'orphan'
        }}
        # else: within cutoff -> live, skip entirely.
    }} else {{
        # LF-D10 contract: tagged with hyper-v-mcp: but NO 'created=' segment
        # (or malformed/missing) -> fail-closed as 'unknown-age'. Reported but
        # never auto-destroyed. Without this branch the row would be silently
        # dropped, leaving a tagged VM the operator can't see in the report.
        $reason = 'unknown-age'
    }}

    if ($null -ne $reason) {{
        # Only 'orphan' rows are eligible for destroy. 'unknown-age' is ALWAYS reported only.
        if ($reason -eq 'orphan' -and -not $dryRun) {{
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

# Issue #207 / VC-CO-D2: defense-in-depth empty-host guard.
# Some PS hosts can leave $orphans as $null (e.g., when no iterations ran in
# the foreach), and `$null.Count` would surface as 0 only because PS coerces
# $null to a single-element pipeline in some contexts. Guard explicitly on
# $null OR an empty array, and force array semantics via -InputObject @(...)
# so a single-element result is still emitted as a JSON array (not a bare
# object). Depth 4 matches the inline shape above.
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

        // Issue #207 / VC-CO-D3..D5: defense-in-depth C#-side filter for degenerate
        // empty rows that some PS hosts can emit (e.g., a single `{}` element). This
        // filter is scoped to CleanupOrphansAsync ONLY; ParseVmInfoList and
        // MapJsonToVmInfo remain unchanged (design constraint C2).
        var parsed = ParseVmInfoList(result.Stdout, hostProfile.HostId);
        return FilterEmptyOrphanRows(parsed, _logger, hostProfile.HostId, dryRun, result.Stdout);
    }

    /// <summary>
    /// Issue #207 / VC-CO-D3..D5 defense-in-depth filter scoped to
    /// <see cref="CleanupOrphansAsync"/>. Drops rows that have both an empty
    /// <see cref="VmInfo.VmId"/> AND an empty <see cref="VmInfo.Name"/>
    /// (the "sentinel orphan" shape observed in TC-W14 / TC-L13). When at
    /// least one row is dropped, emits exactly one structured warning so
    /// operators can detect future PS-host regressions without changing the
    /// public envelope.
    /// </summary>
    /// <remarks>
    /// Per design constraint C2 this helper is the ONLY C#-side defense layer.
    /// <see cref="ParseVmInfoList"/> and <see cref="MapJsonToVmInfo"/> are
    /// intentionally untouched so all other call sites keep byte-identical
    /// behavior (constraint C1).
    /// </remarks>
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

    // ─── ISO Installation ───────────────────────────────────────────────────

    /// <summary>
    /// Autounattend.xml template for unattended Windows 11 installation.
    /// Placeholders: {locale}, {windowsEdition}, {adminPassword}, {productKeyElement}, {vmName}.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Autounattend Template.
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

    /// <summary>
    /// Stripped-down unattend.xml template for specialize + oobeSystem passes only.
    /// This file is copied to W:\Windows\Panther\unattend.xml by the WinPE DISM script.
    /// It must NOT include the windowsPE pass (which has complex cmd.exe paths that
    /// cause "Windows could not parse or process unattend answer file" errors).
    /// All component elements require publicKeyToken for the specialize pass parser.
    /// Placeholders: {locale}, {adminPassword}, {vmName}.
    /// </summary>
    private const string PantherUnattendTemplate = @"<?xml version=""1.0"" encoding=""utf-8""?>
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

    /// <inheritdoc />
    /// <remarks>
    /// Implements the 13-step ISO installation orchestration from design doc.
    /// Steps 1-8 failure: full rollback (destroy VM + delete artifacts).
    /// Step 9 timeout: preserve VM, return INSTALL_TIMEOUT error with VM info.
    /// Steps 9-11 failure: preserve VM, return error with VM info + cleanup artifacts.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Internal Orchestration Sequence.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — Rollback Policy.
    /// </remarks>
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
        CancellationToken ct = default)
    {
        // ──────────────────────────────────────────────────────────────────
        // Issue #97 — Step 1: C#-side input validation BEFORE any PowerShell.
        // Order matches /myplans/vm-management/iso-installation/iso-installation-design.md
        // §"Step 1: Validate Inputs":
        //   1. ISO existence            → ISO_NOT_FOUND
        //   2. OS-family check (D16)    → OS_NOT_SUPPORTED  (always; never bypassable)
        //   3. Resource-floor preflight → INSUFFICIENT_RESOURCES (gated by skipPreflight)
        //   4. VM name uniqueness        → VM_ALREADY_EXISTS (still PS-side, cheap)
        // ──────────────────────────────────────────────────────────────────

        // (1) ISO existence — fail fast with a typed exception so ErrorMapper emits
        // ISO_NOT_FOUND. The PS script also defends against this, but doing it here
        // gives a structured error before any process spawn.
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
        {
            throw new IsoNotFoundException(isoPath ?? string.Empty);
        }

        // (2) OS-family check (ISO-D16) — ALWAYS runs, NEVER bypassed by skipPreflight.
        // Mounts the ISO read-only via PowerShell, looks for sources\install.wim,
        // dismounts in a finally block (no leaked mounts on failure).
        var (isWindows, diagnostic) = await _isoInspector
            .ContainsWindowsInstallWimWithDiagnosticAsync(isoPath, ct)
            .ConfigureAwait(false);
        if (!isWindows)
        {
            _logger.LogWarning(
                "vm_os_install rejected non-Windows ISO '{IsoPath}': {Diagnostic}",
                isoPath, diagnostic ?? "no install.wim found");
            throw new OsNotSupportedException(isoPath);
        }

        // (3) Resource-floor preflight (ISO-D17) — short-circuit on first failure.
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
        // See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D7.
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

        // Generate autounattend XML content with parameter substitution.
        // When no product key is provided, use the well-known Generic Volume License Key (GVLK)
        // for the selected edition. These are Microsoft-published KMS client setup keys that
        // allow Windows Setup to proceed without prompting — they do NOT activate Windows.
        // See: https://learn.microsoft.com/windows-server/get-started/kms-client-activation-keys
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

        _logger.LogInformation(
            "Starting OS installation for VM '{VmName}' on host '{HostId}' from ISO '{IsoPath}' " +
            "with {CpuCount} CPUs, {MemoryMB}MB RAM, {DiskSizeGB}GB disk, timeout={TimeoutMinutes}min",
            name, hostId, isoPath, cpuCount, memoryMB, diskSizeGB, timeoutMinutes);

        // Build the comprehensive PowerShell script that performs all 13 steps.
        // The script handles rollback internally and returns JSON with either
        // success data or error info for the C# layer to interpret.
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
Import-Module Hyper-V -ErrorAction Stop

# ══════════════════════════════════════════════════════════════════
# Parameters
# ══════════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════
# Rollback function for pre-installation failures (Steps 1-8)
# ══════════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════
# Cleanup function for post-installation (temp artifacts only)
# ══════════════════════════════════════════════════════════════════
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
    # ══════════════════════════════════════════════════════════════
    # Step 1: Validate Inputs
    # ══════════════════════════════════════════════════════════════
    # NOTE (Issue #97): Resource-floor preflight (cpuCount/memoryMB/diskSizeGB)
    # has moved to the C#-side validator at the top of OsInstallAsync — it now
    # emits structured INSUFFICIENT_RESOURCES errors and honors skipPreflight.
    # The OS-family check (sources\install.wim, ISO-D16) also runs C#-side and
    # is mandatory; non-Windows ISOs are rejected before this script runs.
    # See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D16, ISO-D17.
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

    # ══════════════════════════════════════════════════════════════
    # Step 2: Create Empty VHDX
    # ══════════════════════════════════════════════════════════════
    New-Item -ItemType Directory -Path $vmDir -Force | Out-Null
    New-VHD -Path $vhdxPath -SizeBytes $diskSizeBytes -Dynamic -ComputerName localhost | Out-Null

    # ══════════════════════════════════════════════════════════════
    # Step 3: Create Gen 2 VM with TPM + Secure Boot
    # ══════════════════════════════════════════════════════════════
    New-VM -Name $name -Generation 2 -MemoryStartupBytes $memoryBytes `
           -VHDPath $vhdxPath -SwitchName $switchName -ComputerName localhost | Out-Null
    $vmCreated = $true

    Set-VMProcessor -VMName $name -Count $cpuCount -ComputerName localhost
    Set-VMFirmware -VMName $name -EnableSecureBoot On -ComputerName localhost
    Set-VMKeyProtector -VMName $name -NewLocalKeyProtector -ComputerName localhost
    Enable-VMTPM -VMName $name -ComputerName localhost
    Set-VM -Name $name -Notes ""hyper-v-mcp:created=$(Get-Date -Format o);type=iso-install"" `
           -ComputerName localhost

    # ══════════════════════════════════════════════════════════════
    # Step 4: Generate Autounattend.xml
    # ══════════════════════════════════════════════════════════════
    New-Item -ItemType Directory -Path $autounattendDir -Force | Out-Null
    $xmlPath = Join-Path $autounattendDir 'Autounattend.xml'
    $xmlContent = '{escapedAutounattendXml}'
    $xmlContent | Out-File -FilePath $xmlPath -Encoding UTF8

    # Write stripped-down unattend.xml (specialize/oobe only) — UTF-8 WITHOUT BOM.
    # The Windows Setup specialize pass parser rejects files with BOM.
    $pantherXmlPath = Join-Path $autounattendDir 'unattend.xml'
    $pantherXml = '{escapedPantherXml}'
    [System.IO.File]::WriteAllText($pantherXmlPath, $pantherXml, [System.Text.UTF8Encoding]::new($false))

    # ══════════════════════════════════════════════════════════════
    # Step 5: Create Autounattend ISO
    # ══════════════════════════════════════════════════════════════
    $oscdimg = Get-Command oscdimg.exe -ErrorAction SilentlyContinue
    if ($oscdimg) {{
        & oscdimg.exe -u2 -udfver102 $autounattendDir $autounattendIsoPath 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {{ throw ""oscdimg.exe failed with exit code $LASTEXITCODE"" }}
    }} else {{
        try {{
            # Add C# helper to read IMAPI2FS COM IStream, which PowerShell cannot
            # call directly because the COM IStream interface methods (Read, Stat, Seek)
            # are not exposed through the COM interop wrapper in PowerShell 5.1.
            # The C# helper casts to System.Runtime.InteropServices.ComTypes.IStream
            # which has proper RCW marshalling support.
            if (-not ([System.Management.Automation.PSTypeName]'IStreamHelper').Type) {{
                Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class IStreamHelper
{{
    public static void WriteStreamToFile(object comStream, string filePath)
    {{
        IStream istream = (IStream)comStream;
        System.Runtime.InteropServices.ComTypes.STATSTG stat;
        istream.Stat(out stat, 0);
        long totalBytes = stat.cbSize;
        istream.Seek(0, 0, IntPtr.Zero);

        byte[] buffer = new byte[65536];
        long totalRead = 0;
        using (FileStream fs = File.Create(filePath))
        {{
            while (totalRead < totalBytes)
            {{
                int toRead = (int)Math.Min(buffer.Length, totalBytes - totalRead);
                IntPtr bytesReadPtr = Marshal.AllocHGlobal(4);
                try
                {{
                    istream.Read(buffer, toRead, bytesReadPtr);
                    int bytesRead = Marshal.ReadInt32(bytesReadPtr);
                    if (bytesRead <= 0) break;
                    fs.Write(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                }}
                finally
                {{
                    Marshal.FreeHGlobal(bytesReadPtr);
                }}
            }}
        }}
    }}
}}
'@ -Language CSharp
            }}

            $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
            $fsi.FileSystemsToCreate = 4  # UDF
            $fsi.VolumeName = 'AUTOUNATTEND'
            $fsi.Root.AddTree($autounattendDir, $false)
            $resultImage = $fsi.CreateResultImage()
            $resultStream = $resultImage.ImageStream
            try {{
                [IStreamHelper]::WriteStreamToFile($resultStream, $autounattendIsoPath)
            }} finally {{
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($resultStream) | Out-Null
                [System.Runtime.InteropServices.Marshal]::ReleaseComObject($fsi) | Out-Null
            }}
        }} catch {{
            throw ""Failed to create autounattend ISO: $($_.Exception.Message)""
        }}
    }}

    if (-not (Test-Path $autounattendIsoPath)) {{
        throw ""Autounattend ISO was not created at $autounattendIsoPath""
    }}

    # ══════════════════════════════════════════════════════════════
    # Step 6: Mount ISOs
    # ══════════════════════════════════════════════════════════════
    Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation 1 `
                   -Path $isoPath -ComputerName localhost
    Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation 2 `
                   -Path $autounattendIsoPath -ComputerName localhost

    # ══════════════════════════════════════════════════════════════
    # Step 7: Set DVD Boot Order
    # ══════════════════════════════════════════════════════════════
    $dvd = Get-VMDvdDrive -VMName $name -ComputerName localhost |
           Where-Object {{ $_.ControllerLocation -eq 1 }}
    Set-VMFirmware -VMName $name -FirstBootDevice $dvd -ComputerName localhost

    # ══════════════════════════════════════════════════════════════
    # Step 8: Start VM
    # ══════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════
# Step 9: Monitor Installation Progress
# Post-step-8: failures preserve VM (no full rollback)
# ══════════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════
# Step 10: Unmount ISOs + Set HDD Boot
# ══════════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════
# Step 11: Bootstrap VM (WinRM + PS remoting)
# ══════════════════════════════════════════════════════════════════
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
            # Module autoload can fail in non-console child PowerShell processes, surfacing as a
            # misleading VirtualizationException from New-PSSession. The explicit
            # Import-Module Microsoft.PowerShell.Security and Import-Module Hyper-V calls at the
            # top of this bootstrap script mitigate that; -VMId is just the addressing form used here.
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

        # Enable PSRemoting
        Enable-PSRemoting -Force -SkipNetworkProfileCheck 2>&1 | Out-Null

        # Set execution policy
        Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Force -Scope LocalMachine 2>&1 | Out-Null

        # Enable WinRM
        Set-Service -Name WinRM -StartupType Automatic 2>&1 | Out-Null
        Start-Service -Name WinRM -ErrorAction SilentlyContinue 2>&1 | Out-Null

        # Configure firewall rule for WinRM
        $rule = Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP' -ErrorAction SilentlyContinue
        if (-not $rule) {{
            New-NetFirewallRule -Name 'WINRM-HTTP-In-TCP' -DisplayName 'WinRM HTTP' `
                -Direction Inbound -Protocol TCP -LocalPort 5985 -Action Allow 2>&1 | Out-Null
        }}

        # Get guest IP address
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

# ══════════════════════════════════════════════════════════════════
# Step 12: Clean Up Temp Artifacts
# ══════════════════════════════════════════════════════════════════
Invoke-ArtifactCleanup

# ══════════════════════════════════════════════════════════════════
# Step 13: Return Result
# ══════════════════════════════════════════════════════════════════
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

        // Security: The script contains plaintext credentials (admin password, product key).
        // PowerShellExecutor writes it to a temp .ps1 file and cleans up in a finally block,
        // but if the process crashes mid-execution, the temp file may persist.
        // We track temp files before/after to ensure secondary cleanup of any lingering
        // credential-bearing scripts.
        // See /myplans/security/security-design.md — Credential handling in temp files.
        var tempDir = Path.GetTempPath();
        var preExistingTempFiles = new HashSet<string>(
            Directory.GetFiles(tempDir, "hvmcp-*.ps1"), StringComparer.OrdinalIgnoreCase);

        PowerShellResult result;
        try
        {
            // SD-D4: OS-install scripts use a variable-backed credential pattern and embed
            // plaintext admin passwords in unattended XML; the v1 script-dump masker cannot
            // redact those, so dumping is disabled for this code path.
            // See /myplans/operational/script-dump/script-dump-design.md §1, §5 (non-goal #6), and Decision SD-D4.
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

        // Parse the JSON result from the PowerShell script.
        // The script returns either a success envelope or an error envelope.
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
                result.State = VmStateMap.GetValueOrDefault(stateInt, $"Unknown({stateInt})");
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

    // ─── Private Helpers ────────────────────────────────────────────────────

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

    /// <summary>
    /// Checks a <see cref="PowerShellResult"/> for errors and throws appropriate typed exceptions.
    /// Maps PowerShell error messages to domain exceptions for consistent error handling.
    ///
    /// The <paramref name="isCreateOperation"/> flag prevents over-broad error classification:
    /// during create operations, "not found" or "does not exist" errors typically refer to
    /// missing base VHDX or invalid storage paths, NOT a missing VM. Only non-create operations
    /// (get, start, stop, remove, checkpoint) should map these patterns to VmNotFoundException.
    /// </summary>
    private static void HandleError(PowerShellResult result, string hostId, string? vmId,
        string? vmName = null, bool isCreateOperation = false)
    {
        if (result.Success)
            return;

        var errorText = result.Stderr;

        // Map known error patterns to typed exceptions.
        // For create operations, "not found" / "does not exist" refers to missing base VHDX
        // or invalid storage paths — NOT a missing VM. Only map to VmNotFoundException for
        // operations that look up an existing VM (get, start, stop, remove, checkpoint).
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

        // Hyper-V State is an integer enum. Map to string name.
        var state = "Unknown";
        if (element.TryGetProperty("State", out var stateProp))
        {
            if (stateProp.ValueKind == JsonValueKind.Number && stateProp.TryGetInt32(out var stateInt))
            {
                state = VmStateMap.GetValueOrDefault(stateInt, $"Unknown({stateInt})");
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

        // Optional classification reason emitted by vm_cleanup_orphans (LF-D10).
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

    /// <summary>
    /// Builds a PowerShell script snippet that guards the base VHDX against mutation
    /// (ADR-4 / ST-D1, refined by ST-D6a).
    ///
    /// As of ST-D6a, SHA-256 is computed host-side via <see cref="IBaseImageHashCache"/>
    /// — NOT inline here — so the cache cannot be bypassed by pipeline cancellation
    /// and the dual-hash cost is paid at most once per (path, stat-tuple, TTL).
    /// This snippet now only enforces the ReadOnly filesystem attribute.
    /// </summary>
    private static string BuildBaseVhdxGuardScript(string escapedBaseVhdx)
    {
        return $@"
# ADR-4 / ST-D1 (refined by ST-D6a): Base VHDX mutation guard — ReadOnly enforcement only.
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

    /// <inheritdoc />
    /// <remarks>
    /// Issue #51: Returns the host-side absolute path to the VM's primary VHDX
    /// via <c>Get-VMHardDiskDrive | Select-Object -First 1</c>. Used by
    /// <c>vm_create_base_image</c> to locate the disk to copy.
    /// </remarks>
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

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
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
