using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Routes each guest-targeted call to the SSH or PowerShell-Direct transport. It is a pure
/// dispatch decorator registered as <see cref="IPowerShellDirectChannel"/> so existing consumers
/// stay unchanged, and the Windows PSDirect path must remain byte-identical.
/// See /myplans/remoting/linux-guest-support/linux-ssh-exec-first-slice-design.md — LGS-SSH-D3/D4.
/// </summary>
public sealed class GuestChannelRouter : IPowerShellDirectChannel
{
    /// <summary>Total budget for one classification attempt (KVP probe plus any SSH-banner leg).</summary>
    private static readonly TimeSpan ClassificationBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long an undeterminable result suppresses re-probing — the PROBE only, never the
    /// refusal. Short enough that a still-booting Linux guest is re-probed within roughly one
    /// boot-progress step, long enough that a burst of guest calls collapses onto one probe.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D31, LGR-D32.
    /// </summary>
    private static readonly TimeSpan NegativeCacheExpiry = TimeSpan.FromSeconds(5);

    /// <summary>Settling delay before the single best-effort re-read of the intrinsic items.</summary>
    private static readonly TimeSpan RetryReadDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound on negative-cache entries; the oldest is evicted at capacity.</summary>
    private const int NegativeCacheCapacity = 256;

    private readonly PowerShellDirectChannel _windowsChannel;
    private readonly SshGuestChannel _linuxChannel;
    private readonly IHostResolver _hostResolver;
    private readonly IGuestRoutingHintStore _hintStore;
    private readonly ILogger<GuestChannelRouter> _logger;
    private readonly IGuestOsProbe? _guestOsProbe;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Timestamps of the most recent undeterminable probe per VM. Holds NO OS claim and is not
    /// readable by <see cref="Select"/> — only "do not re-probe yet". Without it, every guest call
    /// against a never-determinable VM (i.e. every Windows guest) would probe on the hot path.
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D19.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _undeterminableAt = new();

    /// <summary>
    /// Probe-outcome marker vocabulary. Message text plus log marker is the only supported way to
    /// tell refusal causes apart (a new error code is forbidden and every refusal collapses onto
    /// SESSION_FAILED), so each marker travels with the single routing attempt that produced it. It
    /// MUST NOT be shared per VM: interleaved attempts would relabel each other's cause.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D22, LGR-D33, LGR-D35.
    /// </summary>
    private const string OutcomeUndeterminable = "probe: undeterminable";
    private const string OutcomeSuppressed = "probe: suppressed-negative-cache";
    private const string OutcomeTimeout = "probe: timeout";
    private const string OutcomeFaulted = "probe: faulted";

    /// <summary>Serializes admission and eviction so the capacity bound is a real invariant.</summary>
    private readonly object _negativeCacheLock = new();

    public GuestChannelRouter(
        PowerShellDirectChannel windowsChannel,
        SshGuestChannel linuxChannel,
        IHostResolver hostResolver,
        IGuestRoutingHintStore hintStore,
        ILogger<GuestChannelRouter> logger,
        IGuestOsProbe? guestOsProbe = null,
        TimeProvider? timeProvider = null)
    {
        _windowsChannel = windowsChannel ?? throw new ArgumentNullException(nameof(windowsChannel));
        _linuxChannel = linuxChannel ?? throw new ArgumentNullException(nameof(linuxChannel));
        _hostResolver = hostResolver ?? throw new ArgumentNullException(nameof(hostResolver));
        _hintStore = hintStore ?? throw new ArgumentNullException(nameof(hintStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _guestOsProbe = guestOsProbe;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// A per-VM Linux hint wins over the static host property, so a known-Linux guest is never
    /// misrouted to PowerShell Direct. An unusable hint still defers to a host-scoped Linux/SSH
    /// profile first: install-time IP resolution can fail while the operator's own SSH config is
    /// reachable, and that path is still SSH.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D3/LGR-D8.
    /// </summary>
    /// <remarks>
    /// <para>Only a recorded KNOWN classification may select a transport; the host's operating
    /// system is not evidence of the guest's, so absence of a record refuses (LGR-D27). The refusal
    /// is raised before any endpoint is resolved, so the session store's exclusive-ownership
    /// invariant is never entered on the undeterminable path (LGR-D34).</para>
    /// <para>On disagreement between a usable per-VM endpoint and a usable host-scoped one the
    /// per-VM endpoint wins (LGR-D16): preferring host-scoped would open SSH to the Hyper-V host
    /// itself and run the caller's command on the wrong machine. Stays synchronous and probe-free —
    /// a probe here would be sync-over-async on the hot path of all five facade methods (LGR-A2).</para>
    /// </remarks>
    private IPowerShellDirectChannel Select(string hostId, string vmId, string? probeOutcome = null)
    {
        var profile = _hostResolver.Resolve(hostId);
        var hostIsLinux = profile is not null && profile.IsLinuxGuest;

        if (!_hintStore.TryGet(hostId, vmId, out var hint))
        {
            var outcome = probeOutcome ?? OutcomeUndeterminable;
            _logger.LogWarning(
                "GuestChannelRouter: refusing {VmId} — no known guest-OS classification ({ProbeOutcome}); no transport selected.",
                vmId,
                outcome);
            throw new GuestRoutingUnavailableException(
                vmId,
                $"The guest OS of VM '{vmId}' was not determined, so no transport was selected. " +
                "The command can succeed once that guest's operating system becomes determinable.");
        }

        if (hint.GuestOs == GuestOsKind.Linux)
        {
            if (hint.HasUsableSshEndpoint)
            {
                _logger.LogDebug("GuestChannelRouter: routing {VmId} to SSH channel (Linux hint).", vmId);
                return _linuxChannel;
            }

            // "GuestOs == linux" alone is not evidence of reachability: the same usability test
            // SshSessionStore applies must gate the fallback, or routing would select SSH for an
            // endpoint the session store cannot resolve and the failure would surface late.
            if (hostIsLinux && profile!.ResolveSshEndpoint() is not null)
            {
                _logger.LogDebug(
                    "GuestChannelRouter: routing {VmId} to SSH channel (Linux hint without endpoint; host-scoped SSH configuration).",
                    vmId);
                return _linuxChannel;
            }

            _logger.LogWarning(
                "GuestChannelRouter: refusing {VmId} — known-Linux guest with no reachable SSH endpoint.",
                vmId);
            throw new GuestRoutingUnavailableException(
                vmId,
                $"The Linux guest '{vmId}' has no reachable SSH endpoint; guest routing failed closed.");
        }

        if (hostIsLinux)
        {
            _logger.LogDebug("GuestChannelRouter: routing {HostId} to SSH channel (Linux guest).", hostId);
            return _linuxChannel;
        }

        _logger.LogDebug(
            "GuestChannelRouter: routing {VmId} to PowerShell Direct channel (known-Windows hint).", vmId);
        return _windowsChannel;
    }

    /// <summary>
    /// Determines the guest OS and records the resulting known classification — Linux or Windows —
    /// before routing, when the store has no classification for this VM yet.
    /// </summary>
    /// <remarks>
    /// <para>Called from the five facade methods so it runs AFTER the GUID-validation and
    /// <c>VM_NOT_RUNNING</c> guards short-circuit upstream and BEFORE the routing decision — which
    /// is what makes a determinable Linux VM known-Linux no later than the routing of its first
    /// guest-interaction command, with no extra registration and no caller input.
    /// <c>vm_create</c> cannot express guest OS at all, so the server must determine it itself.</para>
    /// <para>This never surfaces the probe's OWN exception type, an unmapped error, or a new error
    /// code: every non-determination funnels into the single refusal <see cref="Select"/> raises.
    /// The probe is no longer advisory in the sense of "cannot fail the caller's command" — an
    /// undeterminable outcome is now a caller-visible refusal — so that superseded premise MUST NOT
    /// be relied on here. The caller's own cancellation propagates unchanged.</para>
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D17, LGR-D25 (C-2 note), LGR-D27.
    /// </remarks>
    private async Task<string?> EnsureClassifiedAsync(
        string hostId, string vmId, CancellationToken callerCt)
    {
        if (_guestOsProbe is null || string.IsNullOrWhiteSpace(vmId))
        {
            return null;
        }

        if (_hintStore.TryGet(hostId, vmId, out _))
        {
            return null;
        }

        var negativeKey = GuestRoutingHintStore.BuildKey(hostId, vmId);
        var now = _timeProvider.GetUtcNow();
        if (_undeterminableAt.TryGetValue(negativeKey, out var lastUndeterminableAt))
        {
            if (now - lastUndeterminableAt < NegativeCacheExpiry)
            {
                _logger.LogDebug(
                    "GuestChannelRouter: {ProbeOutcome} for {VmId}.", OutcomeSuppressed, vmId);
                return OutcomeSuppressed;
            }
            _undeterminableAt.TryRemove(negativeKey, out _);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        budget.CancelAfter(ClassificationBudget);

        try
        {
            var hint = await ProbeWithinBudgetAsync(hostId, vmId, budget.Token).ConfigureAwait(false);
            if (hint is not null)
            {
                _hintStore.Record(hostId, vmId, hint);
                // Any KNOWN determination — Linux or Windows — clears the suppression (LGR-D28).
                _undeterminableAt.TryRemove(negativeKey, out _);
                _logger.LogDebug(
                    "GuestChannelRouter: probe: {Determination} for {VmId}.",
                    hint.GuestOs == GuestOsKind.Linux ? "linux" : "windows",
                    vmId);
                return null;
            }

            _logger.LogDebug(
                "GuestChannelRouter: {ProbeOutcome} for {VmId}.", OutcomeUndeterminable, vmId);
            RecordUndeterminable(negativeKey, now);
            return OutcomeUndeterminable;
        }
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("GuestChannelRouter: {ProbeOutcome} for {VmId}.", OutcomeTimeout, vmId);
            RecordUndeterminable(negativeKey, now);
            return OutcomeTimeout;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GuestChannelRouter: {ProbeOutcome} for {VmId}.", OutcomeFaulted, vmId);
            RecordUndeterminable(negativeKey, now);
            return OutcomeFaulted;
        }
    }

    /// <summary>
    /// Runs the KVP probe and, when it is undeterminable, the SSH-banner secondary signal.
    /// </summary>
    /// <remarks>
    /// A probe that ignores its token is ABANDONED rather than awaited — a WMI/KVP call can block
    /// uninterruptibly, and awaiting it would make the budget decorative (LGR-D25(d)).
    /// </remarks>
    private async Task<GuestRoutingHint?> ProbeWithinBudgetAsync(
        string hostId,
        string vmId,
        CancellationToken budgetCt)
    {
        var probeTask = _guestOsProbe!.ProbeAsync(hostId, vmId, budgetCt);
        var budgetExpiry = Task.Delay(Timeout.InfiniteTimeSpan, budgetCt);
        var finished = await Task.WhenAny(probeTask, budgetExpiry).ConfigureAwait(false);
        if (!ReferenceEquals(finished, probeTask))
        {
            budgetCt.ThrowIfCancellationRequested();
        }

        var hint = await probeTask.ConfigureAwait(false);
        if (hint is not null)
        {
            return hint;
        }

        var bannerHint = await ProbeSshBannerAsync(hostId, vmId, budgetCt).ConfigureAwait(false);
        if (bannerHint is not null)
        {
            return bannerHint;
        }

        return await RetryReadWithinBudgetAsync(hostId, vmId, budgetCt).ConfigureAwait(false);
    }

    /// <summary>
    /// One best-effort re-read of the intrinsic items after a short settling delay.
    /// </summary>
    /// <remarks>
    /// KVP intrinsic data is characteristically absent for a window after a guest or host restart,
    /// and under LGR-D27 that window refuses commands a Windows guest has no other way to pass.
    /// Completion is NOT guaranteed and no admission threshold is asserted: the read costs an
    /// unmeasured amount, so it is attempted whenever budget remains and the shared deadline simply
    /// truncates it. A truncated or failed retry yields the ordinary refusal, never a guess.
    /// See myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D36.
    /// </remarks>
    private async Task<GuestRoutingHint?> RetryReadWithinBudgetAsync(
        string hostId,
        string vmId,
        CancellationToken budgetCt)
    {
        if (budgetCt.IsCancellationRequested)
        {
            return null;
        }

        _logger.LogDebug("GuestChannelRouter: probe: retried for {VmId}.", vmId);
        await Task.Delay(RetryReadDelay, budgetCt).ConfigureAwait(false);

        var retryTask = _guestOsProbe!.ProbeAsync(hostId, vmId, budgetCt);
        var budgetExpiry = Task.Delay(Timeout.InfiniteTimeSpan, budgetCt);
        var finished = await Task.WhenAny(retryTask, budgetExpiry).ConfigureAwait(false);
        if (!ReferenceEquals(finished, retryTask))
        {
            budgetCt.ThrowIfCancellationRequested();
        }
        return await retryTask.ConfigureAwait(false);
    }

    /// <summary>
    /// LGR-D24 secondary signal, consulted only when KVP OS identity is undeterminable and an
    /// endpoint has been established for this VM alone.
    /// </summary>
    /// <remarks>
    /// The endpoint MUST come from per-VM evidence, never from the host profile: that resolver
    /// falls back to <c>ComputerName</c>, which for the default local profile is the Hyper-V host
    /// itself (<c>localhost:22</c>). A banner from the host, or from one statically configured
    /// address shared by every VM on it, would otherwise record a Linux hint for whichever VM
    /// happened to be classified and route a Windows guest's commands to another machine.
    /// </remarks>
    private async Task<GuestRoutingHint?> ProbeSshBannerAsync(
        string hostId,
        string vmId,
        CancellationToken budgetCt)
    {
        var endpoint = await _guestOsProbe!
            .ResolveVmScopedSshEndpointAsync(hostId, vmId, budgetCt)
            .ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var responded = await KvpGuestOsProbe
            .RespondsWithSshBannerAsync(endpoint.Value.SshHost, endpoint.Value.SshPort, budgetCt)
            .ConfigureAwait(false);

        return responded
            ? new GuestRoutingHint(GuestOsKind.Linux, endpoint.Value.SshHost, endpoint.Value.SshPort)
            : null;
    }

    /// <remarks>
    /// Admission and eviction are serialized: the keys are workload-controlled, so a capacity
    /// enforced by separate concurrent-dictionary operations would not be an invariant under
    /// parallel first-seen requests.
    /// </remarks>
    private void RecordUndeterminable(string negativeKey, DateTimeOffset now)
    {
        lock (_negativeCacheLock)
        {
            if (!_undeterminableAt.ContainsKey(negativeKey))
            {
                while (_undeterminableAt.Count >= NegativeCacheCapacity)
                {
                    var oldest = _undeterminableAt
                        .OrderBy(entry => entry.Value)
                        .Select(entry => entry.Key)
                        .FirstOrDefault();
                    if (oldest is null || !_undeterminableAt.TryRemove(oldest, out _))
                    {
                        break;
                    }
                }
            }
            _undeterminableAt[negativeKey] = now;
        }
    }

    /// <summary>Test seam for the capacity invariant.</summary>
    internal int NegativeCacheCount => _undeterminableAt.Count;

    /// <summary>
    /// Drops both the routing hint and any negative-cache entry for a VM, so a recreated VM
    /// identifier cannot inherit a stale classification.
    /// See /myplans/remoting/linux-guest-support/linux-guest-routing-design.md — LGR-D20.
    /// </summary>
    public void ForgetGuestClassification(string hostId, string vmId)
    {
        if (string.IsNullOrWhiteSpace(vmId))
        {
            return;
        }
        _hintStore.Remove(hostId, vmId);
        _undeterminableAt.TryRemove(GuestRoutingHintStore.BuildKey(hostId, vmId), out _);
    }

    /// <summary>
    /// Classifies when needed, then routes.
    /// </summary>
    /// <remarks>
    /// The no-probe path MUST stay synchronous through <see cref="Select"/> so
    /// <see cref="GuestRoutingUnavailableException"/> still reaches the caller before any Task is
    /// returned (LGR-D3). Marking the facades <c>async</c> outright would wrap that throw in the
    /// returned Task and change an established fail-closed contract.
    /// </remarks>
    private Task<TResult> RouteAsync<TResult>(
        string hostId,
        string vmId,
        Func<IPowerShellDirectChannel, Task<TResult>> operation,
        CancellationToken ct)
    {
        if (!NeedsClassification(hostId, vmId))
        {
            return operation(Select(hostId, vmId));
        }

        return ClassifyThenRouteAsync();

        async Task<TResult> ClassifyThenRouteAsync()
        {
            var probeOutcome = await EnsureClassifiedAsync(hostId, vmId, ct).ConfigureAwait(false);
            return await operation(Select(hostId, vmId, probeOutcome)).ConfigureAwait(false);
        }
    }

    private bool NeedsClassification(string hostId, string vmId)
        => _guestOsProbe is not null
            && !string.IsNullOrWhiteSpace(vmId)
            && !_hintStore.TryGet(hostId, vmId, out _);

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeScriptAsync(
        string hostId, string vmId, string username, string password,
        string script, IDictionary<string, object?>? args = null, CancellationToken ct = default)
        => RouteAsync(
            hostId,
            vmId,
            channel => channel.InvokeScriptAsync(hostId, vmId, username, password, script, args, ct),
            ct);

    /// <inheritdoc />
    public Task<PowerShellHostResult> InvokeScriptWithTimeoutAsync(
        string hostId, string vmId, string username, string password,
        string script, IDictionary<string, object?>? args, int timeoutSeconds, CancellationToken ct = default)
        => RouteAsync(
            hostId,
            vmId,
            channel => channel.InvokeScriptWithTimeoutAsync(
                hostId, vmId, username, password, script, args, timeoutSeconds, ct),
            ct);

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyToSessionAsync(
        string hostId, string vmId, string username, string password,
        string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        => RouteAsync(
            hostId,
            vmId,
            channel => channel.CopyToSessionAsync(
                hostId, vmId, username, password, localSourcePath, guestDestinationPath, ct),
            ct);

    /// <inheritdoc />
    public Task<PowerShellHostResult> CopyFromSessionAsync(
        string hostId, string vmId, string username, string password,
        string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        => RouteAsync(
            hostId,
            vmId,
            channel => channel.CopyFromSessionAsync(
                hostId, vmId, username, password, guestSourcePath, localDestinationPath, ct),
            ct);

    /// <inheritdoc />
    public Task EvictSessionAsync(string hostId, string vmId, CancellationToken ct = default)
    {
        if (!NeedsClassification(hostId, vmId))
        {
            return Select(hostId, vmId).EvictSessionAsync(hostId, vmId, ct);
        }

        return ClassifyThenEvictAsync();

        async Task ClassifyThenEvictAsync()
        {
            var probeOutcome = await EnsureClassifiedAsync(hostId, vmId, ct).ConfigureAwait(false);
            await Select(hostId, vmId, probeOutcome)
                .EvictSessionAsync(hostId, vmId, ct).ConfigureAwait(false);
        }
    }
}
