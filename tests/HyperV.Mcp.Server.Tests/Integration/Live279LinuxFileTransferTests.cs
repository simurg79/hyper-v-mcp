using System.Security.Cryptography;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Integration;

/// <summary>
/// Issue #279: moves a real file both directions against a live Linux guest through the real
/// <see cref="SshSessionStore"/> + <see cref="SshGuestChannel"/> + <see cref="FileTransferService"/>
/// stack — no fakes anywhere in the path.
///
/// <para>Correctness is judged by SHA-256 equality of the bytes at each end plus an in-guest
/// listing of the delivered file, never by a successful-looking envelope.</para>
///
/// <para>Env-gated and skip-clean, matching <see cref="LiveLinuxSshExecTests"/>.</para>
/// </summary>
[Trait("Category", "LiveE2E")]
public class Live279LinuxFileTransferTests
{
    private const string HostId = "live-279";
    private const string VmId = "27927927-0279-4279-8279-279279279279";

    private readonly ITestOutputHelper _output;
    public Live279LinuxFileTransferTests(ITestOutputHelper output) => _output = output;

    private static bool CanRun(
        out string? host, out string? user, out string? password, out int port)
    {
        host = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_HOST");
        user = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_USER");
        password = Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_PASSWORD");
        port = int.TryParse(Environment.GetEnvironmentVariable("HYPERV_MCP_SSH_PORT"), out var parsed)
            ? parsed : 22;

        return Environment.GetEnvironmentVariable("HYPERV_MCP_RUN_REAL_PS") == "1"
            && !string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(user)
            && !string.IsNullOrWhiteSpace(password);
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Fact]
    public async Task RealFile_MovesBothDirections_VerifiedByHashAndInGuestListing()
    {
        if (!CanRun(out var host, out var user, out var password, out var port))
        {
            _output.WriteLine(
                "SKIP: Live279LinuxFileTransferTests requires HYPERV_MCP_RUN_REAL_PS=1 plus " +
                "HYPERV_MCP_SSH_HOST / HYPERV_MCP_SSH_USER / HYPERV_MCP_SSH_PASSWORD. " +
                "Prerequisites not met — not executed.");
            return;
        }

        // ComputerName is the Hyper-V HOST (local); only the guest is reached over SSH, so the
        // SSH coordinates live on SshHost/SshPort.
        var profile = new HostProfile
        {
            HostId = HostId,
            ComputerName = "localhost",
            GuestOs = "linux",
            SshHost = host,
            SshPort = port,
        };

        var store = new SshSessionStore(
            new SshExecClientFactory(),
            new SingleProfileHostResolver(profile),
            new GuestRoutingHintStore(),
            NullLogger<SshSessionStore>.Instance);

        var localSource = Path.Combine(Path.GetTempPath(), $"issue279_src_{Guid.NewGuid():N}.bin");
        var localRoundTrip = Path.Combine(Path.GetTempPath(), $"issue279_rt_{Guid.NewGuid():N}.bin");
        var guestPath = $"/tmp/issue279_{Guid.NewGuid():N}.bin";

        try
        {
            var channel = new SshGuestChannel(store, NullLogger<SshGuestChannel>.Instance);
            var service = new FileTransferService(
                channel, new SingleProfileHostResolver(profile),
                NullLogger<FileTransferService>.Instance);

            var payload = RandomNumberGenerator.GetBytes(512 * 1024);
            await File.WriteAllBytesAsync(localSource, payload);
            var sourceHash = Sha256Of(localSource);
            _output.WriteLine($"host source: {payload.Length} bytes, sha256={sourceHash}");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            // ── to guest ──────────────────────────────────────────────────
            var toGuest = await service.CopyToGuestAsync(
                HostId, VmId, localSource, guestPath,
                isDirectory: false, username: user, password: password, ct: cts.Token);

            toGuest.BytesTransferred.Should().Be(payload.Length,
                "the guest-observed size must equal the source length");

            // In-guest listing: the guest itself reports the delivered file and its own hash.
            var listing = await channel.InvokeScriptAsync(
                HostId, VmId, user!, password!, script: "ignored",
                args: new Dictionary<string, object?>
                {
                    ["cmd"] = $"ls -l {guestPath} && sha256sum {guestPath}",
                    ["sh"] = "default",
                },
                ct: cts.Token);

            var listingJson = (string)listing.Output.Single()!;
            _output.WriteLine($"in-guest listing: {listingJson}");
            listingJson.Should().Contain(guestPath, "the guest must list the delivered file");
            listingJson.Should().Contain(sourceHash.ToLowerInvariant(),
                "the guest's own sha256sum must match the host source hash");

            // ── from guest ────────────────────────────────────────────────
            var fromGuest = await service.CopyFromGuestAsync(
                HostId, VmId, guestPath, localRoundTrip,
                username: user, password: password, ct: cts.Token);

            fromGuest.BytesTransferred.Should().Be(payload.Length);
            Sha256Of(localRoundTrip).Should().Be(sourceHash,
                "the round-tripped bytes must be identical to the original");

            _output.WriteLine("Live279: real file moved both directions, hashes match.");
        }
        finally
        {
            try
            {
                await store.EvictAsync(HostId, VmId);
            }
            catch { /* best-effort teardown */ }
            store.Dispose();
            try { if (File.Exists(localSource)) File.Delete(localSource); } catch { }
            try { if (File.Exists(localRoundTrip)) File.Delete(localRoundTrip); } catch { }
        }
    }

    private sealed class SingleProfileHostResolver : IHostResolver
    {
        private readonly HostProfile _profile;
        public SingleProfileHostResolver(HostProfile profile) => _profile = profile;
        public HostProfile? Resolve(string? hostId) => _profile;
        public HostProfile ResolveRequired(string? hostId) => _profile;
    }
}
