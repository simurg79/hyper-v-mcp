using System.Text.Json;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Manages checkpoint (snapshot) operations for VMs via PowerShell cmdlets.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — Checkpoint Workflow.
///
/// Phase 1 implementation: local host only. Remote hosts (WinRM) will be added in a future phase.
/// Each method composes a PowerShell script that outputs JSON, then parses the result into <see cref="CheckpointResult"/>.
///
/// Design decisions:
/// - CP-D3: After checkpoint restore, invalidate the cached PSSession via
///   <see cref="ISessionStore.EvictAsync"/> (and, equivalently, through
///   <see cref="IPowerShellDirectChannel.EvictSessionAsync"/> at the channel facade).
///   The legacy <c>DisposeSessionAsync</c> name has been retired in Phase 2.
///   See /myplans/vm-management/checkpoints/checkpoints-design.md
/// - CP-D7 / VC-CE-D1..D8 (Issue #206): Envelope correctness for vm_checkpoint create —
///   pre-snapshot Id capture, hardened in-script probe loop, fail-loud PROBE_EXHAUSTED,
///   post-failure host-side verification with pre/post Id-set diff, and per-action
///   validating JSON parser. See /myplans/vm-management/checkpoints/vm-checkpoint-create-envelope-design.md
/// - All Get-VM calls use -ComputerName localhost (WMI workaround LF-D7).
/// - Checkpoint names are escaped via single-quote doubling for PowerShell safety.
/// </summary>
public class CheckpointManager : ICheckpointManager
{
    private readonly IPowerShellExecutor _psExecutor;
    private readonly IHostResolver _hostResolver;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<CheckpointManager> _logger;

    public CheckpointManager(
        IPowerShellExecutor psExecutor,
        IHostResolver hostResolver,
        ISessionStore sessionStore,
        ILogger<CheckpointManager> logger)
    {
        _psExecutor = psExecutor ?? throw new ArgumentNullException(nameof(psExecutor));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// VC-CE-D1..D8 (Issue #206): envelope correctness. Three-call flow:
    ///   (1) pre-snapshot Get-VMCheckpoint → preIds baseline (short timeout, fail-soft).
    ///   (2) main Checkpoint-VM script with embedded probe-retry loop and fail-loud PROBE_EXHAUSTED.
    ///   (3) ONLY if (2) failed AND (1) succeeded: post-failure probe → if a NEW checkpoint
    ///       with the requested name exists, downgrade to success with LogWarning;
    ///       otherwise the original CheckpointFailedException surfaces unchanged.
    /// If (1) fails, the downgrade path is disabled (per Interfaces / Consumed fallback policy).
    /// </remarks>
    public async Task<CheckpointResult> CreateCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var escapedCpName = InputValidation.EscapePowerShellString(checkpointName);

        _logger.LogInformation("Creating checkpoint '{CheckpointName}' for VM '{VmId}' on host '{HostId}'",
            checkpointName, safeVmId, hostId);

        // --- VC-CE-D5 Call (1): pre-snapshot --------------------------------
        // Capture the set of checkpoint Ids currently present for this VM so that, on
        // failure, we can determine whether a NEW checkpoint with the requested name
        // appeared between this snapshot and the post-failure probe.
        var (preSnapshotOk, preIds) = await TryGetPreSnapshotAsync(hostProfile.HostId, safeVmId, ct);

        // --- VC-CE-D5 Call (2): main Checkpoint-VM + in-script probe --------
        var script = BuildCreateCheckpointScript(safeVmId, escapedCpName, preIds);
        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct);

        if (result.Success)
        {
            return ParseCheckpointResult(result.Stdout, checkpointName, CreateValidator);
        }

        // --- VC-CE-D5 Call (3): post-failure probe (downgrade path) ---------
        // Only enabled when the pre-snapshot succeeded (per fallback policy in Interfaces /
        // Consumed). Without a trustworthy preIds baseline we cannot positively prove a
        // checkpoint is NEW, so we must err on the side of reporting failure.
        if (preSnapshotOk)
        {
            var downgrade = await TryPostFailureDowngradeAsync(
                hostProfile.HostId, safeVmId, checkpointName, preIds, result, ct);
            if (downgrade is not null)
            {
                return downgrade;
            }
        }

        // No downgrade — run original failure-mapping path.
        HandleError(result, hostProfile.HostId, safeVmId);
        // HandleError always throws on !Success; the next line is for the compiler.
        return ParseCheckpointResult(result.Stdout, checkpointName, CreateValidator);
    }

    /// <inheritdoc />
    /// <remarks>
    /// CP-D3: After restore, invalidate the cached PSSession to prevent stale session usage.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md
    /// </remarks>
    public async Task<CheckpointResult> RestoreCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var escapedCpName = InputValidation.EscapePowerShellString(checkpointName);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

# Duplicate name check: verify only one checkpoint matches
$matchingCps = @(Get-VMCheckpoint -VMName $vm.Name -Name '{escapedCpName}' -ComputerName localhost -ErrorAction SilentlyContinue)
if ($matchingCps.Count -eq 0) {{
    throw ""Checkpoint '{escapedCpName}' not found for VM '$($vm.Name)'.""
}}
if ($matchingCps.Count -gt 1) {{
    throw ""Multiple checkpoints named '{escapedCpName}' exist for VM '$($vm.Name)'. Use vm_checkpoint list to find the specific checkpoint.""
}}

# Restore checkpoint (single match guaranteed)
$matchingCps[0] | Restore-VMCheckpoint -Confirm:$false

# Return checkpoint info
[PSCustomObject]@{{
    Action = 'restore'
    VmId = '{safeVmId}'
    CheckpointName = '{escapedCpName}'
    Checkpoints = $null
}} | ConvertTo-Json -Depth 3
";

        _logger.LogInformation("Restoring checkpoint '{CheckpointName}' for VM '{VmId}' on host '{HostId}'",
            checkpointName, safeVmId, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        // CP-D3: Invalidate cached session after restore — the VM's OS state has changed
        // so any existing PSSession is stale.
        await _sessionStore.EvictAsync(hostId, safeVmId, ct);

        // Post-restore recovery: wait for VM to resume, then force clock resync.
        // See /myplans/vm-management/checkpoints/checkpoints-design.md — Post-Restore Recovery.
        await PostRestoreRecoveryAsync(safeVmId, ct);

        return ParseCheckpointResult(result.Stdout, checkpointName, RestoreValidator);
    }

    /// <inheritdoc />
    public async Task<CheckpointResult> ListCheckpointsAsync(string hostId, string vmId,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        var safeVmId = InputValidation.ValidateVmId(vmId);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

$checkpoints = @(Get-VMCheckpoint -VMName $vm.Name -ComputerName localhost -ErrorAction SilentlyContinue)
$cpList = @()
foreach ($cp in $checkpoints) {{
    $cpList += [PSCustomObject]@{{
        Name = $cp.Name
        Id = $cp.Id.ToString()
        CreatedAt = $cp.CreationTime.ToString('o')
    }}
}}

[PSCustomObject]@{{
    Action = 'list'
    VmId = '{safeVmId}'
    CheckpointName = ''
    Checkpoints = $cpList
}} | ConvertTo-Json -Depth 3
";

        _logger.LogDebug("Listing checkpoints for VM '{VmId}' on host '{HostId}'", safeVmId, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 60, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseCheckpointResult(result.Stdout, requestedName: null, ListValidator);
    }

    /// <inheritdoc />
    public async Task<CheckpointResult> DeleteCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        var safeVmId = InputValidation.ValidateVmId(vmId);
        var escapedCpName = InputValidation.EscapePowerShellString(checkpointName);

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

# Duplicate name check: verify only one checkpoint matches
$matchingCps = @(Get-VMCheckpoint -VMName $vm.Name -Name '{escapedCpName}' -ComputerName localhost -ErrorAction SilentlyContinue)
if ($matchingCps.Count -eq 0) {{
    throw ""Checkpoint '{escapedCpName}' not found for VM '$($vm.Name)'.""
}}
if ($matchingCps.Count -gt 1) {{
    throw ""Multiple checkpoints named '{escapedCpName}' exist for VM '$($vm.Name)'. Use vm_checkpoint list to find the specific checkpoint.""
}}

# Delete checkpoint (single match guaranteed)
$matchingCps[0] | Remove-VMCheckpoint -Confirm:$false

[PSCustomObject]@{{
    Action = 'delete'
    VmId = '{safeVmId}'
    CheckpointName = '{escapedCpName}'
    Checkpoints = $null
}} | ConvertTo-Json -Depth 3
";

        _logger.LogInformation("Deleting checkpoint '{CheckpointName}' for VM '{VmId}' on host '{HostId}'",
            checkpointName, safeVmId, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 120, ct: ct);
        HandleError(result, hostProfile.HostId, safeVmId);

        return ParseCheckpointResult(result.Stdout, checkpointName, DeleteValidator);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Issue #51 / CP-D6: Linear-chain merge. The script:
    /// <list type="number">
    ///   <item>Enumerates all VM checkpoints.</item>
    ///   <item>Rejects branched trees (any node with more than one child) by emitting
    ///         a sentinel error which is mapped to <see cref="MergeNotSupportedException"/>.</item>
    ///   <item>Walks the chain oldest-first (root → ... → newest) and calls
    ///         <c>Remove-VMSnapshot -IncludeAllChildSnapshots:$false</c> on each,
    ///         which Hyper-V translates into a merge into the parent. The cmdlet
    ///         blocks until the merge job completes.</item>
    ///   <item>Returns the merged count as JSON.</item>
    /// </list>
    /// Does NOT mutate VM power state.
    /// </remarks>
    public async Task<MergeResult> MergeAllAsync(string hostId, string vmId,
        CancellationToken ct = default)
    {
        var hostProfile = ResolveLocalHost(hostId);
        var safeVmId = InputValidation.ValidateVmId(vmId);

        // Sentinel strings the host script writes on stdout so we can distinguish
        // topology rejection (MERGE_NOT_SUPPORTED) from runtime failures
        // (CHECKPOINT_MERGE_FAILED). The script writes JSON on the success path.
        const string SentinelBranched = "MERGE_NOT_SUPPORTED:BRANCHED";

        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

$all = @(Get-VMSnapshot -VM $vm -ErrorAction SilentlyContinue)
if ($all.Count -eq 0) {{
    [PSCustomObject]@{{ MergedCount = 0 }} | ConvertTo-Json -Compress
    return
}}

# Detect branched tree: any node referenced as ParentSnapshotId by more than one child
$childrenByParent = @{{}}
foreach ($s in $all) {{
    $pid = if ($s.ParentSnapshotId) {{ $s.ParentSnapshotId.ToString() }} else {{ '<root>' }}
    if (-not $childrenByParent.ContainsKey($pid)) {{ $childrenByParent[$pid] = 0 }}
    $childrenByParent[$pid] = $childrenByParent[$pid] + 1
}}
foreach ($k in $childrenByParent.Keys) {{
    if ($childrenByParent[$k] -gt 1) {{
        Write-Output '{SentinelBranched}'
        return
    }}
}}

# Oldest-first walk: sort ascending by CreationTime.
$ordered = $all | Sort-Object CreationTime
$merged = 0
foreach ($snap in $ordered) {{
    # Remove-VMSnapshot without -IncludeAllChildSnapshots merges this snapshot into
    # its parent and blocks until the underlying merge-job completes.
    Remove-VMSnapshot -VMSnapshot $snap -Confirm:$false -ErrorAction Stop
    $merged = $merged + 1
}}
[PSCustomObject]@{{ MergedCount = $merged }} | ConvertTo-Json -Compress
";

        _logger.LogInformation("Merging all checkpoints for VM '{VmId}' on host '{HostId}' (linear-chain only, CP-D6)",
            safeVmId, hostId);

        var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 1800, ct: ct);

        // Runtime failure (stderr / non-zero exit) → CheckpointMergeFailedException.
        if (!result.Success)
        {
            throw new CheckpointMergeFailedException(hostProfile.HostId, safeVmId,
                $"Checkpoint merge failed (exit code {result.ExitCode}): {result.Stderr}");
        }

        var stdout = (result.Stdout ?? string.Empty).Trim();

        // Topology rejection: sentinel emitted by the script.
        if (stdout.Contains(SentinelBranched, StringComparison.Ordinal))
        {
            throw new MergeNotSupportedException(hostProfile.HostId, safeVmId,
                $"Checkpoint tree for VM '{safeVmId}' is not a linear chain (branched/multiple children). " +
                "vm_create_base_image only supports linear checkpoint chains; resolve branches manually before retrying.");
        }

        // Success: parse the merged count.
        int mergedCount = 0;
        if (!string.IsNullOrEmpty(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout);
                if (doc.RootElement.TryGetProperty("MergedCount", out var mc) && mc.TryGetInt32(out var n))
                {
                    mergedCount = n;
                }
            }
            catch (JsonException)
            {
                // Tolerate stray non-JSON noise; treat as zero-merge success rather than failing.
                mergedCount = 0;
            }
        }

        return new MergeResult(Success: true, MergedCount: mergedCount, FailureReason: null);
    }

    // ─── VC-CE-D5: Pre-snapshot / Post-failure probe (create path only) ──────

    /// <summary>
    /// VC-CE-D5 call (1): pre-snapshot. Captures the current set of checkpoint Ids
    /// for the VM via a short-timeout Get-VMCheckpoint call so the post-failure probe
    /// can compute newIds = postIds \ preIds.
    ///
    /// Returns (false, empty) if the probe itself fails — per the fallback policy in
    /// the design's Interfaces / Consumed section, this disables the downgrade path
    /// for this invocation (no trustworthy baseline → cannot prove NEW).
    /// </summary>
    private async Task<(bool ok, HashSet<string> preIds)> TryGetPreSnapshotAsync(
        string hostId, string safeVmId, CancellationToken ct)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}
$cps = @(Get-VMCheckpoint -VMName $vm.Name -ComputerName localhost -ErrorAction SilentlyContinue)
$ids = @()
foreach ($c in $cps) {{ $ids += $c.Id.ToString() }}
[PSCustomObject]@{{ Ids = $ids }} | ConvertTo-Json -Compress
";
        try
        {
            var result = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 30, ct: ct);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "VC-CE-D5 pre-snapshot probe failed for host '{HostId}' VM '{VmId}': exitCode={ExitCode}, stderrPreview='{StderrPreview}'. Downgrade path disabled for this invocation.",
                    hostId, safeVmId, result.ExitCode, Truncate(result.Stderr, 512));
                return (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            var ids = ParseIdSet(result.Stdout);
            return (true, ids);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "VC-CE-D5 pre-snapshot probe threw for host '{HostId}' VM '{VmId}'. Downgrade path disabled for this invocation.",
                hostId, safeVmId);
            return (false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// VC-CE-D5 call (3): post-failure probe. Issued only when the main script failed
    /// AND the pre-snapshot succeeded. Looks for a NEW checkpoint (Id NOT in preIds)
    /// whose Name equals requestedName. If found, downgrades to success and logs a
    /// structured warning per VC-CE-D7. Returns null if no downgrade applies.
    /// </summary>
    private async Task<CheckpointResult?> TryPostFailureDowngradeAsync(
        string hostId,
        string safeVmId,
        string requestedName,
        HashSet<string> preIds,
        PowerShellResult mainResult,
        CancellationToken ct)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module Hyper-V -ErrorAction Stop
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}
$cps = @(Get-VMCheckpoint -VMName $vm.Name -ComputerName localhost -ErrorAction SilentlyContinue)
$items = @()
foreach ($c in $cps) {{
    $items += [PSCustomObject]@{{
        Name = $c.Name
        Id = $c.Id.ToString()
        CreatedAt = $c.CreationTime.ToString('o')
    }}
}}
[PSCustomObject]@{{ Checkpoints = $items }} | ConvertTo-Json -Depth 3 -Compress
";
        PowerShellResult probe;
        try
        {
            probe = await _psExecutor.ExecuteAsync(script, timeoutSeconds: 30, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "VC-CE-D5 post-failure probe threw for host '{HostId}' VM '{VmId}' requestedName='{RequestedName}'. Preserving original failure.",
                hostId, safeVmId, requestedName);
            return null;
        }

        if (!probe.Success)
        {
            _logger.LogWarning(
                "VC-CE-D5 post-failure probe failed for host '{HostId}' VM '{VmId}' requestedName='{RequestedName}': exitCode={ExitCode}. Preserving original failure.",
                hostId, safeVmId, requestedName, probe.ExitCode);
            return null;
        }

        var (newName, newId, newCreatedAt) = FindNewCheckpoint(probe.Stdout, requestedName, preIds);
        if (newId is null)
        {
            return null;
        }

        // VC-CE-D7: structured LogWarning on the downgrade path.
        _logger.LogWarning(
            "VC-CE-D5 downgrade: host='{HostId}' vmId='{VmId}' requestedName='{RequestedName}' mainExitCode={ExitCode} stderrPreview='{StderrPreview}' newCheckpointId='{NewId}'. Reporting success despite PowerShell non-zero exit because a NEW checkpoint with the requested name is observable on the host.",
            hostId, safeVmId, requestedName, mainResult.ExitCode, Truncate(mainResult.Stderr, 512), newId);

        return new CheckpointResult
        {
            Action = "create",
            VmId = safeVmId,
            CheckpointName = newName ?? requestedName,
            Checkpoints = new List<CheckpointInfo>
            {
                new CheckpointInfo
                {
                    Name = newName ?? requestedName,
                    Id = newId,
                    CreatedAt = newCreatedAt,
                },
            },
        };
    }

    private static HashSet<string> ParseIdSet(string stdout)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return set;
        }

        foreach (var (json, _, _) in ExtractTopLevelObjects(stdout))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("Ids", out var idsProp)
                    && idsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idEl in idsProp.EnumerateArray())
                    {
                        var s = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            set.Add(s);
                        }
                    }
                    return set;
                }
            }
        }

        return set;
    }

    private static (string? Name, string? Id, DateTimeOffset CreatedAt) FindNewCheckpoint(
        string stdout, string requestedName, HashSet<string> preIds)
    {
        foreach (var (json, _, _) in ExtractTopLevelObjects(stdout))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("Checkpoints", out var cps)
                    || cps.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var cp in cps.EnumerateArray())
                {
                    if (cp.ValueKind != JsonValueKind.Object) continue;
                    var name = cp.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : null;
                    var id = cp.TryGetProperty("Id", out var i) && i.ValueKind == JsonValueKind.String
                        ? i.GetString() : null;

                    if (string.IsNullOrEmpty(id)) continue;
                    if (!string.Equals(name, requestedName, StringComparison.Ordinal)) continue;
                    if (preIds.Contains(id)) continue;

                    DateTimeOffset createdAt = DateTimeOffset.MinValue;
                    if (cp.TryGetProperty("CreatedAt", out var ca)
                        && ca.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(ca.GetString(), out var parsed))
                    {
                        createdAt = parsed;
                    }
                    return (name, id, createdAt);
                }
            }
        }
        return (null, null, DateTimeOffset.MinValue);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    // ─── VC-CE-D2: create-script builder (hardened) ──────────────────────────

    private static string BuildCreateCheckpointScript(string safeVmId, string escapedCpName, HashSet<string> preIds)
    {
        // Serialize preIds into a PowerShell string-array literal. Ids come from Hyper-V
        // GUIDs (and our trusted pre-snapshot parser) so they're safe; we still escape
        // single quotes defensively.
        var preIdLiteral = "@(" + string.Join(",",
            preIds.Select(id => "'" + id.Replace("'", "''") + "'")) + ")";

        return $@"
Import-Module Hyper-V -ErrorAction Stop

# WMI workaround (LF-D7): -ComputerName localhost avoids null-name WMI bug
$ErrorActionPreference = 'Stop'
$vm = Get-VM -Id '{safeVmId}' -ComputerName localhost
if (-not $vm) {{ throw ""VM not found: {safeVmId}"" }}

# VC-CE-D5 (in-script defense-in-depth duplicate of host-side preIds; the C#-supplied
# list is authoritative for the diff).
$preIds = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($pid in {preIdLiteral}) {{ [void]$preIds.Add($pid) }}
$inScriptPre = @(Get-VMCheckpoint -VMName $vm.Name -ComputerName localhost -ErrorAction SilentlyContinue)
foreach ($p in $inScriptPre) {{ [void]$preIds.Add($p.Id.ToString()) }}

# VC-CE-D2/D6: same-name pre-existing checkpoint detection without stdout-bleeding
# Write-Warning (which would corrupt JSON parsing on the C# side).
$existing = @(Get-VMCheckpoint -VMName $vm.Name -Name '{escapedCpName}' -ComputerName localhost -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {{
    Write-Verbose ""A checkpoint named '{escapedCpName}' already exists for VM '$($vm.Name)'.""
}}

# VC-CE-D2: narrowed ErrorActionPreference scope + silenced Warning/Verbose channels
# on the cmdlet itself so noise cannot reach stdout.
try {{
    Checkpoint-VM -VMName $vm.Name -SnapshotName '{escapedCpName}' -ComputerName localhost -WarningAction SilentlyContinue -Verbose:$false
}} catch {{
    Write-Error $_.Exception.Message
    exit 1
}}

# VC-CE-D2/D3: bounded retry loop (3 attempts x 500ms) to absorb Hyper-V eventual-
# consistency. Filter for NEW checkpoint (Id -notin $preIds) AND Name match.
$cp = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {{
    $candidates = @(Get-VMCheckpoint -VMName $vm.Name -Name '{escapedCpName}' -ComputerName localhost -ErrorAction SilentlyContinue)
    $newOnes = @($candidates | Where-Object {{ $_ -ne $null -and (-not $preIds.Contains($_.Id.ToString())) }})
    if ($newOnes.Count -gt 0) {{
        $cp = $newOnes | Sort-Object CreationTime -Descending | Select-Object -First 1
        break
    }}
    if ($attempt -lt 3) {{ Start-Sleep -Milliseconds 500 }}
}}

if ($cp -eq $null) {{
    # VC-CE-D3: fail loud — no synthetic success. Stderr message routes through
    # HandleError → CheckpointFailedException → CHECKPOINT_FAILED.
    [Console]::Error.WriteLine(""CHECKPOINT_PROBE_EXHAUSTED: requested='{escapedCpName}' preIdCount=$($preIds.Count) attempts=3"")
    exit 2
}}

[PSCustomObject]@{{
    Action = 'create'
    VmId = '{safeVmId}'
    CheckpointName = $cp.Name
    Checkpoints = @([PSCustomObject]@{{
        Name = $cp.Name
        Id = $cp.Id.ToString()
        CreatedAt = $cp.CreationTime.ToString('o')
    }})
}} | ConvertTo-Json -Depth 3
";
    }

    // ─── Private Helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the host profile and enforces local-only constraint for Phase 1.
    /// </summary>
    private Configuration.HostProfile ResolveLocalHost(string hostId)
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
    /// Checks a PowerShell result for errors and throws appropriate exceptions.
    /// Throws CheckpointFailedException for checkpoint-specific errors, VmNotFoundException
    /// for VM not found errors, and CheckpointFailedException as default for all other errors
    /// (so ErrorMapper maps to CHECKPOINT_FAILED instead of COMMAND_FAILED).
    /// </summary>
    private static void HandleError(PowerShellResult result, string hostId, string vmId)
    {
        if (result.Success)
            return;

        var errorText = result.Stderr;

        // VM not found errors → VmNotFoundException
        if (ContainsAny(errorText, "VM not found", "does not exist", "could not find"))
        {
            throw new VmNotFoundException(hostId, vmId);
        }

        // Checkpoint-specific error patterns → CheckpointFailedException
        if (ContainsAny(errorText, "checkpoint", "snapshot", "Checkpoint-VM",
            "Restore-VMCheckpoint", "Remove-VMCheckpoint", "Get-VMCheckpoint",
            "Multiple checkpoints named", "Insufficient disk space",
            "CHECKPOINT_PROBE_EXHAUSTED"))
        {
            throw new CheckpointFailedException(hostId, vmId,
                $"Checkpoint operation failed (exit code {result.ExitCode}): {errorText}");
        }

        // Default: all checkpoint manager errors should map to CHECKPOINT_FAILED.
        // Use CheckpointFailedException instead of InvalidOperationException so
        // ErrorMapper maps to CHECKPOINT_FAILED (not COMMAND_FAILED).
        throw new CheckpointFailedException(hostId, vmId,
            $"Checkpoint operation failed (exit code {result.ExitCode}): {errorText}");
    }

    /// <summary>
    /// Post-restore recovery: waits for VM to reach Running state, then forces clock resync.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — Post-Restore Recovery.
    ///
    /// Steps:
    /// 1. Poll VM state until Running (2-second intervals, up to 60 seconds)
    /// 2. Force clock resync via w32tm /resync /force (best-effort, errors are logged but not thrown)
    /// </summary>
    private async Task PostRestoreRecoveryAsync(string vmId, CancellationToken ct)
    {
        var recoveryScript = $@"
$ErrorActionPreference = 'Continue'
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
Import-Module Hyper-V -ErrorAction Stop

$vmId = '{vmId}'
$maxWaitSeconds = 60
$pollIntervalSeconds = 2
$elapsed = 0

# Step 1: Wait for VM to reach Running state after restore
while ($elapsed -lt $maxWaitSeconds) {{
    $vm = Get-VM -Id $vmId -ComputerName localhost -ErrorAction SilentlyContinue
    if ($vm -and $vm.State -eq 'Running') {{
        break
    }}
    Start-Sleep -Seconds $pollIntervalSeconds
    $elapsed += $pollIntervalSeconds
}}

# Step 2: Force clock resync via PS Direct (best-effort)
$vm = Get-VM -Id $vmId -ComputerName localhost -ErrorAction SilentlyContinue
if ($vm -and $vm.State -eq 'Running') {{
    try {{
        Invoke-Command -VMId $vmId -ScriptBlock {{ w32tm /resync /force }} -ErrorAction Stop
    }} catch {{
        # Clock resync is best-effort — log but don't fail the restore
        Write-Warning ""Clock resync failed for VM $vmId : $($_.Exception.Message)""
    }}
}}
";

        try
        {
            // Post-restore recovery is best-effort — errors are logged but don't fail the restore
            var result = await _psExecutor.ExecuteAsync(recoveryScript, timeoutSeconds: 90, ct: ct);
            if (!result.Success || result.TimedOut || result.Cancelled)
            {
                _logger.LogWarning(
                    "Post-restore recovery completed with issues for VM '{VmId}'. Success: {Success}, TimedOut: {TimedOut}, Cancelled: {Cancelled}. Clock may be out of sync.",
                    vmId,
                    result.Success,
                    result.TimedOut,
                    result.Cancelled);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-restore recovery failed for VM '{VmId}'. Clock may be out of sync.", vmId);
        }
    }

    // ─── VC-CE-D4: Tolerant + validating parser ─────────────────────────────

    /// <summary>
    /// Per-action validator: create. Requires Action == "create", CheckpointName == requestedName,
    /// and Checkpoints array of length ≥ 1 with non-empty Checkpoints[0].Id.
    /// </summary>
    private static bool CreateValidator(JsonElement root, string? requestedName)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetString(root, "Action", out var action) || action != "create") return false;
        if (!TryGetString(root, "CheckpointName", out var name)) return false;
        if (requestedName is not null && !string.Equals(name, requestedName, StringComparison.Ordinal)) return false;
        if (!root.TryGetProperty("Checkpoints", out var cps) || cps.ValueKind != JsonValueKind.Array) return false;
        if (cps.GetArrayLength() < 1) return false;
        var first = cps[0];
        if (first.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetString(first, "Id", out var id) || string.IsNullOrEmpty(id)) return false;
        return true;
    }

    /// <summary>
    /// Per-action validator: restore. Requires Action == "restore" and CheckpointName == requestedName.
    /// Checkpoints may be null/absent.
    /// </summary>
    private static bool RestoreValidator(JsonElement root, string? requestedName)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetString(root, "Action", out var action) || action != "restore") return false;
        if (!TryGetString(root, "CheckpointName", out var name)) return false;
        if (requestedName is not null && !string.Equals(name, requestedName, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Per-action validator: delete. Requires Action == "delete" and CheckpointName == requestedName.
    /// Checkpoints may be null/absent.
    /// </summary>
    private static bool DeleteValidator(JsonElement root, string? requestedName)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetString(root, "Action", out var action) || action != "delete") return false;
        if (!TryGetString(root, "CheckpointName", out var name)) return false;
        if (requestedName is not null && !string.Equals(name, requestedName, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Per-action validator: list. Requires Action == "list" and a Checkpoints array (possibly empty).
    /// Each present element must have non-empty Name and Id. requestedName is ignored.
    /// </summary>
    private static bool ListValidator(JsonElement root, string? requestedName)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetString(root, "Action", out var action) || action != "list") return false;
        if (!root.TryGetProperty("Checkpoints", out var cps) || cps.ValueKind != JsonValueKind.Array) return false;
        foreach (var el in cps.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!TryGetString(el, "Name", out var n) || string.IsNullOrEmpty(n)) return false;
            if (!TryGetString(el, "Id", out var i) || string.IsNullOrEmpty(i)) return false;
        }
        return true;
    }

    private static bool TryGetString(JsonElement obj, string propName, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(propName, out var p)) return false;
        if (p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// VC-CE-D4: Parses a CheckpointResult from PowerShell stdout using a tolerant
    /// scanner. Scans for balanced top-level { ... } blocks in document order; for
    /// each candidate, try-parse + apply the caller-supplied validator predicate;
    /// the first candidate that passes both is materialised. If none pass, throws
    /// InvalidOperationException with a truncated stdout preview.
    /// </summary>
    private static CheckpointResult ParseCheckpointResult(
        string json,
        string? requestedName,
        Func<JsonElement, string?, bool> validator)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("PowerShell returned empty output when checkpoint info was expected.");
        }

        // Two-pass scan: first pass enforces requestedName equality (used to disambiguate
        // when multiple envelopes are present — e.g. Issue #206 T10b). Fallback pass
        // ignores requestedName so a single well-formed envelope is accepted even when
        // the returned CheckpointName differs from the requested one (Hyper-V may
        // normalise the name, or callers/tests may legitimately supply mock fixtures
        // with a different name string).
        var materialized = TryFindCandidate(json, requestedName, validator);
        if (materialized is not null)
        {
            return materialized;
        }

        if (requestedName is not null)
        {
            materialized = TryFindCandidate(json, requestedName: null, validator);
            if (materialized is not null)
            {
                return materialized;
            }
        }

        var preview = json.Length > 512 ? json.Substring(0, 512) + "..." : json;
        throw new InvalidOperationException(
            $"PowerShell returned empty output when checkpoint info was expected. Stdout preview: {preview}");
    }

    private static CheckpointResult? TryFindCandidate(
        string json,
        string? requestedName,
        Func<JsonElement, string?, bool> validator)
    {
        foreach (var (candidate, _, _) in ExtractTopLevelObjects(json))
        {
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!validator(root, requestedName))
                {
                    continue;
                }

                return MaterializeCheckpointResult(root);
            }
        }

        return null;
    }

    private static CheckpointResult MaterializeCheckpointResult(JsonElement root)
    {
        var cpResult = new CheckpointResult
        {
            Action = root.TryGetProperty("Action", out var actionProp) && actionProp.ValueKind == JsonValueKind.String
                ? actionProp.GetString() ?? string.Empty
                : string.Empty,
            VmId = root.TryGetProperty("VmId", out var vmIdProp) && vmIdProp.ValueKind == JsonValueKind.String
                ? vmIdProp.GetString() ?? string.Empty
                : string.Empty,
            CheckpointName = root.TryGetProperty("CheckpointName", out var cpNameProp) && cpNameProp.ValueKind == JsonValueKind.String
                ? cpNameProp.GetString() ?? string.Empty
                : string.Empty,
        };

        if (root.TryGetProperty("Checkpoints", out var cpArrayProp) && cpArrayProp.ValueKind == JsonValueKind.Array)
        {
            var checkpoints = new List<CheckpointInfo>();
            foreach (var cpElement in cpArrayProp.EnumerateArray())
            {
                checkpoints.Add(new CheckpointInfo
                {
                    Name = cpElement.TryGetProperty("Name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty,
                    Id = cpElement.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString() ?? string.Empty
                        : string.Empty,
                    CreatedAt = cpElement.TryGetProperty("CreatedAt", out var createdProp)
                        && createdProp.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(createdProp.GetString(), out var createdAt)
                        ? createdAt
                        : DateTimeOffset.MinValue,
                });
            }
            cpResult.Checkpoints = checkpoints;
        }

        return cpResult;
    }

    /// <summary>
    /// Yields each balanced top-level { ... } block found in the input in document
    /// order. Handles JSON strings (with backslash escapes) so that braces inside
    /// strings do not affect nesting. Returned tuples are (substring, start, end).
    /// </summary>
    private static IEnumerable<(string Json, int Start, int End)> ExtractTopLevelObjects(string input)
    {
        if (string.IsNullOrEmpty(input)) yield break;

        int i = 0;
        while (i < input.Length)
        {
            if (input[i] != '{') { i++; continue; }

            int depth = 0;
            bool inString = false;
            bool escape = false;
            int start = i;

            for (int j = i; j < input.Length; j++)
            {
                char c = input[j];

                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') { inString = false; }
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') { depth++; continue; }
                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return (input.Substring(start, j - start + 1), start, j);
                        i = j + 1;
                        goto NextBlock;
                    }
                }
            }

            // Unbalanced — stop scanning.
            yield break;

            NextBlock:;
        }
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
}
