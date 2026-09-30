using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #283 — vm_create administrator-password seeding regression guards.
///
/// Every correctness assertion runs a REAL production type
/// (<see cref="UnattendSeeder"/>, <see cref="BaseImageGeneralizationProbe"/>,
/// <see cref="GuestReadinessPoller"/>, <see cref="HyperVManager"/>) over a recording fake at the
/// <see cref="IPowerShellExecutor"/> boundary, so a permissive stand-in cannot mask a defect in the
/// type under test.
/// See internal documentation — VCAP-D21 / VCAP-D24.
///
/// Un-gated: no HYPERV_MCP_RUN_REAL_PS, no live host, no skipped tests.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue283VmCreateCredentialSeedingTests
{
    private const string LocalHostId = "local";
    private const string BaseVhdxPath = @"C:\Base\base.vhdx";
    private const string StorageRoot = @"C:\HyperVMCP\VMs";
    private const string VmName = "issue283-vm";
    private const string DiffVhdxPath = @"C:\HyperVMCP\VMs\issue283-vm\issue283-vm.vhdx";

    /// <summary>Distinctive so a leak into any captured text is unambiguous.</summary>
    private const string AdminPassword = "Zq7!unattend-Secret-283";

    // ── Recording fake at the real executor seam ─────────────────────────────

    private sealed record RecordedCall(
        string Script,
        int TimeoutSeconds,
        bool AllowDump,
        bool CarriedSecrets,
        IReadOnlyDictionary<string, string>? SecretEnvironment,
        bool CancellationTokenCanBeCanceled);

    private sealed class RecordingPowerShellExecutor : IPowerShellExecutor
    {
        private readonly Func<string, PowerShellResult> _responder;

        public RecordingPowerShellExecutor(Func<string, PowerShellResult> responder)
        {
            _responder = responder;
        }

        public List<RecordedCall> Calls { get; } = new();

        public IEnumerable<string> Scripts => Calls.Select(call => call.Script);

        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
        {
            Calls.Add(new RecordedCall(script, timeoutSeconds, allowDump, false, null, ct.CanBeCanceled));
            return Task.FromResult(_responder(script));
        }

        public Task<PowerShellResult> ExecuteWithSecretsAsync(
            string script, IReadOnlyDictionary<string, string> secretEnvironment,
            int timeoutSeconds = 300, CancellationToken ct = default)
        {
            // Dumping is always suppressed for secret-bearing calls, so the recorded allowDump
            // mirrors that contract rather than a parameter that does not exist.
            Calls.Add(new RecordedCall(
                script, timeoutSeconds, false, true, secretEnvironment, ct.CanBeCanceled));
            return Task.FromResult(_responder(script));
        }
    }

    /// <summary>
    /// Implements only <see cref="IPowerShellExecutor.ExecuteAsync"/>, so it inherits the interface
    /// default for the secret-bearing entry point — the anti-masking guard under test.
    /// </summary>
    private sealed class ExecuteOnlyPowerShellExecutor : IPowerShellExecutor
    {
        public Task<PowerShellResult> ExecuteAsync(
            string script, int timeoutSeconds = 300, CancellationToken ct = default, bool allowDump = true)
            => Task.FromResult(Ok(string.Empty));
    }

    // ── Result helpers ───────────────────────────────────────────────────────

    private static PowerShellResult Ok(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 5,
    };

    private static PowerShellResult TimedOut() => new()
    {
        ExitCode = -1,
        Stdout = string.Empty,
        Stderr = string.Empty,
        TimedOut = true,
        Cancelled = false,
        DurationMs = 5,
    };

    private static readonly Regex CleanNoncePattern = new(@"\|CLEAN:([0-9a-fA-F]{32})", RegexOptions.Compiled);

    /// <summary>
    /// Echoes back the nonce the production code issued for this very dispatch, which is the only
    /// way a script can prove its dismount actually ran.
    /// </summary>
    private static string WithIssuedNonce(string script, string verdict)
    {
        var match = CleanNoncePattern.Match(script);
        return match.Success ? verdict + "|CLEAN:" + match.Groups[1].Value : verdict;
    }

    private static bool IsPreMountProbe(string script)
        => script.Contains("NOT_MOUNTED", StringComparison.Ordinal)
           && !script.Contains("Mount-VHD -Path", StringComparison.Ordinal);

    private static bool IsOutOfBandDismount(string script)
        => script.Contains("DISMOUNT_FAILED", StringComparison.Ordinal);

    private static bool IsSeedScript(string script)
        => script.Contains("WriteAllText", StringComparison.Ordinal);

    // The in-guest readiness poll deletes the same file through a live session, so the offline
    // scrub is identified by the absence of that session rather than by the deletion alone.
    private static bool IsScrubScript(string script)
        => script.Contains("Remove-Item -LiteralPath $unattendPath", StringComparison.Ordinal)
           && !IsReadinessPoll(script);

    private static bool IsReadinessPoll(string script)
        => script.Contains("New-PSSession", StringComparison.Ordinal);

    private static bool IsGeneralizationProbe(string script)
        => script.Contains("IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE", StringComparison.Ordinal);

    // ── Production types over the recording fake ─────────────────────────────

    private static UnattendSeeder BuildSeeder(RecordingPowerShellExecutor executor)
        => new(executor, NullLogger<UnattendSeeder>.Instance);

    private static BaseImageGeneralizationProbe BuildGeneralizationProbe(RecordingPowerShellExecutor executor)
        => new(executor, NullLogger<BaseImageGeneralizationProbe>.Instance);

    private static GuestReadinessPoller BuildReadinessPoller(RecordingPowerShellExecutor executor)
        => new(executor, NullLogger<GuestReadinessPoller>.Instance);

    private static ServerOptions BuildOptions() => new()
    {
        DefaultHostId = LocalHostId,
        Hosts = new Dictionary<string, HostProfile>
        {
            [LocalHostId] = new HostProfile
            {
                HostId = LocalHostId,
                ComputerName = "localhost",
                TrustPolicy = "local",
                BaseVhdxPath = BaseVhdxPath,
                StorageRoot = StorageRoot,
            },
        },
        MaxConcurrentOperations = 8,
    };

    /// <summary>
    /// Real manager whose seeding, preflight and readiness collaborators are also production types
    /// over the same executor, so the only substituted component is the PowerShell boundary.
    /// </summary>
    private static HyperVManager BuildManager(RecordingPowerShellExecutor executor)
        => new(
            executor,
            new HostResolver(BuildOptions()),
            BuildOptions(),
            NullLogger<HyperVManager>.Instance,
            new TestIsoInspector());

    private static string VmInfoJson(string state) => $$"""
    {
      "Id": "83838383-8383-8383-8383-838383838383",
      "Name": "{{VmName}}",
      "State": "{{state}}",
      "ProcessorCount": 2,
      "MemoryMB": 4096,
      "UptimeSeconds": 0
    }
    """;

    /// <summary>
    /// Full happy-path responder for a password-bearing create. Individual tests override single
    /// stages by wrapping it.
    /// </summary>
    private static PowerShellResult RespondToCreateFlow(string script)
    {
        if (script.Contains("'inconclusive'", StringComparison.Ordinal)) return Ok("absent");
        if (IsOutOfBandDismount(script)) return Ok("DISMOUNTED");
        if (IsPreMountProbe(script)) return Ok("NOT_MOUNTED");
        if (IsGeneralizationProbe(script)) return Ok(WithIssuedNonce(script, "GENERALIZED"));
        if (IsReadinessPoll(script)) return Ok("READY");
        if (IsSeedScript(script)) return Ok(WithIssuedNonce(script, "SEEDED"));
        if (IsScrubScript(script)) return Ok(WithIssuedNonce(script, "SCRUBBED"));
        if (script.Contains("New-VHD", StringComparison.Ordinal)) return Ok(VmInfoJson("Off"));
        if (script.Contains("'STARTED'", StringComparison.Ordinal)) return Ok("STARTED");
        if (script.Contains("Stop-VM", StringComparison.Ordinal)) return Ok("STOPPED");
        return Ok(string.Empty);
    }

    // ── Guarantee 1 — the answer file is rendered and injected at the clone's Panther path ──

    [Fact]
    public async Task Seed_RendersSuppliedPassword_AndWritesClonePantherPath()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "SEEDED")));

        await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        var seedCall = executor.Calls.Single(call => IsSeedScript(call.Script));
        seedCall.CarriedSecrets.Should().BeTrue(
            "the rendered answer file carries the plaintext password and must reach the child process " +
            "out-of-band rather than through the script text.");
        seedCall.SecretEnvironment!.Should().ContainKey(UnattendSeeder.UnattendXmlEnvVar);
        seedCall.SecretEnvironment[UnattendSeeder.UnattendXmlEnvVar].Should().Contain(AdminPassword,
            "the answer file handed to the child process must carry the password the caller supplied.");

        seedCall.Script.Should().Contain($"Mount-VHD -Path $diffPath",
            "the clone — not the base image — is the disk that gets the answer file.");
        seedCall.Script.Should().Contain(DiffVhdxPath);
        seedCall.Script.Should().Contain(@"Windows\Panther");
        seedCall.Script.Should().Contain("unattend.xml");
        seedCall.Script.Should().Contain("UTF8Encoding($false)",
            "the specialize parser rejects a BOM.");
    }

    [Fact]
    public void RenderedUnattend_CarriesPassword_AtCanonicalGuestPath()
    {
        var rendered = UnattendSeeder.RenderPantherUnattend(VmName, AdminPassword, "en-US");

        rendered.Should().Contain(AdminPassword);
        rendered.Should().Contain("<AdministratorPassword>");
        UnattendSeeder.GuestPantherRelativePath.Should().Be(@"Windows\Panther\unattend.xml",
            "an inherited answer file at this exact path silently beats any other delivery mechanism.");
    }

    [Fact]
    public async Task Create_WithPassword_SeedsBeforeFirstBoot_AndReportsPasswordApplied()
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        var info = await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        info.PasswordApplied.Should().BeTrue();
        info.State.Should().Be("Running");

        var scripts = executor.Scripts.ToList();
        var createIndex = scripts.FindIndex(script => script.Contains("New-VHD", StringComparison.Ordinal));
        var seedIndex = scripts.FindIndex(IsSeedScript);
        var startIndex = scripts.FindIndex(script => script.Contains("'STARTED'", StringComparison.Ordinal));

        seedIndex.Should().BeGreaterThan(createIndex,
            "the differencing disk must exist before it can be mounted offline.");
        startIndex.Should().BeGreaterThan(seedIndex,
            "first boot consumes the answer file, so the guest must not boot before it is written.");
        scripts[createIndex].Should().Contain("$autoStart = $false",
            "the primary script must not start the VM on the password path.");
    }

    // ── Guarantee 2 — no password means the pre-existing behavior, byte for byte ──

    [Fact]
    public async Task Create_WithoutPassword_PerformsNoSeedingProbeOrPoll_AndReportsPasswordAppliedFalse()
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        var info = await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, CancellationToken.None);

        info.PasswordApplied.Should().BeFalse(
            "the result must state that no password was applied rather than omitting the fact.");

        executor.Scripts.Should().NotContain(script => IsGeneralizationProbe(script),
            "the generalization preflight runs only for password-bearing calls.");
        executor.Scripts.Should().NotContain(script => script.Contains("Mount-VHD", StringComparison.Ordinal),
            "no offline mount may happen on the unchanged no-password path.");
        executor.Scripts.Should().NotContain(script => script.Contains("New-PSSession", StringComparison.Ordinal),
            "the readiness poll must not run without a password.");
    }

    [Fact]
    public async Task Create_WithoutPassword_KeepsAutoStartInPrimaryScript()
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: true,
            verifyBaseImageHash: false, CancellationToken.None);

        executor.Scripts.Single(script => script.Contains("New-VHD", StringComparison.Ordinal))
            .Should().Contain("$autoStart = $true",
                "the no-password path must keep starting the VM inside the primary script.");
    }

    // ── Guarantee 3 — a non-generalized or unprobeable base fails early and creates nothing ──

    [Theory]
    [InlineData("NOT_GENERALIZED")]
    [InlineData("ERROR:the offline hive could not be loaded")]
    [InlineData("something unrecognized")]
    public async Task Create_WithPassword_NonGeneralizedBase_FailsBeforeAnyArtifactExists(string probeVerdict)
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsGeneralizationProbe(script)
                ? Ok(WithIssuedNonce(script, probeVerdict))
                : RespondToCreateFlow(script));

        var act = async () => await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        (await act.Should().ThrowAsync<BaseImageNotGeneralizedException>()).Which
            .Message.Should().NotContain(AdminPassword);

        executor.Scripts.Should().NotContain(script => script.Contains("New-VHD", StringComparison.Ordinal),
            "the rejection must precede artifact creation so there is nothing to roll back.");
        executor.Scripts.Should().NotContain(script => IsSeedScript(script),
            "no answer file may be written for a base that was never confirmed generalized.");
    }

    [Fact]
    public void BaseImageNotGeneralizedException_MapsToItsOwnCode_NotTheGenericCatchAll()
    {
        var response = new ErrorMapper().MapException(
            new BaseImageNotGeneralizedException("base is not generalized", indeterminate: false));

        response.ErrorCode.Should().Be(ErrorCodes.BaseNotGeneralized,
            "the typed exception extends InvalidOperationException and would otherwise be swallowed " +
            "by the generic COMMAND_FAILED arm.");
    }

    // ── Guarantee 4 — the mount lifecycle is leak-safe on failure paths ──

    [Fact]
    public async Task Seed_ScriptGuaranteesDismountInFinally_AndVerifiesDetachment()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "SEEDED")));

        await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        var seedScript = executor.Calls.Single(call => IsSeedScript(call.Script)).Script;
        seedScript.Should().Contain("} finally {");
        seedScript.Should().Contain("Dismount-VHD");
        seedScript.Should().Contain("Get-VHD -Path $path -ErrorAction Stop).Attached",
            "the cmdlet staying silent is not evidence the disk detached.");
    }

    [Fact]
    public async Task Seed_WhenScriptFailsWithoutDismountProof_DismountsOutOfBand()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNTED")
            : Ok("ERROR:the answer file was not present after writing"));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        await act.Should().ThrowAsync<InvalidOperationException>();
        executor.Calls.Should().Contain(call => IsOutOfBandDismount(call.Script),
            "a leaked mount holds a write lock on the clone and would block Start-VM.");
        executor.Calls.Single(call => IsOutOfBandDismount(call.Script))
            .CancellationTokenCanBeCanceled.Should().BeFalse(
                "the cleanup must run on an unsignalled token — the caller's token is already " +
                "signalled on the very paths that need it.");
    }

    [Fact]
    public async Task Probe_WhenScriptTimesOut_DismountsOutOfBand_AndReportsIndeterminate()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNTED")
            : TimedOut());

        var state = await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        state.Should().Be(BaseImageGeneralizationState.Indeterminate);
        executor.Calls.Should().Contain(call => IsOutOfBandDismount(call.Script));
    }

    [Fact]
    public async Task Probe_MountsBaseImageReadOnly()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "GENERALIZED")));

        await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        executor.Calls.Single(call => IsGeneralizationProbe(call.Script)).Script
            .Should().Contain("Mount-VHD -Path $path -ReadOnly",
                "the base image is never mutated by the preflight.");
    }

    // ── Guarantee 5 — dumps suppressed, password never surfaces in captured text ──

    [Fact]
    public async Task Seed_SuppressesScriptDumps_AndKeepsPasswordOutOfEveryScript()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "SEEDED")));

        await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        executor.Calls.Should().OnlyContain(call => call.AllowDump == false,
            "every call on the credential-bearing path suppresses the script-dump diagnostic.");
        executor.Calls.Should().OnlyContain(call => !call.Script.Contains(AdminPassword),
            "the executor persists the composed script to a host temp file, so the password must " +
            "never be interpolated into script text.");
    }

    [Fact]
    public async Task Seed_WhenScriptErrorEchoesPassword_FailureMessageIsRedacted()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNTED")
            : Ok(WithIssuedNonce(script, $"ERROR:write failed for {AdminPassword}")));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which
            .Message.Should().NotContain(AdminPassword);
    }

    [Fact]
    public async Task ReadinessPoll_SuppressesScriptDumps_AndKeepsPasswordOutOfScriptText()
    {
        var executor = new RecordingPowerShellExecutor(_ => Ok("READY"));

        await BuildReadinessPoller(executor).WaitForLoginReadyAsync(VmName, AdminPassword, 150);

        var pollCall = executor.Calls.Single();
        pollCall.CarriedSecrets.Should().BeTrue();
        pollCall.AllowDump.Should().BeFalse();
        pollCall.Script.Should().NotContain(AdminPassword);
        pollCall.SecretEnvironment!.Should().ContainKey(GuestReadinessPoller.AdminPasswordEnvVar);
    }

    [Fact]
    public async Task Create_WithPassword_NeverLeaksPasswordIntoAnyDispatchedScript()
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        executor.Calls.Should().OnlyContain(call => !call.Script.Contains(AdminPassword));
    }

    // ── Guarantee 6 — a non-positive ownership probe blocks dispatch entirely ──

    [Theory]
    [InlineData("MOUNTED")]
    [InlineData("PROBE_FAILED")]
    [InlineData("")]
    [InlineData("unrecognized chatter")]
    public async Task Seed_WhenPreMountProbeIsNotPositive_NeverDispatchesTheMountScript(string probeStdout)
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok(probeStdout) : Ok(WithIssuedNonce(script, "SEEDED")));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        await act.Should().ThrowAsync<InvalidOperationException>();
        executor.Calls.Should().NotContain(call => IsSeedScript(call.Script),
            "a queued would-be-clean success must not be reachable: an unowned mount may be " +
            "neither operated on nor dismounted.");
        executor.Calls.Should().NotContain(call => IsOutOfBandDismount(call.Script));
    }

    [Fact]
    public async Task Seed_WhenPreMountProbeTimesOut_NeverDispatchesTheMountScript()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? TimedOut() : Ok(WithIssuedNonce(script, "SEEDED")));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        await act.Should().ThrowAsync<InvalidOperationException>();
        executor.Calls.Should().NotContain(call => IsSeedScript(call.Script));
    }

    [Theory]
    [InlineData("MOUNTED")]
    [InlineData("PROBE_FAILED")]
    [InlineData("")]
    [InlineData("unrecognized chatter")]
    public async Task Probe_WhenPreMountProbeIsNotPositive_NeverDispatchesTheMountScript(string probeStdout)
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok(probeStdout) : Ok(WithIssuedNonce(script, "GENERALIZED")));

        var state = await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        state.Should().Be(BaseImageGeneralizationState.Indeterminate);
        executor.Calls.Should().NotContain(call => IsGeneralizationProbe(call.Script));
        executor.Calls.Should().NotContain(call => IsOutOfBandDismount(call.Script));
    }

    [Fact]
    public async Task Probe_WhenPreMountProbeTimesOut_NeverDispatchesTheMountScript()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? TimedOut() : Ok(WithIssuedNonce(script, "GENERALIZED")));

        var state = await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        state.Should().Be(BaseImageGeneralizationState.Indeterminate);
        executor.Calls.Should().NotContain(call => IsGeneralizationProbe(call.Script));
    }

    // ── Anti-masking guard on the secret-bearing seam itself ──

    [Fact]
    public async Task ExecuteWithSecretsAsync_DefaultImplementation_Throws()
    {
        IPowerShellExecutor executor = new ExecuteOnlyPowerShellExecutor();

        var act = async () => await executor.ExecuteWithSecretsAsync(
            "Write-Output 'x'", new Dictionary<string, string> { ["SECRET"] = "value" });

        await act.Should().ThrowAsync<NotSupportedException>(
            "a silent forward would let a fake report success without proving the secret ever " +
            "reached the child process.");
    }

    // ── CLEAN-nonce protocol ──

    [Fact]
    public async Task Seed_WithForgedCleanNonce_DowngradesSuccessWhenCleanupCannotBeConfirmed()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNT_FAILED")
            : Ok("SEEDED|CLEAN:00000000000000000000000000000000"));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, AdminPassword, "en-US");

        await act.Should().ThrowAsync<InvalidOperationException>(
            "only the nonce this call issued proves the dismount ran; a surviving mount would " +
            "block Start-VM.");
        executor.Calls.Should().Contain(call => IsOutOfBandDismount(call.Script));
    }

    [Fact]
    public async Task Probe_WithForgedCleanNonce_DowngradesVerdictWhenCleanupCannotBeConfirmed()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNT_FAILED")
            : Ok("GENERALIZED|CLEAN:00000000000000000000000000000000"));

        var state = await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        state.Should().Be(BaseImageGeneralizationState.Indeterminate);
    }

    [Fact]
    public async Task Probe_WithMissingCleanNonce_StillDismountsOutOfBand()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED")
            : IsOutOfBandDismount(script) ? Ok("DISMOUNTED")
            : Ok("GENERALIZED"));

        var state = await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        state.Should().Be(BaseImageGeneralizationState.Generalized);
        executor.Calls.Should().Contain(call => IsOutOfBandDismount(call.Script));
    }

    // ── Readiness poll classification, bound, and window start ──

    [Theory]
    [InlineData("READY", GuestReadinessOutcome.LoginReadyAndScrubbed)]
    [InlineData("READY_SCRUB_UNCONFIRMED", GuestReadinessOutcome.LoginReadyScrubUnconfirmed)]
    [InlineData("CREDENTIAL_REJECTED", GuestReadinessOutcome.CredentialRejected)]
    [InlineData("DEADLINE", GuestReadinessOutcome.DeadlineReached)]
    [InlineData("POLL_FAILED:module load failed", GuestReadinessOutcome.PollFailed)]
    [InlineData("", GuestReadinessOutcome.PollFailed)]
    public async Task ReadinessPoll_MapsEachTerminalVerdict(string stdout, GuestReadinessOutcome expected)
    {
        var executor = new RecordingPowerShellExecutor(_ => Ok(stdout));

        var outcome = await BuildReadinessPoller(executor).WaitForLoginReadyAsync(VmName, AdminPassword, 150);

        outcome.Should().Be(expected);
    }

    [Fact]
    public void ReadinessPollScript_StopsOnCredentialRejection_AndKeepsPollingOnTransportFailures()
    {
        var script = GuestReadinessPoller.BuildPollScript(VmName, 150);

        var credentialIndex = script.IndexOf(
            "System.Management.Automation.Remoting.PSDirectException", StringComparison.Ordinal);
        var transportIndex = script.IndexOf(
            "System.Management.Automation.Remoting.PSRemotingTransportException", StringComparison.Ordinal);

        credentialIndex.Should().BeGreaterThan(-1);
        transportIndex.Should().BeGreaterThan(credentialIndex,
            "credential rejection must be matched before the transport family it shares a namespace " +
            "with, or a wrong password would be polled out to the deadline.");
        script.Should().Contain("PSRemotingDataStructureException");
        script.Should().Contain("if (-not $isTransport)",
            "anything outside the transport family must surface now rather than be mis-reported " +
            "as a readiness timeout.");
        script.Should().Contain("Start-Sleep -Seconds $backoffSeconds");
    }

    [Fact]
    public void ReadinessPollScript_IsBoundedByAWallClockDeadline()
    {
        var script = GuestReadinessPoller.BuildPollScript(VmName, 150);

        script.Should().Contain("(Get-Date).AddSeconds(150)");
        script.Should().Contain("while ((Get-Date) -lt $deadline)");
        script.Should().Contain("Write-Output 'DEADLINE'");
    }

    [Theory]
    [InlineData(30, 150)]
    [InlineData(600, 600)]
    public async Task Create_WithPassword_AppliesTheReadinessFloor(int createBudgetSeconds, int expectedLimitSeconds)
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createBudgetSeconds, CancellationToken.None);

        executor.Calls.Single(call => call.Script.Contains("New-PSSession", StringComparison.Ordinal))
            .Script.Should().Contain($"(Get-Date).AddSeconds({expectedLimitSeconds})",
                "the effective readiness limit is the create budget floored at 150 seconds.");
    }

    [Fact]
    public async Task Create_WithPassword_StartsTheReadinessWindowAtFirstPowerOn()
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        var scripts = executor.Scripts.ToList();
        var startIndex = scripts.FindIndex(script => script.Contains("'STARTED'", StringComparison.Ordinal));
        var pollIndex = scripts.FindIndex(script => script.Contains("New-PSSession", StringComparison.Ordinal));

        pollIndex.Should().BeGreaterThan(startIndex,
            "the readiness window is measured from first power-on, not from the start of the call.");
    }

    [Fact]
    public void ReadinessNotReachedException_MapsToItsOwnCode_NotTheGenericCatchAll()
    {
        var mapper = new ErrorMapper();

        mapper.MapException(new ReadinessNotReachedException(VmName, "never became login-ready", true))
            .ErrorCode.Should().Be(ErrorCodes.ReadinessNotReached);

        mapper.MapException(new InvalidOperationException("an ordinary operational failure"))
            .ErrorCode.Should().Be(ErrorCodes.CommandFailed,
                "the control case pins that the two new arms are extra ordering, not a broadened " +
                "generic arm.");
    }

    // ── Input validation ──

    [Theory]
    [InlineData("            ")]
    [InlineData("\t\t\t\t\t\t\t\t\t")]
    public void WhitespaceOnlyPassword_IsRejectedEvenWhenLongEnough(string password)
    {
        password.Length.Should().BeGreaterThanOrEqualTo(8,
            "the guard under test is the whitespace rule, not the length rule.");

        var act = () => InputValidation.ValidateAdminPassword(password);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Seed_RejectsWhitespaceOnlyPassword_BeforeDispatchingAnyScript()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "SEEDED")));

        var act = async () => await BuildSeeder(executor).SeedAsync(DiffVhdxPath, VmName, "         ", "en-US");

        await act.Should().ThrowAsync<ArgumentException>();
        executor.Calls.Should().BeEmpty(
            "a password the validator rejects must not reach an offline mount.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("            ")]
    [InlineData("short1!")]
    public async Task Create_WithInvalidPassword_IsRejectedOnTheDirectManagerPath(string password)
    {
        var executor = new RecordingPowerShellExecutor(RespondToCreateFlow);

        var act = async () => await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, password, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>(
            "a direct IHyperVManager caller must not bypass the dispatcher-level password rule.");
        executor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_ResolvesTheWindowsVolumeByGuidAccessPath_NotOnlyByDriveLetter()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsPreMountProbe(script) ? Ok("NOT_MOUNTED") : Ok(WithIssuedNonce(script, "GENERALIZED")));

        await BuildGeneralizationProbe(executor).ProbeAsync(BaseVhdxPath);

        var probeScript = executor.Calls.Single(call => IsGeneralizationProbe(call.Script)).Script;
        probeScript.Should().Contain("$partition.AccessPaths",
            "an offline-mounted VHD frequently exposes only a volume GUID path, so resolving " +
            "solely by drive letter yields a spurious not-generalized rejection.");
        probeScript.Should().Contain(@"'\\?\Volume*'");
        probeScript.Should().Contain("-NoDriveLetter");
    }

    // ── Scrub before rollback ──

    private static bool IsStartScript(string script)
        => script.Contains("'STARTED'", StringComparison.Ordinal);

    [Fact]
    public async Task Create_WhenStartFailsAfterSeeding_ScrubsTheAnswerFileBeforeRollback()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsStartScript(script) ? Ok("Start-VM reported a failure") : RespondToCreateFlow(script));

        var act = async () => await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        await act.Should().ThrowAsync<VmCreateRollbackException>();

        var scripts = executor.Scripts.ToList();
        var stopIndex = scripts.FindIndex(script => script.Contains("Stop-VM", StringComparison.Ordinal));
        var scrubIndex = scripts.FindIndex(IsScrubScript);
        var rollbackIndex = scripts.FindIndex(script => script.Contains("Remove-VM", StringComparison.Ordinal));

        stopIndex.Should().BeGreaterThan(-1, "an offline re-mount requires the VM to be Off.");
        scrubIndex.Should().BeGreaterThan(stopIndex);
        rollbackIndex.Should().BeGreaterThan(scrubIndex,
            "rollback may delete the disk, but the plaintext artifact must be removed on the way " +
            "to every terminal outcome, including this one.");
    }

    [Fact]
    public async Task Create_WhenScrubCannotBeConfirmed_FailureSaysSo()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsStartScript(script) ? Ok("Start-VM reported a failure")
            : script.Contains("Stop-VM", StringComparison.Ordinal) ? Ok("NOT_STOPPED")
            : RespondToCreateFlow(script));

        var act = async () => await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<VmCreateRollbackException>()).Which;
        thrown.Message.Should().NotContain(AdminPassword);
        executor.Scripts.Should().NotContain(script => IsScrubScript(script),
            "an offline mount must not be attempted against a VM that was not confirmed stopped.");
    }

    [Fact]
    public async Task Create_WhenStartReportsNotStarted_IsTreatedAsFailure()
    {
        var executor = new RecordingPowerShellExecutor(script =>
            IsStartScript(script) ? Ok("NOT_STARTED") : RespondToCreateFlow(script));

        var act = async () => await BuildManager(executor).CreateVmAsync(
            LocalHostId, VmName, BaseVhdxPath, 2, 4096, autoStart: false,
            verifyBaseImageHash: false, AdminPassword, createTimeBudgetSeconds: 600,
            CancellationToken.None);

        await act.Should().ThrowAsync<VmCreateRollbackException>();

        var scripts = executor.Scripts.ToList();
        var stopIndex = scripts.FindIndex(script => script.Contains("Stop-VM", StringComparison.Ordinal));
        var scrubIndex = scripts.FindIndex(IsScrubScript);
        var rollbackIndex = scripts.FindIndex(script => script.Contains("Remove-VM", StringComparison.Ordinal));

        stopIndex.Should().BeGreaterThan(-1);
        scrubIndex.Should().BeGreaterThan(stopIndex);
        rollbackIndex.Should().BeGreaterThan(scrubIndex);
    }
}
