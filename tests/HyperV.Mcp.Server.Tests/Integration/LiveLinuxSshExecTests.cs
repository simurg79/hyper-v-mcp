using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #209 / LGS-SSH-D1 acceptance criterion 7: a live Tier-2 test that runs a single real
/// SSH-exec command against an already-reachable, already-running Ubuntu 24.04 guest through the
/// real <see cref="SshSessionStore"/> + <see cref="SshGuestChannel"/> stack (no mocks, no fakes).
///
/// <para>Mirrors the <see cref="LiveEndToEndTests"/> posture: <c>[Trait("Category","LiveE2E")]</c>,
/// env-gated, and <b>skip-clean</b> — because xUnit v2 has no runtime skip, a test whose
/// prerequisites are absent <b>reports as passed</b> after emitting a <c>SKIP:</c> log line; it
/// never throws on a machine that lacks SSH env configuration.</para>
///
/// <para>Enable by setting <c>HYPERV_MCP_RUN_REAL_PS=1</c> plus the guest SSH coordinates:
/// <c>HYPERV_MCP_SSH_HOST</c>, <c>HYPERV_MCP_SSH_USER</c>, <c>HYPERV_MCP_SSH_PASSWORD</c> (and the
/// optional <c>HYPERV_MCP_SSH_PORT</c>, default 22). Run via
/// <c>dotnet test --filter "Category=LiveE2E"</c>.</para>
/// </summary>
[Trait("Category", "LiveE2E")]
public class LiveLinuxSshExecTests
{
    private readonly ITestOutputHelper _output;

    public LiveLinuxSshExecTests(ITestOutputHelper output) => _output = output;

    private const string HostId = "live-linux";
    private const string VmId = "live-linux-guest";

    /// <summary>
    /// True only when the live SSH gate and all required SSH coordinates are present. Populates the
    /// out-params so a skipped run can log exactly which coordinate was missing.
    /// </summary>
    private static bool CanRunLiveSshTest(
        out string? host, out string? user, out string? password, out int port)
    {
        host = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_HOST");
        user = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_USER");
        password = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_PASSWORD");
        var portRaw = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_PORT");
        port = int.TryParse(portRaw, out var parsed) ? parsed : 22;

        var gateEnabled = string.Equals(
            Environment.GetEnvironmentVariable("HYPERV_MCP_RUN_REAL_PS"), "1", StringComparison.Ordinal);

        return gateEnabled
            && !string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(user)
            && !string.IsNullOrWhiteSpace(password);
    }

    [Fact]
    public async Task LiveSshExec_EchoCommand_ReturnsWellFormedCommandResult()
    {
        if (!CanRunLiveSshTest(out var host, out var user, out var password, out var port))
        {
            // Skip-clean: report passed, emit a SKIP: line naming the absent gate/coordinates.
            _output.WriteLine(
                "SKIP: LiveLinuxSshExecTests requires HYPERV_MCP_RUN_REAL_PS=1 plus " +
                "HYPERV_MCP_SSH_HOST / HYPERV_MCP_SSH_USER / HYPERV_MCP_SSH_PASSWORD. " +
                "Prerequisites not met — not executed.");
            return;
        }

        _output.WriteLine($"LiveLinuxSshExec: connecting to {host}:{port} as {user}.");

        var profile = new HostProfile
        {
            HostId = HostId,
            ComputerName = host!,
            GuestOs = "linux",
            SshHost = host,
            SshPort = port,
        };
        var resolver = new SingleProfileHostResolver(profile);

        var store = new SshSessionStore(
            new SshExecClientFactory(),
            resolver,
            new GuestRoutingHintStore(),
            NullLogger<SshSessionStore>.Instance);
        try
        {
            var channel = new SshGuestChannel(store, NullLogger<SshGuestChannel>.Instance);

            const string marker = "hyperv-mcp-live-ssh-209";
            var args = new Dictionary<string, object?>
            {
                ["cmd"] = $"echo {marker}",
                ["sh"] = "default",
            };

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hostResult = await channel.InvokeScriptAsync(
                HostId, VmId, user!, password!, script: "ignored", args: args, ct: cts.Token);

            hostResult.Success.Should().BeTrue("a successful echo must yield exit 0.");
            hostResult.ExitCode.Should().Be(0);

            var envelopeJson = (string)hostResult.Output.Single()!;
            using var envelope = JsonDocument.Parse(envelopeJson);
            var root = envelope.RootElement;
            root.GetProperty("Stdout").GetString().Should().Contain(marker,
                "the guest must echo the marker back over the SSH exec channel.");
            root.GetProperty("ExitCode").GetInt32().Should().Be(0);
            envelopeJson.Should().NotContain(password!, "the password must never leak into the envelope.");

            _output.WriteLine("LiveLinuxSshExec: real SSH exec succeeded.");
        }
        finally
        {
            await EvictAndDispose(store);
        }
    }

    private static async Task EvictAndDispose(SshSessionStore store)
    {
        try { await store.EvictAsync(HostId, VmId); } catch { /* best-effort teardown */ }
        store.Dispose();
    }

    /// <summary>
    /// Minimal <see cref="IHostResolver"/> that always returns a single configured profile — enough
    /// for the SSH store to resolve the guest endpoint in the live test without the full DI stack.
    /// </summary>
    private sealed class SingleProfileHostResolver : IHostResolver
    {
        private readonly HostProfile _profile;
        public SingleProfileHostResolver(HostProfile profile) => _profile = profile;
        public HostProfile? Resolve(string? hostId) => _profile;
        public HostProfile ResolveRequired(string? hostId) => _profile;
    }
}
