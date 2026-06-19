using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for issue #113 — out-of-band ISO mount cleanup when the
/// inspection PS child is killed (timeout / cancellation / unrecognized output)
/// before its in-script <c>finally</c>-block <c>Dismount-DiskImage</c> can run.
///
/// Design: see
/// /myplans/vm-management/iso-installation/iso-inspector-mount-cleanup-design.md
/// (decisions MC-D1..MC-D8). Tests are scenario-named to map back to the
/// design-doc cleanup-trigger matrix.
/// </summary>
public sealed class Issue113IsoInspectorMountCleanupTests
{
    private const string IsoPath = @"C:\isos\Win11.iso";

    private static IsoInspector CreateSut(IPowerShellExecutor exec) =>
        new(exec, new NullLogger<IsoInspector>());

    /// <summary>
    /// Scripted mock of <see cref="IPowerShellExecutor"/> that returns
    /// pre-queued results in order. Captures every dispatched script for
    /// assertion of "did the cleanup PS get fired with Dismount-DiskImage".
    /// </summary>
    private sealed class ScriptedExecutor : IPowerShellExecutor
    {
        private readonly Queue<Func<string, CancellationToken, Task<PowerShellResult>>> _responses = new();
        public List<(string Script, int Timeout, bool TokenCanCancel)> Calls { get; } = new();

        public ScriptedExecutor Then(Func<string, CancellationToken, Task<PowerShellResult>> resp)
        {
            _responses.Enqueue(resp);
            return this;
        }

        public ScriptedExecutor ThenResult(PowerShellResult result)
        {
            _responses.Enqueue((_, _) => Task.FromResult(result));
            return this;
        }

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            Calls.Add((script, timeoutSeconds, ct.CanBeCanceled));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No scripted response queued for ExecuteAsync call #" + Calls.Count);
            }
            return _responses.Dequeue().Invoke(script, ct);
        }
    }

    private static PowerShellResult Ok(string stdout) => new() { ExitCode = 0, Stdout = stdout };
    private static PowerShellResult TimedOut() => new() { ExitCode = 1, Stdout = string.Empty, TimedOut = true };

    // ---------------------------------------------------------------------
    // Cleanup-trigger matrix
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Inspection_TimedOut_And_NotPreMounted_FiresOutOfBandDismount_With_NonCancellableToken()
    {
        // Probe says NOT_MOUNTED → inspection times out (no recognized line)
        // → expect a third call carrying Dismount-DiskImage on CT.None.
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(TimedOut())
            .ThenResult(Ok("DISMOUNTED"));

        var sut = CreateSut(exec);
        var (found, diag) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        diag.Should().Contain("timed out");
        exec.Calls.Should().HaveCount(3);
        exec.Calls[2].Script.Should().Contain("Dismount-DiskImage");
        // MC-D2: cleanup must run on CancellationToken.None (non-cancellable).
        exec.Calls[2].TokenCanCancel.Should().BeFalse(
            "out-of-band cleanup must use CancellationToken.None per MC-D2");
    }

    [Fact]
    public async Task Inspection_Cancelled_And_NotPreMounted_FiresOutOfBandDismount_AndRethrows()
    {
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .Then((_, _) => Task.FromException<PowerShellResult>(new OperationCanceledException()))
            .ThenResult(Ok("DISMOUNTED"));

        var sut = CreateSut(exec);

        // MC-D5/MC-D6: cancellation is rethrown unchanged after cleanup runs.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath));

        exec.Calls.Should().HaveCount(3);
        exec.Calls[2].Script.Should().Contain("Dismount-DiskImage");
    }

    [Fact]
    public async Task Inspection_PostDispatchException_And_NotPreMounted_FiresOutOfBandDismount()
    {
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .Then((_, _) => Task.FromException<PowerShellResult>(new InvalidOperationException("boom")))
            .ThenResult(Ok("DISMOUNTED"));

        var sut = CreateSut(exec);
        var (found, diag) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        diag.Should().Contain("boom");
        exec.Calls.Should().HaveCount(3, "post-dispatch exceptions must trigger cleanup (MC-Q3 / 🟡 #3)");
        exec.Calls[2].Script.Should().Contain("Dismount-DiskImage");
    }

    [Fact]
    public async Task Inspection_UnrecognizedOutput_And_NotPreMounted_FiresOutOfBandDismount()
    {
        // Stdout exists but contains no recognized terminal line — MC-D1
        // says assume the in-script finally did NOT run.
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(Ok("garbled banner without terminal line"))
            .ThenResult(Ok("DISMOUNTED"));

        var sut = CreateSut(exec);
        var (found, _) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        exec.Calls.Should().HaveCount(3);
        exec.Calls[2].Script.Should().Contain("Dismount-DiskImage");
    }

    // ---------------------------------------------------------------------
    // No-cleanup paths
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Inspection_TimedOut_But_PreMounted_SkipsCleanup()
    {
        // MC-D3 + MC-Q2: pre-existing mount means SOMEONE ELSE owns
        // dismount; we must NOT issue our own.
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("MOUNTED"))
            .ThenResult(TimedOut());

        var sut = CreateSut(exec);
        var (found, _) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        exec.Calls.Should().HaveCount(2,
            "pre-existing mount must suppress out-of-band cleanup (MC-D3)");
    }

    [Fact]
    public async Task Inspection_WIN_OK_DoesNotFireCleanup()
    {
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(Ok("WIN_OK"));

        var sut = CreateSut(exec);
        var (found, diag) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeTrue();
        diag.Should().BeNull();
        exec.Calls.Should().HaveCount(2,
            "recognized terminal output trusts the in-script finally (MC-D5)");
    }

    [Fact]
    public async Task Inspection_NO_WIM_DoesNotFireCleanup()
    {
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(Ok("NO_WIM"));

        var sut = CreateSut(exec);
        var (found, _) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        exec.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Inspection_ERROR_DoesNotFireCleanup()
    {
        // 🟡 #2 (Gate 2 review): an ERROR: line proves the script's catch
        // ran, which means its finally also ran ⇒ no leak ⇒ no cleanup.
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(Ok("ERROR:Mount-DiskImage failed"));

        var sut = CreateSut(exec);
        var (found, diag) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        diag.Should().Be("Mount-DiskImage failed");
        exec.Calls.Should().HaveCount(2,
            "ERROR: terminal line proves the in-script finally ran (🟡 #2)");
    }

    [Fact]
    public async Task ProbeFailure_TreatedAsNotPreMounted_AndCleanupStillRunsOnFailure()
    {
        // Probe failure must be non-fatal; falls through to inspection,
        // and (per design) is treated as wasAlreadyMountedBeforeUs == false.
        var exec = new ScriptedExecutor()
            .Then((_, _) => Task.FromException<PowerShellResult>(new InvalidOperationException("probe blew up")))
            .ThenResult(TimedOut())
            .ThenResult(Ok("DISMOUNTED"));

        var sut = CreateSut(exec);
        var (found, _) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        exec.Calls.Should().HaveCount(3,
            "probe failure must be treated as not-pre-mounted, so cleanup still runs (design: probe is best-effort)");
        exec.Calls[2].Script.Should().Contain("Dismount-DiskImage");
    }

    [Fact]
    public async Task CleanupFailure_IsSwallowed_AndDoesNotChangeOriginalOutcome()
    {
        // MC-D6: cleanup throwing must not propagate or alter the inspection
        // result (here: timeout → (false, "...timed out...")).
        var exec = new ScriptedExecutor()
            .ThenResult(Ok("NOT_MOUNTED"))
            .ThenResult(TimedOut())
            .Then((_, _) => Task.FromException<PowerShellResult>(new InvalidOperationException("dismount blew up")));

        var sut = CreateSut(exec);
        var (found, diag) = await sut.ContainsWindowsInstallWimWithDiagnosticAsync(IsoPath);

        found.Should().BeFalse();
        diag.Should().Contain("timed out",
            "cleanup failures must never overwrite the original inspection diagnostic (MC-D6)");
    }
}
