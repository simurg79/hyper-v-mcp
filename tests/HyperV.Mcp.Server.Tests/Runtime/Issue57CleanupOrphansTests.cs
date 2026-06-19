using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Regression tests for Issue #57 — vm_cleanup_orphans returned a generic
/// SDK invocation error instead of a structured envelope. Fix in
/// <c>ToolDispatcher.HandleCleanupOrphansAsync</c>:
/// <list type="bullet">
///   <item>strict-bool <c>dryRun</c> ⇒ <c>ArgumentException("dryRun")</c> ⇒ INVALID_PARAMETER</item>
///   <item>missing <c>hostId</c> + no <c>DefaultHostId</c> ⇒ <c>ArgumentException("hostId")</c> ⇒ INVALID_PARAMETER</item>
///   <item>arbitrary manager exception ⇒ bare rethrow ⇒ <c>ErrorMapper</c> generic
///         sanitization arm ⇒ INTERNAL_ERROR with a redacted "An internal error occurred."
///         message (raw inner type/message logged to stderr but not leaked to clients)</item>
/// </list>
/// Mirrors the style of <see cref="ReviewFeedbackRegressionTests"/>.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue57CleanupOrphansTests
{
    private static ToolDispatcher BuildDispatcher(
        Mock<IHyperVManager> hvManager,
        ServerOptions options)
    {
        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        return new ToolDispatcher(
            hvManager.Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new Mock<IHostResolver>().Object,
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);
    }

    /// <summary>
    /// Issue #57 acceptance criterion: invoking vm_cleanup_orphans with default/empty
    /// args must produce a structured envelope (not the bare SDK
    /// "An error occurred invoking 'vm_cleanup_orphans'" message). With a default host
    /// configured and the manager returning an empty list, this is success: true.
    /// </summary>
    [Fact]
    public async Task Issue57_DefaultArgs_Returns_Structured_Envelope_Not_Bare_Invocation_Error()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var hvManager = new Mock<IHyperVManager>();
        hvManager
            .Setup(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VmInfo>().AsReadOnly());

        var dispatcher = BuildDispatcher(hvManager, options);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response.Should().NotBeNull("dispatcher must always emit a structured envelope (Issue #57).");
        response!.Success.Should().BeTrue(
            "with empty args and a configured DefaultHostId + no orphans, the call should succeed.");

        // Forbid the regressed bare SDK message.
        var envelope = json ?? string.Empty;
        envelope.Should().NotContain(
            "An error occurred invoking 'vm_cleanup_orphans'",
            "Issue #57: must NOT surface the generic SDK invocation error.");

        // dryRun should default to true and the manager must be called with it.
        hvManager.Verify(
            m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()),
            Times.Once,
            "default dryRun is true.");
    }

    /// <summary>
    /// Issue #57: when neither <c>hostId</c> arg nor <c>DefaultHostId</c> is set, the
    /// handler throws <c>ArgumentException("hostId")</c> which the dispatcher's outer
    /// catch + ErrorMapper map to INVALID_PARAMETER — NOT a generic invocation error.
    /// </summary>
    [Fact]
    public async Task Issue57_DefaultArgs_NoDefaultHost_Returns_InvalidParameter_HostId()
    {
        var options = new ServerOptions { DefaultHostId = string.Empty };
        var hvManager = new Mock<IHyperVManager>(MockBehavior.Strict);
        var dispatcher = BuildDispatcher(hvManager, options);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "missing hostId with no DefaultHostId must surface INVALID_PARAMETER (Issue #57).");
        response.Error.Should().Contain("hostId",
            "the parameter name must be named in the message so the caller can fix it.");

        hvManager.Verify(
            m => m.CleanupOrphansAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "manager must not be reached when validation fails.");
    }

    /// <summary>
    /// Issue #57: a non-boolean <c>dryRun</c> (e.g. string "yes") must produce a
    /// structured INVALID_PARAMETER envelope naming the parameter — replicating the
    /// #56-cure pattern via <c>GetStrictBoolArg</c>.
    /// </summary>
    [Fact]
    public async Task Issue57_BadType_DryRun_Returns_InvalidParameter()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var hvManager = new Mock<IHyperVManager>(MockBehavior.Strict);
        var dispatcher = BuildDispatcher(hvManager, options);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?> { ["dryRun"] = "yes" },
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse(
            "non-boolean dryRun must produce a structured failure (Issue #57).");
        response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter,
            "GetStrictBoolArg throws ArgumentException(\"dryRun\") which maps to INVALID_PARAMETER.");
        response.Error.Should().Contain("dryRun",
            "INVALID_PARAMETER messages must name the offending parameter.");

        hvManager.Verify(
            m => m.CleanupOrphansAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "manager must not be invoked when argument parsing fails.");
    }

    /// <summary>
    /// Issue #57 (Gate 6 round 2 cure): when the underlying manager throws an
    /// arbitrary, non-mapped exception (e.g. a raw <see cref="Exception"/>),
    /// the handler must route it through <c>ErrorMapper</c>'s sanitized
    /// generic arm (ErrorMapper.cs:356) instead of rewrapping it as
    /// <c>InvalidOperationException</c> carrying the raw type+message.
    ///
    /// Post-fix invariant asserted here:
    ///   - Response is a structured failure envelope (not a bare SDK string).
    ///   - <c>ErrorCode</c> is <see cref="RuntimeErrorCodes.InternalError"/>
    ///     (ErrorMapper.cs:360).
    ///   - <c>Error</c> message is non-empty and human-readable, but
    ///     <b>sanitized</b> — it must NOT contain the raw inner exception
    ///     type name (<c>System.Exception</c>) or its raw message
    ///     (<c>boom-from-manager</c>).
    ///
    /// Note: the catch arm in <c>HandleCleanupOrphansAsync</c> also writes the
    /// raw exception (type + message + stack) to <c>Console.Error</c> at error
    /// severity for operator-side root-cause preservation. The dispatcher does
    /// not currently take an injectable <c>ILogger&lt;T&gt;</c>, so that
    /// stderr-log behavior is verified in the integration harness, not here.
    /// </summary>
    [Fact]
    public async Task Issue57_UnderlyingManager_Throws_Surfaces_Sanitized_InternalError_Envelope()
    {
        var options = new ServerOptions { DefaultHostId = "local" };
        var hvManager = new Mock<IHyperVManager>();
        hvManager
            .Setup(m => m.CleanupOrphansAsync("local", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom-from-manager"));

        var dispatcher = BuildDispatcher(hvManager, options);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);

        // Structured failure envelope (not a bare SDK invocation string).
        response!.Success.Should().BeFalse(
            "non-mapped manager exceptions must produce a structured failure response.");

        // Populated errorCode from ErrorMapper's generic sanitization arm
        // (ErrorMapper.cs ~line 356/360 → RuntimeErrorCodes.InternalError).
        response.ErrorCode.Should().Be(RuntimeErrorCodes.InternalError,
            "raw System.Exception falls into ErrorMapper's catch-all arm which " +
            "produces INTERNAL_ERROR with a sanitized message (ErrorMapper.cs:356).");

        // Human-readable, non-empty sanitized message.
        response.Error.Should().NotBeNullOrWhiteSpace(
            "the sanitized arm must still emit a human-readable message.");

        // Sanitization invariant: the raw inner-exception payload must NOT leak
        // through to the client envelope.
        response.Error.Should().NotContain("boom-from-manager",
            "ErrorMapper's generic arm must redact raw exception messages " +
            "(Gate 6 round 2 finding 2).");
        response.Error.Should().NotContain("System.Exception",
            "raw exception type names must not be forwarded to the client.");

        // Forbid the regressed bare SDK message — the structured envelope
        // is what callers see.
        (json ?? string.Empty).Should().NotContain(
            "An error occurred invoking 'vm_cleanup_orphans'");

        // And forbid the raw payload anywhere in the JSON (defense in depth).
        (json ?? string.Empty).Should().NotContain("boom-from-manager",
            "the raw manager-side message must not appear anywhere in the wire envelope.");
    }

    /// <summary>
    /// Issue #57: <see cref="HostNotFoundException"/> from the manager must keep its
    /// dedicated mapping (HOST_NOT_FOUND) — proving the typed catch chain in
    /// <c>HandleCleanupOrphansAsync</c> propagates well-mapped types instead of
    /// rewrapping them.
    /// </summary>
    [Fact]
    public async Task Issue57_HostNotFoundException_Propagates_To_HostNotFound()
    {
        var options = new ServerOptions { DefaultHostId = "ghost-host" };
        var hvManager = new Mock<IHyperVManager>();
        hvManager
            .Setup(m => m.CleanupOrphansAsync("ghost-host", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HostNotFoundException("ghost-host"));

        var dispatcher = BuildDispatcher(hvManager, options);

        var json = await dispatcher.DispatchAsync(
            "vm_cleanup_orphans",
            new Dictionary<string, object?>(),
            CancellationToken.None);

        var response = JsonSerializer.Deserialize<McpToolResponse>(json);
        response!.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.HostNotFound,
            "HostNotFoundException must keep its dedicated mapping; the typed catch chain must NOT rewrap it.");
    }
}
