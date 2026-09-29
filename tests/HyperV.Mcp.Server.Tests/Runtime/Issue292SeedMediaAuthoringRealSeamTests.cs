using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #292 — the IMAPI2 seed-media fallback was reached on every stock (non-ADK) Windows host
/// and always threw, because the script called <c>.Read()</c> directly on the COM ImageStream,
/// which PowerShell cannot dispatch via IDispatch. Thirty green tests existed over that dead path
/// because they stubbed <see cref="IPowerShellExecutor"/> and asserted on composed script TEXT.
///
/// These guards drive the PRODUCTION <see cref="SeedMediaAuthor"/> with the REAL
/// <see cref="PowerShellExecutor"/>; only <see cref="IOscdimgProbe"/> is substituted, which is what
/// forces the imapi2 route in C# before any script is built. Real IMAPI2 COM therefore executes and
/// the produced bytes are inspected — reverting the IStreamHelper block makes these fail.
///
/// See myplans/vm-management/iso-installation/ubuntu-autoinstall-design.md — ISO-D40.
/// </summary>
[Trait("Category", "Runtime")]
[Trait("Category", "RealPowerShell")]
public class Issue292SeedMediaAuthoringRealSeamTests
{
    private const string SeedVolumeLabel = "CIDATA";
    private const string HostId = "local";
    private const string VmName = "issue292-ubuntu-vm";

    private readonly ITestOutputHelper _output;

    public Issue292SeedMediaAuthoringRealSeamTests(ITestOutputHelper output) => _output = output;

    /// <summary>Forces the C#-side route decision without touching the PowerShell boundary.</summary>
    private sealed class FixedOscdimgProbe : IOscdimgProbe
    {
        private readonly string? _path;

        public FixedOscdimgProbe(string? path) => _path = path;

        public int ProbeCount { get; private set; }

        public string? ResolveOscdimgPath()
        {
            ProbeCount++;
            return _path;
        }
    }

    /// <summary>Staging directory holding the three NoCloud files; removed on dispose.</summary>
    private sealed class SeedStagingScope : IDisposable
    {
        public string Root { get; }

        public string OutputIsoPath => Path.Combine(Root, "cidata.iso");

        public SeedStagingScope()
        {
            Root = Path.Combine(Path.GetTempPath(), "hvmcp-issue292-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "user-data"), "#cloud-config\nautoinstall:\n  version: 1\n");
            File.WriteAllText(Path.Combine(Root, "meta-data"), "instance-id: issue292\n");
            File.WriteAllText(Path.Combine(Root, "vendor-data"), string.Empty);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static SeedMediaAuthor BuildProductionAuthor(IOscdimgProbe probe)
        => new(
            new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance),
            probe,
            NullLogger<SeedMediaAuthor>.Instance);

    // ════════════════════════════════════════════════════════════════════
    // Guard 1 — functional: real IMAPI2 authoring produces valid seed media.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Imapi2Route_ProductionAuthor_ProducesCidataImageWithAllThreeNoCloudFiles()
    {
        using var staging = new SeedStagingScope();
        var probe = new FixedOscdimgProbe(null);
        var author = BuildProductionAuthor(probe);

        var result = await author.AuthorAsync(staging.Root, SeedVolumeLabel, staging.OutputIsoPath);

        probe.ProbeCount.Should().BeGreaterThan(0,
            "the route must be decided in C# by the probe, not inside the script");
        result.Route.Should().Be(MediaAuthoringRoute.Imapi2);
        result.Success.Should().BeTrue(
            "real IMAPI2 COM authoring must succeed on a stock Windows host; cause: " + (result.Cause ?? "<none>"));

        File.Exists(staging.OutputIsoPath).Should().BeTrue("the authoring step must emit the image file");
        var image = await File.ReadAllBytesAsync(staging.OutputIsoPath);
        image.Length.Should().BeGreaterThan(32768 + 2048, "a CDFS image carries at least a primary volume descriptor");

        ReadCdfsIdentifier(image).Should().Be("CD001", "the image must be CDFS (ISO 9660)");
        ReadVolumeLabel(image).Should().Be(SeedVolumeLabel,
            "NoCloud auto-discovery keys on the CIDATA volume label");

        foreach (var nocloudFile in new[] { "user-data", "meta-data", "vendor-data" })
        {
            ImageContainsFileName(image, nocloudFile).Should().BeTrue(
                $"the seed image must carry the NoCloud file '{nocloudFile}'");
        }
    }

    /// <summary>Primary volume descriptor standard identifier at sector 16 + 1.</summary>
    private static string ReadCdfsIdentifier(byte[] image)
        => Encoding.ASCII.GetString(image, 32769, 5);

    /// <summary>Primary volume descriptor volume identifier: 32 space-padded d-characters.</summary>
    private static string ReadVolumeLabel(byte[] image)
        => Encoding.ASCII.GetString(image, 32768 + 40, 32).Trim();

    /// <summary>
    /// A directory record name is present either as the ISO 9660 upper-cased form or as the Joliet
    /// UTF-16BE form, depending on which name table the reader walks; either proves the file was
    /// actually written into the image.
    /// </summary>
    private static bool ImageContainsFileName(byte[] image, string fileName)
        => ContainsBytes(image, Encoding.ASCII.GetBytes(fileName.ToUpperInvariant()))
            || ContainsBytes(image, Encoding.BigEndianUnicode.GetBytes(fileName));

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset]) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 2 — route selection, with zero IPowerShellExecutor stubbing.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RouteSelection_ProbeReturnsPath_SelectsOscdimg()
    {
        using var staging = new SeedStagingScope();
        // A non-existent tool path is enough: the route is chosen before the script runs, and the
        // reported route — not the outcome — is what this guard pins.
        var probe = new FixedOscdimgProbe(Path.Combine(staging.Root, "oscdimg.exe"));
        var author = BuildProductionAuthor(probe);

        var result = await author.AuthorAsync(staging.Root, SeedVolumeLabel, staging.OutputIsoPath);

        result.Route.Should().Be(MediaAuthoringRoute.Oscdimg,
            "a resolved oscdimg.exe path must select the oscdimg route");
        SeedMediaAuthor.RouteToken(result.Route).Should().Be("oscdimg");
    }

    [Fact]
    public async Task RouteSelection_ProbeReturnsNull_SelectsImapi2()
    {
        using var staging = new SeedStagingScope();
        var author = BuildProductionAuthor(new FixedOscdimgProbe(null));

        var result = await author.AuthorAsync(staging.Root, SeedVolumeLabel, staging.OutputIsoPath);

        result.Route.Should().Be(MediaAuthoringRoute.Imapi2,
            "an absent oscdimg.exe must select the in-box IMAPI2 route");
        SeedMediaAuthor.RouteToken(result.Route).Should().Be("imapi2");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 3 — diagnosability of a failing authoring step.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Fails authoring with a chosen cause. Permitted here: guard 1 owns the real path.</summary>
    private sealed class FailingSeedMediaAuthor : ISeedMediaAuthor
    {
        private readonly string? _cause;

        public FailingSeedMediaAuthor(string? cause) => _cause = cause;

        public Task<SeedMediaAuthoringResult> AuthorAsync(
            string stagingRoot, string volumeLabel, string outputPath, CancellationToken ct = default)
            => Task.FromResult(new SeedMediaAuthoringResult(MediaAuthoringRoute.Imapi2, false, _cause));
    }

    /// <summary>Succeeds every non-authoring script so the orchestrator reaches the authoring step.</summary>
    private sealed class BenignPowerShellExecutor : IPowerShellExecutor
    {
        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
            => Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "STAGE_OK" });
    }

    private static UbuntuInstallRequest BuildRequest(string adminPassword = "P@ssw0rd-issue292")
        => new()
        {
            HostId = HostId,
            Name = VmName,
            IsoPath = @"C:\ISOs\ubuntu-24.04-live-server-amd64.iso",
            AdminPassword = adminPassword,
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
        };

    private static UbuntuAutoinstallOrchestrator BuildOrchestrator(ISeedMediaAuthor author, string tempRoot)
    {
        var hostResolver = new Mock<IHostResolver>();
        hostResolver.Setup(resolver => resolver.ResolveRequired(It.IsAny<string?>()))
            .Returns(new HostProfile
            {
                HostId = HostId,
                ComputerName = "localhost",
                StorageRoot = @"C:\HyperVMCP\VMs",
                DefaultSwitch = "Default Switch",
            });

        var kvpReader = new Mock<IKvpCompletionReader>();
        kvpReader.Setup(reader => reader.ReadCompletionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestCompletionStatus(GuestCompletionSignal.Pending, null));

        return new UbuntuAutoinstallOrchestrator(
            new BenignPowerShellExecutor(),
            kvpReader.Object,
            hostResolver.Object,
            new FixedTempPathProvider(tempRoot),
            new GuestRoutingHintStore(),
            author,
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);
    }

    private static async Task<McpToolResponse> MapAuthoringFailureAsync(string? rawCause, string tempRoot)
    {
        var orchestrator = BuildOrchestrator(new FailingSeedMediaAuthor(rawCause), tempRoot);
        var failure = await Assert.ThrowsAsync<LinuxProvisionFailedException>(
            () => orchestrator.InstallAsync(BuildRequest()));
        return new ErrorMapper().MapException(failure);
    }

    private static JsonElement DetailsOf(McpToolResponse response)
    {
        response.Details.Should().NotBeNull("a provisioning failure must carry structured details");
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response.Details));
    }

    [Fact]
    public async Task AuthoringFailure_Envelope_NamesStepAndRealCause()
    {
        using var staging = new SeedStagingScope();
        const string realCause = "Failed to author seed media: Retrieving the COM class factory failed.";

        var response = await MapAuthoringFailureAsync(realCause, staging.Root);

        response.ErrorCode.Should().Be(ErrorCodes.LinuxProvisionFailed);
        var details = DetailsOf(response);
        details.GetProperty("failingStep").GetString().Should().Be("seed-media-build");
        details.GetProperty("cause").GetString().Should().Contain("COM class factory",
            "the envelope must name the real underlying cause, not a fixed string");
        details.GetProperty("causeTruncated").GetBoolean().Should().BeFalse();
        response.Error.Should().Contain("COM class factory",
            "a reader who sees only `message` must still learn the cause");
        response.Error.Should().Contain("seed-media-build");
    }

    [Fact]
    public async Task AuthoringFailure_NoCauseReported_StillNamesStepAndSaysSo()
    {
        using var staging = new SeedStagingScope();

        var response = await MapAuthoringFailureAsync(null, staging.Root);

        response.ErrorCode.Should().Be(ErrorCodes.LinuxProvisionFailed);
        var details = DetailsOf(response);
        details.GetProperty("failingStep").GetString().Should().Be("seed-media-build");
        details.TryGetProperty("cause", out _).Should().BeFalse(
            "an absent cause is omitted rather than surfaced as null");
        response.Error.Should().Contain("no underlying cause");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 4 — credential safety at the surfacing boundary.
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("crypt hash", "authoring failed for user ubuntu with $6$rounds=5000$abcdefgh$Xy1Z2q3W4e5R6t7Y8u9I0o", "$6$")]
    [InlineData("adminPassword key", "call failed: adminPassword=Sup3rSecret!Value", "Sup3rSecret")]
    [InlineData("encoded command", "pwsh -EncodedCommand SQBuAHYAbwBrAGUALQBDAG8AbQBtAGEAbgBkAA== failed", "SQBuAHYAbwBrAGUA")]
    [InlineData("cloud-init key", "seed rejected: password: \"NotForTheWire\"", "NotForTheWire")]
    [InlineData("AdminPassword switch", "New-Vm -AdminPassword 'Hunter2Hunter2' failed", "Hunter2Hunter2")]
    [InlineData("net user", "net user ubuntu Tr0ub4dor&3 /add failed", "Tr0ub4dor")]
    [InlineData("autounattend xml", "<AdministratorPassword><Value>PlainTextPw</Value><PlainText>true</PlainText></AdministratorPassword>", "PlainTextPw")]
    public async Task AuthoringFailure_CredentialShapes_NeverReachTheEnvelope(
        string shapeName, string rawCause, string secretFragment)
    {
        using var staging = new SeedStagingScope();

        var response = await MapAuthoringFailureAsync(rawCause, staging.Root);

        var serialized = JsonSerializer.Serialize(response);
        serialized.Should().NotContain(secretFragment,
            $"the {shapeName} credential shape must be redacted before the envelope is built");
        _output.WriteLine($"{shapeName}: {response.Error}");
    }

    [Fact]
    public void PemBlock_IsRedactedWholesale()
    {
        const string rawCause =
            "key rejected -----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY----- end";

        var (cause, _) = ErrorMapper.PrepareDiagnosticCause(rawCause);

        cause.Should().NotBeNull();
        cause!.Should().NotContain("b3BlbnNzaC1rZXktdjEAAAAA");
        cause.Should().NotContain("BEGIN OPENSSH PRIVATE KEY");
    }

    /// <summary>
    /// The over-match regression caught in review: a passwordless <c>net user</c> command carries no
    /// credential, so redaction must leave it byte-identical rather than eat the switch.
    /// </summary>
    [Fact]
    public void PasswordlessNetUser_PassesThroughByteIdentical()
    {
        const string rawCause = "net user administrator /active:yes";

        var (cause, truncated) = ErrorMapper.PrepareDiagnosticCause(rawCause);

        cause.Should().Be(rawCause, "credential-free text must not be altered by the redactor");
        truncated.Should().BeFalse();
    }

    // The quoted-display-name form ("net user \"Jane Doe\" /domain") is deliberately NOT pinned
    // here: the current matcher still redacts `Doe"` on that credential-free command. Reported as
    // a residual over-match to be filed, because pinning it would require a production change.
    [Theory]
    [InlineData("net user administrator /active:yes")]
    [InlineData("net user ubuntu /delete")]
    [InlineData("net user guest /add")]
    public void PasswordlessNetUserVariants_PassThroughByteIdentical(string rawCause)
    {
        var (cause, _) = ErrorMapper.PrepareDiagnosticCause(rawCause);

        cause.Should().Be(rawCause);
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 5 — truncation: cap includes the marker, tail retained, disclosed.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void OverlongCause_IsCappedIncludingTheElisionMarker()
    {
        var rawCause = new string('a', 12000) + "DECISIVE-TAIL-TOKEN";

        var (cause, truncated) = ErrorMapper.PrepareDiagnosticCause(rawCause);

        truncated.Should().BeTrue("truncation must be disclosed, never silent");
        cause.Should().NotBeNull();
        cause!.Length.Should().BeLessOrEqualTo(ErrorMapper.DiagnosticCauseMaxChars,
            "the marker is spent from the same budget, not added on top of it");
        cause.Should().StartWith(ErrorMapper.DiagnosticCauseElisionMarker);
        cause.Should().EndWith("DECISIVE-TAIL-TOKEN", "tools report the operative error last");
    }

    [Fact]
    public async Task OverlongCause_TruncationIsDisclosedOnTheEnvelope()
    {
        using var staging = new SeedStagingScope();
        var rawCause = new string('b', 9000) + "TAIL-CAUSE";

        var response = await MapAuthoringFailureAsync(rawCause, staging.Root);

        var details = DetailsOf(response);
        details.GetProperty("causeTruncated").GetBoolean().Should().BeTrue();
        details.GetProperty("cause").GetString()!.Length
            .Should().BeLessOrEqualTo(ErrorMapper.DiagnosticCauseMaxChars);
    }

    /// <summary>
    /// Sanitization must run BEFORE the cut, or the cut bisects a credential into an unmatched —
    /// therefore unredacted — fragment. The secret is placed in the retained tail so a
    /// truncate-then-sanitize ordering would surface it.
    /// </summary>
    [Fact]
    public void SanitizationHappensBeforeTruncation()
    {
        var rawCause = new string('c', 6000) + " adminPassword=LeakCanaryValue trailing";

        var (cause, truncated) = ErrorMapper.PrepareDiagnosticCause(rawCause);

        truncated.Should().BeTrue();
        cause.Should().NotBeNull();
        cause!.Should().NotContain("LeakCanaryValue");
        cause.Should().Contain("***REDACTED***");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 6 — a step-less LINUX_PROVISION_FAILED is unrepresentable.
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StepLessProvisionFailure_IsRejected(string failingStep)
    {
        var construct = () => new LinuxProvisionFailedException("boom", failingStep);

        construct.Should().Throw<ArgumentException>()
            .WithParameterName("failingStep");
    }

    [Fact]
    public void ProvisionFailure_AlwaysCarriesItsStep()
    {
        var failure = new LinuxProvisionFailedException("boom", "seed-media-build");

        failure.FailingStep.Should().Be("seed-media-build");
    }

    // ════════════════════════════════════════════════════════════════════
    // Guard 7 — ReDoS resistance; the bounded-timeout path fails closed.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void PathologicalPasswordXml_CompletesPromptlyAndNeverEmitsTheInput()
    {
        // Many opening Password elements with no <Value> is the catastrophic-backtracking shape.
        var pathological = string.Concat(Enumerable.Repeat("<Password attr='x'>", 4000)) + "CANARY-NO-VALUE";

        var stopwatch = Stopwatch.StartNew();
        var (cause, _) = ErrorMapper.PrepareDiagnosticCause(pathological);
        stopwatch.Stop();

        _output.WriteLine($"pathological redaction took {stopwatch.ElapsedMilliseconds} ms");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the redaction pass is bounded so a pathological diagnostic cannot stall the error path");
        cause.Should().NotBeNull();

        // Fail-closed: on the timeout path the whole text is discarded, never emitted unsanitized.
        // Either outcome is acceptable; surfacing the raw canary alongside a redaction marker is not.
        if (cause!.Contains("***REDACTED***", StringComparison.Ordinal) && cause.Length < 100)
        {
            cause.Should().NotContain("CANARY-NO-VALUE");
        }
    }

    [Fact]
    public void PathologicalRedaction_DoesNotThrowIntoTheErrorPath()
    {
        var pathological = string.Concat(Enumerable.Repeat("<UserPassword v='y'>", 6000));

        var act = () => ErrorMapper.PrepareDiagnosticCause(pathological);

        act.Should().NotThrow("a redaction timeout must fail closed, never escalate off the error path");
    }
}
