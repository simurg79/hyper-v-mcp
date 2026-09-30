using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #279: Linux guest file transfer over SSH/SFTP, exercised at the real
/// <see cref="ISshExecClient"/> seam.
///
/// <para>The fake below is NOT a recorder. Its exec implementation interprets the POSIX commands
/// the channel emits, and its SFTP implementation copies real bytes, both against a real temp
/// directory standing in for the guest filesystem. Assertions read the destination content — a
/// capture-only mock would let a transfer that moved nothing pass (the PR #278 failure mode).</para>
/// </summary>
public class Issue279LinuxFileTransferRealSeamTests : IDisposable
{
    private const string HostId = "host-279";
    private const string VmId = "27927927-0279-4279-8279-279279279279";
    private const string User = "ubuntu";
    private const string Password = "P@ssw0rd-279";

    private readonly string _guestRoot;
    private readonly string _hostRoot;

    public Issue279LinuxFileTransferRealSeamTests()
    {
        _guestRoot = Directory.CreateTempSubdirectory("issue279_guest_").FullName;
        _hostRoot = Directory.CreateTempSubdirectory("issue279_host_").FullName;
    }

    public void Dispose()
    {
        TryDelete(_guestRoot);
        TryDelete(_hostRoot);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private (FileTransferService Service, ExecutingGuestClient Client) BuildStack(
        ExecutingGuestClient? existingClient = null)
    {
        var client = existingClient ?? new ExecutingGuestClient(_guestRoot);
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var service = new FileTransferService(
            channel, new StubHostResolver(), NullLogger<FileTransferService>.Instance);
        return (service, client);
    }

    private sealed class StubHostResolver : IHostResolver
    {
        private static readonly HostProfile Profile = new()
        {
            HostId = HostId,
            ComputerName = "localhost",
        };

        public HostProfile? Resolve(string? hostId) => Profile;
        public HostProfile ResolveRequired(string? hostId) => Profile;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ════════════════════════════════════════════════════════════════════
    // Both directions actually move bytes.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyFileToGuest_WritesRealBytes_AndReportsGuestObservedSize()
    {
        var (service, client) = BuildStack();
        var sourcePath = Path.Combine(_hostRoot, "payload.bin");
        var payload = RandomNumberGenerator.GetBytes(64 * 1024);
        await File.WriteAllBytesAsync(sourcePath, payload);

        var result = await service.CopyToGuestAsync(
            HostId, VmId, sourcePath, "/home/ubuntu/payload.bin",
            isDirectory: false, username: User, password: Password);

        // Destination CONTENT is the assertion, not the envelope: a fake that recorded the
        // transfer without performing it would leave this file absent.
        var landed = client.GuestPathToLocal("/home/ubuntu/payload.bin");
        File.Exists(landed).Should().BeTrue("the transfer must actually write the file into the guest");
        Sha256Of(landed).Should().Be(Sha256Of(sourcePath), "the delivered bytes must be identical");
        result.BytesTransferred.Should().Be(payload.Length);
        result.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task CopyFileFromGuest_WritesRealBytes_ToHost()
    {
        var (service, client) = BuildStack();
        var guestFile = client.GuestPathToLocal("/home/ubuntu/out.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(guestFile)!);
        var payload = RandomNumberGenerator.GetBytes(48 * 1024);
        await File.WriteAllBytesAsync(guestFile, payload);

        var destination = Path.Combine(_hostRoot, "pulled.bin");
        var result = await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/out.bin", destination,
            username: User, password: Password);

        File.Exists(destination).Should().BeTrue();
        Sha256Of(destination).Should().Be(Sha256Of(guestFile));
        result.BytesTransferred.Should().Be(payload.Length);
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: the ensure-parent intent must really create the directory.
    // Previously this returned Success having done nothing: the script was discarded and run as an
    // empty shell command. See internal documentation
    // — LGS-XFER-D0.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyFileToGuest_CreatesMissingParentDirectory_OnGuest()
    {
        var (service, client) = BuildStack();
        var sourcePath = Path.Combine(_hostRoot, "nested.txt");
        await File.WriteAllTextAsync(sourcePath, "nested payload", Encoding.UTF8);

        await service.CopyToGuestAsync(
            HostId, VmId, sourcePath, "/home/ubuntu/deeply/nested/dir/nested.txt",
            isDirectory: false, username: User, password: Password);

        var landed = client.GuestPathToLocal("/home/ubuntu/deeply/nested/dir/nested.txt");
        File.Exists(landed).Should().BeTrue(
            "the ensure-parent intent must create the parent chain rather than silently no-op");
        (await File.ReadAllTextAsync(landed)).Should().Be("nested payload");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: an unrecognized script with no explicit command must fail loudly.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UnrecognizedScriptWithoutCommand_Fails_RatherThanSilentlySucceeding()
    {
        var client = new ExecutingGuestClient(_guestRoot);
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);

        // Transfer-shaped arguments (the service binds 'path') with a script the translator does
        // not recognize — what a renamed or re-bodied transfer constant would produce.
        var act = async () => await channel.InvokeScriptAsync(
            HostId, VmId, User, Password,
            script: "param($path)\n(Get-Item $path).Length # drifted copy of a transfer script",
            args: new Dictionary<string, object?> { ["path"] = "/home/ubuntu/thing.bin" });

        await act.Should().ThrowAsync<PowerShellDirectChannelException>(
            "a script that is neither an explicit command nor a known transfer intent must not " +
            "report success after doing nothing");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: exec keeps its JSON envelope while transfer emits a bare value.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecPath_StillEmitsJsonEnvelope_WhileTransferIntentEmitsBareValue()
    {
        var client = new ExecutingGuestClient(_guestRoot);
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);

        var execResult = await channel.InvokeScriptAsync(
            HostId, VmId, User, Password, script: "ignored",
            args: new Dictionary<string, object?> { ["cmd"] = "echo hello", ["sh"] = "default" });

        var envelope = (string)execResult.Output.Single()!;
        // CommandExecutor.ParseJsonResult requires these members on the exec path.
        envelope.Should().Contain("\"Stdout\"").And.Contain("\"ExitCode\"");

        var guestFile = client.GuestPathToLocal("/tmp/sized.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(guestFile)!);
        await File.WriteAllBytesAsync(guestFile, new byte[1234]);

        var verifyResult = await channel.InvokeScriptAsync(
            HostId, VmId, User, Password,
            script: FileTransferService.VerifyFileScript,
            args: new Dictionary<string, object?> { ["path"] = "/tmp/sized.bin" });

        // Bare value: the transfer service parses this directly, so an envelope would break it.
        verifyResult.Output.Single().Should().Be(1234L);
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: denial maps to AUTH_FAILED, absence to FILE_NOT_FOUND, and a
    // generic transport fault to TRANSFER_FAILED — never COMMAND_FAILED.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PermissionDeniedDuringTransfer_MapsToAuthFailed()
    {
        var client = new ExecutingGuestClient(_guestRoot) { DenyUploads = true };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "denied.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var mapper = new ErrorMapper();
        var thrown = await Record.ExceptionAsync(async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/root/denied.txt"));

        thrown.Should().NotBeNull();
        var envelope = mapper.MapException(thrown!);
        envelope.ErrorCode.Should().Be(ErrorCodes.AuthFailed,
            "a post-connection denial is distinguishable from a transport error (verified by live spike)");
        // The CONSUMER-visible message is the assertion: composing context at the throw site proves
        // nothing if the mapper arm then substitutes fixed text for it.
        (envelope.Error ?? string.Empty).Should().Contain("to guest").And.Contain(VmId)
            .And.Contain("/root/denied.txt");
    }

    [Fact]
    public async Task MissingGuestSourceDuringTransfer_MapsToFileNotFound()
    {
        var client = new ExecutingGuestClient(_guestRoot);
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);

        var mapper = new ErrorMapper();
        var thrown = await Record.ExceptionAsync(async () => await channel.CopyFromSessionAsync(
            HostId, VmId, User, Password, "/home/ubuntu/absent.bin",
            Path.Combine(_hostRoot, "absent.bin")));

        thrown.Should().NotBeNull();
        var envelope = mapper.MapException(thrown!);
        envelope.ErrorCode.Should().Be(ErrorCodes.FileNotFound);
        (envelope.Error ?? string.Empty).Should().Contain("from guest").And.Contain(VmId)
            .And.Contain("/home/ubuntu/absent.bin");
    }

    [Fact]
    public async Task InitialSessionOpenFailure_DuringTransfer_NamesDirectionVmAndPath()
    {
        // Acquiring the session used to happen OUTSIDE the transfer's catch, so this reached the
        // caller as an uncontextualized SESSION_FAILED naming neither direction nor the path.
        var channel = new SshGuestChannel(
            new UnopenableSessionStore(), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "unopenable.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var thrown = await Record.ExceptionAsync(async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/unopenable.txt"));

        thrown.Should().NotBeNull();
        var envelope = new ErrorMapper().MapException(thrown!);
        envelope.ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "an unopenable session stays a session fault; only its message gains context");
        (envelope.Error ?? string.Empty).Should().Contain("to guest").And.Contain(VmId)
            .And.Contain("/home/ubuntu/unopenable.txt");
    }

    [Fact]
    public void NonTransferDenialAndAbsence_KeepTheirFixedEnvelopeText()
    {
        // The transfer arms sit above the shared UnauthorizedAccessException / FileNotFoundException
        // arms. Host-side callers (HyperVManager, ImagePathResolver) reach those shared arms with
        // messages never proven wire-safe, so their fixed text must be unchanged by this work.
        var mapper = new ErrorMapper();

        var denial = mapper.MapException(
            new UnauthorizedAccessException(@"Access to 'C:\HyperVMCP\Images\secret.vhdx' is denied."));
        denial.ErrorCode.Should().Be(ErrorCodes.AuthFailed);
        denial.Error.Should().Be("Authentication failed or access was denied.");

        var missing = mapper.MapException(
            new FileNotFoundException(@"Could not find file 'C:\HyperVMCP\Images\secret.vhdx'."));
        missing.ErrorCode.Should().Be(ErrorCodes.FileNotFound);
        missing.Error.Should().Be("The specified file was not found.");
    }

    [Fact]
    public async Task GenericTransportFaultDuringTransfer_MapsToTransferFailed_NotCommandFailed()
    {
        var client = new ExecutingGuestClient(_guestRoot) { FaultUploads = true };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "fault.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var mapper = new ErrorMapper();
        var thrown = await Record.ExceptionAsync(async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/fault.txt"));

        thrown.Should().NotBeNull();
        // A bare InvalidOperationException would land on COMMAND_FAILED; TRANSFER_FAILED is only
        // reachable via IOException. See
        // internal documentation — LGS-XFER-D4.
        mapper.MapException(thrown!).ErrorCode.Should().Be(ErrorCodes.TransferFailed);
    }

    [Fact]
    public async Task CancellationDuringTransfer_PropagatesUnenveloped()
    {
        using var cts = new CancellationTokenSource();
        // Cancel once the transfer is ALREADY streaming bytes. Cancelling beforehand only proved
        // the entry check; it could not detect I/O that ignores the token once started, which is
        // the hang this guard exists to prevent.
        var client = new ExecutingGuestClient(_guestRoot)
        {
            OnUploadStarted = () => cts.Cancel(),
        };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "cancel.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var act = async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/cancel.txt", cts.Token);

        (await act.Should().ThrowAsync<OperationCanceledException>().WaitAsync(TimeSpan.FromSeconds(30)))
            .Which.Should().NotBeNull();
        File.Exists(client.GuestPathToLocal("/home/ubuntu/cancel.txt")).Should().BeFalse(
            "a transfer cancelled mid-flight must not be completed anyway");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: verification failure must fail, never succeed with verified:false.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TruncatedDelivery_FailsVerification_RatherThanReportingSuccess()
    {
        var (service, _) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateUploadsTo = 8 });

        var sourcePath = Path.Combine(_hostRoot, "truncated.bin");
        await File.WriteAllBytesAsync(sourcePath, RandomNumberGenerator.GetBytes(4096));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyToGuestAsync(
            HostId, VmId, sourcePath, "/home/ubuntu/truncated.bin",
            isDirectory: false, username: User, password: Password));

        // A short write must FAIL. Reporting the guest-observed 8 bytes as a successful,
        // verified transfer is the false success this guard exists to prevent.
        thrown.Should().NotBeNull(
            "a truncated upload must not be reported as a completed transfer");
        thrown!.Message.Should().Contain("4096").And.Contain("8");
        new ErrorMapper().MapException(thrown).ErrorCode.Should().Be(ErrorCodes.TransferFailed);
    }

    [Fact]
    public async Task TruncatedDirectoryDelivery_ToGuest_Fails_RatherThanReportingSuccess()
    {
        var (service, _) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateUploadsTo = 16 });

        var sourceDirectory = Path.Combine(_hostRoot, "tree-truncated");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceDirectory, "big.bin"), RandomNumberGenerator.GetBytes(4096));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyToGuestAsync(
            HostId, VmId, sourceDirectory, "/home/ubuntu/tree-truncated",
            isDirectory: true, username: User, password: Password));

        thrown.Should().NotBeNull(
            "a short write during directory expansion must not be reported as delivered");
        // The archive is the failing path here; naming it is what tells the operator the delivery
        // was short rather than the source being bad.
        thrown!.Message.Should().Contain("16").And.Contain(".zip");
    }

    [Fact]
    public async Task ShortWriteOfASingleExpandedFile_Fails_RatherThanReportingSuccess()
    {
        // Only the per-file writes beneath the destination are short; the archive itself arrives
        // intact, so this reaches the expand step's own reconciliation rather than the earlier
        // archive check.
        var (service, _) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateUploadsUnder = "/home/ubuntu/expand-short" });

        var sourceDirectory = Path.Combine(_hostRoot, "expand-short-src");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "payload.txt"), new string('p', 400));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyToGuestAsync(
            HostId, VmId, sourceDirectory, "/home/ubuntu/expand-short",
            isDirectory: true, username: User, password: Password));

        thrown.Should().NotBeNull(
            "a file the guest holds only partially must not be counted as delivered");
        thrown!.Message.Should().Contain("400").And.Contain("payload.txt");
    }

    [Fact]
    public async Task ShortUploadOfTheStagedArchive_FromGuest_Fails_RatherThanReportingSuccess()
    {
        // The from-guest directory flow stages the tree on the host, archives it, and writes the
        // ARCHIVE BACK to a guest temp path. Only downloads were ever truncated, so the
        // reconciliation of that upload against the size the guest holds was unasserted and could
        // be deleted with every guard still green.
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateUploadsTo = 16 });

        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/stage-short");
        Directory.CreateDirectory(guestDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(guestDirectory, "big.bin"), RandomNumberGenerator.GetBytes(4096));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/stage-short",
            Path.Combine(_hostRoot, "stage-short-dest"), username: User, password: Password));

        thrown.Should().NotBeNull(
            "an archive the guest holds only partially must not be treated as staged");
        thrown.Should().BeOfType<GuestTransferFailedException>();
        // Naming the truncated size and the archive is what distinguishes a short STAGING upload
        // from the later "not a readable archive" failure, which reports a corrupt file instead.
        thrown!.Message.Should().Contain("staging").And.Contain("16").And.Contain(".zip");
        new ErrorMapper().MapException(thrown).ErrorCode.Should().Be(ErrorCodes.TransferFailed);
    }

    [Fact]
    public async Task TruncatedDirectoryDelivery_FromGuest_Fails_RatherThanReportingSuccess()
    {
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateDownloadsTo = 16 });

        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/tree-pull-short");
        Directory.CreateDirectory(guestDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(guestDirectory, "big.bin"), RandomNumberGenerator.GetBytes(4096));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/tree-pull-short",
            Path.Combine(_hostRoot, "pulled-short"), username: User, password: Password));

        thrown.Should().NotBeNull(
            "a short read from the guest must not be reported as a complete directory copy");
        // Asserting the SPECIFIC failure matters: a raw archive-parser error would satisfy a
        // bare "something threw" check while proving nothing about the reconciliation.
        thrown.Should().BeOfType<GuestTransferFailedException>();
        thrown!.Message.Should().Contain("4096").And.Contain("16");
        new ErrorMapper().MapException(thrown).ErrorCode.Should().Be(ErrorCodes.TransferFailed);
        // The VM must be named even though this failure is raised deep inside the archive step.
        // Keying "already contextualized" off the message wording let an inner failure that merely
        // opened with the same word suppress the wrap that supplies the VM.
        thrown.Message.Should().Contain(VmId).And.Contain("from guest");
    }

    [Fact]
    public async Task TruncatedSingleFileDelivery_FromGuest_ReportsTransferFailed_NamingVmAndDirection()
    {
        // The pull-side mirror of the push-side truncation guard. Previously this threw a generic
        // invalid-operation failure, which maps to COMMAND_FAILED and names neither VM nor
        // direction — telling the operator a command misbehaved rather than that bytes were lost.
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { TruncateDownloadsTo = 8 });

        var guestFile = client.GuestPathToLocal("/home/ubuntu/short-pull.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(guestFile)!);
        await File.WriteAllBytesAsync(guestFile, RandomNumberGenerator.GetBytes(2048));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/short-pull.bin",
            Path.Combine(_hostRoot, "short-pull.bin"), username: User, password: Password));

        thrown.Should().NotBeNull("a short read must not be reported as a completed transfer");
        new ErrorMapper().MapException(thrown!).ErrorCode.Should().Be(ErrorCodes.TransferFailed);
        thrown!.Message.Should().Contain("from guest").And.Contain(VmId)
            .And.Contain("/home/ubuntu/short-pull.bin").And.Contain("2048").And.Contain("8");
    }

    // ════════════════════════════════════════════════════════════════════
    // Directory semantics, both directions. Without these the archive/expand
    // code could be wholly wrong while every other issue-279 test passes.
    // File-vs-directory TYPE collision is deliberately unspecified, so it is not asserted here. See
    // internal documentation — OQ-3.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CopyDirectoryToGuest_WritesEveryFile_PreservingRelativeLayout()
    {
        var (service, client) = BuildStack();
        var sourceDirectory = Path.Combine(_hostRoot, "tree");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "sub", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "sub", "mid.txt"), "middle");
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "sub", "deeper", "leaf.txt"), "leaf-content");

        var result = await service.CopyToGuestAsync(
            HostId, VmId, sourceDirectory, "/home/ubuntu/tree",
            isDirectory: true, username: User, password: Password);

        (await File.ReadAllTextAsync(client.GuestPathToLocal("/home/ubuntu/tree/root.txt")))
            .Should().Be("root");
        (await File.ReadAllTextAsync(client.GuestPathToLocal("/home/ubuntu/tree/sub/mid.txt")))
            .Should().Be("middle");
        (await File.ReadAllTextAsync(client.GuestPathToLocal("/home/ubuntu/tree/sub/deeper/leaf.txt")))
            .Should().Be("leaf-content");
        result.IsDirectory.Should().BeTrue();
        result.BytesTransferred.Should().Be(4 + 6 + 12,
            "the reported total is the sum of the sizes the GUEST holds");
    }

    [Fact]
    public async Task CopyDirectoryToGuest_OverwritesSameNamedFile_AndKeepsUnrelatedGuestEntries()
    {
        var (service, client) = BuildStack();
        var sourceDirectory = Path.Combine(_hostRoot, "tree-overwrite");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "shared.txt"), "new-content");

        var existingShared = client.GuestPathToLocal("/home/ubuntu/dest/shared.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existingShared)!);
        await File.WriteAllTextAsync(existingShared, "stale-content-that-is-longer");
        var unrelated = client.GuestPathToLocal("/home/ubuntu/dest/unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep me");

        var result = await service.CopyToGuestAsync(
            HostId, VmId, sourceDirectory, "/home/ubuntu/dest",
            isDirectory: true, username: User, password: Password);

        (await File.ReadAllTextAsync(existingShared)).Should().Be("new-content");
        File.Exists(unrelated).Should().BeTrue("entries this copy did not target must survive");
        (await File.ReadAllTextAsync(unrelated)).Should().Be("keep me");
        result.BytesTransferred.Should().Be("new-content".Length,
            "only the transferred entry is counted, never unrelated pre-existing content");
    }

    [Fact]
    public async Task CopyDirectoryFromGuest_WritesEveryFile_PreservingRelativeLayout()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/pull");
        Directory.CreateDirectory(Path.Combine(guestDirectory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "top.txt"), "top");
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "sub", "nested.txt"), "nested!");

        var destination = Path.Combine(_hostRoot, "pulled-tree");
        var result = await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/pull", destination, username: User, password: Password);

        (await File.ReadAllTextAsync(Path.Combine(destination, "top.txt"))).Should().Be("top");
        (await File.ReadAllTextAsync(Path.Combine(destination, "sub", "nested.txt")))
            .Should().Be("nested!");
        result.IsDirectory.Should().BeTrue();
        result.BytesTransferred.Should().Be(3 + 7);
    }

    [Fact]
    public async Task CopyDirectoryFromGuest_OverwritesSameNamedFile_AndExcludesUnrelatedHostEntries()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/pull2");
        Directory.CreateDirectory(guestDirectory);
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "shared.txt"), "fresh");

        var destination = Path.Combine(_hostRoot, "merge-dest");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "shared.txt"), "old-and-longer");
        await File.WriteAllTextAsync(Path.Combine(destination, "unrelated.bin"), new string('x', 500));

        var result = await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/pull2", destination, username: User, password: Password);

        (await File.ReadAllTextAsync(Path.Combine(destination, "shared.txt"))).Should().Be("fresh");
        File.Exists(Path.Combine(destination, "unrelated.bin")).Should().BeTrue();
        result.BytesTransferred.Should().Be("fresh".Length,
            "pre-existing host entries must not be counted as transferred bytes");
    }

    [Fact]
    public async Task DirectoryListing_IsNulDelimited_SoNewlineBearingNamesSurvive()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/odd");
        Directory.CreateDirectory(guestDirectory);
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "plain.txt"), "plain");

        // A POSIX name may legally contain a newline; Windows cannot represent one, so such an
        // entry CANNOT be staged and the copy must fail loudly rather than lose it. What the NUL
        // delimiter buys is that the failure names the ENTIRE entry: a line-delimited listing
        // would split it into the two nonexistent paths 'two' and 'lines.txt' and report a
        // missing-file error naming neither.
        client.ExtraListingEntries.Add(("two\nlines.txt", "unrepresentable"));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/odd", Path.Combine(_hostRoot, "odd-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull("an entry the host cannot represent must not be silently dropped");
        thrown!.Message.Should().Contain("two").And.Contain("lines.txt",
            "the whole guest name must survive the listing intact; splitting it on the newline " +
            "would name only a fragment");
    }

    [Fact]
    public async Task ListingFailure_FailsTheTransfer_RatherThanArchivingAPartialTree()
    {
        // find fails while base64 still exits 0 — the real pipeline hazard. Without pipefail the
        // channel sees a successful command carrying a truncated listing and reports the partial
        // archive as a completed directory transfer.
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot) { FailListingWithPartialOutput = true });

        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/unreadable");
        Directory.CreateDirectory(guestDirectory);
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "present.txt"), "present");

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/unreadable", Path.Combine(_hostRoot, "unreadable-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull(
            "a listing that failed must fail the transfer, never yield a silently partial archive");
        thrown!.Message.Should().Contain("exited",
            "the listing command's non-zero status is what must surface, rather than being " +
            "swallowed by base64's own success");
    }

    [Fact]
    public async Task CaseCollidingGuestNames_FailLoudly_RatherThanCollapsingIntoOneStagedFile()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/case");
        Directory.CreateDirectory(guestDirectory);
        // 'A' and 'a' are distinct on the guest but one file on a case-insensitive host staging
        // tree; both are delivered in memory because Windows cannot hold them side by side.
        client.ExtraListingEntries.Add(("A.txt", "upper"));
        client.ExtraListingEntries.Add(("a.txt", "lower"));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/case", Path.Combine(_hostRoot, "case-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull(
            "one guest file overwriting another in staging must fail, not silently vanish");
        thrown!.Message.Should().Contain("collide");
        // Naming BOTH entries is the point: "something collided" leaves the operator to rediscover
        // which guest names were involved.
        thrown.Message.Should().Contain("A.txt").And.Contain("a.txt");
    }

    [Fact]
    public async Task CaseCollidingGuestDirectories_FailLoudly_RatherThanMergingSilently()
    {
        // Directories were staged with no collision check at all, so two guest directories aliased
        // by Windows path semantics merged into one and their differing content was interleaved
        // into a "successful" archive.
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot)
            {
                ExtraListingDirectories = { "Data", "data" },
            });
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/case-dirs");
        Directory.CreateDirectory(guestDirectory);

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/case-dirs", Path.Combine(_hostRoot, "case-dirs-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull("two distinct guest directories must not merge into one silently");
        thrown!.Message.Should().Contain("collide").And.Contain("Data").And.Contain("data");
    }

    [Fact]
    public async Task GuestFileAliasedOntoAGuestDirectory_FailsLoudly()
    {
        // A file and a directory can alias each other too ('Logs' vs 'logs'), which no
        // file-only registry could see; one then overwrites the other during staging.
        var (service, client) = BuildStack(
            new ExecutingGuestClient(_guestRoot)
            {
                ExtraListingDirectories = { "Logs" },
            });
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/mixed-case");
        Directory.CreateDirectory(guestDirectory);
        client.ExtraListingEntries.Add(("logs", "file-not-directory"));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/mixed-case", Path.Combine(_hostRoot, "mixed-case-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull();
        thrown!.Message.Should().Contain("collide").And.Contain("Logs").And.Contain("logs");
    }

    [Fact]
    public async Task EmptyGuestDirectory_ReachesTheHost_RatherThanBeingDropped()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/with-empty");
        Directory.CreateDirectory(Path.Combine(guestDirectory, "hollow"));
        await File.WriteAllTextAsync(Path.Combine(guestDirectory, "kept.txt"), "kept");

        var destination = Path.Combine(_hostRoot, "with-empty-dest");
        await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/with-empty", destination, username: User, password: Password);

        Directory.Exists(Path.Combine(destination, "hollow")).Should().BeTrue(
            "a directory carrying no file is still content the copy must preserve");
    }

    [Fact]
    public async Task GuestListingEscapingTheStagingDirectory_IsRefused()
    {
        var (service, client) = BuildStack();
        var guestDirectory = client.GuestPathToLocal("/home/ubuntu/evil");
        Directory.CreateDirectory(guestDirectory);
        client.ExtraListingEntries.Add(("../../escaped.txt", "pwned"));

        var thrown = await Record.ExceptionAsync(async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/home/ubuntu/evil", Path.Combine(_hostRoot, "evil-dest"),
            username: User, password: Password));

        thrown.Should().NotBeNull("a guest-chosen path must not be able to write outside staging");
        thrown!.Message.Should().Contain("outside the host staging directory");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard: a connection lost mid-transfer is a SESSION failure, and the
    // session remains reusable afterwards.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectionLostDuringTransfer_MapsToSessionFailed_NotTransferFailed()
    {
        var client = new ExecutingGuestClient(_guestRoot) { LoseConnectionOnUpload = true };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "dropped.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var thrown = await Record.ExceptionAsync(async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/dropped.txt"));

        thrown.Should().NotBeNull();
        new ErrorMapper().MapException(thrown!).ErrorCode.Should().Be(ErrorCodes.SessionFailed,
            "a lost connection is a session fault the caller retries, not a transfer fault");
    }

    [Fact]
    public async Task SessionRemainsUsable_AfterAConnectionLossDuringTransfer()
    {
        // A REPLACING store, not one fake whose failure flag is cleared: the dropped client stays
        // permanently broken, so the retry can only succeed if the store actually handed back a
        // different, freshly-connected client. Clearing a flag on a reused fake proved nothing.
        var store = new ReplacingSessionStore(() => new ExecutingGuestClient(_guestRoot));
        var channel = new SshGuestChannel(store, NullLogger<SshGuestChannel>.Instance);
        store.Current.LoseConnectionOnUpload = true;

        var sourcePath = Path.Combine(_hostRoot, "retry.txt");
        await File.WriteAllTextAsync(sourcePath, "retry payload");

        var dropped = await Record.ExceptionAsync(async () => await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/retry.txt"));
        new ErrorMapper().MapException(dropped!).ErrorCode.Should().Be(ErrorCodes.SessionFailed);

        var poisoned = store.Current;
        await channel.EvictSessionAsync(HostId, VmId);

        var retry = await channel.CopyToSessionAsync(
            HostId, VmId, User, Password, sourcePath, "/home/ubuntu/retry.txt");

        retry.Success.Should().BeTrue();
        store.Current.Should().NotBeSameAs(poisoned, "the dead client must be replaced, not reused");
        poisoned.Disposals.Should().Be(1, "the dropped connection must be closed, not leaked");
        (await File.ReadAllTextAsync(store.Current.GuestPathToLocal("/home/ubuntu/retry.txt")))
            .Should().Be("retry payload");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard (FR-ERR-5): a transfer failure names direction, VM, and path.
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferFailure_NamesDirectionVmAndPath(bool uploading)
    {
        var client = new ExecutingGuestClient(_guestRoot)
        {
            FaultUploads = uploading,
            FaultDownloads = !uploading,
        };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var sourcePath = Path.Combine(_hostRoot, "ctx.txt");
        await File.WriteAllTextAsync(sourcePath, "x");

        var guestPath = uploading ? "/home/ubuntu/ctx-up.bin" : "/home/ubuntu/ctx-down.bin";
        var thrown = await Record.ExceptionAsync(async () =>
        {
            if (uploading)
            {
                await channel.CopyToSessionAsync(
                    HostId, VmId, User, Password, sourcePath, guestPath);
            }
            else
            {
                await channel.CopyFromSessionAsync(
                    HostId, VmId, User, Password, guestPath, Path.Combine(_hostRoot, "ctx-out.bin"));
            }
        });

        thrown.Should().NotBeNull();
        var surfaced = new ErrorMapper().MapException(thrown!).Error ?? string.Empty;
        surfaced.Should().Contain(uploading ? "to guest" : "from guest")
            .And.Contain(VmId)
            .And.Contain(guestPath);
    }

    [Fact]
    public async Task ConnectionLostDuringVerification_MapsToSessionFailed_NotCommandFailed()
    {
        // The drop happens in the verification COMMAND rather than in the bytes. Untranslated it
        // reached the generic wrap and surfaced as a command failure, telling the caller to inspect
        // output that never arrived instead of to reopen the session.
        var client = new ExecutingGuestClient(_guestRoot) { LoseConnectionOnStat = true };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);

        var thrown = await Record.ExceptionAsync(async () => await channel.InvokeScriptAsync(
            HostId, VmId, User, Password,
            script: FileTransferService.VerifyFileScript,
            args: new Dictionary<string, object?> { ["path"] = "/home/ubuntu/verify.bin" }));

        thrown.Should().NotBeNull();
        new ErrorMapper().MapException(thrown!).ErrorCode.Should().Be(ErrorCodes.SessionFailed);
    }

    [Fact]
    public async Task TranslatedIntentFailure_NamesDirectionVmAndPath()
    {
        var client = new ExecutingGuestClient(_guestRoot) { FailStatWithGenericError = true };
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);

        // The failure occurs inside a TRANSLATED INTENT rather than inside SFTP — the path that
        // previously left the caller with the inner message and no direction/VM/path.
        var thrown = await Record.ExceptionAsync(async () => await channel.InvokeScriptAsync(
            HostId, VmId, User, Password,
            script: FileTransferService.VerifyFileScript,
            args: new Dictionary<string, object?> { ["path"] = "/home/ubuntu/nowhere.bin" }));

        thrown.Should().NotBeNull();
        var surfaced = new ErrorMapper().MapException(thrown!).Error ?? string.Empty;
        surfaced.Should().Contain("guest")
            .And.Contain(VmId)
            .And.Contain("/home/ubuntu/nowhere.bin");
    }

    // ════════════════════════════════════════════════════════════════════
    // Fakes: they EXECUTE. See class remarks.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>A store whose session can never be opened, as an unreachable guest behaves.</summary>
    private sealed class UnopenableSessionStore : ISshSessionStore
    {
        public Task<ISshExecClient> GetOrCreateAsync(
            string hostId, string vmId, string username, string password, CancellationToken ct = default)
            => throw new SshSessionOpenException(
                vmId, $"Failed to open an SSH session to the Linux guest VM '{vmId}'. Connection refused.");

        public Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SingleClientSessionStore : ISshSessionStore
    {
        private readonly ISshExecClient _client;
        public SingleClientSessionStore(ISshExecClient client) => _client = client;

        public Task<ISshExecClient> GetOrCreateAsync(
            string hostId, string vmId, string username, string password, CancellationToken ct = default)
            => Task.FromResult(_client);

        public Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Models the real store's replacement behaviour: eviction disposes the cached client and the
    /// next call builds a brand-new one.
    /// </summary>
    private sealed class ReplacingSessionStore : ISshSessionStore
    {
        private readonly Func<ExecutingGuestClient> _factory;

        public ReplacingSessionStore(Func<ExecutingGuestClient> factory)
        {
            _factory = factory;
            Current = factory();
        }

        public ExecutingGuestClient Current { get; private set; }

        public Task<ISshExecClient> GetOrCreateAsync(
            string hostId, string vmId, string username, string password, CancellationToken ct = default)
            => Task.FromResult<ISshExecClient>(Current);

        public Task EvictAsync(string hostId, string vmId, CancellationToken ct = default)
        {
            Current.Dispose();
            Current = _factory();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Interprets the POSIX commands the channel emits and performs real file I/O against a temp
    /// directory rooted at the guest's "/". Nothing here merely records.
    /// </summary>
    private sealed class ExecutingGuestClient : ISshExecClient
    {
        private readonly string _root;

        public ExecutingGuestClient(string root)
        {
            _root = root;
            // '/tmp' and the login user's home always exist on a real guest, and the staging flows
            // upload into them without an mkdir. Seeding them keeps the missing-parent rejection
            // in UploadAsync meaningful rather than a blanket failure of every transfer; deeper
            // paths remain absent, which is what the ensure-parent guard exercises.
            Directory.CreateDirectory(Path.Combine(_root, "tmp"));
            Directory.CreateDirectory(Path.Combine(_root, "home", "ubuntu"));
        }

        /// <summary>Guest paths whose bytes live in memory (see <see cref="ExtraListingEntry"/>).</summary>
        private readonly Dictionary<string, byte[]> _virtualGuestFiles = new(StringComparer.Ordinal);

        public bool DenyUploads { get; init; }
        public bool FaultUploads { get; init; }
        public bool FaultDownloads { get; init; }
        public bool FailStatWithGenericError { get; init; }
        public int? TruncateUploadsTo { get; init; }

        /// <summary>Truncates only uploads whose guest path starts with this prefix.</summary>
        public string? TruncateUploadsUnder { get; init; }
        public int? TruncateDownloadsTo { get; init; }

        /// <summary>Set to make LoseConnectionOnUpload drop mid-transfer; cleared to let a retry pass.</summary>
        public bool LoseConnectionOnUpload { get; set; }

        /// <summary>
        /// Guest-listed entries that cannot exist on the host filesystem under those literal names
        /// (newline-bearing, case-colliding, or traversal paths), stored in memory so the listing
        /// reports them exactly as a POSIX guest would.
        /// </summary>
        public List<(string RelativePath, string Content)> ExtraListingEntries { get; } = new();

        /// <summary>
        /// Guest-listed DIRECTORIES that cannot coexist on the host under those literal names.
        /// </summary>
        public List<string> ExtraListingDirectories { get; } = new();

        /// <summary>Makes the listing's find leg fail after emitting partial output.</summary>
        public bool FailListingWithPartialOutput { get; init; }

        /// <summary>Runs once an upload has begun, so a test can cancel mid-transfer.</summary>
        public Action? OnUploadStarted { get; init; }

        /// <summary>Drops the connection while running a size-verification command.</summary>
        public bool LoseConnectionOnStat { get; init; }

        public bool IsConnected => true;

        internal string GuestPathToLocal(string guestPath)
            => Path.Combine(_root, guestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Commands seen, recorded IN ADDITION to executing them — never instead of.</summary>
        internal List<string> IssuedCommands { get; } = new();

        public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IssuedCommands.Add(commandText);
            return Task.FromResult(Interpret(commandText));
        }

        private SshCommandResult Interpret(string commandText)
        {
            if (commandText.StartsWith("echo ${TMPDIR:-/tmp}", StringComparison.Ordinal))
                return new SshCommandResult("/tmp\n", string.Empty, 0);

            if (commandText.StartsWith("mkdir -p ", StringComparison.Ordinal))
            {
                var target = GuestPathToLocal(Unquote(commandText["mkdir -p ".Length..]));
                Directory.CreateDirectory(target);
                return new SshCommandResult(string.Empty, string.Empty, 0);
            }

            // Probe/verify use -L for target metadata (#380); this fake has no symlinks to distinguish.
            if (commandText.StartsWith("stat -L -c %F ", StringComparison.Ordinal) ||
                commandText.StartsWith("stat -c %F ", StringComparison.Ordinal))
            {
                // The channel issues '%F && %s' as one command for the probe intent.
                var prefixLength = commandText.StartsWith("stat -L -c %F ", StringComparison.Ordinal)
                    ? "stat -L -c %F ".Length : "stat -c %F ".Length;
                var quoted = commandText[prefixLength..];
                var separator = quoted.IndexOf("&&", StringComparison.Ordinal);
                var pathPart = separator < 0 ? quoted : quoted[..separator];
                var local = GuestPathToLocal(Unquote(pathPart.Trim()));
                if (Directory.Exists(local))
                    return new SshCommandResult("directory\n", string.Empty, 0);
                if (File.Exists(local))
                    return new SshCommandResult(
                        $"regular file\n{new FileInfo(local).Length}\n", string.Empty, 0);
                return new SshCommandResult(
                    string.Empty, $"stat: cannot statx: No such file or directory\n", 1);
            }

            if (commandText.StartsWith("stat -L -c %s ", StringComparison.Ordinal) ||
                commandText.StartsWith("stat -c %s ", StringComparison.Ordinal))
            {
                var sizePrefixLength = commandText.StartsWith("stat -L -c %s ", StringComparison.Ordinal)
                    ? "stat -L -c %s ".Length : "stat -c %s ".Length;
                var guestPath = Unquote(commandText[sizePrefixLength..]);
                if (LoseConnectionOnStat)
                    throw new GuestConnectionLostException(
                        "the connection was lost while running a command");
                if (FailStatWithGenericError)
                    return new SshCommandResult(string.Empty, "stat: I/O error\n", 1);
                if (_virtualGuestFiles.TryGetValue(guestPath, out var virtualBytes))
                    return new SshCommandResult($"{virtualBytes.Length}\n", string.Empty, 0);
                var local = GuestPathToLocal(guestPath);
                if (!File.Exists(local))
                    return new SshCommandResult(
                        string.Empty, "stat: cannot statx: No such file or directory\n", 1);
                return new SshCommandResult($"{new FileInfo(local).Length}\n", string.Empty, 0);
            }

            var findIndex = commandText.IndexOf("find ", StringComparison.Ordinal);
            if (findIndex >= 0)
            {
                // Real pipeline semantics: a pipeline's status is its LAST command's unless
                // 'pipefail' is set. Modelling this is the whole point — a fake that returned
                // find's status directly would hide a listing failure the production shell
                // silently swallows.
                var pipefailEnabled = commandText.Contains("pipefail", StringComparison.Ordinal);

                var directoryArgument = commandText[(findIndex + "find ".Length)..];
                // -H affects only root links (#380); this fake's roots are real directories.
                if (directoryArgument.StartsWith("-H ", StringComparison.Ordinal))
                {
                    directoryArgument = directoryArgument["-H ".Length..];
                }
                var end = directoryArgument.IndexOf(" -mindepth", StringComparison.Ordinal);
                if (end < 0) end = directoryArgument.IndexOf(" -type", StringComparison.Ordinal);
                var guestDirectory = Unquote(
                    directoryArgument[..(end < 0 ? directoryArgument.Length : end)].Trim());
                var local = GuestPathToLocal(guestDirectory);

                var findFailed = !Directory.Exists(local) || FailListingWithPartialOutput;
                var relativePaths = new List<string>();
                if (Directory.Exists(local) && !FailListingWithPartialOutput)
                {
                    foreach (var file in Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories))
                    {
                        relativePaths.Add("f/" + Path.GetRelativePath(local, file).Replace('\\', '/'));
                    }
                    foreach (var directory in Directory.EnumerateDirectories(
                                 local, "*", SearchOption.AllDirectories))
                    {
                        relativePaths.Add("d/" + Path.GetRelativePath(local, directory).Replace('\\', '/'));
                    }
                }
                foreach (var extra in ExtraListingEntries)
                {
                    relativePaths.Add("f/" + extra.RelativePath);
                    _virtualGuestFiles[$"{guestDirectory.TrimEnd('/')}/{extra.RelativePath}"] =
                        Encoding.UTF8.GetBytes(extra.Content);
                }
                foreach (var extraDirectory in ExtraListingDirectories)
                {
                    relativePaths.Add("d/" + extraDirectory);
                }

                // The channel asks for a NUL-delimited, base64-wrapped listing so a name may
                // legally contain a newline.
                var payload = string.Concat(relativePaths.Select(entry => entry + "\0"));
                var encodedListing =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "\n";

                if (findFailed)
                {
                    // base64 still consumes find's partial output and exits 0, so without pipefail
                    // the pipeline reports success carrying a truncated listing.
                    // Deliberately NOT a denial or a missing path: those classify on their own
                    // merits, so only a neutral fault isolates the pipeline-status behaviour.
                    return pipefailEnabled
                        ? new SshCommandResult(encodedListing, "find: I/O error\n", 1)
                        : new SshCommandResult(encodedListing, string.Empty, 0);
                }
                return new SshCommandResult(encodedListing, string.Empty, 0);
            }

            if (commandText.StartsWith("rm -rf -- ", StringComparison.Ordinal) ||
                commandText.StartsWith("rm -f -- ", StringComparison.Ordinal))
            {
                var marker = commandText.StartsWith("rm -rf -- ", StringComparison.Ordinal)
                    ? "rm -rf -- ".Length : "rm -f -- ".Length;
                var local = GuestPathToLocal(Unquote(commandText[marker..]));
                if (Directory.Exists(local)) Directory.Delete(local, recursive: true);
                else if (File.Exists(local)) File.Delete(local);
                return new SshCommandResult(string.Empty, string.Empty, 0);
            }

            // Exec path: the channel wraps the caller's command for /bin/bash.
            return new SshCommandResult("hello\n", string.Empty, 0);
        }

        public async Task UploadAsync(
            string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Stands in for the point where real SFTP begins streaming: whatever cancels here must
            // still interrupt the write below rather than be observed only on entry.
            OnUploadStarted?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (DenyUploads)
                throw new UnauthorizedAccessException($"denied: {guestDestinationPath}");
            if (LoseConnectionOnUpload)
                throw new GuestConnectionLostException(
                    $"the connection dropped while writing {guestDestinationPath}");
            if (FaultUploads)
                throw new GuestTransferFailedException(
                    $"transport fault writing {guestDestinationPath}");

            var target = GuestPathToLocal(guestDestinationPath);
            // A real SFTP server has no implicit mkdir: writing under a missing parent fails with
            // "No such file". Creating the parent here would perform the production ensure-parent
            // step ON ITS BEHALF, leaving that guard green even with the step deleted. Only the
            // 'mkdir -p' interpretation above may create guest directories.
            var parentDirectory = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(parentDirectory))
            {
                throw new GuestTransferFailedException(
                    $"No such file or directory: the parent of {guestDestinationPath} does not " +
                    "exist on the guest.");
            }
            var bytes = await File.ReadAllBytesAsync(localSourcePath, ct);
            if (TruncateUploadsTo is int limit && bytes.Length > limit)
            {
                bytes = bytes[..limit];
            }
            if (TruncateUploadsUnder is string prefix &&
                guestDestinationPath.StartsWith(prefix, StringComparison.Ordinal) &&
                bytes.Length > 4)
            {
                bytes = bytes[..4];
            }
            await File.WriteAllBytesAsync(target, bytes, ct);
        }

        public async Task DownloadAsync(
            string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FaultDownloads)
                throw new GuestTransferFailedException(
                    $"transport fault reading {guestSourcePath}");

            byte[] bytes;
            if (_virtualGuestFiles.TryGetValue(guestSourcePath, out var virtualBytes))
            {
                bytes = virtualBytes;
            }
            else
            {
                var source = GuestPathToLocal(guestSourcePath);
                if (!File.Exists(source))
                    throw new FileNotFoundException($"absent on guest: {guestSourcePath}", guestSourcePath);
                bytes = await File.ReadAllBytesAsync(source, ct);
            }

            if (TruncateDownloadsTo is int limit && bytes.Length > limit)
            {
                bytes = bytes[..limit];
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);
            await File.WriteAllBytesAsync(localDestinationPath, bytes, ct);
        }

        internal int Disposals { get; private set; }

        public void Dispose() => Disposals++;

        private static string Unquote(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            {
                return trimmed[1..^1].Replace("'\\''", "'");
            }
            return trimmed;
        }
    }
}
