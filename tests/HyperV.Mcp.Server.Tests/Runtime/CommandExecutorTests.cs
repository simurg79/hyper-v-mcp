using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Unit tests for the rewritten <see cref="CommandExecutor"/> (issue #52, ST-4/ST-7).
/// The executor now goes through <see cref="IPowerShellDirectChannel"/> and parses
/// a PascalCase JSON envelope (<c>{ Stdout, Stderr, ExitCode, DurationMs }</c>) emitted
/// from the inner script.
///
/// Coverage:
/// - Success path returns parsed <see cref="CommandResult"/>
/// - Truncation logic for stdout/stderr (CMD-D3)
/// - Cancellation surfaces <c>Cancelled = true, ExitCode = -1</c> (CMD-D4)
/// - Local-only host enforcement
/// - vmId GUID validation, shell validation, argument validation
/// - Redaction is applied to structured stderr in <see cref="CommandExecutor.ParseJsonResult"/>
/// </summary>
[Trait("Category", "Runtime")]
public class CommandExecutorTests
{
    private readonly Mock<IPowerShellDirectChannel> _mockChannel;
    private readonly Mock<IHostResolver> _mockHostResolver;
    private readonly ILogger<CommandExecutor> _logger;
    private readonly CommandExecutor _commandExecutor;

    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";

    public CommandExecutorTests()
    {
        _mockChannel = new Mock<IPowerShellDirectChannel>();
        _mockHostResolver = new Mock<IHostResolver>();
        _logger = NullLoggerFactory.Instance.CreateLogger<CommandExecutor>();

        _mockHostResolver.Setup(r => r.ResolveRequired(It.IsAny<string>()))
            .Returns(new HostProfile { HostId = "local", ComputerName = "localhost" });

        _commandExecutor = new CommandExecutor(
            _mockChannel.Object, _mockHostResolver.Object, _logger);
    }

    /// <summary>
    /// Builds a <see cref="PowerShellHostResult"/> whose <c>Output</c> contains a
    /// single PascalCase JSON envelope string, mimicking what the in-guest script
    /// emits after wrapping by the channel.
    /// </summary>
    private static PowerShellHostResult SuccessResultWithJson(
        string stdout = "hello world",
        string stderr = "",
        int exitCode = 0,
        long durationMs = 100)
    {
        var json = $@"{{""Stdout"":""{EscapeJson(stdout)}"",""Stderr"":""{EscapeJson(stderr)}"",""ExitCode"":{exitCode},""DurationMs"":{durationMs}}}";
        return new PowerShellHostResult(
            Success: true,
            Output: new object?[] { json },
            Stderr: string.Empty,
            ExitCode: 0);
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private void SetupChannel(PowerShellHostResult result)
    {
        // CommandExecutor calls InvokeScriptWithTimeoutAsync (Issue #52, Gate 6 Fix #2).
        _mockChannel
            .Setup(c => c.InvokeScriptWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    // ─── Success path ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_ParsesPascalCaseJsonEnvelope()
    {
        SetupChannel(SuccessResultWithJson(stdout: "hello", exitCode: 0, durationMs: 150));

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "echo hello", "cmd",
            username: "testuser", password: "testpass");

        result.Stdout.Should().Be("hello");
        result.Stderr.Should().BeEmpty();
        result.ExitCode.Should().Be(0);
        result.DurationMs.Should().Be(150);
        result.TimedOut.Should().BeFalse();
        result.Cancelled.Should().BeFalse();
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteCommandAsync_NonZeroExitCode_ParsedCorrectly()
    {
        SetupChannel(SuccessResultWithJson(
            stdout: "", stderr: "command not found", exitCode: 1));

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "invalidcmd", "cmd",
            username: "testuser", password: "testpass");

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Be("command not found");
    }

    [Fact]
    public async Task ExecuteCommandAsync_PassesArgsThroughChannel()
    {
        IDictionary<string, object?>? capturedArgs = null;
        _mockChannel
            .Setup(c => c.InvokeScriptWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, IDictionary<string, object?>?, int, CancellationToken>(
                (_, _, _, _, _, args, _, _) => capturedArgs = args)
            .ReturnsAsync(SuccessResultWithJson());

        await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "dir", "cmd",
            username: "testuser", password: "testpass");

        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().ContainKey("cmd");
        capturedArgs.Should().ContainKey("sh");
        capturedArgs!["cmd"].Should().Be("dir");
        capturedArgs!["sh"].Should().Be("cmd");
    }

    // ─── Truncation ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_StdoutExceedsLimit_Truncated()
    {
        var bigStdout = new string('A', 600 * 1024);
        SetupChannel(SuccessResultWithJson(stdout: bigStdout));

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "cmd", "cmd",
            username: "testuser", password: "testpass");

        result.Truncated.Should().BeTrue();
        result.Stdout.Should().Contain("OUTPUT TRUNCATED");
    }

    [Fact]
    public async Task ExecuteCommandAsync_StderrExceedsLimit_Truncated()
    {
        var bigStderr = new string('E', 200 * 1024);
        SetupChannel(SuccessResultWithJson(stderr: bigStderr, exitCode: 1));

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "fail", "cmd",
            username: "testuser", password: "testpass");

        result.Truncated.Should().BeTrue();
        result.Stderr.Should().Contain("OUTPUT TRUNCATED");
    }

    [Fact]
    public void TruncateOutput_WithinLimit_ReturnsUnchanged()
    {
        CommandExecutor.TruncateOutput("hello world", 1024).Should().Be("hello world");
    }

    [Fact]
    public void TruncateOutput_ExceedsLimit_Truncates()
    {
        var output = new string('X', 2000);
        var result = CommandExecutor.TruncateOutput(output, 1024);
        result.Should().Contain("OUTPUT TRUNCATED");
        result.Length.Should().BeLessThan(output.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TruncateOutput_NullOrEmpty_ReturnsSame(string? input)
    {
        CommandExecutor.TruncateOutput(input!, 1024).Should().Be(input);
    }

    [Fact]
    public void IsOverLimit_WithinLimit_ReturnsFalse()
    {
        CommandExecutor.IsOverLimit("hello", 1024).Should().BeFalse();
    }

    [Fact]
    public void IsOverLimit_ExceedsLimit_ReturnsTrue()
    {
        CommandExecutor.IsOverLimit(new string('X', 2000), 1024).Should().BeTrue();
    }

    // ─── Cancellation (CMD-D4) ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_CancelledThroughChannel_ReturnsCancelledResult()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockChannel
            .Setup(c => c.InvokeScriptWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "cmd", "cmd",
            username: "testuser", password: "testpass", ct: cts.Token);

        result.Cancelled.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().BeEmpty();
    }

    // ─── Redaction ──────────────────────────────────────────────────────

    [Fact]
    public void ParseJsonResult_RedactsPasswordFromStructuredStderr()
    {
        const string password = "S3cretP@ss!";
        var hostResult = new PowerShellHostResult(
            Success: true,
            Output: new object?[]
            {
                $@"{{""Stdout"":"""",""Stderr"":""Failed with password {password}"",""ExitCode"":1,""DurationMs"":100}}"
            },
            Stderr: string.Empty,
            ExitCode: 0);

        var result = _commandExecutor.ParseJsonResult(hostResult, "local", "vm1", password);

        result.Stderr.Should().NotContain(password);
        result.Stderr.Should().Contain("***REDACTED***");
    }

    [Fact]
    public void ParseJsonResult_EmptyOutput_ReturnsRawHostStderr()
    {
        var hostResult = new PowerShellHostResult(
            Success: false,
            Output: Array.Empty<object?>(),
            Stderr: "channel-level failure",
            ExitCode: 1);

        var result = _commandExecutor.ParseJsonResult(hostResult, "local", "vm1");

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("channel-level failure");
        result.Stdout.Should().BeEmpty();
    }

    [Fact]
    public void ParseJsonResult_InvalidJson_FallsBackToRawOutput()
    {
        var hostResult = new PowerShellHostResult(
            Success: true,
            Output: new object?[] { "not valid json{{{" },
            Stderr: string.Empty,
            ExitCode: 0);

        var result = _commandExecutor.ParseJsonResult(hostResult, "local", "vm1");

        result.Stdout.Should().Contain("not valid json");
    }

    // ─── Local-only host enforcement ────────────────────────────────────

    [Fact]
    public async Task ExecuteCommandAsync_RemoteHost_ThrowsNotSupportedException()
    {
        _mockHostResolver.Setup(r => r.ResolveRequired("remote-host"))
            .Returns(new HostProfile { HostId = "remote-host", ComputerName = "remote.server.com" });

        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(
            "remote-host", TestVmId, "dir", "cmd",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Remote host*not supported*Phase 1*");
    }

    [Fact]
    public async Task ExecuteScriptAsync_RemoteHost_ThrowsNotSupportedException()
    {
        _mockHostResolver.Setup(r => r.ResolveRequired("remote-host"))
            .Returns(new HostProfile { HostId = "remote-host", ComputerName = "remote.server.com" });

        Func<Task> act = () => _commandExecutor.ExecuteScriptAsync(
            "remote-host", TestVmId, "Get-Process", "powershell",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Remote host*not supported*Phase 1*");
    }

    // ─── Argument / GUID / shell validation ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteCommandAsync_InvalidHostId_ThrowsArgumentException(string? hostId)
    {
        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(hostId!, TestVmId, "cmd");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteCommandAsync_InvalidVmId_ThrowsArgumentException(string? vmId)
    {
        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(TestHostId, vmId!, "cmd");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteCommandAsync_InvalidCommand_ThrowsArgumentException(string? command)
    {
        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(TestHostId, TestVmId, command!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteScriptAsync_InvalidScript_ThrowsArgumentException(string? script)
    {
        Func<Task> act = () => _commandExecutor.ExecuteScriptAsync(TestHostId, TestVmId, script!);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("'; Remove-Item -Recurse C:\\ -Force; '")]
    [InlineData("vm-name-123")]
    public async Task ExecuteCommandAsync_NonGuidVmId_ThrowsArgumentException(string vmId)
    {
        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(
            TestHostId, vmId, "dir", "cmd",
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*vmId must be a valid GUID*");
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("'; malicious-cmd; '")]
    [InlineData("zsh")]
    public async Task ExecuteCommandAsync_InvalidShell_ThrowsArgumentException(string shell)
    {
        Func<Task> act = () => _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "dir", shell,
            username: "testuser", password: "testpass");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid shell*Allowed values*");
    }

    // ─── EscapePowerShellString helper (still public on the type) ──────

    [Fact]
    public void EscapePowerShellString_DoublesSingleQuotes()
    {
        CommandExecutor.EscapePowerShellString("it's a test").Should().Be("it''s a test");
    }

    // ─── Issue #52, Gate 6 Fix #2 — timeout plumbing ─────────────────────

    /// <summary>
    /// CommandExecutor must forward the caller's <c>timeoutSeconds</c> verbatim to
    /// <see cref="IPowerShellDirectChannel.InvokeScriptWithTimeoutAsync"/>.
    /// </summary>
    [Fact]
    public async Task ExecuteCommandAsync_ForwardsTimeoutToChannel()
    {
        int observedTimeout = -1;
        _mockChannel
            .Setup(c => c.InvokeScriptWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, IDictionary<string, object?>?, int, CancellationToken>(
                (_, _, _, _, _, _, t, _) => observedTimeout = t)
            .ReturnsAsync(SuccessResultWithJson());

        await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "dir", "cmd",
            timeoutSeconds: 17,
            username: "testuser", password: "testpass");

        observedTimeout.Should().Be(17,
            "CommandExecutor must forward timeoutSeconds verbatim to the channel (Gate 6 Fix #2)");
    }

    /// <summary>
    /// When the channel surfaces a <see cref="TimeoutException"/> (host-enforced timeout),
    /// the executor must return a <see cref="CommandResult"/> with <c>TimedOut=true</c>
    /// — NOT propagate the exception, NOT report Cancelled. (Gate 6 Fix #2; CMD-D4.)
    /// </summary>
    [Fact]
    public async Task ExecuteCommandAsync_ChannelThrowsTimeout_ReturnsTimedOutResult()
    {
        _mockChannel
            .Setup(c => c.InvokeScriptWithTimeoutAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("PowerShell invocation exceeded the 1s timeout."));

        var result = await _commandExecutor.ExecuteCommandAsync(
            TestHostId, TestVmId, "sleep 60", "cmd",
            timeoutSeconds: 1,
            username: "testuser", password: "testpass");

        result.TimedOut.Should().BeTrue("timeout from channel must surface as TimedOut (CMD-D4)");
        result.Cancelled.Should().BeFalse("timeout is distinct from caller cancellation");
        result.ExitCode.Should().Be(-1);
        result.DurationMs.Should().BeGreaterThanOrEqualTo(1000,
            "DurationMs should reflect at least the timeout window");
    }
}
