using System.Security.Principal;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #125 — Part B (CPF-D3/D4/D7) live, NON-MOCKED regression for Checkpoint-VM
/// host selection — the live coverage the #206 mocked tests could not provide. Two tiers,
/// both using the REAL <see cref="PowerShellExecutor"/>:
///  • Tier 1 — host-selection assertion (any Hyper-V host; no env gate, no VM). Proves
///    the selected executable resolves Checkpoint-VM metadata under -NonInteractive; a
///    host that trips the PS7 "Value cannot be null" bug (CPF-D3) FAILS.
///  • Tier 2 — real create→delete round-trip (env-gated, operator opt-in; disposable VM
///    only). Asserts no "cannot be null" leak; always cleans up; skips when the gate is
///    off so production/donor VMs are never touched.
/// xUnit v2 has no first-class skip, so gated tests early-return and REPORT AS PASSED —
/// check the output for "SKIP:" lines.
/// See internal documentation
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RealPowerShell")]
public class Issue125CheckpointVmRealExecutorRegressionTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Operator opt-in for the live round-trip tier (CPF-D7 / OQ-1).</summary>
    private const string RunRealPsEnvVar = "HYPERV_MCP_RUN_REAL_PS";

    /// <summary>
    /// GUID of a DISPOSABLE test VM the operator authorizes for a real checkpoint
    /// create→delete round-trip. MUST NOT be a production or donor VM. When unset, the
    /// round-trip tier skips. The donor VM (win11-mcp-test) MUST NOT be used here.
    /// </summary>
    private const string TestVmIdEnvVar = "HYPERV_MCP_TEST_VM_ID";

    private const string LocalHostId = "local";

    public Issue125CheckpointVmRealExecutorRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── Tier 1: real-executor Checkpoint-VM metadata selection (no VM, no env gate) ──

    /// <summary>
    /// CPF-D3 / CPF-D7 Part B selection assertion. Runs the detection probe's representative
    /// Checkpoint-VM metadata resolution against the already-selected executable via the real
    /// spawn path: the selected interpreter must resolve it under -NonInteractive without the
    /// PS7 "cannot be null" failure. Early-returns (reported passed) on a non-Hyper-V machine.
    /// </summary>
    [Fact]
    public async Task RealExecutor_ResolvesCheckpointVmMetadata_UnderNonInteractive()
    {
        if (!HyperVAvailable())
        {
            _output.WriteLine(
                "SKIP: RealExecutor_ResolvesCheckpointVmMetadata_UnderNonInteractive — Hyper-V (vmms) not available on this machine.");
            return;
        }

        var executor = new PowerShellExecutor(
            NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());
        _output.WriteLine($"Selected PowerShell executable: {executor.ExecutablePath}");

        // Mirror the CPF-D3 probe: resolve Checkpoint-VM and touch a parameter's metadata.
        // The script maps the 'cannot be null' failure class to a distinct exit code so the
        // assertion message is unambiguous.
        var result = await executor.ExecuteAsync(
            "Import-Module Hyper-V -ErrorAction Stop; " +
            "try { " +
            "  $cmd = Get-Command Checkpoint-VM -ErrorAction Stop; " +
            "  $cmd.Parameters['SnapshotName'] | Out-Null; " +
            "  $cmd.Parameters['VMName'] | Out-Null " +
            "} catch { " +
            "  if ($_.Exception.Message -match 'cannot be null') { Write-Error 'CPF-NULLNAME'; exit 2 } " +
            "  else { Write-Error $_.Exception.Message; exit 1 } " +
            "}; " +
            "Write-Output 'CheckpointVM-META-OK'",
            timeoutSeconds: 30);

        _output.WriteLine(
            $"exit={result.ExitCode} timedOut={result.TimedOut} stdout='{result.Stdout.Trim()}' stderr='{result.Stderr.Trim()}'");

        result.ExitCode.Should().NotBe(2,
            $"CPF-D3/#125: the selected executable '{executor.ExecutablePath}' must NOT hit the PS7 " +
            "non-interactive 'Value cannot be null' bug when resolving Checkpoint-VM metadata — " +
            "that is the exact live failure #125 reproduces");
        result.ExitCode.Should().Be(0,
            $"the selected executable '{executor.ExecutablePath}' must resolve Checkpoint-VM parameter " +
            "metadata under -NonInteractive (the representative checkpoint-path probe, CPF-D3)");
        result.Stdout.Should().Contain("CheckpointVM-META-OK",
            "the representative Checkpoint-VM metadata resolution must succeed on the selected interpreter");
        result.Stderr.Should().NotContain("cannot be null",
            "no 'Value cannot be null' diagnostic may surface for Checkpoint-VM on the selected interpreter");
    }

    // ── Tier 2: real create→delete round-trip (env-gated; disposable test VM only) ──

    /// <summary>
    /// CPF-D7 / OQ-1 live round-trip. Drives the real <see cref="CheckpointManager"/> to
    /// CREATE then DELETE a uniquely-named checkpoint on a disposable test VM, asserting a
    /// well-formed envelope and no "cannot be null" leak (the masked PS7 error pre-fix).
    ///
    /// SAFETY: gated by <see cref="RunRealPsEnvVar"/>==1 AND a non-empty
    /// <see cref="TestVmIdEnvVar"/>. Targets ONLY the operator-named disposable VM (donor
    /// win11-mcp-test and production VMs are out of scope). Always deletes in a finally block.
    /// </summary>
    [Fact]
    public async Task RealCheckpointManager_CreateThenDelete_RoundTrip_NoNullNameLeak()
    {
        var gate = Environment.GetEnvironmentVariable(RunRealPsEnvVar);
        var testVmId = Environment.GetEnvironmentVariable(TestVmIdEnvVar);

        // Opt-in MUST be the exact literal "1". Treating any non-"0" value as enabled would
        // let HYPERV_MCP_RUN_REAL_PS=false (or =2) silently trigger a real create→delete on a
        // live VM — a destructive footgun. Match the documented gate (==1) precisely.
        if (gate != "1")
        {
            _output.WriteLine(
                $"SKIP: RealCheckpointManager_CreateThenDelete_RoundTrip_NoNullNameLeak — {RunRealPsEnvVar} not set to 1 (live round-trip is operator opt-in).");
            return;
        }

        if (!HyperVAvailable() || !IsElevated())
        {
            _output.WriteLine(
                "SKIP: RealCheckpointManager_CreateThenDelete_RoundTrip_NoNullNameLeak — requires an elevated process on a Hyper-V host.");
            return;
        }

        if (string.IsNullOrWhiteSpace(testVmId))
        {
            _output.WriteLine(
                $"SKIP: RealCheckpointManager_CreateThenDelete_RoundTrip_NoNullNameLeak — {TestVmIdEnvVar} (disposable test VM GUID) not set. " +
                "Refusing to run a live checkpoint round-trip without an explicit disposable target (donor/production VMs are out of scope).");
            return;
        }

        var manager = BuildRealCheckpointManager();
        var checkpointName = $"hvmcp-it-{Guid.NewGuid():N}".Substring(0, 24);
        _output.WriteLine($"Live round-trip on test VM {testVmId} with checkpoint '{checkpointName}'.");

        try
        {
            // CREATE — the operation that fails (masked) pre-fix on an affected host.
            var createResult = await manager.CreateCheckpointAsync(LocalHostId, testVmId, checkpointName);

            createResult.Should().NotBeNull();
            createResult.Action.Should().Be("create");
            createResult.Checkpoints.Should().NotBeNullOrEmpty(
                "a real checkpoint with the requested name must be observable (VC-CE-D1) — " +
                "this is the post-fix success #125 requires");
            createResult.Checkpoints![0].Id.Should().NotBeNullOrEmpty();
            _output.WriteLine($"Created checkpoint Id={createResult.Checkpoints[0].Id}");
        }
        catch (CheckpointFailedException ex)
        {
            // If the host is still affected, the now-unmasked (CPF-D1) message is visible
            // here. Surface it verbatim so the live re-run is self-diagnosing per OQ-1.
            ex.Message.Should().NotContain("Value cannot be null",
                "CPF-D3/#125: a real Checkpoint-VM create must not fail with the PS7 non-interactive " +
                $"'Value cannot be null' bug after the fix. Unmasked diagnostic: {ex.Message}");
            throw;
        }
        finally
        {
            // Best-effort cleanup runs unconditionally: per #206 the create wrapper can throw
            // even after the checkpoint side-effect lands, so gating cleanup on an observed
            // success would leak that checkpoint. DeleteCheckpointAsync is a harmless no-op
            // when nothing was created, so deleting by name is always safe here.
            try
            {
                var del = await manager.DeleteCheckpointAsync(LocalHostId, testVmId, checkpointName);
                del.Action.Should().Be("delete");
                _output.WriteLine($"Cleanup: deleted checkpoint '{checkpointName}'.");
            }
            catch (Exception cleanupEx)
            {
                _output.WriteLine(
                    $"Cleanup WARNING: failed to delete checkpoint '{checkpointName}' on VM {testVmId}: {cleanupEx.Message}. Manual cleanup may be required.");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CheckpointManager BuildRealCheckpointManager()
    {
        var psExecutor = new PowerShellExecutor(
            NullLoggerFactory.Instance.CreateLogger<PowerShellExecutor>());

        var options = new ServerOptions
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                },
            },
        };
        var hostResolver = new HostResolver(options);

        var psHost = new PowerShellHost(NullLoggerFactory.Instance.CreateLogger<PowerShellHost>());
        var sessionStore = new SessionStore(
            psHost, NullLoggerFactory.Instance.CreateLogger<SessionStore>());

        return new CheckpointManager(
            psExecutor, hostResolver, sessionStore,
            NullLoggerFactory.Instance.CreateLogger<CheckpointManager>());
    }

    /// <summary>True when the Hyper-V management service (vmms) exists and is Running.</summary>
    private static bool HyperVAvailable()
    {
        try
        {
            var vmms = System.ServiceProcess.ServiceController
                .GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, "vmms", StringComparison.OrdinalIgnoreCase));
            return vmms?.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
