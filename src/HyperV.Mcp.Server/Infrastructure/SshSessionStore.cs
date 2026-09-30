using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Default <see cref="ISshSessionStore"/>. Owns per-(hostId,vmId) SSH connections to Linux
/// guests, serializing get-or-create with a per-key <see cref="SemaphoreSlim"/> exactly like
/// <see cref="SessionStore"/> (LGS-SSH-D2). Resolves the guest SSH endpoint (host + port) from
/// the <see cref="Configuration.HostProfile"/> host-property hint. Connect/open failures are
/// translated into <see cref="SshSessionOpenException"/> (→ SESSION_FAILED, LGS-SSH-D5) with a
/// credential-redacted message; SSH keys / private material are never embedded (LGS-SSH-D6).
/// </summary>
public sealed class SshSessionStore : ISshSessionStore, IDisposable
{
    private readonly ISshExecClientFactory _clientFactory;
    private readonly IHostResolver _hostResolver;
    private readonly IGuestRoutingHintStore _hintStore;
    private readonly ILogger<SshSessionStore> _logger;

    private readonly ConcurrentDictionary<string, ISshExecClient> _clients = new();

    // TODO(SM-D3 follow-up, mirrors SessionStore): per-key semaphores are added on first
    // access and never removed — an acceptable small leak until a shared idle sweeper exists.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private bool _disposed;

    public SshSessionStore(
        ISshExecClientFactory clientFactory,
        IHostResolver hostResolver,
        IGuestRoutingHintStore hintStore,
        ILogger<SshSessionStore> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _hintStore = hintStore ?? throw new ArgumentNullException(nameof(hintStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ISshExecClient> GetOrCreateAsync(
        string hostId,
        string vmId,
        string username,
        string password,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        // A usable per-VM hint owns the guest SSH endpoint and MUST NOT borrow ComputerName (that
        // is the host, not a NAT'd guest). An UNUSABLE hint (install-time IP resolution failed)
        // defers to the host-scoped endpoint, matching GuestChannelRouter.Select so the routing
        // decision and the endpoint resolution cannot disagree.
        // See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D3/LGR-D8.
        string sshHost;
        int sshPort;
        if (_hintStore.TryGet(hostId, vmId, out var hint) &&
            hint.GuestOs == GuestOsKind.Linux &&
            hint.HasUsableSshEndpoint)
        {
            sshHost = hint.SshHost!;
            sshPort = hint.SshPort;
        }
        else
        {
            var profile = _hostResolver.ResolveRequired(hostId);
            var endpoint = profile.ResolveSshEndpoint()
                ?? throw new SshSessionOpenException(
                    vmId,
                    $"Failed to open an SSH session to the Linux guest '{vmId}': no reachable SSH endpoint is configured.");
            sshHost = endpoint.SshHost;
            sshPort = endpoint.SshPort;
        }

        var key = BuildKey(hostId, vmId);
        var gate = GetLock(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                if (existing.IsConnected)
                {
                    return existing;
                }
                _logger.LogInformation(
                    "SshSessionStore: cached SSH session for {HostId}/{VmId} is disconnected, recreating",
                    hostId, vmId);
                TryDispose(existing);
                _clients.TryRemove(key, out _);
            }

            ISshExecClient client;
            try
            {
                client = await _clientFactory
                    .ConnectAsync(sshHost, sshPort, username, password, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // SSH.NET states the decisive cause in a NESTED exception, so the chain must be
                // carried forward — but only redacted here, where the request password is still in
                // scope. The mapper has no password and cannot remove an encoded secret, so it
                // MUST NOT walk the raw chain.
                // See /myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D3.
                var chainLinks = CollectRedactedChain(ex, password);
                var redactedChain = string.Join(" -- ", chainLinks);
                var decisiveCause = chainLinks.Count > 0 ? chainLinks[^1] : null;
                var redacted = RedactAll(ex.Message ?? string.Empty, password);
                _logger.LogError(
                    "SshSessionStore: failed to open SSH session to {Host}:{Port} for {HostId}/{VmId} ({ExType})",
                    sshHost, sshPort, hostId, vmId, ex.GetType().Name);

                // The SSH leg had no spill site, so a bounded envelope's omission marker would name
                // a referent that does not exist. Spill here so both channels give the operator the
                // same path to the omitted detail.
                // See /myplans/remoting/session-management/session-open-error-envelope-design.md — SOE-D12.
                var spillText = RedactAll(ex.ToString(), password);
                var spillSummary = StderrSpillHelper.Spill(spillText);

                // The typed exception carries the redacted chain as data, NOT the raw
                // exception object, so no consumer can reach unredacted nested text.
                throw new SshSessionOpenException(
                    vmId,
                    $"Failed to open SSH session to the Linux guest '{vmId}': {redacted}",
                    innerException: null,
                    spillSummary: spillSummary,
                    redactedCauseChain: redactedChain,
                    decisiveCause: decisiveCause);
            }

            _clients[key] = client;
            _logger.LogInformation(
                "SshSessionStore: opened SSH session to {Host}:{Port} for {HostId}/{VmId}",
                sshHost, sshPort, hostId, vmId);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);

        var key = BuildKey(hostId, vmId);
        var gate = GetLock(key);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_clients.TryRemove(key, out var client))
            {
                _logger.LogDebug("SshSessionStore: evicting SSH session for {HostId}/{VmId}", hostId, vmId);
                TryDispose(client);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            TryDispose(client);
        }
        foreach (var sem in _locks.Values)
        {
            try { sem.Dispose(); } catch { /* best-effort */ }
        }
        _clients.Clear();
        _locks.Clear();
    }

    private static string BuildKey(string hostId, string vmId) => $"{hostId}::{vmId}";

    /// <summary>
    /// Maximum exception-chain depth retained. A malformed or adversarial chain must not turn
    /// redaction into an unbounded walk.
    /// </summary>
    private const int MaxChainDepth = 16;

    /// <summary>
    /// Flattens the exception chain into fully redacted message strings. Bounded by depth AND by
    /// a visited-reference set, because a chain can be cyclic when built through reflection or a
    /// custom exception type that returns itself.
    /// </summary>
    internal static List<string> CollectRedactedChain(Exception root, string password)
    {
        var links = new List<string>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var link = root; link is not null && links.Count < MaxChainDepth; link = link.InnerException)
        {
            if (!visited.Add(link))
            {
                break;
            }
            var text = RedactAll(link.Message ?? string.Empty, password);
            if (!string.IsNullOrWhiteSpace(text) &&
                !links.Any(existing => existing.IndexOf(text, StringComparison.Ordinal) >= 0))
            {
                links.Add(text);
            }
        }
        return links;
    }

    /// <summary>
    /// Every prohibited password representation plus the pattern-based defensive pass. Applied
    /// here, where the password is in scope — downstream passes cannot recognize an encoded secret.
    /// </summary>
    internal static string RedactAll(string text, string password)
        => StderrSpillHelper.RedactDefensively(
            CredentialResolver.RedactPasswordRepresentations(text ?? string.Empty, password));

    private SemaphoreSlim GetLock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private void TryDispose(ISshExecClient client)
    {
        try { client.Dispose(); }
        catch (Exception ex)
        {
            _logger.LogWarning("SshSessionStore: SSH client disposal failed ({ExType})", ex.GetType().Name);
        }
    }
}
