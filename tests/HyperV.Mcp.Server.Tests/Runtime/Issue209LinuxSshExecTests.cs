using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Uses real SshGuestChannel, GuestChannelRouter and ErrorMapper with a fake SSH client; no server required. Covers result
/// envelopes, COMMAND_FAILED versus SESSION_FAILED, redaction, the five-method facade, per-key serialization and Linux/Windows routing.
/// StderrSpillHelperTests covers PGP/PEM redaction; LiveLinuxSshExecTests covers live Tier 2.</summary>
[Trait("Category", "Runtime")]
public class Issue209LinuxSshExecTests
{
    private const string HostId = "linux-host";
    private const string VmId = "linux-vm";
    private const string Username = "ubuntu";
    private const string Password = "Sup3rS3cret!UNIQUE-209";


    /// <summary>Scripted in-memory SSH client records commands/disposal and rejects execution after disposal to expose exec/evict races; no
    /// network.</summary>
    private sealed class FakeSshExecClient : ISshExecClient
    {
        private readonly SshCommandResult _result;
        public List<string> ExecutedCommands { get; } = new();
        public int DisposeCount { get; private set; }
        public bool Connected { get; set; } = true;

        public FakeSshExecClient(SshCommandResult result) => _result = result;

        public bool IsConnected => Connected;

        public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            if (DisposeCount > 0)
            {
                throw new ObjectDisposedException(
                    nameof(FakeSshExecClient),
                    "regression guard: a disposed SSH client must never be asked to execute.");
            }
            ExecutedCommands.Add(commandText);
            return Task.FromResult(_result);
        }

        // Transfer is not exercised by this exec-focused fixture. Throwing (rather than returning a completed task) keeps it from silently
        // standing in for a transfer that never moved bytes.
        public Task UploadAsync(string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
            => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

        public Task DownloadAsync(string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
            => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

        public void Dispose() => DisposeCount++;
    }

    /// <summary>A blocking delegate holds exec in flight to test serialization and exec/evict exclusion.</summary>
    private sealed class GateProbeExecClient : ISshExecClient
    {
        private readonly Func<SshCommandResult> _body;
        public int DisposeCount { get; private set; }
        public bool ExecutedAfterDispose { get; private set; }

        public GateProbeExecClient(Func<SshCommandResult> body) => _body = body;

        public bool IsConnected => true;

        public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            if (DisposeCount > 0) ExecutedAfterDispose = true;
            // Blocking probes need dedicated threads so pool starvation cannot mimic operation-gate contention.
            return Task.Factory.StartNew(_body, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public Task UploadAsync(string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
            => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

        public Task DownloadAsync(string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
            => throw new NotSupportedException("This exec-only fake does not perform SFTP transfers.");

        public void Dispose() => DisposeCount++;
    }

    /// <summary>Single-client fake mirrors store teardown: eviction disposes and forgets the shared client; opening can fail as
    /// scripted.</summary>
    private sealed class FakeSshSessionStore : ISshSessionStore
    {
        private ISshExecClient? _client;
        private readonly Exception? _openFailure;
        public int GetOrCreateCount;
        public int EvictCount;

        public FakeSshSessionStore(ISshExecClient? client, Exception? openFailure = null)
        {
            _client = client;
            _openFailure = openFailure;
        }

        public Task<ISshExecClient> GetOrCreateAsync(
            string hostId, string vmId, string username, string password, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetOrCreateCount);
            if (_openFailure is not null) throw _openFailure;
            return Task.FromResult(_client!);
        }

        public Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref EvictCount);
            _client?.Dispose();
            _client = null;
            return Task.CompletedTask;
        }
    }

    private static SshGuestChannel CreateChannel(ISshSessionStore store) =>
        new(store, NullLogger<SshGuestChannel>.Instance);

    private static Dictionary<string, object?> CommandArgs(string command, string shell = "default") =>
        new() { ["cmd"] = command, ["sh"] = shell };


    [Fact]
    public async Task AC1_AC2_SuccessfulExec_ProducesWellFormedCommandResult_WithStdoutStderrExit()
    {
        var client = new FakeSshExecClient(new SshCommandResult("hello world", "warn line", 0));
        var channel = CreateChannel(new FakeSshSessionStore(client));

        var hostResult = await channel.InvokeScriptAsync(
            HostId, VmId, Username, Password, script: "ignored-ps-wrapper",
            args: CommandArgs("echo hello world"));

        hostResult.Success.Should().BeTrue("SSH exit status 0 must map to a successful host result.");
        hostResult.ExitCode.Should().Be(0);

        var executorResult = ParseThroughExecutor(hostResult);
        executorResult.ExitCode.Should().Be(0);
        executorResult.Stdout.Should().Be("hello world");
        executorResult.Stderr.Should().Be("warn line");
        executorResult.TimedOut.Should().BeFalse();
        executorResult.Cancelled.Should().BeFalse();

        using var envelope = JsonDocument.Parse((string)hostResult.Output.Single()!);
        var root = envelope.RootElement;
        root.TryGetProperty("Stdout", out _).Should().BeTrue();
        root.TryGetProperty("Stderr", out _).Should().BeTrue();
        root.TryGetProperty("ExitCode", out _).Should().BeTrue();
        root.TryGetProperty("DurationMs", out _).Should().BeTrue();
    }

    [Fact]
    public void AC1_FullMcpEnvelope_Has_Unchanged_TopLevel_Keys()
    {
        // Caller-visible envelope keys are transport-independent; replacing the channel must not change them.
        var response = McpToolResponse.Ok(new { Stdout = "out", ExitCode = 0 });
        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.TryGetProperty("success", out _).Should().BeTrue();
        root.TryGetProperty("error", out _).Should().BeTrue();
        root.TryGetProperty("errorCode", out _).Should().BeTrue();
        root.TryGetProperty("data", out _).Should().BeTrue();
        root.TryGetProperty("state", out _).Should().BeTrue();
    }


    [Fact]
    public async Task AC2_NonZeroExit_YieldsNormalResult_NotSessionFailure()
    {
        var client = new FakeSshExecClient(new SshCommandResult("partial", "boom: exit 3", 3));
        var channel = CreateChannel(new FakeSshSessionStore(client));

        // A non-zero remote exit must NOT throw SshSessionOpenException — it is a normal result.
        var hostResult = await channel.InvokeScriptAsync(
            HostId, VmId, Username, Password, "ignored", CommandArgs("false"));

        hostResult.Success.Should().BeFalse("non-zero SSH exit must surface as an unsuccessful result.");
        hostResult.ExitCode.Should().Be(3);

        var executorResult = ParseThroughExecutor(hostResult);
        executorResult.ExitCode.Should().Be(3,
            "AC2: a non-zero remote exit flows through as the command's exit code (COMMAND_FAILED " +
            "path), not a session failure.");
        executorResult.TimedOut.Should().BeFalse();
        executorResult.Cancelled.Should().BeFalse();
        executorResult.Stderr.Should().Contain("boom: exit 3");
    }

    [Fact]
    public void AC2_ErrorMapper_DoesNot_Classify_NonSessionFailures_As_SessionFailed()
    {
        // The only SSH exception the mapper treats as SESSION_FAILED is SshSessionOpenException. A generic channel failure (non-open) must
        // not be mislabeled a session failure.
        var mapper = new ErrorMapper();
        var response = mapper.MapException(
            new PowerShellDirectChannelException("remote command exited non-zero",
                new InvalidOperationException("generic")));
        response.ErrorCode.Should().NotBe(ErrorCodes.SessionFailed,
            "a non-open channel failure must not be classified as SESSION_FAILED (AC2/AC3 boundary).");
    }


    [Fact]
    public async Task AC3_SessionOpenFailure_Propagates_TypedException_From_Channel()
    {
        var openFailure = new SshSessionOpenException(
            VmId, $"Failed to open SSH session to the Linux guest '{VmId}': connection refused.");
        var channel = CreateChannel(new FakeSshSessionStore(client: null, openFailure));

        Func<Task> act = () => channel.InvokeScriptAsync(
            HostId, VmId, Username, Password, "ignored", CommandArgs("echo hi"));

        // The channel must rethrow the typed exception unwrapped so ErrorMapper's typed arm fires.
        await act.Should().ThrowAsync<SshSessionOpenException>();
    }

    [Fact]
    public void AC3_ErrorMapper_Maps_SshSessionOpenException_To_SessionFailed_NonEmptyError()
    {
        var mapper = new ErrorMapper();
        var response = mapper.MapException(new SshSessionOpenException(
            VmId, $"Failed to open SSH session to the Linux guest '{VmId}': connection refused."));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "LGS-SSH-D5: an SSH session-open failure classifies as SESSION_FAILED.");
        response.ErrorCode.Should().NotBe(ErrorCodes.FileNotFound,
            "AC3: an SSH open failure must never be misclassified as FILE_NOT_FOUND.");
        response.Error.Should().NotBeNullOrWhiteSpace(
            "AC3: the composed error string must be non-empty for a session-open failure.");
    }

    [Fact]
    public void AC3_ErrorMapper_EmptyMessage_Still_NonEmptyError_And_SessionFailed()
    {
        // Even an empty inner message must produce a non-empty session error, matching PSDirect.
        var mapper = new ErrorMapper();
        var response = mapper.MapException(new SshSessionOpenException(VmId, string.Empty));

        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed);
        response.Error.Should().NotBeNullOrWhiteSpace();
    }


    [Fact]
    public async Task AC4_Password_Never_Appears_In_CommandResult_Output_Or_Error()
    {
        // Strip echoed passwords before they reach results, envelopes or logs.
        var client = new FakeSshExecClient(new SshCommandResult(
            Stdout: $"echoed password was {Password}",
            Stderr: $"stderr leaked {Password} here",
            ExitStatus: 0));
        var channel = CreateChannel(new FakeSshSessionStore(client));

        var hostResult = await channel.InvokeScriptAsync(
            HostId, VmId, Username, Password, "ignored", CommandArgs("echo $PW"));

        var envelopeJson = (string)hostResult.Output.Single()!;
        envelopeJson.Should().NotContain(Password, "the literal password must not survive in the envelope.");
        (hostResult.Stderr ?? string.Empty).Should().NotContain(Password);
        envelopeJson.Should().Contain("***REDACTED***");

        var executorResult = ParseThroughExecutor(hostResult);
        executorResult.Stdout.Should().NotContain(Password);
        executorResult.Stderr.Should().NotContain(Password);
    }

    [Fact]
    public async Task AC4_PrivateKey_Block_In_Stderr_Is_Stripped_From_CommandResult()
    {
        // Remove OpenSSH private-key blocks before producing CommandResult. StderrSpillHelperTests covers PGP and PEM variants.
        const string keyBlock =
            "-----BEGIN OPENSSH PRIVATE KEY-----\n" +
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gt\n" +
            "ZWQyNTUxOQAAACDsecret-key-body-must-not-leak-AAAAAAAAA\n" +
            "-----END OPENSSH PRIVATE KEY-----";
        var client = new FakeSshExecClient(new SshCommandResult(
            Stdout: "ok",
            Stderr: $"leaked key follows:\n{keyBlock}\ntrailing noise",
            ExitStatus: 0));
        var channel = CreateChannel(new FakeSshSessionStore(client));

        var hostResult = await channel.InvokeScriptAsync(
            HostId, VmId, Username, Password, "ignored", CommandArgs("cat id_ed25519"));

        var executorResult = ParseThroughExecutor(hostResult);
        executorResult.Stderr.Should().NotContain("BEGIN OPENSSH PRIVATE KEY");
        executorResult.Stderr.Should().NotContain("secret-key-body-must-not-leak");
        executorResult.Stderr.Should().Contain("***REDACTED-PRIVATE-KEY***");
        executorResult.Stderr.Should().Contain("trailing noise",
            "redaction must remove only the key block, not the surrounding diagnostic text.");
    }


    [Fact]
    public void AC5_SshGuestChannel_Implements_The_Five_Method_Facade()
    {
        IPowerShellDirectChannel facade = CreateChannel(
            new FakeSshSessionStore(new FakeSshExecClient(new SshCommandResult("", "", 0))));
        facade.Should().BeAssignableTo<IPowerShellDirectChannel>();
    }

    // These cases prove SFTP dispatch replaced transfer deferral, not delivered bytes; Issue279LinuxFileTransferRealSeamTests covers
    // content.

    [Fact]
    public async Task CopyToSession_ReachesTransferSeam_RatherThanThrowingDeferred()
    {
        var channel = CreateChannel(
            new FakeSshSessionStore(new FakeSshExecClient(new SshCommandResult("", "", 0))));
        Func<Task> act = () => channel.CopyToSessionAsync(
            HostId, VmId, Username, Password, "/local/src", "/guest/dst");

        // The exec-only fake refuses SFTP, so reaching it proves dispatch; a surviving deferral would instead surface as "not implemented
        // in this slice".
        (await act.Should().ThrowAsync<IOException>()).WithInnerException<NotSupportedException>()
            .Which.Message.Should().Contain("does not perform SFTP transfers");
    }

    [Fact]
    public async Task CopyFromSession_ReachesTransferSeam_RatherThanThrowingDeferred()
    {
        var channel = CreateChannel(
            new FakeSshSessionStore(new FakeSshExecClient(new SshCommandResult("", "", 0))));
        Func<Task> act = () => channel.CopyFromSessionAsync(
            HostId, VmId, Username, Password, "/guest/src", "/local/dst");

        (await act.Should().ThrowAsync<IOException>()).WithInnerException<NotSupportedException>()
            .Which.Message.Should().Contain("does not perform SFTP transfers");
    }

    [Fact]
    public async Task AC5_Invocations_On_Same_Key_Serialize_Under_The_OperationGate()
    {
        // Same-key exec bodies must not overlap; overlap raises measured concurrency to 2.
        var release = new SemaphoreSlim(0, 2);
        var maxConcurrency = 0;
        var current = 0;
        var syncRoot = new object();

        var blockingClient = new GateProbeExecClient(() =>
        {
            lock (syncRoot)
            {
                current++;
                maxConcurrency = Math.Max(maxConcurrency, current);
            }
            release.Wait(TimeSpan.FromSeconds(5));
            lock (syncRoot) { current--; }
            return new SshCommandResult("done", "", 0);
        });
        var channel = CreateChannel(new FakeSshSessionStore(blockingClient));

        var call1 = channel.InvokeScriptAsync(HostId, VmId, Username, Password, "ignored", CommandArgs("a"));
        var call2 = channel.InvokeScriptAsync(HostId, VmId, Username, Password, "ignored", CommandArgs("b"));

        // Give both a chance to start; if the gate serializes, only one exec body is ever in-flight.
        await Task.Delay(250);
        release.Release(2);
        await Task.WhenAll(call1, call2);

        maxConcurrency.Should().Be(1,
            "AC5: per-(hostId,vmId) invocations must serialize under the operation gate.");
    }

    [Fact]
    public async Task AC5_Different_Keys_Do_Not_Serialize_Against_Each_Other()
    {
        // Different keys may overlap: serialization is per-key, not global.
        var started = new SemaphoreSlim(0, 2);
        var release = new SemaphoreSlim(0, 2);

        var clientA = new GateProbeExecClient(() =>
        {
            started.Release();
            release.Wait(TimeSpan.FromSeconds(5));
            return new SshCommandResult("a", "", 0);
        });
        var clientB = new GateProbeExecClient(() =>
        {
            started.Release();
            release.Wait(TimeSpan.FromSeconds(5));
            return new SshCommandResult("b", "", 0);
        });

        var channelA = CreateChannel(new FakeSshSessionStore(clientA));
        var channelB = CreateChannel(new FakeSshSessionStore(clientB));

        var callA = channelA.InvokeScriptAsync("host-a", "vm-a", Username, Password, "ignored", CommandArgs("a"));
        var callB = channelB.InvokeScriptAsync("host-b", "vm-b", Username, Password, "ignored", CommandArgs("b"));

        // Both exec bodies must be able to start before either is released — proving no cross-key block.
        var firstStarted = await started.WaitAsync(TimeSpan.FromSeconds(3));
        var secondStarted = await started.WaitAsync(TimeSpan.FromSeconds(3));
        release.Release(2);
        await Task.WhenAll(callA, callB);

        (firstStarted && secondStarted).Should().BeTrue(
            "AC5: distinct (hostId,vmId) keys must not serialize against each other.");
    }


    [Fact]
    public async Task AC5_Evict_Disposes_The_Cached_Client()
    {
        var client = new FakeSshExecClient(new SshCommandResult("", "", 0));
        var store = new FakeSshSessionStore(client);
        var channel = CreateChannel(store);

        await channel.InvokeScriptAsync(HostId, VmId, Username, Password, "ignored", CommandArgs("echo"));
        await channel.EvictSessionAsync(HostId, VmId);

        store.EvictCount.Should().Be(1);
        client.DisposeCount.Should().Be(1, "evict must dispose the cached SSH client exactly once.");
    }

    [Fact]
    public async Task AC5_Exec_And_Evict_Are_Mutually_Exclusive_DisposedClient_Never_Executed()
    {
        // Race eviction against blocked exec: disposal must wait for exec completion so no disposed client receives a command.
        var release = new SemaphoreSlim(0, 1);
        var client = new GateProbeExecClient(() =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            return new SshCommandResult("done", "", 0);
        });
        var store = new FakeSshSessionStore(client);
        var channel = CreateChannel(store);

        var execCall = channel.InvokeScriptAsync(HostId, VmId, Username, Password, "ignored", CommandArgs("slow"));
        await Task.Delay(200);
        var evictCall = channel.EvictSessionAsync(HostId, VmId);

        // Evict must be blocked behind the gate until exec completes.
        evictCall.IsCompleted.Should().BeFalse("evict must wait for the in-flight exec under the gate.");
        release.Release();
        await Task.WhenAll(execCall, evictCall);

        client.DisposeCount.Should().Be(1);
        client.ExecutedAfterDispose.Should().BeFalse(
            "AC5 regression: a disposed SSH client must never be executed.");
    }


    [Fact]
    public async Task AC6_Router_Routes_LinuxGuest_To_Ssh_Channel()
    {
        var harness = new RouterHarness(guestOs: "linux");

        await harness.Router.InvokeScriptAsync(
            HostId, VmId, Username, Password, "ignored", CommandArgs("echo"));

        harness.SshStore.GetOrCreateCount.Should().BeGreaterThan(0,
            "AC6: a Linux-guest profile must route to the SSH channel.");
        harness.WindowsHostInvocations.Should().Be(0,
            "the Windows PSDirect host must not be touched for a Linux guest.");
    }

    [Fact]
    public async Task AC6_Router_Routes_WindowsGuest_To_PSDirect_Channel_Unaffected()
    {
        var harness = new RouterHarness(guestOs: "windows");

        // Assert routing only: the real PSDirect wrapper cannot complete against a mocked host. Reaching the Windows host proves selection;
        // post-routing failures are ignored.
        await InvokeIgnoringChannelExecution(harness);

        harness.WindowsHostInvocations.Should().Be(1,
            "AC6: a Windows-guest profile must route to the PSDirect channel (real host invoked once).");
        harness.SshStore.GetOrCreateCount.Should().Be(0,
            "AC6: the SSH channel must not be touched for a Windows guest.");
    }

    [Fact]
    public async Task AC6_Router_DefaultProfile_Routes_To_PSDirect()
    {
        // The default windows GuestOs hint preserves routing for existing profiles.
        var harness = new RouterHarness(guestOs: null);

        await InvokeIgnoringChannelExecution(harness);

        harness.WindowsHostInvocations.Should().Be(1);
        harness.SshStore.GetOrCreateCount.Should().Be(0);
    }

    /// <summary>Ignore post-routing execution failures from the real PSDirect wrapper against a mocked host. Recorded host/SSH-store calls
    /// independently assert selection.</summary>
    private static async Task InvokeIgnoringChannelExecution(RouterHarness harness)
    {
        try
        {
            await harness.Router.InvokeScriptAsync(
                HostId, VmId, Username, Password, "ignored", CommandArgs("echo"));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // Routing was recorded; ignore the mocked-host execution failure.
        }
    }


    /// <summary>Use real CommandExecutor.ParseJsonResult so assertions see the caller-visible result.</summary>
    private static CommandResult ParseThroughExecutor(PowerShellHostResult hostResult)
    {
        var executor = new CommandExecutor(
            Mock.Of<IPowerShellDirectChannel>(),
            Mock.Of<IHostResolver>(),
            NullLogger<CommandExecutor>.Instance);
        return executor.ParseJsonResult(hostResult, HostId, VmId, Password);
    }

    /// <summary>Real router and channels use a mocked Windows host/session store and fake SSH store. The GuestOs hint drives selection;
    /// recorded calls distinguish Windows from SSH without replacing the selector.</summary>
    private sealed class RouterHarness
    {
        public GuestChannelRouter Router { get; }
        public FakeSshSessionStore SshStore { get; }
        public int WindowsHostInvocations => _hostMock.Invocations.Count;
        private readonly Mock<IPowerShellHost> _hostMock = new();

        public RouterHarness(string? guestOs)
        {
            SshStore = new FakeSshSessionStore(new FakeSshExecClient(new SshCommandResult("ok", "", 0)));
            var sshChannel = new SshGuestChannel(SshStore, NullLogger<SshGuestChannel>.Instance);

            // Return a benign non-null envelope so retries cannot fail on null. Any recorded host call proves PSDirect routing.
            var benign = new PowerShellHostResult(true, new object?[] { "{}" }, string.Empty, 0);
            _hostMock
                .Setup(h => h.InvokeWithTimeoutAsync(
                    It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                    It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(benign);
            _hostMock
                .Setup(h => h.InvokeAsync(
                    It.IsAny<string>(), It.IsAny<IDictionary<string, object?>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(benign);

            var sessionMock = new Mock<ISessionStore>();
            sessionMock
                .Setup(s => s.GetOrCreateAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionHandle(HostId, VmId, "HvMcp-win"));

            var windowsChannel = new PowerShellDirectChannel(
                _hostMock.Object, sessionMock.Object, NullLogger<PowerShellDirectChannel>.Instance);

            var profile = new HostProfile { HostId = HostId, ComputerName = "localhost" };
            if (guestOs is not null) profile.GuestOs = guestOs;

            var resolver = new Mock<IHostResolver>();
            resolver.Setup(r => r.Resolve(It.IsAny<string>())).Returns(profile);
            resolver.Setup(r => r.ResolveRequired(It.IsAny<string>())).Returns(profile);

            // The host-property selector requires a known guest classification; unclassified guests refuse routing. Seed Windows to reach
            // default routing.
            var hintStore = new GuestRoutingHintStore();
            hintStore.Record(HostId, VmId, new GuestRoutingHint(GuestOsKind.Windows, null, 0));

            Router = new GuestChannelRouter(
                windowsChannel, sshChannel, resolver.Object,
                hintStore,
                NullLogger<GuestChannelRouter>.Instance);
        }
    }
}
