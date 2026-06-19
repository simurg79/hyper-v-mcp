using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #209 (sub-finding) / VC-SO-D1..D8: PSDirect session-open failures
/// against Linux guests previously returned the wrong envelope
/// (<c>errorCode:"FILE_NOT_FOUND"</c> with empty <c>error</c>) because the
/// underlying New-PSSession stderr text frequently contains "cannot find
/// path"-shaped phrases that hit the ST-6 path-not-found substring arm before
/// any session-open classifier could fire.
///
/// <para>These tests pin the corrected behavior (SESSION_FAILED with a
/// non-empty error string) across:
/// <list type="bullet">
/// <item>T1: Direct <see cref="SessionOpenFailedException"/> typed arm.</item>
/// <item>T2-T5: <c>vm_run_command</c> / <c>vm_run_script</c> /
/// <c>vm_copy_file</c> / <c>vm_get_file</c> failures (simulated by the
/// channel wrapper, mirroring how tools surface failures in production).</item>
/// <item>T6: <see cref="FileNotFoundException"/> still maps to FILE_NOT_FOUND
/// (C5 backward-compat lock).</item>
/// <item>T7: <c>PSRemotingTransportException</c> still maps to SESSION_FAILED
/// via the existing ST-6 typed arm (regression guard for issue #52).</item>
/// <item>T8: Substring defense-in-depth for plain
/// <see cref="InvalidOperationException"/> bypassing the typed wrap, even when
/// the message also contains "cannot find path".</item>
/// </list></para>
///
/// See <c>myplans/remoting/session-management/psdirect-linux-session-open-classification-design.md</c>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue209LinuxPSDirectClassificationTests
{
    private readonly ErrorMapper _mapper = new();

    private const string LinuxStderrSample =
        "New-PSSession: [linux-vm] Connecting to remote server linux-vm failed " +
        "with the following error message: The Hyper-V socket target process is " +
        "not listening. + CategoryInfo : OpenError: (linux-vm:String) [New-PSSession], " +
        "PSRemotingTransportException + FullyQualifiedErrorId : 2100,PSSessionOpenFailed " +
        "vmhypervsocketclient: cannot find path on guest.";

    // ── T1: typed exception directly ────────────────────────────────────
    [Fact]
    public void T1_SessionOpenFailedException_Direct_MapsTo_SessionFailed_NonEmptyError()
    {
        var ex = new SessionOpenFailedException(
            sessionName: "HvMcp-linux-vm",
            vmId: "linux-vm",
            message: $"Failed to create PSSession 'HvMcp-linux-vm': {LinuxStderrSample}");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "VC-SO-D3: PSDirect session-open failures must classify as SESSION_FAILED, " +
            "not FILE_NOT_FOUND, even when stderr contains 'cannot find path'.");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "VC-SO-D5: the composed error string must be non-empty for all session-open failures.");
        response.Error.Should().Contain("PSSessionOpenFailed");
    }

    // ── T2-T5: channel-wrapped tool surfacing ───────────────────────────
    // In production, VmTools call into IPowerShellDirectChannel which wraps
    // non-cancellation failures in PowerShellDirectChannelException with the
    // inner exception preserved. ErrorMapper unwraps once and classifies by
    // inner type. These four cases exercise exactly that path for each tool.

    private void AssertChannelWrappedSessionOpenMapsToSessionFailed(string toolLabel)
    {
        var inner = new SessionOpenFailedException(
            sessionName: $"HvMcp-{toolLabel}",
            vmId: toolLabel,
            message: $"Failed to create PSSession 'HvMcp-{toolLabel}': {LinuxStderrSample}");
        var wrapper = new PowerShellDirectChannelException(
            $"PSDirect channel failed during {toolLabel}: {LinuxStderrSample}",
            inner);

        var response = _mapper.MapException(wrapper);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            $"{toolLabel} session-open failures must classify as SESSION_FAILED.");
        response.ErrorCode.Should().NotBe(ErrorCodes.FileNotFound,
            $"{toolLabel} must NOT be misclassified as FILE_NOT_FOUND even though stderr " +
            "contains 'cannot find path' (the original Issue #209 sub-finding bug).");
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void T2_VmRunCommand_SessionOpenFailure_MapsTo_SessionFailed()
        => AssertChannelWrappedSessionOpenMapsToSessionFailed("vm_run_command");

    [Fact]
    public void T3_VmRunScript_SessionOpenFailure_MapsTo_SessionFailed()
        => AssertChannelWrappedSessionOpenMapsToSessionFailed("vm_run_script");

    [Fact]
    public void T4_VmCopyFile_SessionOpenFailure_MapsTo_SessionFailed_NotFileNotFound()
    {
        // The original Issue #209 sub-finding bug specifically misclassified
        // vm_copy_file failures as FILE_NOT_FOUND because the path-not-found
        // substring arm caught "cannot find path" in the New-PSSession stderr
        // BEFORE any session-open classifier ran. This test is the strongest
        // regression guard.
        AssertChannelWrappedSessionOpenMapsToSessionFailed("vm_copy_file");
    }

    [Fact]
    public void T5_VmGetFile_SessionOpenFailure_MapsTo_SessionFailed()
        => AssertChannelWrappedSessionOpenMapsToSessionFailed("vm_get_file");

    // ── T6: C5 backward-compat lock — plain FileNotFoundException ───────
    [Fact]
    public void T6_PlainFileNotFoundException_StillMapsTo_FileNotFound()
    {
        var ex = new FileNotFoundException(
            "Could not find file 'C:\\missing\\foo.txt'.",
            "C:\\missing\\foo.txt");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.FileNotFound,
            "C5 backward-compat: non-session-open FileNotFoundException must remain " +
            "FILE_NOT_FOUND.");
    }

    // ── T7: Issue #52 ST-6 regression guard — PSRemotingTransportException ─
    [Fact]
    public void T7_PSRemotingTransportException_StillMapsTo_SessionFailed()
    {
        var ex = new System.Management.Automation.Remoting.PSRemotingTransportException(
            "The PSSession transport dropped.");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "C5 backward-compat: the existing Issue #52 ST-6 typed arm for " +
            "PSRemotingTransportException must continue to classify as SESSION_FAILED.");
    }

    // ── T8: substring fallback (typed wrap bypassed) ────────────────────
    [Fact]
    public void T8_PlainInvalidOperationException_WithPSSessionOpenFailed_MapsTo_SessionFailed()
    {
        // Simulates a code path that bypasses the SessionStore typed wrap and
        // surfaces the failure as a plain InvalidOperationException. The
        // message INTENTIONALLY contains both "PSSessionOpenFailed" AND
        // "cannot find path" — the substring fallback must win because it sits
        // above the path-not-found arm (VC-SO-D4 ordering invariant).
        var ex = new InvalidOperationException(
            "New-PSSession failed: cannot find path. " +
            "FullyQualifiedErrorId: 2100,PSSessionOpenFailed");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "VC-SO-D4: the substring fallback arm for PSSessionOpenFailed must sit " +
            "above the path-not-found arm so it wins even when the message contains " +
            "'cannot find path'.");
        response.ErrorCode.Should().NotBe(ErrorCodes.FileNotFound);
        response.Error.Should().NotBeNullOrWhiteSpace();
    }
}
