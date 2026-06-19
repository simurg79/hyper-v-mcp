using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #170 / VC-D12: tests for <see cref="ToolDispatcher.ResolveVmCreateTimeoutSeconds"/>.
///
/// Contract:
/// - Default value is <c>120</c> when <c>HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS</c> is unset.
/// - Honors valid integer overrides within the inclusive range <c>60..600</c>.
/// - Clamps/rejects out-of-range or non-integer values back to the 120s default
///   (the helper logs a warning and returns the default — the test asserts the
///   numeric fallback, which is the observable behavior the caller depends on).
///
/// Lives in the <c>EnvVarMutating</c> collection because the helper reads
/// <see cref="Environment.GetEnvironmentVariable(string)"/> directly.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public sealed class Issue170VmCreateTimeoutTests : IDisposable
{
    private const string EnvVar = "HYPERV_MCP_VM_CREATE_TIMEOUT_SECONDS";
    private readonly string? _original;

    public Issue170VmCreateTimeoutTests()
    {
        _original = Environment.GetEnvironmentVariable(EnvVar);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _original);
    }

    private static ToolDispatcher BuildDispatcher()
    {
        var options = new ServerOptions
        {
            DefaultHostId = "local",
            Hosts = new Dictionary<string, HostProfile>
            {
                ["local"] = new HostProfile
                {
                    HostId = "local",
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                },
            },
        };

        var gate = new Mock<IConcurrencyGate>();
        gate.Setup(g => g.AcquireGlobalSlotAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireHostLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        gate.Setup(g => g.AcquireVmLockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        return new ToolDispatcher(
            new Mock<IHyperVManager>().Object,
            new Mock<ICommandExecutor>().Object,
            new Mock<IFileTransferService>().Object,
            new Mock<ICheckpointManager>().Object,
            new HostResolver(options),
            new ErrorMapper(),
            gate.Object,
            new Mock<IPowerShellExecutor>().Object,
            new Mock<IPowerShellDirectChannel>().Object,
            options);
    }

    [Fact]
    public void DefaultTimeout_Is120Seconds_OverridableViaEnvVar()
    {
        var dispatcher = BuildDispatcher();

        // ── Default (env var unset) ──────────────────────────────────────
        Environment.SetEnvironmentVariable(EnvVar, null);
        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(120, "VC-D12 default per Phase A design");

        // ── Whitespace / empty → default ─────────────────────────────────
        Environment.SetEnvironmentVariable(EnvVar, "   ");
        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(120, "whitespace must be treated as unset");

        // ── Valid in-range overrides ─────────────────────────────────────
        Environment.SetEnvironmentVariable(EnvVar, "60");
        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(60, "lower inclusive bound must be honored");

        Environment.SetEnvironmentVariable(EnvVar, "300");
        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(300, "mid-range override must be honored");

        Environment.SetEnvironmentVariable(EnvVar, "600");
        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(600, "upper inclusive bound must be honored");
    }

    [Theory]
    [InlineData("59")]    // just below floor
    [InlineData("0")]     // zero
    [InlineData("-1")]    // negative
    [InlineData("601")]   // just above ceiling
    [InlineData("100000")] // wildly out of range
    public void OutOfRangeValues_FallBackToDefault(string raw)
    {
        var dispatcher = BuildDispatcher();
        Environment.SetEnvironmentVariable(EnvVar, raw);

        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(120,
                "VC-D12: values outside 60..600 must clamp to the default with a LogWarning");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("120s")]
    [InlineData("1.5")]
    [InlineData("12 0")]
    public void NonIntegerValues_FallBackToDefault(string raw)
    {
        var dispatcher = BuildDispatcher();
        Environment.SetEnvironmentVariable(EnvVar, raw);

        dispatcher.ResolveVmCreateTimeoutSeconds()
            .Should().Be(120,
                "VC-D12: unparseable values must fall back to the default with a LogWarning");
    }
}
