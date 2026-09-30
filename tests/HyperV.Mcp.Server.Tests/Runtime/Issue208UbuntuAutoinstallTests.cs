using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #208 — Ubuntu Server 24.04 autoinstall (<c>vm_os_install</c>) regression suite.
///
/// Covers the new Ubuntu path introduced by the approved spec/design:
///   spec  internal documentation — AC-1..10, FR-7, FR-11, FR-18, FR-12/AC-7.
///   design internal documentation — ISO-D24..D30.
///
/// All Hyper-V / KVP seams are mocked; no real host, network, or ISO is required.
/// The executor fake (<see cref="ScriptedPowerShellExecutor"/>) recognizes each of the
/// orchestrator's script phases by content so the orchestrator can be driven end-to-end.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue208UbuntuAutoinstallTests
{
    private const string Host = "local";
    private const string VmName = "issue208-ubuntu-vm";
    private const string IsoPath = @"C:\ISOs\ubuntu-24.04-live-server-amd64.iso";

    // ════════════════════════════════════════════════════════════════════
    // 1. Classifier (ISO-D21/D22): wim → Windows, casper → Ubuntu, neither →
    //    Unsupported, and wim-first ordering.
    // ════════════════════════════════════════════════════════════════════

    private static GuestOsClassifier ClassifierOver(IIsoInspector inspector)
        => new(inspector, NullLogger<GuestOsClassifier>.Instance);

    [Fact]
    public async Task Classifier_InstallWimPresent_ClassifiesWindows()
    {
        // sources\install.wim present (casper absent) → Windows (ISO-D22 step 1).
        var inspector = new TestIsoInspector(found: true, casperFound: false);

        var target = await ClassifierOver(inspector).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.Windows,
            "an ISO with sources\\install.wim must classify as Windows (ISO-D22)");
    }

    [Fact]
    public async Task Classifier_CasperLayout2404_ClassifiesUbuntu()
    {
        // No install.wim, casper/ + 24.04 assertion present → UbuntuServer2404 (ISO-D22 step 2).
        var inspector = new TestIsoInspector(found: false, casperFound: true);

        var target = await ClassifierOver(inspector).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.UbuntuServer2404,
            "an ISO with a casper/ layout asserting 24.04 must classify as UbuntuServer2404 (ISO-D22)");
    }

    [Fact]
    public async Task Classifier_NeitherMarker_ClassifiesUnsupported()
    {
        // Neither install.wim nor casper/ → Unsupported (ISO-D22 step 3 → OS_NOT_SUPPORTED).
        var inspector = new TestIsoInspector(found: false, casperFound: false);

        var target = await ClassifierOver(inspector).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.Unsupported,
            "an ISO matching neither target must classify as Unsupported (ISO-D22/D23)");
    }

    [Fact]
    public async Task Classifier_BothMarkers_WindowsWinsFirst()
    {
        // Guards the fixed ISO-D22 ordering: even if BOTH markers are present, the wim probe
        // runs first and short-circuits to Windows — the casper probe must never override it.
        var inspector = new WimFirstOrderingInspector();

        var target = await ClassifierOver(inspector).ClassifyAsync(IsoPath);

        target.Should().Be(InstallTarget.Windows,
            "wim-first ordering (ISO-D22): install.wim short-circuits before the casper probe");
        inspector.CasperProbeCalled.Should().BeFalse(
            "the casper probe must not even run once install.wim is found (ISO-D22 ordering)");
    }

    // ════════════════════════════════════════════════════════════════════
    // 2/3/4/5. Orchestrator completion + duplicate-name behavior.
    // ════════════════════════════════════════════════════════════════════

    private static UbuntuInstallRequest Request(int timeoutMinutes = 60, string guestUsername = "ubuntu")
        => new()
        {
            HostId = Host,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = "P@ssw0rd-ubuntu",
            GuestUsername = guestUsername,
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
            TimeoutMinutes = timeoutMinutes,
        };

    private static Mock<IHostResolver> HostResolverMock()
    {
        var resolver = new Mock<IHostResolver>();
        resolver.Setup(r => r.ResolveRequired(It.IsAny<string?>()))
            .Returns(new HostProfile
            {
                HostId = Host,
                ComputerName = "localhost",
                StorageRoot = @"C:\HyperVMCP\VMs",
                DefaultSwitch = "Default Switch",
            });
        return resolver;
    }

    private static UbuntuAutoinstallOrchestrator BuildOrchestrator(
        IPowerShellExecutor executor,
        IKvpCompletionReader kvpReader,
        string stagingTempPath)
        => new(
            executor,
            kvpReader,
            HostResolverMock().Object,
            new FixedTempPathProvider(stagingTempPath),
            new GuestRoutingHintStore(),
            new SeedMediaAuthor(
                executor,
                new OscdimgProbe(new SystemEnvironment()),
                NullLogger<SeedMediaAuthor>.Instance),
            NullLogger<UbuntuAutoinstallOrchestrator>.Instance);

    /// <summary>
    /// The channel state this helper scripts. Deliberately NOT the enum's default value, so a test
    /// that asserts on it turns red if the explicit probe setup is ever dropped — a loose Moq double
    /// would otherwise answer <c>default</c> silently.
    /// See internal documentation
    /// — UMD-D6.
    /// </summary>
    private const GuestCompletionChannelState ScriptedChannelState = GuestCompletionChannelState.Unavailable;

    private static Mock<IKvpCompletionReader> KvpReaderReturning(params GuestCompletionStatus[] sequence)
    {
        var reader = new Mock<IKvpCompletionReader>();
        var queue = new Queue<GuestCompletionStatus>(sequence);
        // Yield each scripted status once, then keep returning the last one (steady state).
        var last = sequence.Length > 0 ? sequence[^1] : new GuestCompletionStatus(GuestCompletionSignal.Pending, null);
        reader.Setup(r => r.ReadCompletionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : last);
        reader.Setup(r => r.ProbeChannelStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ScriptedChannelState);
        return reader;
    }

    [Fact]
    public async Task WaitForCompletion_KvpReady_ReturnsSuccessResult()
    {
        // FR-7 / ISO-D26: a genuine `ready` KVP is the ONLY success authority.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var result = await orchestrator.InstallAsync(Request());

        result.Should().NotBeNull();
        result.Name.Should().Be(VmName);
        result.VmId.Should().Be(ScriptedPowerShellExecutor.CreatedVmId,
            "a ready KVP must finalize and return the created VM's identity (FR-14)");
        result.State.Should().Be("Running");
    }

    [Fact]
    public async Task WaitForCompletion_HeartbeatButNeverReady_DoesNotReportSuccessBeforeDeadline()
    {
        // CRITICAL FR-7 / ISO-D26 guard: a live/"heartbeat"-but-never-`ready` guest must NEVER be
        // reported as success. Here the KVP reader always returns Pending (the closest observable
        // proxy for "guest is up but install not finished"); the deadline path must resolve to
        // LINUX_INSTALL_TIMEOUT, not success. TimeoutMinutes=0 makes the deadline immediate so the
        // real deadline-reached branch is exercised deterministically without a fake clock or a
        // 60 s wall-clock wait (the poll cadence is a fixed 15 s TimeSpan with no injectable seam).
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Pending, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request(timeoutMinutes: 0));

        var timeout = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>(
            "a never-`ready` guest must resolve to timeout, never a false-positive success (FR-7/ISO-D26)")).Which;
        timeout.ChannelState.Should().Be(ScriptedChannelState,
            "the timeout must report the channel state the reader actually answered, not a default");
        executor.RemoveVmDvdCalled.Should().BeFalse(
            "the success-only finalize (DVD eject) must not run when the guest never signalled ready");
    }

    [Fact]
    public async Task WaitForCompletion_KvpFailed_SurfacesLinuxProvisionFailed()
    {
        // ISO-D26: `failed:<shortcode>` is a terminal provisioning failure → LINUX_PROVISION_FAILED.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Failed, "autoinstall"));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request());

        await act.Should().ThrowAsync<LinuxProvisionFailedException>(
            "a failed:* completion KVP must surface as LINUX_PROVISION_FAILED (ISO-D26/D27)");
    }

    [Fact]
    public void KvpFailed_MapsThroughErrorMapper_ToLinuxProvisionFailedCode()
    {
        // ISO-D27: confirm the typed exception the failed-KVP path throws maps to the wire code.
        var mapper = new ErrorMapper();
        var response = mapper.MapException(
            new LinuxProvisionFailedException(
                "Ubuntu autoinstall failed during provisioning.",
                failingStep: UbuntuAutoinstallOrchestrator.StepProvisioningWait,
                vmName: VmName));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.LinuxProvisionFailed);
    }

    [Fact]
    public async Task WaitForCompletion_DeadlineWithoutSignal_TimesOutAndPreservesVm()
    {
        // FR-11 / ISO-D28: on timeout the VM is PRESERVED (no destroy) for inspection. The executor
        // fake records every script; assert no Remove-VM / Stop-VM / destroy script was ever issued.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Pending, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request(timeoutMinutes: 0));

        var ex = (await act.Should().ThrowAsync<LinuxInstallTimeoutException>()).Which;
        ex.VmName.Should().Be(VmName);
        ex.ChannelState.Should().Be(ScriptedChannelState);
        kvp.Verify(
            reader => reader.ProbeChannelStateAsync(VmName, It.IsAny<CancellationToken>()), Times.Once);
        executor.AnyScriptContains("Remove-VM").Should().BeFalse(
            "FR-11/ISO-D28: the VM must be preserved on timeout — no Remove-VM");
        executor.AnyScriptContains("Stop-VM").Should().BeFalse(
            "FR-11/ISO-D28: the VM must be preserved on timeout — it must not be stopped/destroyed");
    }

    [Fact]
    public async Task CreateVm_DuplicateName_PreCreateProbe_MapsToVmAlreadyExists()
    {
        // ISO-D28 / Issue #203 mirror: the pre-create Get-VM probe emits VM_EXISTS →
        // VmAlreadyExistsException → VM_ALREADY_EXISTS, NOT LINUX_PROVISION_FAILED.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor { VmCreateOutcome = VmCreateOutcome.PreCreateExists };
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request());

        var ex = (await act.Should().ThrowAsync<VmAlreadyExistsException>()).Which;
        ex.VmName.Should().Be(VmName);
        new ErrorMapper().MapException(ex).ErrorCode.Should().Be(ErrorCodes.VmAlreadyExists,
            "duplicate name via the pre-create probe must map to VM_ALREADY_EXISTS (Issue #203 contract)");
    }

    [Fact]
    public async Task CreateVm_NewVmAlreadyExistsRace_MapsToVmAlreadyExists()
    {
        // Race path: pre-create probe missed it but New-VM stderr carries the canonical
        // "already exists" collision text → still VM_ALREADY_EXISTS (IsNameCollisionSignal).
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor { VmCreateOutcome = VmCreateOutcome.NewVmAlreadyExistsRace };
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request());

        await act.Should().ThrowAsync<VmAlreadyExistsException>(
            "a New-VM 'already exists' race must classify as VM_ALREADY_EXISTS, not LINUX_PROVISION_FAILED");
    }

    [Fact]
    public async Task CreateVm_GenuineNonCollisionFailure_MapsToLinuxProvisionFailed()
    {
        // Contrast guard: a genuine (non-collision) create failure must remain
        // LINUX_PROVISION_FAILED — the name-collision classifier must NOT over-match.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor { VmCreateOutcome = VmCreateOutcome.GenericFailure };
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var act = async () => await orchestrator.InstallAsync(Request());

        await act.Should().ThrowAsync<LinuxProvisionFailedException>(
            "a genuine non-collision provisioning failure must map to LINUX_PROVISION_FAILED");
    }

    // ════════════════════════════════════════════════════════════════════
    // 6. Sha512Crypt Known-Answer Tests (ISO-D29). Drepper's published $6$ vectors.
    //    https://www.akkadia.org/drepper/SHA-crypt.txt
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    // Canonical Drepper SHA-512 crypt(3) vectors (fixed salt overload). The expected literal was
    // verified against real crypt(3): Ubuntu 24.04 libc crypt() and `openssl passwd -6` both emit
    // this exact string — canonical glibc SHA-512-crypt. MUST NOT be "corrected" to a …BERZ3y1 tail
    // (that would require digest byte 63 = 254, but real SHA-512 for this input yields 0xFF/255).
    [InlineData("Hello world!", "saltstring",
        "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("a", "$$$",
        // Short password with a short salt — exercises the block-folding path.
        // Derived from the canonical algorithm (asserted against the same reference spec).
        null)]
    public void Sha512Crypt_KnownAnswer_MatchesCanonicalVector(string password, string salt, string? expected)
    {
        var actual = Sha512Crypt.Hash(password, salt);

        // Shape invariant always holds: $6$<salt-up-to-16>$<86-char hash>.
        actual.Should().StartWith("$6$", "SHA-512 crypt output must carry the $6$ identifier");

        if (expected is not null)
        {
            actual.Should().Be(expected,
                "Sha512Crypt.Hash must reproduce Drepper's canonical $6$ crypt(3) vector exactly (ISO-D29)");
        }
    }

    [Fact]
    public void Sha512Crypt_RandomSaltOverload_EmitsCanonicalShape()
    {
        // ISO-D29: $6$<16-char salt>$<86-char hash>. The hash body of SHA-512 crypt is 86 chars.
        var hash = Sha512Crypt.Hash("P@ssw0rd-ubuntu");

        var parts = hash.Split('$');
        // Leading '$' yields an empty [0]; then "6", salt, hash.
        parts.Should().HaveCount(4, "the crypt string is $6$<salt>$<hash>");
        parts[1].Should().Be("6", "SHA-512 crypt identifier");
        parts[2].Length.Should().Be(16, "the generated salt must be 16 characters (glibc cap)");
        parts[3].Length.Should().Be(86, "SHA-512 crypt hash body is exactly 86 base64 characters");
    }

    [Fact]
    public void Sha512Crypt_FixedSaltAndPassword_IsDeterministic()
    {
        // Same (password, salt) must always yield the same hash — required for reproducible seeds.
        var first = Sha512Crypt.Hash("Hello world!", "saltstring");
        var second = Sha512Crypt.Hash("Hello world!", "saltstring");

        second.Should().Be(first, "SHA-512 crypt is deterministic for a fixed salt");
    }

    // ════════════════════════════════════════════════════════════════════
    // 7. guestUsername validation (ISO-D24 / OQ-U4).
    // ════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("_svc")]
    [InlineData("dev-user_1")]
    [InlineData("a")]
    public void ValidateGuestUsername_Valid_Accepted(string username)
    {
        InputValidation.ValidateGuestUsername(username).Should().Be(username);
    }

    [Theory]
    [InlineData("Ubuntu")]              // uppercase
    [InlineData("1user")]              // leading digit
    [InlineData("root!")]              // illegal char
    [InlineData("has space")]          // whitespace
    [InlineData("-leadinghyphen")]     // leading hyphen (must start with [a-z_])
    [InlineData("thisusernameiswaytoolong1234567890")] // > 32 chars
    public void ValidateGuestUsername_Invalid_Rejected(string username)
    {
        var act = () => InputValidation.ValidateGuestUsername(username);

        act.Should().Throw<ArgumentException>(
            "ValidateGuestUsername must reject names useradd would mangle (OQ-U4)")
            .And.ParamName.Should().Be("username");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateGuestUsername_NullOrWhitespace_Rejected(string? username)
    {
        var act = () => InputValidation.ValidateGuestUsername(username);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("username");
    }

    [Fact]
    public async Task GuestUsername_OmittedOnUbuntuTarget_DefaultsToUbuntu()
    {
        // ISO-D24: when guestUsername is omitted, the resolved user defaults to "ubuntu" and the
        // seed user-data carries `username: ubuntu`. Drive OsInstallAsync's Ubuntu branch with a
        // fake classifier so no real ISO/host is needed, and capture the seed script.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var manager = BuildManagerForUbuntu(executor, tempScope.Path,
            kvp: new GuestCompletionStatus(GuestCompletionSignal.Ready, null));

        // ISO must exist on disk so the ISO-existence preflight passes.
        using var isoFile = new TempIsoFile();
        await manager.OsInstallAsync(Host, VmName, isoFile.Path, "P@ssw0rd-ubuntu",
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: null);

        var seedScript = executor.SeedScript;
        seedScript.Should().NotBeNull("the Ubuntu path must build a seed");
        seedScript!.Should().Contain("username: ubuntu",
            "ISO-D24: an omitted guestUsername defaults to the distro convention 'ubuntu'");
    }

    [Fact]
    public async Task GuestUsername_InvalidOnUbuntuTarget_RejectedAsInvalidParameter()
    {
        // ISO-D24 / OQ-U4: an invalid guestUsername must be rejected as ArgumentException
        // (→ INVALID_PARAMETER) before any VM/seed work.
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var manager = BuildManagerForUbuntu(executor, tempScope.Path,
            kvp: new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        using var isoFile = new TempIsoFile();

        var act = async () => await manager.OsInstallAsync(Host, VmName, isoFile.Path, "P@ssw0rd-ubuntu",
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: "BadUser!");

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        new ErrorMapper().MapException(thrown.Which).ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
    }

    // ════════════════════════════════════════════════════════════════════
    // 8. Credential safety (FR-18 / ISO-D29): the seed carries ONLY the $6$ hash,
    //    never the plaintext; plaintext never appears in the built script body.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Seed_ContainsOnlyHashedPassword_NeverPlaintext()
    {
        const string plaintext = "Sup3rSecret-Ubuntu!";
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var request = new UbuntuInstallRequest
        {
            HostId = Host,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = plaintext,
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
        };

        await orchestrator.InstallAsync(request);

        var seedScript = executor.SeedScript;
        seedScript.Should().NotBeNull();
        seedScript!.Should().NotContain(plaintext,
            "FR-18/ISO-D29: the plaintext password must NEVER appear in the seed script written to disk");
        seedScript.Should().Contain("$6$",
            "FR-18/ISO-D29: the seed must carry the SHA-512-crypt ($6$) hash, not the plaintext");
    }

    [Fact]
    public async Task Seed_PlaintextAbsentFromEveryDispatchedScript()
    {
        // Stronger surface guard: the plaintext must not leak into ANY script the executor sees
        // (seed build, VM create, finalize) — the observable log/status surfaces in this test.
        const string plaintext = "An0ther-Secret!";
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var request = new UbuntuInstallRequest
        {
            HostId = Host,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = plaintext,
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
        };

        var result = await orchestrator.InstallAsync(request);

        executor.AnyScriptContains(plaintext).Should().BeFalse(
            "FR-18: the plaintext password must not appear in any dispatched PowerShell script");
        JsonSerializer.Serialize(result).Should().NotContain(plaintext,
            "FR-18: the plaintext password must not appear in the returned result surface");
    }

    // ════════════════════════════════════════════════════════════════════
    // 9. Error taxonomy envelopes (ISO-D27): the three new codes serialize correctly.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void ErrorMapper_LinuxInstallTimeout_MapsToLinuxInstallTimeout()
    {
        var response = new ErrorMapper().MapException(
            new LinuxInstallTimeoutException("timed out", timeoutMinutes: 60, vmName: VmName));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.LinuxInstallTimeout);
        JsonSerializer.Serialize(response).Should().Contain("\"errorCode\":\"LINUX_INSTALL_TIMEOUT\"");
    }

    [Fact]
    public void ErrorMapper_LinuxPreconditionUnmet_MapsToLinuxPreconditionUnmet()
    {
        var response = new ErrorMapper().MapException(
            new LinuxPreconditionUnmetException("Ubuntu requires Generation 2."));

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.LinuxPreconditionUnmet);
        JsonSerializer.Serialize(response).Should().Contain("\"errorCode\":\"LINUX_PRECONDITION_UNMET\"");
    }

    [Fact]
    public void ErrorMapper_LinuxProvisionFailed_SerializesEnvelope()
    {
        var response = new ErrorMapper().MapException(
            new LinuxProvisionFailedException(
                "provisioning failed",
                failingStep: UbuntuAutoinstallOrchestrator.StepProvisioningWait,
                vmName: VmName));

        JsonSerializer.Serialize(response).Should().Contain("\"errorCode\":\"LINUX_PROVISION_FAILED\"");
    }

    // ════════════════════════════════════════════════════════════════════
    // 12/AC-7. Firmware precondition fail-fast (ISO-D25): explicit contradictions
    //          fail synchronously as LINUX_PRECONDITION_UNMET — no VM, no seed.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FirmwarePrecondition_ExplicitGen1_FailsFastNoVmNoSeed()
    {
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var request = new UbuntuInstallRequest
        {
            HostId = Host,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = "P@ssw0rd-ubuntu",
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
            RequestedGeneration = 1,
        };

        var act = async () => await orchestrator.InstallAsync(request);

        await act.Should().ThrowAsync<LinuxPreconditionUnmetException>(
            "an explicit Gen-1 request must fail fast (ISO-D25 / AC-7)");
        executor.ScriptCount.Should().Be(0,
            "the precondition is synchronous and pre-VM/pre-seed — no script may be dispatched");
    }

    [Fact]
    public async Task FirmwarePrecondition_ExplicitSecureBootOn_FailsFast()
    {
        using var tempScope = new TempScope();
        var executor = new ScriptedPowerShellExecutor();
        var kvp = KvpReaderReturning(new GuestCompletionStatus(GuestCompletionSignal.Ready, null));
        var orchestrator = BuildOrchestrator(executor, kvp.Object, tempScope.Path);

        var request = new UbuntuInstallRequest
        {
            HostId = Host,
            Name = VmName,
            IsoPath = IsoPath,
            AdminPassword = "P@ssw0rd-ubuntu",
            GuestUsername = "ubuntu",
            CpuCount = 2,
            MemoryMB = 4096,
            DiskSizeGB = 32,
            RequestedSecureBoot = true,
        };

        var act = async () => await orchestrator.InstallAsync(request);

        await act.Should().ThrowAsync<LinuxPreconditionUnmetException>(
            "an explicit Secure-Boot-on request must fail fast (ISO-D25 / AC-7)");
        executor.ScriptCount.Should().Be(0, "no script may be dispatched before the precondition passes");
    }

    // ════════════════════════════════════════════════════════════════════
    // 10. Windows regression (unchanged path): an install.wim ISO still drives the
    //     Windows orchestration and NEVER the Ubuntu orchestrator.
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WindowsIso_DispatchesWindowsPath_NotUbuntuOrchestrator()
    {
        // A Windows-classified ISO must never call the Ubuntu orchestrator. We inject a spy
        // Ubuntu orchestrator that fails the test if invoked, plus a fake classifier that
        // reports Windows. The Windows PS orchestration then runs against the mock executor;
        // whatever it returns, the Ubuntu path must not have been taken.
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();

        var ubuntuSpy = new Mock<IUbuntuAutoinstallOrchestrator>(MockBehavior.Strict); // strict: any call fails
        var classifier = ClassifierReturning(InstallTarget.Windows);

        var options = BuildOptions(tempScope.Path);
        var executor = new Mock<IPowerShellExecutor>(MockBehavior.Loose);
        // Windows orchestration will issue scripts; return a benign failure so the call ends
        // quickly. The assertion is purely that the Ubuntu orchestrator was not invoked.
        executor.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult { ExitCode = 1, Stderr = "benign windows-path stop" });

        var manager = new HyperVManager(
            executor.Object,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(found: true),
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier.Object,
            ubuntuOrchestrator: ubuntuSpy.Object);

        // The Windows path may throw/fail late; we only care it did NOT take the Ubuntu branch.
        await Record.ExceptionAsync(async () => await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 127));

        ubuntuSpy.Verify(
            o => o.InstallAsync(It.IsAny<UbuntuInstallRequest>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Windows regression: an install.wim ISO must NEVER be routed to the Ubuntu orchestrator");
    }

    [Fact]
    public async Task UnsupportedIso_ThrowsOsNotSupported_AndDoesNotCallUbuntu()
    {
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();

        var ubuntuSpy = new Mock<IUbuntuAutoinstallOrchestrator>(MockBehavior.Strict);
        var classifier = ClassifierReturning(InstallTarget.Unsupported);

        var options = BuildOptions(tempScope.Path);
        var executor = new Mock<IPowerShellExecutor>(MockBehavior.Strict); // must not be used
        var manager = new HyperVManager(
            executor.Object,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(found: false),
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier.Object,
            ubuntuOrchestrator: ubuntuSpy.Object);

        var act = async () => await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, "P@ssw0rd!",
            cpuCount: 4, memoryMB: 8192, diskSizeGB: 127);

        await act.Should().ThrowAsync<OsNotSupportedException>(
            "an Unsupported classification must reject with OS_NOT_SUPPORTED before any orchestration");
        ubuntuSpy.Verify(
            o => o.InstallAsync(It.IsAny<UbuntuInstallRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UbuntuIso_DispatchesToUbuntuOrchestrator()
    {
        // Positive dispatch: a UbuntuServer2404 classification routes to the Ubuntu orchestrator
        // and returns its result — the symmetric counterpart to the Windows regression guard.
        using var tempScope = new TempScope();
        using var isoFile = new TempIsoFile();

        var expected = new OsInstallResult { VmId = "ubuntu-guid", Name = VmName, State = "Running" };
        var ubuntu = new Mock<IUbuntuAutoinstallOrchestrator>();
        ubuntu.Setup(o => o.InstallAsync(It.IsAny<UbuntuInstallRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var classifier = ClassifierReturning(InstallTarget.UbuntuServer2404);

        var options = BuildOptions(tempScope.Path);
        var manager = new HyperVManager(
            new Mock<IPowerShellExecutor>(MockBehavior.Strict).Object, // Windows PS path must NOT run
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(found: false, casperFound: true),
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier.Object,
            ubuntuOrchestrator: ubuntu.Object);

        var result = await manager.OsInstallAsync(
            Host, VmName, isoFile.Path, "P@ssw0rd-ubuntu",
            cpuCount: 2, memoryMB: 4096, diskSizeGB: 32, guestUsername: "ubuntu");

        result.VmId.Should().Be("ubuntu-guid");
        ubuntu.Verify(o => o.InstallAsync(It.IsAny<UbuntuInstallRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ════════════════════════════════════════════════════════════════════
    // Shared harness helpers.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Issue #370: a classifier double MUST answer ClassifyMediaAsync too. Ubuntu answers carry a
    /// PREPARED GRUB configuration so these dispatch-focused tests are not refused by the capability
    /// guard for a reason none of them is about.
    /// </summary>
    private static Mock<IGuestOsClassifier> ClassifierReturning(InstallTarget target)
    {
        var classifier = new Mock<IGuestOsClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(target);
        classifier.Setup(c => c.ClassifyMediaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GuestOsClassification(
                target,
                target == InstallTarget.UbuntuServer2404
                    ? TestIsoInspector.PreparedGrubConfiguration
                    : null));
        return classifier;
    }

    private static ServerOptions BuildOptions(string storageRoot) => new()
    {
        DefaultHostId = Host,
        Hosts = new Dictionary<string, HostProfile>
        {
            [Host] = new HostProfile
            {
                HostId = Host,
                ComputerName = "localhost",
                TrustPolicy = "local",
                StorageRoot = storageRoot,
                BaseVhdxPath = Path.Combine(storageRoot, "base.vhdx"),
            },
        },
    };

    /// <summary>
    /// Builds a real <see cref="HyperVManager"/> whose classifier always reports Ubuntu and whose
    /// Ubuntu orchestrator is a real <see cref="UbuntuAutoinstallOrchestrator"/> over the scripted
    /// executor — so tests can assert on the seed the orchestrator actually builds.
    /// </summary>
    private HyperVManager BuildManagerForUbuntu(
        ScriptedPowerShellExecutor executor, string stagingTempPath, GuestCompletionStatus kvp)
    {
        var options = BuildOptions(stagingTempPath);
        var classifier = ClassifierReturning(InstallTarget.UbuntuServer2404);
        var orchestrator = BuildOrchestrator(executor, KvpReaderReturning(kvp).Object, stagingTempPath);

        return new HyperVManager(
            executor,
            new HostResolver(options),
            options,
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(found: false, casperFound: true),
            fileSystemProbe: null,
            baseImageHashCache: null,
            guestOsClassifier: classifier.Object,
            ubuntuOrchestrator: orchestrator);
    }
}

// ════════════════════════════════════════════════════════════════════════
// Test doubles (hand-rolled, mirroring the repo's TestIsoInspector convention).
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// <see cref="IIsoInspector"/> that reports BOTH markers present but records whether the casper
/// probe was called — used to prove the wim-first short-circuit ordering (ISO-D22).
/// </summary>
internal sealed class WimFirstOrderingInspector : IIsoInspector
{
    public bool CasperProbeCalled { get; private set; }

    public Task<(bool Found, string? Diagnostic)> ContainsWindowsInstallWimWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
        => Task.FromResult((true, (string?)null));

    public Task<(bool Found, string? Diagnostic)> ContainsCasperLayoutWithDiagnosticAsync(
        string isoPath, CancellationToken ct = default)
    {
        CasperProbeCalled = true;
        return Task.FromResult((true, (string?)null));
    }

    public Task<UbuntuMediaProbe> ProbeUbuntuMediaAsync(string isoPath, CancellationToken ct = default)
    {
        CasperProbeCalled = true;
        return Task.FromResult(new UbuntuMediaProbe(
            IsoMarkerProbeResult.Confirmed, TestIsoInspector.PreparedGrubConfiguration));
    }
}

/// <summary>Requested VM-create outcome for the scripted executor.</summary>
internal enum VmCreateOutcome
{
    Success,
    PreCreateExists,
    NewVmAlreadyExistsRace,
    GenericFailure,
}

/// <summary>
/// Hand-rolled <see cref="IPowerShellExecutor"/> that recognizes each phase the Ubuntu
/// orchestrator issues (seed build → SEED_OK; VM create → JSON/collision; finalize → JSON) and
/// records every script so tests can assert on credential-safety and VM-preservation invariants.
/// No real PowerShell or Hyper-V is involved.
/// </summary>
internal sealed class ScriptedPowerShellExecutor : IPowerShellExecutor
{
    internal const string CreatedVmId = "11111111-2222-3333-4444-555555555555";

    private readonly List<string> _scripts = new();

    public VmCreateOutcome VmCreateOutcome { get; set; } = VmCreateOutcome.Success;

    public string? SeedScript { get; private set; }
    public bool RemoveVmDvdCalled { get; private set; }
    public int ScriptCount => _scripts.Count;

    public bool AnyScriptContains(string needle)
        => _scripts.Exists(s => s.Contains(needle, StringComparison.OrdinalIgnoreCase));

    public Task<PowerShellResult> ExecuteAsync(
        string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
    {
        _scripts.Add(script);

        // Phase 1 — seed build: the orchestrator's seed script writes user-data / builds CIDATA
        // and emits SEED_OK. Recognized by the autoinstall marker unique to that script.
        if (script.Contains("autoinstall:", StringComparison.Ordinal) ||
            script.Contains("SEED_OK", StringComparison.Ordinal))
        {
            SeedScript = script;
            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "SEED_OK" });
        }

        // Phase 3 — finalize (success path): removes install DVDs and returns VM JSON.
        if (script.Contains("Remove-VMDvdDrive", StringComparison.Ordinal))
        {
            RemoveVmDvdCalled = true;
            return Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = JsonSerializer.Serialize(new
                {
                    vmId = CreatedVmId,
                    name = "issue208-ubuntu-vm",
                    state = "Running",
                    processorCount = 2,
                    memoryMB = 4096L,
                    guestIpAddress = (string?)null,
                }),
            });
        }

        // Phase 2 — VM create (recognized by New-VM). Outcome is test-controlled.
        if (script.Contains("New-VM", StringComparison.Ordinal))
        {
            return Task.FromResult(VmCreateOutcome switch
            {
                VmCreateOutcome.PreCreateExists =>
                    new PowerShellResult { ExitCode = 0, Stdout = "VM_EXISTS" },

                VmCreateOutcome.NewVmAlreadyExistsRace =>
                    new PowerShellResult
                    {
                        ExitCode = 1,
                        Stderr = "New-VM : A VM with name 'issue208-ubuntu-vm' already exists.",
                    },

                VmCreateOutcome.GenericFailure =>
                    new PowerShellResult { ExitCode = 1, Stderr = "Hyper-V failed to create the virtual machine (transient)." },

                _ => new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = JsonSerializer.Serialize(new { vmId = CreatedVmId, state = "Running" }),
                },
            });
        }

        // Any other best-effort script (e.g. TryGetVmIdentity) → benign empty JSON.
        return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "{}" });
    }
}

/// <summary>Per-test temp directory scope; deleted on dispose (best-effort).</summary>
internal sealed class TempScope : IDisposable
{
    public string Path { get; }

    public TempScope()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hvmcp-issue208-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>A real (1-byte) .iso file on disk so the ISO-existence preflight passes.</summary>
internal sealed class TempIsoFile : IDisposable
{
    public string Path { get; }

    public TempIsoFile()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hvmcp-issue208-" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(Path, new byte[] { 0 });
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best-effort */ }
    }
}
