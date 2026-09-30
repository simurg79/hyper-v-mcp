using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #294 — a session-open failure envelope carried >100 KB of text, including the server's own
/// ~210-line bootstrap script body AND diagnostic frames belonging to OTHER VMs. Both defects came
/// from one place: <c>$error</c> is the runspace-global collection of the long-lived singleton
/// <see cref="PowerShellHost"/> runspace, so it accumulates every prior failure for the process
/// lifetime, and rendering a slice of it through <c>Out-String</c> also renders
/// <c>InvocationInfo.PositionMessage</c> — i.e. this script's own body.
///
/// <para>Live evidence from the spike: VM a → 8,510 B / 36 frames / 0 foreign GUIDs; VM b →
/// 15,840 B / 68 frames / <b>8 occurrences of VM a's GUID</b>.</para>
///
/// <para>ANTI-MASK CONTRACT. The contamination is a property of a SHARED runspace across
/// invocations, so a per-call fresh runspace — or a mocked <see cref="IPowerShellHost"/> returning
/// canned stderr — cannot reproduce it and would make this suite self-satisfying (the PR #278 /
/// issue #301 failure mode). These tests therefore drive the REAL <see cref="SessionStore"/>, whose
/// REAL bootstrap script is really executed, twice, against ONE shared runspace that really
/// accumulates <c>$error</c> — exactly as the singleton host does in production. Only the guest
/// cmdlets are shadowed, because there is no Hyper-V guest to fail against off-host.</para>
///
/// Spec:   internal documentation — FR-1, FR-2, FR-3, AC-3, AC-4.
/// Design: internal documentation — SOE-D1, SOE-D2, SOE-D10.
/// Reference pattern: <see cref="Issue291VmPauseRequestedStateRealSeamTests"/>.
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue294SessionEnvelopeIsolationRealSeamTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private const string HostId = "local";
    private const string VmA = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string VmB = "bbbbbbbb-5555-6666-7777-888888888888";

    /// <summary>Appears only in VM A's failure, so any occurrence in VM B's envelope is foreign.</summary>
    private const string VmAForeignSentinel = "FOREIGN-FRAME-FROM-VM-A";

    private readonly SharedRunspaceHost _host;
    private readonly SessionStore _store;

    public Issue294SessionEnvelopeIsolationRealSeamTests(ITestOutputHelper output)
    {
        _output = output;

        // One runspace for the whole fixture — this is what makes $error accumulate across the two
        // VMs, reproducing the production singleton-host lifetime. Get-VM is shadowed to fail with
        // a VM-specific message so each failure is attributable to the VM that caused it.
        _host = new SharedRunspaceHost($@"
function Get-VM {{
    param($Id, $ComputerName, $Name)
    $idText = [string]$Id
    if ($idText -eq '{VmA}') {{
        throw '{VmAForeignSentinel}: Hyper-V socket negotiation refused for guest ' + $idText
    }}
    throw 'The Hyper-V socket target process is not listening for guest ' + $idText
}}
");
        _store = new SessionStore(_host, NullLogger<SessionStore>.Instance);
    }

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Drives the REAL production session-open failure path and maps it exactly as the server does,
    /// returning the caller-visible envelope.
    /// </summary>
    private async Task<McpToolResponse> OpenSessionAndMapFailureAsync(string vmId)
    {
        var mapper = new ErrorMapper();
        try
        {
            await _store.GetOrCreateAsync(HostId, vmId, "administrator", "pw", CancellationToken.None);
            throw new InvalidOperationException(
                "the shadowed Get-VM must make session-open fail; a success means the guard is vacuous");
        }
        catch (Exception ex)
        {
            return mapper.MapException(ex);
        }
    }

    // ── Guard 2: sequential-two-VM $error contamination (THE #294 regression guard) ──

    /// <summary>
    /// THE #294 guard. Two independent session-open failures run in sequence through the real
    /// production path against one shared runspace. VM B's envelope must describe only VM B.
    ///
    /// <para>With the defect restored — the <c>($error[$i] | Out-String)</c> slice in the catch
    /// block — VM B's envelope reproduces VM A's frames verbatim, so both the foreign sentinel and
    /// VM A's identifier appear and this test fails on real contaminated output.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SecondVmEnvelope_ContainsNoIdentifierOrFrameFromFirstVm()
    {
        var first = await OpenSessionAndMapFailureAsync(VmA);
        first.Error.Should().NotBeNullOrWhiteSpace("the first failure must produce a real envelope");
        first.Error.Should().Contain(VmA, "VM A's own envelope legitimately names VM A");
        _output.WriteLine($"VM A envelope ({first.Error!.Length} chars): {first.Error}");

        var second = await OpenSessionAndMapFailureAsync(VmB);
        _output.WriteLine($"VM B envelope ({second.Error!.Length} chars): {second.Error}");

        second.Error.Should().NotContain(VmA,
            "FR-1/AC-4 (#294): VM B's envelope MUST NOT carry VM A's identifier. $error is the " +
            "runspace-global collection of the singleton host runspace; rendering a slice of it " +
            "reproduces earlier VMs' frames in this VM's failure text.");
        second.Error.Should().NotContain(VmAForeignSentinel,
            "FR-1/AC-4 (#294): no diagnostic frame from the VM A attempt may appear in VM B's envelope.");
        second.Error.Should().Contain(VmB,
            "FR-4: the envelope must still name the VM the caller actually addressed.");
    }

    /// <summary>
    /// FR-2 — isolation must survive unrelated intervening activity, not just an immediate pair.
    /// Several further failures run between A and B, growing the runspace-global <c>$error</c>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Isolation_Holds_AcrossInterveningGuestOperations()
    {
        await OpenSessionAndMapFailureAsync(VmA);

        for (var iter = 0; iter < 3; iter++)
        {
            await OpenSessionAndMapFailureAsync($"cccccccc-0000-0000-0000-00000000000{iter}");
        }

        var second = await OpenSessionAndMapFailureAsync(VmB);

        second.Error.Should().NotContain(VmA,
            "FR-2: isolation MUST hold for the whole process lifetime regardless of how many prior " +
            "guest operations occurred.");
        second.Error.Should().NotContain(VmAForeignSentinel,
            "FR-2: an older VM's frame must not resurface once $error has grown.");
    }

    /// <summary>
    /// AC-3 / FR-3 — the bootstrap script body must never reach the caller. The script carries
    /// distinctive internal markers; none may appear in the envelope. This is the >100 KB defect:
    /// <c>Out-String</c> on an ErrorRecord renders <c>InvocationInfo.PositionMessage</c>, i.e. the
    /// script's own source lines.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Envelope_CarriesNoBootstrapScriptBody_AndStaysWithinBound()
    {
        var response = await OpenSessionAndMapFailureAsync(VmA);
        var error = response.Error!;

        foreach (var scriptSentinel in new[]
                 {
                     "$global:__HvMcpSessions",
                     "RC-11.4",
                     "Import-Module Hyper-V",
                     "$rc103aDiscovery",
                     "__rc115Sw",
                 })
        {
            error.Should().NotContain(scriptSentinel,
                $"FR-3/AC-3: '{scriptSentinel}' is uniquely attributable bootstrap-script content " +
                "and MUST NOT reach the caller.");
        }

        error.Length.Should().BeLessThanOrEqualTo(4000,
            "FR-7: the error field of a session-open failure envelope is bounded at 4,000 characters.");
    }

    /// <summary>
    /// FR-10 — bounding and isolation must not change the classification. The condition is a
    /// session-open failure, so it stays SESSION_FAILED.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SessionOpenFailure_StillClassifiesAsSessionFailed()
    {
        var response = await OpenSessionAndMapFailureAsync(VmA);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "FR-10: existing error codes for session-open conditions are unchanged.");
    }

    // ── Guard 3 (part): AC-5b non-vacuity at a REAL failure condition ────────

    /// <summary>
    /// AC-5b non-vacuity. The spec requires that at least one REAL supported session-open failure
    /// condition supplies an authoritative cause in normal operation — otherwise the captured-path
    /// guarantee is vacuous and only ever demonstrated through a purpose-built test seam.
    ///
    /// <para>This exercises the genuine production path with no injected decisive cause: the guest
    /// cmdlet really fails, <see cref="SessionStore"/> really captures the cause structurally, and
    /// the value must arrive on <see cref="SessionOpenFailedException.DecisiveCause"/> and reach the
    /// envelope verbatim.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RealSessionOpenFailure_SuppliesAuthoritativeCause_TakingTheCapturedPath()
    {
        // A concise guest-side failure, as a real refused socket negotiation produces. The
        // condition is the genuine production path; only the guest's own wording is fixed here so
        // the cause lands inside the statement allowance and the CAPTURED path is the one taken.
        using var host = new SharedRunspaceHost(@"
function Get-VM {
    param($Id, $ComputerName, $Name)
    throw 'The Hyper-V socket target process is not listening.'
}
");
        var store = new SessionStore(host, NullLogger<SessionStore>.Instance);

        SessionOpenFailedException? captured = null;
        try
        {
            await store.GetOrCreateAsync(HostId, VmB, "administrator", "pw", CancellationToken.None);
        }
        catch (SessionOpenFailedException ex)
        {
            captured = ex;
        }

        captured.Should().NotBeNull("the real condition must surface the typed session-open failure");
        captured!.DecisiveCause.Should().NotBeNullOrWhiteSpace(
            "AC-5b non-vacuity: a real supported session-open condition MUST supply an authoritative " +
            "cause as a distinct value alongside the failure outcome — not merely as aggregate text. " +
            "Without one, the absolute FR-5a guarantee is never exercised in production.");
        captured.DecisiveCause!.Length.Should().BeLessThanOrEqualTo(1000,
            "the captured path applies to causes fitting the 1,000-character statement allowance");

        var envelope = new ErrorMapper().MapException(captured);
        envelope.Error.Should().Contain(captured.DecisiveCause,
            "FR-5a: a fitting authoritative cause MUST be delivered verbatim and in full.");
    }

    /// <summary>
    /// Shared-runspace <see cref="IPowerShellHost"/> whose <c>$error</c> really accumulates across
    /// invocations, mirroring the production singleton host.
    /// </summary>
    /// <remarks>
    /// The wire/diagnostic split is modelled faithfully (SOE-D10): <c>Stderr</c> is the full
    /// <c>ErrorRecord.ToString()</c> rendering — the representation that carries positional script
    /// text — while <c>WireStderr</c> is composed from NAMED facets only. Collapsing the two would
    /// mask the very disclosure these tests exist to detect.
    /// </remarks>
    private sealed class SharedRunspaceHost : IPowerShellHost, IDisposable
    {
        private readonly Runspace _runspace;
        private bool _disposed;

        internal SharedRunspaceHost(string shadowScript)
        {
            _runspace = RunspaceFactory.CreateRunspace();
            _runspace.Open();

            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(shadowScript);
            ps.Invoke();
        }

        public PowerShellEdition Edition => PowerShellEdition.PowerShell7;

        public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PowerShellHostResult> InvokeAsync(
            string script, IDictionary<string, object?>? args = null, CancellationToken ct = default)
            => InvokeWithTimeoutAsync(script, args, timeoutSeconds: null, ct);

        public Task<PowerShellHostResult> InvokeWithTimeoutAsync(
            string script, IDictionary<string, object?>? args, int? timeoutSeconds,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();

            return Task.Run(() =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;

                if (args is not null)
                {
                    foreach (var pair in args)
                    {
                        _runspace.SessionStateProxy.SetVariable(pair.Key, pair.Value);
                    }
                }

                ps.AddScript(script);

                Collection<PSObject>? output = null;
                var terminated = false;
                try
                {
                    output = ps.Invoke();
                }
                catch
                {
                    terminated = true;
                }

                var full = new StringBuilder();
                var wire = new StringBuilder();
                var firstFull = true;
                var firstWire = true;
                foreach (var record in ps.Streams.Error)
                {
                    if (!firstFull) full.Append('\n');
                    firstFull = false;
                    // The unsafe representation: ToString() on an ErrorRecord renders positional
                    // script text, which is exactly what must not reach the wire.
                    full.Append(record.ToString());
                    if (record.ScriptStackTrace is not null)
                    {
                        full.Append('\n').Append(record.ScriptStackTrace);
                    }

                    if (!firstWire) wire.Append('\n');
                    firstWire = false;
                    wire.Append(record.Exception?.GetType().FullName ?? "(unknown)")
                        .Append(": ")
                        .Append(record.Exception?.Message ?? string.Empty)
                        .Append(" | FQEID=").Append(record.FullyQualifiedErrorId)
                        .Append(" | Category=").Append(record.CategoryInfo?.ToString());
                }

                var success = !terminated && !ps.HadErrors;
                return new PowerShellHostResult(
                    Success: success,
                    Output: output?.Select(item => (object?)item?.BaseObject).ToList() ?? new List<object?>(),
                    Stderr: full.ToString(),
                    ExitCode: success ? 0 : 1,
                    WireStderr: wire.ToString());
            }, ct);
        }

        public Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
            => throw new NotSupportedException("Not used by these tests.");

        public PowerShellHostInitDiagnostics GetInitDiagnostics()
            => new(true, PowerShellEdition.PowerShell7, null, null, null, null);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _runspace.Dispose(); } catch { /* swallow */ }
        }
    }
}
