using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #380: unlike the #279 fake, this fake executes emitted commands with link semantics:
/// stat needs -L for target metadata; find needs -H to descend a linked root.
/// Removing either flag must fail target-content transfer tests.
/// </summary>
public class Issue380SymlinkProbeRealSeamTests : IDisposable
{
    private const string HostId = "host-380";
    private const string VmId = "38038038-0380-4380-8380-380380380380";
    private const string User = "ubuntu";
    private const string Password = "P@ssw0rd-380";

    /// <summary>The live /etc/os-release measurement from issue #380: link 21 bytes, target 400.</summary>
    private const string LinkPath = "/etc/os-release";
    private const string TargetPath = "../usr/lib/os-release";
    private const int TargetSize = 400;

    private readonly string _hostRoot;

    public Issue380SymlinkProbeRealSeamTests()
        => _hostRoot = Directory.CreateTempSubdirectory("issue380_host_").FullName;

    public void Dispose()
    {
        try { if (Directory.Exists(_hostRoot)) Directory.Delete(_hostRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static (FileTransferService Service, SymlinkAwareGuestClient Client) BuildStack(
        SymlinkAwareGuestClient client)
    {
        var channel = new SshGuestChannel(
            new SingleClientSessionStore(client), NullLogger<SshGuestChannel>.Instance);
        var service = new FileTransferService(
            channel, new StubHostResolver(), NullLogger<FileTransferService>.Instance);
        return (service, client);
    }

    private static SymlinkAwareGuestClient GuestWithOsReleaseLink()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddFile("/usr/lib/os-release", new string('o', TargetSize));
        client.AddSymlink(LinkPath, TargetPath);
        return client;
    }

    [Fact]
    public async Task CopyFileFromGuest_WhenRequestedPathIsSymlink_TransfersTargetContent()
    {
        var (service, _) = BuildStack(GuestWithOsReleaseLink());
        var destination = Path.Combine(_hostRoot, "os-release");

        var result = await service.CopyFromGuestAsync(
            HostId, VmId, LinkPath, destination, username: User, password: Password);

        result.BytesTransferred.Should().Be(TargetSize);
        new FileInfo(destination).Length.Should().Be(TargetSize);
    }

    [Fact]
    public async Task ProbeOfSymlink_SizesTheTarget_NotTheLinkItself()
    {
        var client = GuestWithOsReleaseLink();
        var (service, _) = BuildStack(client);

        await service.CopyFromGuestAsync(
            HostId, VmId, LinkPath, Path.Combine(_hostRoot, "probe-sized"),
            username: User, password: Password);

        // The link's own length (21) is what the defect measured against a 400-byte download.
        client.IssuedCommands.Should().Contain(command =>
            command.Contains("stat -L -c %F", StringComparison.Ordinal));
        client.LastProbeReportedSize.Should().Be(TargetSize);
    }

    [Fact]
    public async Task CopyFileFromGuest_WhenRequestedPathIsSymlink_DoesNotReportSizeMismatch()
    {
        var (service, _) = BuildStack(GuestWithOsReleaseLink());

        var act = async () => await service.CopyFromGuestAsync(
            HostId, VmId, LinkPath, Path.Combine(_hostRoot, "no-mismatch"),
            username: User, password: Password);

        await act.Should().NotThrowAsync<GuestTransferFailedException>();
    }

    [Fact]
    public async Task CopyFileFromGuest_RegularFileControl_StillTransfersAndVerifies()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddFile("/usr/lib/os-release", new string('o', TargetSize));
        var (service, _) = BuildStack(client);
        var destination = Path.Combine(_hostRoot, "direct");

        var result = await service.CopyFromGuestAsync(
            HostId, VmId, "/usr/lib/os-release", destination,
            username: User, password: Password);

        result.BytesTransferred.Should().Be(TargetSize);
        result.Verified.Should().BeTrue();
    }

    // The fix changes the measured object, not the requirement to reject a short read.

    [Fact]
    public async Task CopyFileFromGuest_GenuineShortRead_StillFailsAsTransferFailure()
    {
        var client = new SymlinkAwareGuestClient { TruncateDownloadsTo = 10 };
        client.AddFile("/usr/lib/os-release", new string('o', TargetSize));
        client.AddSymlink(LinkPath, TargetPath);
        var (service, _) = BuildStack(client);

        var act = async () => await service.CopyFromGuestAsync(
            HostId, VmId, LinkPath, Path.Combine(_hostRoot, "short"),
            username: User, password: Password);

        (await act.Should().ThrowAsync<GuestTransferFailedException>())
            .Which.Message.Should().Contain("400 bytes but 10 bytes were received");
    }

    [Fact]
    public async Task CopyFileFromGuest_BrokenSymlink_FailsAsNotFound_NotSuccess()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddSymlink("/etc/dangling", "/usr/lib/absent");
        var (service, _) = BuildStack(client);

        var act = async () => await service.CopyFromGuestAsync(
            HostId, VmId, "/etc/dangling", Path.Combine(_hostRoot, "broken"),
            username: User, password: Password);

        await act.Should().ThrowAsync<GuestTransferPathNotFoundException>();
    }

    [Fact]
    public async Task SymlinkToDirectory_ClassifiesAsDirectory_AndCopiesItsContents()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddFile("/var/real/alpha.txt", "alpha");
        client.AddFile("/var/real/beta.txt", "beta");
        client.AddSymlink("/var/linked", "/var/real");
        var (service, _) = BuildStack(client);
        var destination = Path.Combine(_hostRoot, "pulled-dir");

        var result = await service.CopyFromGuestAsync(
            HostId, VmId, "/var/linked", destination,
            username: User, password: Password);

        // Without -H, a linked root yields an empty archive; FileCount is always 0 for directory pulls.
        result.IsDirectory.Should().BeTrue();
        File.Exists(Path.Combine(destination, "alpha.txt")).Should().BeTrue();
        File.Exists(Path.Combine(destination, "beta.txt")).Should().BeTrue();
        result.BytesTransferred.Should().Be("alpha".Length + "beta".Length);
    }

    [Fact]
    public async Task DirectoryListing_IssuesFindWithRootDereference()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddFile("/var/real/alpha.txt", "alpha");
        client.AddSymlink("/var/linked", "/var/real");
        var (service, _) = BuildStack(client);

        await service.CopyFromGuestAsync(
            HostId, VmId, "/var/linked", Path.Combine(_hostRoot, "flagged"),
            username: User, password: Password);

        client.IssuedCommands.Should().Contain(command =>
            command.Contains("find -H ", StringComparison.Ordinal));
        // -L would follow nested links, outside this fix's scope.
        client.IssuedCommands.Should().NotContain(command =>
            command.Contains("find -L ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UploadThroughDestinationSymlink_VerifiesTargetSize()
    {
        var client = new SymlinkAwareGuestClient();
        client.AddFile("/opt/real-config", "seed");
        client.AddSymlink("/etc/config", "/opt/real-config");
        var (service, _) = BuildStack(client);
        var source = Path.Combine(_hostRoot, "config.new");
        await File.WriteAllBytesAsync(source, Encoding.UTF8.GetBytes(new string('c', 128)));

        var result = await service.CopyToGuestAsync(
            HostId, VmId, source, "/etc/config",
            isDirectory: false, username: User, password: Password);

        result.BytesTransferred.Should().Be(128);
        result.Verified.Should().BeTrue();
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
    /// Unlike stat/find, SFTP always dereferences; flag removal must change this in-memory guest's behavior.
    /// </summary>
    private sealed class SymlinkAwareGuestClient : ISshExecClient
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/", "/tmp" };
        private readonly Dictionary<string, string> _symlinks = new(StringComparer.Ordinal);

        public int? TruncateDownloadsTo { get; init; }
        public bool IsConnected => true;
        internal List<string> IssuedCommands { get; } = new();
        internal long? LastProbeReportedSize { get; private set; }

        internal void AddFile(string guestPath, string content)
        {
            _files[guestPath] = Encoding.UTF8.GetBytes(content);
            for (var parent = Parent(guestPath); parent.Length > 0; parent = Parent(parent))
            {
                _directories.Add(parent);
            }
        }

        internal void AddSymlink(string linkPath, string targetPath)
        {
            _symlinks[linkPath] = targetPath;
            for (var parent = Parent(linkPath); parent.Length > 0; parent = Parent(parent))
            {
                _directories.Add(parent);
            }
        }

        private static string Parent(string path)
        {
            var cut = path.LastIndexOf('/');
            return cut <= 0 ? string.Empty : path[..cut];
        }

        /// <summary>
        /// Resolves each component like the kernel, so paths beneath linked roots reach their content.
        /// </summary>
        private string? Resolve(string path)
        {
            var current = string.Empty;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "/" + segment;
                var hops = 0;
                while (_symlinks.TryGetValue(current, out var target) && hops++ < 8)
                {
                    current = target.StartsWith('/')
                        ? target
                        : NormalizeRelative(Parent(current), target);
                }
            }
            return _files.ContainsKey(current) || _directories.Contains(current) ? current : null;
        }

        private static string NormalizeRelative(string baseDirectory, string relative)
        {
            var segments = new List<string>(
                baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                }
                segments.Add(segment);
            }
            return "/" + string.Join('/', segments);
        }

        public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IssuedCommands.Add(commandText);
            return Task.FromResult(Interpret(commandText));
        }

        private static SshCommandResult NotFound()
            => new(string.Empty, "stat: cannot statx: No such file or directory\n", 1);

        private SshCommandResult Interpret(string commandText)
        {
            if (commandText.StartsWith("echo ${TMPDIR:-/tmp}", StringComparison.Ordinal))
                return new SshCommandResult("/tmp\n", string.Empty, 0);

            if (commandText.StartsWith("mkdir -p ", StringComparison.Ordinal))
            {
                _directories.Add(Unquote(commandText["mkdir -p ".Length..]));
                return new SshCommandResult(string.Empty, string.Empty, 0);
            }

            if (commandText.StartsWith("rm -rf -- ", StringComparison.Ordinal) ||
                commandText.StartsWith("rm -f -- ", StringComparison.Ordinal))
            {
                var marker = commandText.StartsWith("rm -rf -- ", StringComparison.Ordinal)
                    ? "rm -rf -- ".Length : "rm -f -- ".Length;
                _files.Remove(Unquote(commandText[marker..]));
                return new SshCommandResult(string.Empty, string.Empty, 0);
            }

            var statIndex = commandText.IndexOf("stat ", StringComparison.Ordinal);
            if (statIndex == 0)
            {
                var dereference = commandText.Contains(" -L ", StringComparison.Ordinal);
                var wantsType = commandText.Contains("%F", StringComparison.Ordinal);
                var firstQuote = commandText.IndexOf('\'');
                var secondQuote = commandText.IndexOf('\'', firstQuote + 1);
                var path = commandText[(firstQuote + 1)..secondQuote];
                return StatOf(path, dereference, wantsType);
            }

            var findIndex = commandText.IndexOf("find ", StringComparison.Ordinal);
            if (findIndex >= 0)
            {
                var dereferenceRoot = commandText.Contains("find -H ", StringComparison.Ordinal) ||
                    commandText.Contains("find -L ", StringComparison.Ordinal);
                var firstQuote = commandText.IndexOf('\'');
                var secondQuote = commandText.IndexOf('\'', firstQuote + 1);
                var root = commandText[(firstQuote + 1)..secondQuote];

                var effectiveRoot = root;
                if (_symlinks.ContainsKey(root))
                {
                    // GNU find without -H/-L reports the link itself and descends nothing.
                    if (!dereferenceRoot)
                    {
                        return new SshCommandResult(
                            Convert.ToBase64String(Array.Empty<byte>()) + "\n", string.Empty, 0);
                    }
                    effectiveRoot = Resolve(root) ?? root;
                }

                var prefix = effectiveRoot.TrimEnd('/') + "/";
                var entries = _files.Keys
                    .Where(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(candidate => "f/" + candidate[prefix.Length..])
                    .ToList();
                var payload = string.Concat(entries.Select(entry => entry + "\0"));
                return new SshCommandResult(
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "\n", string.Empty, 0);
            }

            return new SshCommandResult("hello\n", string.Empty, 0);
        }

        private SshCommandResult StatOf(string path, bool dereference, bool wantsType)
        {
            // Without -L, the kernel still resolves intermediate links; the enumerated-file site needs no -L.
            path = Parent(path).Length > 0
                ? (Resolve(Parent(path)) ?? Parent(path)) + path[Parent(path).Length..]
                : path;

            if (_symlinks.TryGetValue(path, out var target) && !dereference)
            {
                // A link's own %s is its target string's length — the 21 bytes of issue #380.
                var linkSize = target.Length;
                if (wantsType) LastProbeReportedSize = linkSize;
                return new SshCommandResult(
                    wantsType ? $"symbolic link\n{linkSize}\n" : $"{linkSize}\n", string.Empty, 0);
            }

            var resolved = dereference ? Resolve(path) : path;
            if (resolved is null) return NotFound();
            if (_directories.Contains(resolved) && !_files.ContainsKey(resolved))
                return new SshCommandResult("directory\n", string.Empty, 0);
            if (!_files.TryGetValue(resolved, out var bytes)) return NotFound();
            if (wantsType) LastProbeReportedSize = bytes.Length;
            return new SshCommandResult(
                wantsType ? $"regular file\n{bytes.Length}\n" : $"{bytes.Length}\n", string.Empty, 0);
        }

        public async Task UploadAsync(
            string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        {
            var bytes = await File.ReadAllBytesAsync(localSourcePath, ct);
            // SFTP writes through destination links.
            var resolved = _symlinks.TryGetValue(guestDestinationPath, out _)
                ? Resolve(guestDestinationPath) ?? guestDestinationPath
                : guestDestinationPath;
            _files[resolved] = bytes;
        }

        public async Task DownloadAsync(
            string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        {
            var resolved = Resolve(guestSourcePath);
            if (resolved is null || !_files.TryGetValue(resolved, out var bytes))
                throw new FileNotFoundException($"absent on guest: {guestSourcePath}", guestSourcePath);
            if (TruncateDownloadsTo is int limit && bytes.Length > limit)
                bytes = bytes[..limit];
            Directory.CreateDirectory(Path.GetDirectoryName(localDestinationPath)!);
            await File.WriteAllBytesAsync(localDestinationPath, bytes, ct);
        }

        public void Dispose() { }

        private static string Unquote(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\''
                ? trimmed[1..^1].Replace("'\\''", "'")
                : trimmed;
        }
    }
}
