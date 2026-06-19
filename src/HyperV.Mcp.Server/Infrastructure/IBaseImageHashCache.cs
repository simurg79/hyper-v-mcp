namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Content-addressed SHA-256 cache for base VHDX immutability verification.
/// See /myplans/vm-management/storage/storage-design.md — ST-D6a (pre-hash cache).
/// See /myplans/vm-management/vm-create/vm-create-unblocking-design.md — VC-D2/VC-D5/VC-D7.
///
/// ST-D6a was introduced because the per-call dual SHA-256 of a large base VHDX
/// (e.g., a 29 GB Windows reference image) dominates <c>vm_create</c> latency to
/// the point that the inbound MCP client cancels at its 60 s RPC budget. The
/// cache short-circuits the hash on the warm path by keying on a cheap
/// <c>(fullPath, size, mtime, IsReadOnly)</c> tuple; the SHA-256 is only
/// recomputed when the tuple has actually changed.
///
/// VC-D2 extends the contract with <see cref="WarmAsync"/>, a best-effort
/// startup-time pre-population entry point so the first user-driven
/// <c>vm_create</c> after server boot does not pay the cold-hash cost on the
/// request thread (which would otherwise exceed the 60 s MCP transport ceiling).
///
/// VC-D5 (Shape B) further decouples the SHA-256 compute from the inbound MCP
/// cancellation token: once a caller has acquired the per-path coalescing gate
/// the compute runs under a lifetime-scoped CTS, so a single client's timeout
/// no longer aborts a population that other callers (and future cache hits)
/// are waiting on.
///
/// Contract highlights:
/// - Singleton in DI; thread-safe; per-path coalescing prevents thundering-herd
///   recomputes when the same base VHDX is referenced by N concurrent
///   <c>vm_create</c> calls during cache miss.
/// - TTL default 24h, overridable via <c>HYPERV_MCP_BASE_HASH_TTL_SECONDS</c>.
/// - Hashing is performed host-side via <see cref="System.Security.Cryptography.SHA256"/>
///   (NOT inline PowerShell <c>Get-FileHash</c>) so the cache cannot be bypassed
///   by pipeline cancellation.
/// </summary>
public interface IBaseImageHashCache
{
    /// <summary>
    /// Returns the SHA-256 hash (uppercase hex) of the base VHDX at
    /// <paramref name="fullPath"/>, computing and caching it if absent or stale.
    ///
    /// Cache lookup uses a cheap stat tuple
    /// <c>(size, lastWriteTimeUtc, IsReadOnly)</c>; if the tuple matches a
    /// non-expired entry the cached hash is returned without re-reading the
    /// file. If the tuple has moved or the entry has expired (TTL), a fresh
    /// SHA-256 is computed.
    ///
    /// Per-path coalescing: concurrent callers for the same path observe a
    /// single hash computation (the others <c>await</c> the in-flight task).
    ///
    /// VC-D5 (Shape B): the inbound <paramref name="ct"/> governs only the
    /// <c>SemaphoreSlim.WaitAsync</c> step (so a waiter whose own CT fires can
    /// exit cleanly) and the surrounding handler's race. Once a caller owns the
    /// per-path gate the SHA-256 read is detached from the inbound CT and runs
    /// under an application-stopping CTS instead; the originator cancelling no
    /// longer aborts a compute that other callers depend on.
    /// </summary>
    /// <param name="fullPath">Normalized full path to the base VHDX.</param>
    /// <param name="ct">Cancellation token observed during gate acquisition only.</param>
    /// <returns>SHA-256 hash as uppercase hex string.</returns>
    Task<string> GetOrComputeAsync(string fullPath, CancellationToken ct);

    /// <summary>
    /// VC-D8 (Issue #169 Gate 6 remediation, Option D′ hybrid): unconditionally
    /// reads the file at <paramref name="fullPath"/> from disk and recomputes
    /// the SHA-256, **bypassing** the cheap stat-tuple equality short-circuit
    /// that <see cref="GetOrComputeAsync"/> uses on the warm path.
    ///
    /// This is the post-create mutation-guard primitive on the default
    /// (<c>verifyBaseImageHash:true</c>) <c>vm_create</c> path: the parent
    /// VHDX's bytes MUST be re-hashed after <c>New-VHD -Differencing</c>
    /// returns, regardless of whether the stat tuple appears unchanged. Gate 6
    /// finding #1 demonstrated that a preserved-stat mutation (Rows 1/4/5 of
    /// the §ADR-4 Threat Model — sparse in-place overwrite, <c>touch -t</c>
    /// mtime-reset, silent storage-layer bit-flip) would otherwise be silently
    /// re-served from the cache as the post-hash, defeating ST-D6 / Issue #23.
    ///
    /// Coalescing: still uses the per-path <see cref="System.Threading.SemaphoreSlim"/>
    /// (Constraint #6) so a concurrent <see cref="GetOrComputeAsync"/> for the
    /// same path observes a single in-flight read.
    ///
    /// On success the cache entry is updated with the freshly-read SHA-256 and
    /// the post-recompute stat snapshot.
    /// </summary>
    /// <param name="fullPath">Normalized full path to the base VHDX.</param>
    /// <param name="ct">Cancellation token observed during gate acquisition only
    /// (compute itself is detached per VC-D5 Shape B).</param>
    /// <returns>Freshly-computed SHA-256 hash as uppercase hex string.</returns>
    Task<string> ForceRecomputeAsync(string fullPath, CancellationToken ct);

    /// <summary>
    /// VC-D2: Best-effort, non-throwing population of the cache for every entry
    /// in <paramref name="paths"/>.
    ///
    /// Per-path failures (missing file, permission denied, IO error, mid-compute
    /// exception) are captured into the returned <see cref="WarmUpReport"/> as
    /// <see cref="WarmUpPathResult"/> rows with <see cref="WarmUpPathStatus.Failed"/>
    /// and a structured <c>ErrorCode</c> / <c>ErrorMessage</c>. They MUST NOT
    /// propagate out of <see cref="WarmAsync"/>; a single bad path must never
    /// abort warm-up of the remaining paths.
    ///
    /// Coalescing reuses the existing per-path <see cref="System.Threading.SemaphoreSlim"/>
    /// inside <see cref="GetOrComputeAsync"/>; a path being warmed concurrently
    /// with a user-driven <c>vm_create</c> observes a single compute (Constraint
    /// #6 in the design doc).
    ///
    /// Cancellation: <paramref name="ct"/> is typically the host's
    /// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>
    /// token. When it fires mid-warm-up the remaining unprocessed paths are
    /// recorded with <see cref="WarmUpPathStatus.Cancelled"/> and the report's
    /// status is <see cref="WarmUpStatus.Cancelled"/>.
    /// </summary>
    /// <param name="paths">Absolute base-VHDX paths to pre-warm. Duplicates are
    /// de-duplicated case-insensitively.</param>
    /// <param name="ct">Lifetime-scoped cancellation token (see VC-D3).</param>
    /// <returns>Structured per-path outcome report.</returns>
    Task<WarmUpReport> WarmAsync(IEnumerable<string> paths, CancellationToken ct);

    /// <summary>
    /// Snapshot of cache hit / miss / compute counters and current entry count.
    /// Exposed for test observability (Gate 5) and operational diagnostics.
    /// </summary>
    BaseImageHashCacheStats Stats { get; }

    /// <summary>
    /// VC-D14 / VC-D15: snapshot of sidecar (`<base>.vhdx.sha256`) counters plus
    /// the most-recent <c>BASE_IMAGE_MUTATED</c> detection record (nullable).
    /// Surfaced through the <c>vm_diag.baseImageHashCache</c> projection.
    /// </summary>
    BaseImageHashCacheSidecarStats SidecarStats { get; }

    /// <summary>
    /// VC-D15: invoked by <c>HyperVManager.CreateVmAsync</c> at the
    /// <c>BASE_IMAGE_MUTATED</c> throw site (synchronous post-hash mismatch).
    /// Concrete implementations MUST:
    /// <list type="bullet">
    ///   <item><description>Delete the stale sidecar (<c>&lt;base&gt;.vhdx.sha256</c>).</description></item>
    ///   <item><description>Update the <see cref="SidecarStats"/>
    ///     <c>LastMutationDetected</c> field for diagnostics.</description></item>
    ///   <item><description>Emit a structured <c>LogError</c> with
    ///     <c>EventId.Name = "BaseImageMutated"</c> so SIEM scrapers can gate
    ///     on it.</description></item>
    ///   <item><description>Evict the in-memory cache entry so the next caller
    ///     recomputes against current on-disk bytes.</description></item>
    /// </list>
    /// Best-effort: any internal failure (sidecar delete IO error, log sink
    /// throw) MUST be swallowed so the surrounding rollback/error envelope is
    /// not disturbed.
    /// </summary>
    /// <param name="baseImagePath">Absolute path to the mutated base VHDX.</param>
    /// <param name="vmName">VM whose <c>vm_create</c> detected the mutation
    /// (used only for diagnostic attribution; may be <see langword="null"/>).</param>
    /// <param name="expectedSha256">The pre-hash recorded before the differencing clone.</param>
    /// <param name="actualSha256">The post-hash freshly computed after the clone.</param>
    void RecordMutationDetected(
        string baseImagePath,
        string? vmName,
        string expectedSha256,
        string actualSha256);

    /// <summary>
    /// VC-D7: snapshot of the most recent (or in-flight) warm-up cycle, surfaced
    /// through the <c>vm_diag.baseImageHashCache</c> diagnostic block. Returns
    /// <see langword="null"/> until the first warm-up attempt has been scheduled.
    /// Atomic reference swap means callers can poll this property without locks.
    /// </summary>
    WarmUpReport? LatestWarmUpReport { get; }

    /// <summary>
    /// Raised after each successful SHA-256 computation (cache miss).
    /// Tests use this seam to assert that warm-path calls do NOT recompute,
    /// and that cold-path calls compute exactly once even under concurrency.
    /// </summary>
    event EventHandler<BaseImageHashComputedEventArgs>? OnHashComputed;
}

/// <summary>
/// Cache counters snapshot.
/// See <see cref="IBaseImageHashCache.Stats"/>.
/// </summary>
public readonly record struct BaseImageHashCacheStats(
    long Hits,
    long Misses,
    long Computes,
    int Entries);

/// <summary>
/// VC-D14 / VC-D15: sidecar counters plus the most-recent mutation detection
/// record. Sidecar counters are atomic; <see cref="LastMutationDetected"/> is
/// <see langword="null"/> until the first observed mutation.
/// </summary>
public readonly record struct BaseImageHashCacheSidecarStats(
    long SidecarHits,
    long SidecarWrites,
    long SidecarDiscards,
    BaseImageMutationRecord? LastMutationDetected);

/// <summary>
/// VC-D15: latest <c>BASE_IMAGE_MUTATED</c> detection. Projected onto
/// <c>vm_diag.baseImageHashCache.lastMutationDetected</c> as an ISO-8601
/// timestamp + hashes + path triple.
/// </summary>
public sealed record BaseImageMutationRecord(
    string BaseVhdxPath,
    string? VmName,
    DateTimeOffset DetectedAtUtc,
    string ExpectedSha256,
    string ActualSha256);

/// <summary>
/// VC-D7: aggregate result of one warm-up cycle. <see cref="CompletedAtUtc"/>
/// is <see langword="null"/> while <see cref="Status"/> is
/// <see cref="WarmUpStatus.InProgress"/>.
/// </summary>
public sealed record WarmUpReport(
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    WarmUpStatus Status,
    IReadOnlyList<WarmUpPathResult> Paths);

/// <summary>
/// VC-D2 / VC-D7: per-path outcome inside <see cref="WarmUpReport"/>.
/// <see cref="Sha256"/> and <see cref="ElapsedMs"/> are populated only on
/// <see cref="WarmUpPathStatus.Succeeded"/>; <see cref="ErrorCode"/> /
/// <see cref="ErrorMessage"/> only on <see cref="WarmUpPathStatus.Failed"/>.
/// </summary>
public sealed record WarmUpPathResult(
    string Path,
    WarmUpPathStatus Status,
    string? Sha256,
    long? ElapsedMs,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>VC-D7 enum: overall warm-up cycle status.</summary>
public enum WarmUpStatus
{
    /// <summary>No warm-up has been scheduled yet.</summary>
    NotStarted,
    /// <summary>Warm-up is currently iterating its path list.</summary>
    InProgress,
    /// <summary>Warm-up finished (with or without per-path failures).</summary>
    Completed,
    /// <summary>Some paths succeeded, some failed — overall warm-up still finished.</summary>
    Partial,
    /// <summary>Warm-up was aborted because the lifetime CT fired (host shutdown).</summary>
    Cancelled,
    /// <summary>Warm-up was aborted by a resolver-level / catastrophic exception.</summary>
    Failed,
}

/// <summary>VC-D2 / VC-D7 enum: per-path warm-up outcome.</summary>
public enum WarmUpPathStatus
{
    /// <summary>SHA-256 was computed (or was already cached and validated) successfully.</summary>
    Succeeded,
    /// <summary>The path was already a fresh, non-expired cache entry; no compute occurred.</summary>
    AlreadyWarm,
    /// <summary>The path's SHA-256 was freshly computed by this warm-up cycle.</summary>
    WarmedFresh,
    /// <summary>A non-fatal per-path failure (missing file, permission, IO).</summary>
    Failed,
    /// <summary>Lifetime CT fired before this path was visited.</summary>
    Cancelled,
}

/// <summary>
/// Event payload for <see cref="IBaseImageHashCache.OnHashComputed"/>.
/// </summary>
public class BaseImageHashComputedEventArgs : EventArgs
{
    public string FullPath { get; }
    public long FileSize { get; }
    public DateTime LastWriteTimeUtc { get; }
    public bool IsReadOnly { get; }
    public string Sha256 { get; }
    public long ElapsedMs { get; }

    public BaseImageHashComputedEventArgs(
        string fullPath,
        long fileSize,
        DateTime lastWriteTimeUtc,
        bool isReadOnly,
        string sha256,
        long elapsedMs)
    {
        FullPath = fullPath;
        FileSize = fileSize;
        LastWriteTimeUtc = lastWriteTimeUtc;
        IsReadOnly = isReadOnly;
        Sha256 = sha256;
        ElapsedMs = elapsedMs;
    }
}
