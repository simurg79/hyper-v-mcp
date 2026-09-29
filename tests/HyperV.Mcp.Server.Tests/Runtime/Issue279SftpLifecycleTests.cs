using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #279: lifecycle of the SECOND connection SFTP opens.
///
/// <para>A live spike established that SSH.NET's SftpClient authenticates independently of the
/// SshClient, so a transfer owns a connection the exec path does not — and every exit from that
/// connection (failure, cancellation, replacement, eviction, store disposal) must close it.</para>
/// </summary>
[Trait("Category", "Runtime")]
public class Issue279SftpLifecycleTests
{
    // TEST-NET-1 (RFC 5737) is non-routable, so a blocking connect stays in flight long enough for
    // the token race to win deterministically without a live server.
    private const string UnreachableHost = "192.0.2.1";
    private const string HostId = "local";
    private const string VmId = "27927927-0279-4279-8279-279279279279";

    [Fact]
    public async Task SftpConnectFailure_DoesNotHang_AndReportsASessionFault_NotATransferFault()
    {
        // Drives the PRODUCTION SFTP adapter against an unroutable address, so the blocking
        // connect is real. The previous version called a fake's UploadAsync and stayed green even
        // with the production connection handling entirely broken.
        using var client = ProductionClientAgainstUnreachableHost();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var attempt = Record.ExceptionAsync(() =>
            client.UploadAsync(GetType().Assembly.Location, "/tmp/x", cts.Token));
        var completed = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(30)));

        completed.Should().Be(attempt,
            "a cancelled or failed SFTP connect must release the gate promptly, never hang");
        var thrown = await attempt;
        thrown.Should().NotBeNull("an unreachable guest must never look like a delivered file");

        if (thrown is not OperationCanceledException)
        {
            // A connect that FAILED (rather than being cancelled) is a session fault: the bytes
            // never reached a transfer, so the caller must reopen the session, not inspect a file.
            new ErrorMapper().MapException(thrown!).ErrorCode
                .Should().Be(ErrorCodes.SessionFailed);
        }
    }

    /// <summary>
    /// The real SSH.NET-backed client, bound to a non-routable address so its SFTP connect blocks
    /// exactly as it would against an unreachable guest. Built through the production factory's
    /// own adapter rather than a stand-in.
    /// </summary>
    private static ISshExecClient ProductionClientAgainstUnreachableHost()
    {
        var connectionInfo = new Renci.SshNet.ConnectionInfo(
            UnreachableHost, 22, "u",
            new Renci.SshNet.PasswordAuthenticationMethod("u", "p"));
        return SshExecClientFactory.CreateClientForTesting(connectionInfo);
    }

    [Fact]
    public async Task CommandOnATornDownConnection_IsReportedAsASessionFault()
    {
        // Exercises the ADAPTER's own translation, which no other test can reach: the seam-level
        // tests inject an already-typed failure, so they stay green even if this mapping is gone.
        // A disposed SSH.NET client is how a dropped connection actually surfaces here.
        var client = ProductionClientAgainstUnreachableHost();
        client.Dispose();

        var thrown = await Record.ExceptionAsync(() => client.ExecuteAsync("stat -c %s /tmp/x"));

        thrown.Should().BeOfType<GuestConnectionLostException>(
            "a command against a dead connection tells the caller to reopen the session, not that " +
            "the command itself misbehaved");
        new ErrorMapper().MapException(thrown!).ErrorCode.Should().Be(ErrorCodes.SessionFailed);
    }

    [Fact]
    public async Task EvictingASession_DisposesTheClient_SoItsSftpConnectionCannotSurvive()
    {
        var factory = new RecordingFactory();
        using var store = BuildStore(factory);

        var client = (RecordingClient)await store.GetOrCreateAsync(HostId, VmId, "u", "p");
        await store.EvictAsync(HostId, VmId);

        client.Disposals.Should().Be(1, "eviction must close the transfer connection too");
    }

    [Fact]
    public async Task ReplacingADisconnectedSession_DisposesTheOldClient()
    {
        var factory = new RecordingFactory();
        using var store = BuildStore(factory);

        var first = (RecordingClient)await store.GetOrCreateAsync(HostId, VmId, "u", "p");
        first.Connected = false;

        var second = await store.GetOrCreateAsync(HostId, VmId, "u", "p");

        second.Should().NotBeSameAs(first);
        first.Disposals.Should().Be(1,
            "a replaced client's SFTP connection must not be left open behind the new one");
    }

    [Fact]
    public async Task DisposingTheStore_DisposesEveryCachedClient()
    {
        var factory = new RecordingFactory();
        var store = BuildStore(factory);

        var client = (RecordingClient)await store.GetOrCreateAsync(HostId, VmId, "u", "p");
        store.Dispose();

        client.Disposals.Should().Be(1);
    }

    private static SshSessionStore BuildStore(ISshExecClientFactory factory)
        => new(factory, new LoopbackHostResolver(), new GuestRoutingHintStore(),
            NullLogger<SshSessionStore>.Instance);

    private static async Task<ISshExecClient> ConnectedFakeAsync()
    {
        var factory = new RecordingFactory();
        return await factory.ConnectAsync(UnreachableHost, 22, "u", "p");
    }

    private sealed class LoopbackHostResolver : IHostResolver
    {
        private static readonly HostProfile Profile = new()
        {
            HostId = HostId,
            ComputerName = "localhost",
            SshHost = UnreachableHost,
            SshPort = 22,
        };

        public HostProfile? Resolve(string? hostId) => Profile;
        public HostProfile ResolveRequired(string? hostId) => Profile;
    }

    private sealed class RecordingFactory : ISshExecClientFactory
    {
        public Task<ISshExecClient> ConnectAsync(
            string host, int port, string username, string password, CancellationToken ct = default)
            => Task.FromResult<ISshExecClient>(new RecordingClient());
    }

    private sealed class RecordingClient : ISshExecClient
    {
        public int Disposals { get; private set; }
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;

        public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
            => Task.FromResult(new SshCommandResult(string.Empty, string.Empty, 0));

        public async Task UploadAsync(
            string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        {
            // Stands in for the blocking SFTP connect: it must observe the token rather than
            // continue while the caller has moved on.
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task DownloadAsync(
            string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Dispose() => Disposals++;
    }
}
