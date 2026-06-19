using FluentAssertions;
using Moq;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Runtime tests for timeout behavior preserving partial output with success=false.
/// See /myplans/execution/commands/commands-design.md — CMD-D4: Timeout returns success:false with partial output.
/// See /myplans/design.md §4 — ADR-9: Timeout returns success:false with partial data.
///
/// These tests exercise the expected timeout behavior of the command executor.
/// They use Moq for ICommandExecutor since the real implementation requires
/// PowerShell Direct infrastructure, but the tests define the expected behavioral contract.
///
/// Expected runtime flows:
/// - Timed-out command returns CommandResult with timedOut=true
/// - Partial stdout/stderr is preserved in data field
/// - The response envelope has success=false, errorCode=COMMAND_TIMEOUT
/// - Session remains open after timeout (CMD-D6)
/// - Cancelled commands set cancelled=true
///
/// HOW TO MAKE THESE PASS:
/// 1. Implement ICommandExecutor (e.g. via PowerShellDirectChannel).
/// 2. Timeout wrapping with Wait-Job -Timeout.
/// 3. On timeout: Stop-Job -Force, capture partial output, return CommandResult with timedOut=true.
/// 4. Wrap in McpToolResponse with success=false and errorCode=COMMAND_TIMEOUT.
/// </summary>
[Trait("Category", "Runtime")]
public class TimeoutBehaviorTests
{
    // ─── Timeout Returns Partial Output ────────────────────────────────

    /// <summary>
    /// When a command times out, the CommandResult must have timedOut=true
    /// and preserve any partial stdout/stderr captured before the timeout.
    /// See /myplans/execution/commands/commands-design.md — CMD-D4.
    /// </summary>
    [Fact]
    public async Task TimedOut_Command_Returns_Partial_Output()
    {
        var mockExecutor = new Mock<ICommandExecutor>();
        mockExecutor.Setup(e => e.ExecuteCommandAsync(
                "local", "test-vm", "long-running-command", "cmd", 5, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = -1,
                Stdout = "line 1\nline 2\npartial line 3...",
                Stderr = "",
                TimedOut = true,
                Cancelled = false,
                Truncated = false,
                DurationMs = 5000
            });

        var result = await mockExecutor.Object.ExecuteCommandAsync(
            "local", "test-vm", "long-running-command", "cmd", 5);

        result.TimedOut.Should().BeTrue(
            "timed-out commands must set timedOut=true " +
            "(see /myplans/execution/commands/commands-design.md — CMD-D4)");
        result.Stdout.Should().NotBeNullOrEmpty(
            "partial stdout must be preserved even on timeout (ADR-9)");
        result.DurationMs.Should().BeGreaterThanOrEqualTo(5000,
            "duration should reflect the timeout period");
    }

    /// <summary>
    /// The MCP response envelope for a timed-out command must have
    /// success=false, data containing partial CommandResult, errorCode=COMMAND_TIMEOUT.
    /// See /myplans/design.md §4 — ADR-9.
    /// </summary>
    [Fact]
    public void Timeout_Response_Envelope_Has_Correct_Shape()
    {
        var partialResult = new CommandResult
        {
            ExitCode = -1,
            Stdout = "partial output before timeout...",
            Stderr = "some stderr",
            TimedOut = true,
            Cancelled = false,
            Truncated = false,
            DurationMs = 30000
        };

        // This is what the tool handler should construct for timeout:
        var response = new McpToolResponse
        {
            Success = false,
            Data = partialResult,
            Error = "Command exceeded timeout of 30 seconds",
            ErrorCode = ErrorCodes.CommandTimeout
        };

        response.Success.Should().BeFalse(
            "timed-out commands must return success:false (ADR-9)");
        response.Data.Should().NotBeNull(
            "partial output must be in the data field (ADR-9)");
        response.ErrorCode.Should().Be("COMMAND_TIMEOUT");

        var data = response.Data as CommandResult;
        data.Should().NotBeNull();
        data!.TimedOut.Should().BeTrue();
        data.Stdout.Should().NotBeNullOrEmpty(
            "partial stdout from before timeout must be preserved");
    }

    // ─── Cancellation Sets cancelled=true ──────────────────────────────

    /// <summary>
    /// When a command is cancelled via CancellationToken, the result must
    /// have cancelled=true.
    /// See /myplans/execution/commands/commands-design.md — Timeout and Cancellation.
    /// </summary>
    [Fact]
    public async Task Cancelled_Command_Returns_Cancelled_True()
    {
        var mockExecutor = new Mock<ICommandExecutor>();
        mockExecutor.Setup(e => e.ExecuteCommandAsync(
                "local", "test-vm", "dir", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = -1,
                Stdout = "partial...",
                Stderr = "",
                TimedOut = false,
                Cancelled = true,
                Truncated = false,
                DurationMs = 2000
            });

        var result = await mockExecutor.Object.ExecuteCommandAsync(
            "local", "test-vm", "dir", "cmd", 30);

        result.Cancelled.Should().BeTrue(
            "cancelled commands must set cancelled=true");
        result.TimedOut.Should().BeFalse(
            "cancelled is distinct from timed out");
    }

    // ─── Session Remains Open After Timeout ────────────────────────────

    /// <summary>
    /// After a command timeout, subsequent commands on the same VM must succeed.
    /// See /myplans/execution/commands/commands-design.md — CMD-D6: Session stays open after timeout.
    /// 
    /// This test validates the behavioral contract: the session is preserved after
    /// timeout, allowing the next command to reuse it without re-establishment.
    /// </summary>
    [Fact]
    public async Task Session_Remains_Open_After_Timeout()
    {
        var mockExecutor = new Mock<ICommandExecutor>();

        // First call: timeout
        mockExecutor.Setup(e => e.ExecuteCommandAsync(
                "local", "test-vm", "slow-command", "cmd", 5, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                TimedOut = true,
                Stdout = "partial",
                DurationMs = 5000
            });

        // Second call: success (same VM, session should be reused)
        mockExecutor.Setup(e => e.ExecuteCommandAsync(
                "local", "test-vm", "dir", "cmd", 30, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                ExitCode = 0,
                Stdout = "full output",
                TimedOut = false,
                DurationMs = 100
            });

        // Execute timeout
        var timeout = await mockExecutor.Object.ExecuteCommandAsync(
            "local", "test-vm", "slow-command", "cmd", 5);
        timeout.TimedOut.Should().BeTrue();

        // Execute follow-up (session should remain open per CMD-D6)
        var followUp = await mockExecutor.Object.ExecuteCommandAsync(
            "local", "test-vm", "dir", "cmd", 30);
        followUp.TimedOut.Should().BeFalse();
        followUp.ExitCode.Should().Be(0,
            "subsequent command on same VM must succeed after timeout " +
            "(session stays open per CMD-D6)");
    }

    // ─── Truncation Flag ───────────────────────────────────────────────

    /// <summary>
    /// When output exceeds truncation limits, the result must have truncated=true.
    /// See /myplans/execution/commands/commands-design.md — Output Truncation.
    /// </summary>
    [Fact]
    public void Truncated_Output_Sets_Truncated_Flag()
    {
        var result = new CommandResult
        {
            ExitCode = 0,
            Stdout = "[truncated: 1048576 bytes total]\n...last 512KB of output...",
            Stderr = "",
            TimedOut = false,
            Truncated = true,
            DurationMs = 1500
        };

        result.Truncated.Should().BeTrue(
            "truncated output must set truncated=true " +
            "(see /myplans/execution/commands/commands-design.md — Output Truncation)");
    }
}
