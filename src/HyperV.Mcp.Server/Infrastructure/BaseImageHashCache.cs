using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// In-process implementation of <see cref="IBaseImageHashCache"/> (ST-D6a +
/// VC-D2 / VC-D5 / VC-D7).
///
/// Storage: <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the normalized
/// full path (case-insensitive on Windows). Per-path coalescing is implemented via
/// a second <see cref="ConcurrentDictionary{TKey, TValue}"/> of
/// <see cref="SemaphoreSlim"/> instances so the SHA-256 of a given path is computed
/// at most once concurrently, even when N <c>vm_create</c> calls miss the cache for
/// the same base VHDX at the same time.
///
/// Hashing reads the file with <see cref="FileShare.Read"/> in async streaming mode
/// and feeds bytes to a single <see cref="IncrementalHash"/> instance so a 29 GB
/// parent VHDX does not require 29 GB of managed memory.
///
/// VC-D5 (Shape B): once a caller acquires the per-path semaphore, the SHA-256 read
/// is detached from the inbound MCP <see cref="CancellationToken"/> and instead runs
/// under a CTS linked to <see cref="IHostApplicationLifetime.ApplicationStopping"/>.
/// Concretely:
/// <list type="number">
///   <item>Inbound CT governs <c>gate.WaitAsync(ct)</c> (waiters can exit cleanly).</item>
///   <item>After gate acquisition the gate-holder constructs a fresh CTS linked
///   to <c>ApplicationStopping</c> and passes <em>that</em> token to the SHA-256
///   pipeline. The inbound CT is dropped at this seam.</item>
///   <item>The dispatcher / handler is expected to race the cache task against
///   the inbound CT externally (see <c>HyperVManager.CreateVmAsync</c>) and
///   surface <c>-32001</c> without observing <c>cacheTask</c>, so the detached
///   compute survives for the benefit of subsequent callers.</item>
/// </list>
///
/// See /myplans/vm-management/storage/storage-design.md — ST-D6a.
/// See /myplans/vm-management/vm-create/vm-create-unblocking-design.md — VC-D2, VC-D5, VC-D7.
/// </summary>
public class BaseImageHashCache : IBaseImageHashCache, IDisposable
{
    /// <summary>Default TTL: 24h. Override via <c>HYPERV_MCP_BASE_HASH_TTL_SECONDS</c>.</summary>
    // sidecar is not subject to this TTL — see VC-D14 / VC-D16
    public const int DefaultTtlSeconds = 24 * 60 * 60;

    /// <summary>Environment variable for overriding the TTL.</summary>
    public const string TtlEnvVar = "HYPERV_MCP_BASE_HASH_TTL_SECONDS";

    private const int HashStreamBufferSize = 1 * 1024 * 1024; // 1 MiB

    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<BaseImageHashCache> _logger;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// VC-D5: the application-stopping token used to detach compute from the
    /// inbound MCP CT. When <see cref="IHostApplicationLifetime"/> is not
    /// available (e.g., unit tests using the parameterless constructor) we fall
    /// back to <see cref="CancellationToken.None"/>, which means compute is
    /// effectively unbounded — acceptable for tests, and behaviorally identical
    /// to the pre-VC-D5 contract for callers that never cancel.
    /// </summary>
    private readonly CancellationToken _lifetimeToken;

    private long _hits;
    private long _misses;
    private long _computes;
    // VC-D14 / VC-D15 / VC-D17 — sidecar counters and last-mutation surface.
    private long _sidecarHits;
    private long _sidecarWrites;
    private long _sidecarDiscards;
    private BaseImageMutationRecord? _lastMutationDetected;
    private bool _disposed;

    // VC-D14 / VC-D16: sidecar file extension appended to the normalized base path
    // (e.g. "C:\\Images\\base.vhdx" → "C:\\Images\\base.vhdx.sha256").
    private const string SidecarExtension = ".sha256";
    private const int SidecarSchemaVersion = 1;

    // VC-D7: atomically-swappable snapshot of the latest warm-up cycle.
    private WarmUpReport? _latestWarmUpReport;

    /// <summary>
    /// Production constructor used by DI. The <paramref name="lifetime"/>'s
    /// <see cref="IHostApplicationLifetime.ApplicationStopping"/> token is the
    /// VC-D5 Shape B kill-switch for in-flight SHA-256 computes.
    /// </summary>
    public BaseImageHashCache(ILogger<BaseImageHashCache> logger, IHostApplicationLifetime lifetime)
        : this(logger, lifetime?.ApplicationStopping ?? throw new ArgumentNullException(nameof(lifetime)))
    {
    }

    /// <summary>
    /// Test constructor: no lifetime wiring. Compute runs to completion unless
    /// the inbound CT fires <em>before</em> gate acquisition (Shape B's seam
    /// only swaps the post-gate token). Production code must use the
    /// <see cref="IHostApplicationLifetime"/> overload via DI.
    /// </summary>
    public BaseImageHashCache(ILogger<BaseImageHashCache> logger)
        : this(logger, CancellationToken.None)
    {
    }

    /// <summary>
    /// Internal constructor for fine-grained test control over the lifetime CT
    /// (e.g., simulating host-shutdown mid-compute).
    /// </summary>
    internal BaseImageHashCache(ILogger<BaseImageHashCache> logger, CancellationToken lifetimeToken)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetimeToken = lifetimeToken;
        _ttl = ResolveTtl();
        _logger.LogInformation(
            "BaseImageHashCache initialized (ST-D6a + VC-D5 Shape B) with TTL={TtlSeconds}s",
            (int)_ttl.TotalSeconds);
    }

    /// <inheritdoc />
    public BaseImageHashCacheStats Stats => new(
        Hits: Interlocked.Read(ref _hits),
        Misses: Interlocked.Read(ref _misses),
        Computes: Interlocked.Read(ref _computes),
        Entries: _entries.Count);

    /// <inheritdoc />
    public BaseImageHashCacheSidecarStats SidecarStats => new(
        SidecarHits: Interlocked.Read(ref _sidecarHits),
        SidecarWrites: Interlocked.Read(ref _sidecarWrites),
        SidecarDiscards: Interlocked.Read(ref _sidecarDiscards),
        LastMutationDetected: Volatile.Read(ref _lastMutationDetected));

    /// <inheritdoc />
    public WarmUpReport? LatestWarmUpReport => Volatile.Read(ref _latestWarmUpReport);

    /// <inheritdoc />
    public event EventHandler<BaseImageHashComputedEventArgs>? OnHashComputed;

    /// <inheritdoc />
    public async Task<string> GetOrComputeAsync(string fullPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("Path must not be empty.", nameof(fullPath));

        var normalized = NormalizePath(fullPath);

        // Cheap stat first — drives both cache lookup AND validation that the file exists.
        var stat = StatFile(normalized);

        // Fast path: matching entry, not expired.
        if (_entries.TryGetValue(normalized, out var existing) &&
            existing.MatchesStat(stat) &&
            !existing.IsExpired(_ttl))
        {
            Interlocked.Increment(ref _hits);
            return existing.Sha256;
        }

        Interlocked.Increment(ref _misses);

        // Per-path coalescing: at most one SHA-256 computation in flight per path.
        // VC-D5 (Shape B) — inbound CT governs ONLY the gate wait, so a waiter
        // whose own CT fires can bail out without affecting the gate-holder's
        // compute.
        var gate = _locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — another thread may have computed
            // a fresh hash while we were waiting.
            stat = StatFile(normalized);
            if (_entries.TryGetValue(normalized, out existing) &&
                existing.MatchesStat(stat) &&
                !existing.IsExpired(_ttl))
            {
                // Promote the late wake-up to a hit so Stats accurately reflects
                // observed coalescing behavior.
                Interlocked.Increment(ref _hits);
                Interlocked.Decrement(ref _misses);
                return existing.Sha256;
            }

            // VC-D5 Shape B: drop the inbound CT here. The compute runs under a
            // lifetime-scoped CTS so a single caller's cancellation (case #1) or
            // a waiter's cancellation (case #2) does NOT abort the SHA-256 read.
            // Cases #3 (all-cancel) and #4 (host shutdown) are both captured:
            // #3 collapses to the same detached-compute path as #1; #4 fires
            // the lifetime token and the read pipeline observes cancellation.
            // Case #5 (compute exception) propagates from ComputeSha256Async,
            // is logged below, and is re-thrown so the originator's
            // `await cacheTask` surfaces a real error envelope; the cache entry
            // is NOT written, so the next caller retries cleanly.
            using var lifetimeLinked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            var computeCt = lifetimeLinked.Token;

            // VC-D14 / VC-D16: sidecar fast-path inside the per-path gate.
            // After the in-memory cache miss but BEFORE recomputing, attempt to
            // load `<base>.vhdx.sha256`. If the sidecar's recorded stat-tuple
            // matches the current FileInfo, promote it into the in-memory cache
            // and return — collapsing the post-restart re-hash cost from "full
            // file read" to "small JSON read + stat compare". Mismatches and
            // corrupt JSON discard the sidecar and fall through to full compute.
            if (TryReadSidecar(normalized, out var sidecar))
            {
                if (sidecar!.MatchesStat(stat))
                {
                    var promoted = new CacheEntry(
                        FullPath: normalized,
                        FileSize: stat.Size,
                        LastWriteTimeUtc: stat.MTime,
                        IsReadOnly: stat.IsReadOnly,
                        Sha256: sidecar.Sha256,
                        ComputedAtUtc: DateTime.UtcNow);
                    _entries[normalized] = promoted;
                    Interlocked.Increment(ref _sidecarHits);
                    Interlocked.Decrement(ref _misses);
                    Interlocked.Increment(ref _hits);
                    _logger.LogInformation(
                        "BaseImageHashCache sidecar hit for {Path} (Size={Size} bytes) — VC-D14 fast-path.",
                        normalized, stat.Size);
                    return sidecar.Sha256;
                }

                // Stat-tuple mismatch — sidecar is stale (base replaced or
                // mutated). Delete it; a fresh sidecar is written below.
                Interlocked.Increment(ref _sidecarDiscards);
                _logger.LogInformation(
                    "BaseImageHashCache sidecar stat-tuple mismatch for {Path}; discarding and recomputing — VC-D14/VC-D16.",
                    normalized);
                TryDeleteSidecar(normalized);
            }

            var sw = Stopwatch.StartNew();
            string sha;
            try
            {
                sha = await ComputeSha256Async(normalized, computeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                // Case #4 — host shutdown. Re-throw as a clean cancellation so
                // the warm-up `Task.Run` body / detached compute can log it as
                // expected and the cache remains unwritten.
                _logger.LogInformation(
                    "BaseImageHashCache SHA-256 for {Path} cancelled by host shutdown.",
                    normalized);
                throw;
            }
            catch (Exception ex)
            {
                // Case #5 — IO error, disk full mid-hash, file deleted mid-read,
                // etc. Log once and propagate; the cache entry is NOT written so
                // the next caller re-enters the gate and retries from scratch.
                _logger.LogError(
                    ex,
                    "BaseImageHashCache SHA-256 compute failed for {Path}; cache entry NOT written, next caller will retry.",
                    normalized);
                throw;
            }
            sw.Stop();

            var entry = new CacheEntry(
                FullPath: normalized,
                FileSize: stat.Size,
                LastWriteTimeUtc: stat.MTime,
                IsReadOnly: stat.IsReadOnly,
                Sha256: sha,
                ComputedAtUtc: DateTime.UtcNow);

            _entries[normalized] = entry;
            Interlocked.Increment(ref _computes);

            _logger.LogInformation(
                "BaseImageHashCache computed SHA-256 for {Path} (Size={Size} bytes, ElapsedMs={ElapsedMs})",
                normalized, stat.Size, sw.ElapsedMilliseconds);

            // VC-D14 / VC-D17 — best-effort sidecar write. Failures are logged
            // but MUST NOT fail vm_create (sidecar is advisory persistence).
            TryWriteSidecar(normalized, sha, stat);

            try
            {
                OnHashComputed?.Invoke(this, new BaseImageHashComputedEventArgs(
                    fullPath: normalized,
                    fileSize: stat.Size,
                    lastWriteTimeUtc: stat.MTime,
                    isReadOnly: stat.IsReadOnly,
                    sha256: sha,
                    elapsedMs: sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                // Event-handler failures must never surface as cache failures.
                _logger.LogWarning(ex, "BaseImageHashCache.OnHashComputed handler threw; suppressed.");
            }

            return sha;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> ForceRecomputeAsync(string fullPath, CancellationToken ct)
    {
        // VC-D8 (Issue #169 Gate 6 remediation): unconditionally re-read the
        // file's bytes and recompute SHA-256, bypassing the stat-tuple
        // short-circuit. Used by HyperVManager.CreateVmAsync's post-create
        // mutation guard on the default verifyBaseImageHash:true path. Per
        // Constraint #6, the per-path SemaphoreSlim remains the sole
        // coalescing primitive; we still acquire it so a concurrent
        // GetOrComputeAsync for the same path observes a single in-flight
        // read.
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new ArgumentException("Path must not be empty.", nameof(fullPath));

        var normalized = NormalizePath(fullPath);
        var stat = StatFile(normalized); // throws FileNotFoundException if missing

        var gate = _locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // VC-D5 Shape B: drop the inbound CT inside the gate — see
            // GetOrComputeAsync for the full rationale.
            using var lifetimeLinked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            var computeCt = lifetimeLinked.Token;

            // Re-stat just before reading so the recorded entry reflects the
            // exact bytes we hashed, not the pre-gate stat. This is what makes
            // a subsequent (cached) GetOrComputeAsync return THIS hash.
            stat = StatFile(normalized);

            var sw = Stopwatch.StartNew();
            string sha;
            try
            {
                sha = await ComputeSha256Async(normalized, computeCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "BaseImageHashCache ForceRecomputeAsync for {Path} cancelled by host shutdown.",
                    normalized);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "BaseImageHashCache ForceRecomputeAsync failed for {Path}; cache entry NOT updated.",
                    normalized);
                throw;
            }
            sw.Stop();

            var entry = new CacheEntry(
                FullPath: normalized,
                FileSize: stat.Size,
                LastWriteTimeUtc: stat.MTime,
                IsReadOnly: stat.IsReadOnly,
                Sha256: sha,
                ComputedAtUtc: DateTime.UtcNow);
            _entries[normalized] = entry;
            Interlocked.Increment(ref _computes);

            _logger.LogInformation(
                "BaseImageHashCache force-recomputed SHA-256 for {Path} (Size={Size} bytes, ElapsedMs={ElapsedMs}) — VC-D8 mutation-guard recompute.",
                normalized, stat.Size, sw.ElapsedMilliseconds);

            // VC-D14 Gate-2 v2 amendment: ForceRecomputeAsync MUST also persist
            // the sidecar on success. This covers the post-create force-recompute
            // path; without it the sidecar would only ever be written by the
            // pre-hash / warm-on-init path and would not reflect the most-recent
            // post-create verification. Best-effort; failure does not fail
            // vm_create (VC-D17).
            TryWriteSidecar(normalized, sha, stat);

            try
            {
                OnHashComputed?.Invoke(this, new BaseImageHashComputedEventArgs(
                    fullPath: normalized,
                    fileSize: stat.Size,
                    lastWriteTimeUtc: stat.MTime,
                    isReadOnly: stat.IsReadOnly,
                    sha256: sha,
                    elapsedMs: sw.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BaseImageHashCache.OnHashComputed handler threw; suppressed.");
            }

            return sha;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WarmUpReport> WarmAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var startedAt = DateTime.UtcNow;
        var distinct = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Publish an in-progress report immediately so vm_diag can observe state.
        var inProgress = new WarmUpReport(
            StartedAtUtc: startedAt,
            CompletedAtUtc: null,
            Status: WarmUpStatus.InProgress,
            Paths: Array.Empty<WarmUpPathResult>());
        Volatile.Write(ref _latestWarmUpReport, inProgress);

        _logger.LogInformation(
            "BaseImageHashCache warm-up starting for {Count} path(s).", distinct.Count);

        var results = new List<WarmUpPathResult>(distinct.Count);
        var anyFailed = false;
        var anySucceeded = false;
        var cancelledRemaining = false;

        foreach (var path in distinct)
        {
            if (ct.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
            {
                results.Add(new WarmUpPathResult(
                    Path: path,
                    Status: WarmUpPathStatus.Cancelled,
                    Sha256: null,
                    ElapsedMs: null,
                    ErrorCode: null,
                    ErrorMessage: null));
                cancelledRemaining = true;
                continue;
            }

            // 🟡 #2 (Issue #169 Gate 6 remediation): determine AlreadyWarm vs
            // WarmedFresh from PER-PATH state, not from the global Stats.Computes
            // counter. The previous global-delta approach could misattribute
            // status when an unrelated concurrent compute (e.g. warm-on-init for
            // a different image) bumped Computes between this path's Before and
            // After samples. Now we peek the cache + stat tuple BEFORE calling
            // GetOrComputeAsync: if a non-expired matching entry already exists
            // for THIS path, the call will be a hit (AlreadyWarm); otherwise it
            // is a miss that this warm-up is responsible for (WarmedFresh).
            bool wasAlreadyWarm = false;
            try
            {
                var preStat = StatFile(path);
                if (_entries.TryGetValue(path, out var preEntry) &&
                    preEntry.MatchesStat(preStat) &&
                    !preEntry.IsExpired(_ttl))
                {
                    wasAlreadyWarm = true;
                }
            }
            catch
            {
                // StatFile throws (FileNotFoundException etc.) — let the
                // GetOrComputeAsync below surface the structured error so the
                // result row carries ErrorCode/ErrorMessage. Treat as not-warm
                // for the status-inference purposes (will not be reached on the
                // failure path anyway).
                wasAlreadyWarm = false;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var sha = await GetOrComputeAsync(path, ct).ConfigureAwait(false);
                sw.Stop();
                var status = wasAlreadyWarm
                    ? WarmUpPathStatus.AlreadyWarm
                    : WarmUpPathStatus.WarmedFresh;

                results.Add(new WarmUpPathResult(
                    Path: path,
                    Status: status,
                    Sha256: sha,
                    ElapsedMs: sw.ElapsedMilliseconds,
                    ErrorCode: null,
                    ErrorMessage: null));
                anySucceeded = true;

                _logger.LogInformation(
                    "BaseImageHashCache warm-up path {Path} {Status} in {ElapsedMs}ms.",
                    path, status, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
            {
                sw.Stop();
                results.Add(new WarmUpPathResult(
                    Path: path,
                    Status: WarmUpPathStatus.Cancelled,
                    Sha256: null,
                    ElapsedMs: sw.ElapsedMilliseconds,
                    ErrorCode: null,
                    ErrorMessage: null));
                cancelledRemaining = true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var (code, message) = ClassifyWarmUpError(ex);
                results.Add(new WarmUpPathResult(
                    Path: path,
                    Status: WarmUpPathStatus.Failed,
                    Sha256: null,
                    ElapsedMs: sw.ElapsedMilliseconds,
                    ErrorCode: code,
                    ErrorMessage: message));
                anyFailed = true;
                _logger.LogWarning(
                    ex,
                    "BaseImageHashCache warm-up path {Path} failed ({Code}): {Message}",
                    path, code, message);
            }
        }

        WarmUpStatus finalStatus;
        if (cancelledRemaining && !anySucceeded && !anyFailed)
        {
            finalStatus = WarmUpStatus.Cancelled;
        }
        else if (cancelledRemaining)
        {
            // Some paths processed, the rest cancelled — record as partial.
            finalStatus = WarmUpStatus.Partial;
        }
        else if (anyFailed && anySucceeded)
        {
            finalStatus = WarmUpStatus.Partial;
        }
        else if (anyFailed)
        {
            finalStatus = WarmUpStatus.Failed;
        }
        else
        {
            finalStatus = WarmUpStatus.Completed;
        }

        var report = new WarmUpReport(
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTime.UtcNow,
            Status: finalStatus,
            Paths: results);
        Volatile.Write(ref _latestWarmUpReport, report);

        var succeeded = results.Count(r =>
            r.Status == WarmUpPathStatus.Succeeded ||
            r.Status == WarmUpPathStatus.AlreadyWarm ||
            r.Status == WarmUpPathStatus.WarmedFresh);
        _logger.LogInformation(
            "BaseImageHashCache warm-up completed: status={Status}, succeeded={Ok}/{Total}, elapsedMs={ElapsedMs}.",
            finalStatus, succeeded, results.Count,
            (long)(report.CompletedAtUtc!.Value - report.StartedAtUtc).TotalMilliseconds);

        return report;
    }

    private static (string Code, string Message) ClassifyWarmUpError(Exception ex) => ex switch
    {
        FileNotFoundException => ("FILE_NOT_FOUND", ex.Message),
        DirectoryNotFoundException => ("DIRECTORY_NOT_FOUND", ex.Message),
        UnauthorizedAccessException => ("UNAUTHORIZED", ex.Message),
        IOException => ("IO_ERROR", ex.Message),
        _ => ("INFRA_FAILURE", $"{ex.GetType().Name}: {ex.Message}"),
    };

    private static string NormalizePath(string fullPath)
    {
        try
        {
            return Path.GetFullPath(fullPath);
        }
        catch
        {
            // If GetFullPath throws (invalid chars, etc.) fall back to the caller-supplied
            // value — StatFile will surface a clean FileNotFoundException/IOException.
            return fullPath;
        }
    }

    private static FileStat StatFile(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists)
        {
            throw new FileNotFoundException(
                $"Base VHDX not found at '{fullPath}'.", fullPath);
        }

        return new FileStat(
            Size: fi.Length,
            MTime: fi.LastWriteTimeUtc,
            IsReadOnly: fi.IsReadOnly);
    }

    private static async Task<string> ComputeSha256Async(string fullPath, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: HashStreamBufferSize,
            useAsync: true);

        var buffer = new byte[HashStreamBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        var digest = hash.GetHashAndReset();
        return Convert.ToHexString(digest); // uppercase hex by default
    }

    private static TimeSpan ResolveTtl()
    {
        var raw = Environment.GetEnvironmentVariable(TtlEnvVar);
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.FromSeconds(DefaultTtlSeconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _locks)
        {
            try { kv.Value.Dispose(); } catch { /* swallow */ }
        }
        _locks.Clear();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void RecordMutationDetected(
        string baseImagePath,
        string? vmName,
        string expectedSha256,
        string actualSha256)
    {
        if (string.IsNullOrWhiteSpace(baseImagePath))
            return;

        string normalized;
        try
        {
            normalized = NormalizePath(baseImagePath);
        }
        catch
        {
            normalized = baseImagePath;
        }

        // VC-D15 (a): delete the now-provably-stale sidecar.
        TryDeleteSidecar(normalized);

        // VC-D15 (b): record the latest mutation for vm_diag projection.
        var record = new BaseImageMutationRecord(
            BaseVhdxPath: normalized,
            VmName: vmName,
            DetectedAtUtc: DateTimeOffset.UtcNow,
            ExpectedSha256: expectedSha256,
            ActualSha256: actualSha256);
        Volatile.Write(ref _lastMutationDetected, record);

        // Evict the in-memory cache entry so the next caller recomputes
        // against the on-disk (mutated) bytes, rather than serving a stale
        // pre-mutation hash from memory.
        _entries.TryRemove(normalized, out _);

        // VC-D15 (c): structured error log with EventId so SIEM scrapers can
        // gate on it. Includes path + vmName + both hashes; no credentials.
        _logger.LogError(
            new EventId(170, "BaseImageMutated"),
            "BASE_IMAGE_MUTATED detected for base '{BaseVhdxPath}' (vm={VmName}): expectedSha256={ExpectedSha256} actualSha256={ActualSha256}; sidecar deleted, cache evicted.",
            normalized, vmName ?? "<unknown>", expectedSha256, actualSha256);
    }

    // ─────────────────────────────────────────────────────────────────────
    // VC-D14 / VC-D16 / VC-D17 — sidecar helpers (private).
    //
    // Sidecar lives at `<base>.vhdx.sha256` and is a plain JSON document
    // (unsigned, trust-on-first-compute per VC-D16). The stat-tuple in the
    // sidecar is compared against the live FileInfo at read time to detect
    // base replacement / mutation between server lifetimes.
    //
    // Atomic write: WriteAllText to `<path>.tmp` + File.Move(replace:true).
    // Failure surface: every helper is non-throwing — the worst case is a
    // performance regression (re-hash on next call), never a vm_create failure.
    // ─────────────────────────────────────────────────────────────────────

    private static string GetSidecarPath(string normalizedBasePath) =>
        normalizedBasePath + SidecarExtension;

    private static readonly JsonSerializerOptions s_sidecarJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private bool TryReadSidecar(string normalizedBasePath, out SidecarPayload? payload)
    {
        payload = null;
        var sidecarPath = GetSidecarPath(normalizedBasePath);
        try
        {
            if (!File.Exists(sidecarPath))
            {
                return false;
            }

            var json = File.ReadAllText(sidecarPath);
            var parsed = JsonSerializer.Deserialize<SidecarPayload>(json, s_sidecarJsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Sha256))
            {
                // Corrupt / empty JSON — discard.
                Interlocked.Increment(ref _sidecarDiscards);
                _logger.LogWarning(
                    "BaseImageHashCache sidecar at {SidecarPath} is corrupt or empty; deleting and recomputing — VC-D16.",
                    sidecarPath);
                TryDeleteSidecar(normalizedBasePath);
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (JsonException jsonEx)
        {
            // Corrupt JSON — log and discard per VC-D16.
            Interlocked.Increment(ref _sidecarDiscards);
            _logger.LogWarning(
                jsonEx,
                "BaseImageHashCache sidecar at {SidecarPath} contains invalid JSON; deleting and recomputing — VC-D16.",
                sidecarPath);
            TryDeleteSidecar(normalizedBasePath);
            return false;
        }
        catch (Exception ex)
        {
            // IO error reading sidecar — non-fatal, fall through to recompute.
            _logger.LogWarning(
                ex,
                "BaseImageHashCache sidecar read failed for {SidecarPath}; recomputing — VC-D17.",
                sidecarPath);
            return false;
        }
    }

    private void TryWriteSidecar(string normalizedBasePath, string sha256, FileStat stat)
    {
        var sidecarPath = GetSidecarPath(normalizedBasePath);
        var tempPath = sidecarPath + ".tmp";
        try
        {
            var payload = new SidecarPayload
            {
                SchemaVersion = SidecarSchemaVersion,
                Sha256 = sha256,
                FileSize = stat.Size,
                LastWriteTimeUtc = stat.MTime,
                IsReadOnly = stat.IsReadOnly,
                ComputedAtUtc = DateTime.UtcNow,
            };
            var json = JsonSerializer.Serialize(payload, s_sidecarJsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, sidecarPath, overwrite: true);
            Interlocked.Increment(ref _sidecarWrites);
            _logger.LogDebug(
                "BaseImageHashCache sidecar written for {Path} → {SidecarPath} — VC-D14.",
                normalizedBasePath, sidecarPath);
        }
        catch (Exception ex)
        {
            // VC-D17: sidecar write failure is advisory only — log a warning
            // and continue. The in-memory cache is already updated, so this
            // process serves cached hashes correctly; only cross-restart
            // performance suffers.
            _logger.LogWarning(
                ex,
                "BaseImageHashCache sidecar write failed for {SidecarPath}; in-memory cache unaffected — VC-D17.",
                sidecarPath);
            // Best-effort temp cleanup.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* swallow */ }
        }
    }

    private void TryDeleteSidecar(string normalizedBasePath)
    {
        var sidecarPath = GetSidecarPath(normalizedBasePath);
        try
        {
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
                _logger.LogDebug(
                    "BaseImageHashCache sidecar deleted at {SidecarPath} — VC-D14/VC-D15.",
                    sidecarPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "BaseImageHashCache sidecar delete failed for {SidecarPath} — VC-D17.",
                sidecarPath);
        }
    }

    /// <summary>
    /// VC-D14 sidecar JSON payload. Public so JSON serialization works in any
    /// trim/AOT context; consumer code should treat this as an internal contract.
    /// </summary>
    public sealed class SidecarPayload
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("fileSize")]
        public long FileSize { get; set; }

        [JsonPropertyName("lastWriteTimeUtc")]
        public DateTime LastWriteTimeUtc { get; set; }

        [JsonPropertyName("isReadOnly")]
        public bool IsReadOnly { get; set; }

        [JsonPropertyName("computedAtUtc")]
        public DateTime ComputedAtUtc { get; set; }

        internal bool MatchesStat(FileStat stat) =>
            stat.Size == FileSize &&
            stat.MTime == LastWriteTimeUtc &&
            stat.IsReadOnly == IsReadOnly;
    }

    internal readonly record struct FileStat(long Size, DateTime MTime, bool IsReadOnly);

    private sealed record CacheEntry(
        string FullPath,
        long FileSize,
        DateTime LastWriteTimeUtc,
        bool IsReadOnly,
        string Sha256,
        DateTime ComputedAtUtc)
    {
        public bool MatchesStat(FileStat stat) =>
            stat.Size == FileSize &&
            stat.MTime == LastWriteTimeUtc &&
            stat.IsReadOnly == IsReadOnly;

        public bool IsExpired(TimeSpan ttl) =>
            DateTime.UtcNow - ComputedAtUtc > ttl;
    }
}
