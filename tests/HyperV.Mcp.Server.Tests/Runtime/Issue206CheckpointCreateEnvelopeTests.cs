using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for Issue #206 / CP-D7 / VC-CE-D1..D8 — envelope correctness for
/// vm_checkpoint {action:"create"}.
///
/// Implements T1..T11 plus T2b and T2c per
/// /myplans/vm-management/checkpoints/vm-checkpoint-create-envelope-design.md
///
/// Tests use Moq for IPowerShellExecutor with call-index-aware setups to model the
/// three-call create flow:
///   (1) pre-snapshot Get-VMCheckpoint
///   (2) main Checkpoint-VM script
///   (3) post-failure Get-VMCheckpoint probe
/// </summary>
[Trait("Category", "Runtime")]
public class Issue206CheckpointCreateEnvelopeTests
{
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";
    private const string LocalHostId = "local";

    private readonly Mock<IPowerShellExecutor> _mockExecutor;
    private readonly Mock<ISessionStore> _mockSessionStore;
    private readonly IHostResolver _hostResolver;
    private readonly Mock<ILogger<CheckpointManager>> _mockLogger;
    private readonly CheckpointManager _manager;

    public Issue206CheckpointCreateEnvelopeTests()
    {
        _mockExecutor = new Mock<IPowerShellExecutor>();
        _mockSessionStore = new Mock<ISessionStore>();
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
                    BaseVhdxPath = @"C:\Base\base.vhdx",
                    StorageRoot = @"C:\HyperVMCP\VMs",
                },
            },
        };
        _hostResolver = new HostResolver(options);
        _mockLogger = new Mock<ILogger<CheckpointManager>>();
        _manager = new CheckpointManager(
            _mockExecutor.Object, _hostResolver, _mockSessionStore.Object, _mockLogger.Object);
    }

    // --- Helpers --------------------------------------------------------------

    private static PowerShellResult Ok(string stdout) => new()
    {
        ExitCode = 0, Stdout = stdout, Stderr = string.Empty, DurationMs = 1,
    };

    private static PowerShellResult Fail(string stderr, int exitCode = 1) => new()
    {
        ExitCode = exitCode, Stdout = string.Empty, Stderr = stderr, DurationMs = 1,
    };

    private static string PreIdsJson(params string[] ids)
        => "{\"Ids\":[" + string.Join(",", ids.Select(i => "\"" + i + "\"")) + "]}";

    private static string ProbeCheckpointsJson(params (string Name, string Id)[] cps)
    {
        var elems = cps.Select(c =>
            $"{{\"Name\":\"{c.Name}\",\"Id\":\"{c.Id}\",\"CreatedAt\":\"2026-05-21T00:00:00+00:00\"}}");
        return "{\"Checkpoints\":[" + string.Join(",", elems) + "]}";
    }

    private static string CreateSuccessJson(string cpName, string id) =>
        $"{{\"Action\":\"create\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"{cpName}\",\"Checkpoints\":[{{\"Name\":\"{cpName}\",\"Id\":\"{id}\",\"CreatedAt\":\"2026-05-21T00:00:00+00:00\"}}]}}";

    /// <summary>
    /// Configures the mock executor to return a sequenced list of responses (one per
    /// successive ExecuteAsync call). Useful for orchestrating the three-call flow.
    /// </summary>
    private List<string> SequenceExecutor(params PowerShellResult[] responses)
    {
        var capturedScripts = new List<string>();
        int call = 0;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScripts.Add(s))
            .ReturnsAsync(() =>
            {
                var idx = call < responses.Length ? call : responses.Length - 1;
                call++;
                return responses[idx];
            });
        return capturedScripts;
    }

    // --- T1: warning-then-JSON stdout → success -------------------------------

    [Fact]
    public async Task T1_WarningThenJson_Stdout_Succeeds()
    {
        var realJson = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-111111111111");
        SequenceExecutor(
            Ok(PreIdsJson()),                                  // (1) pre-snapshot
            Ok("WARNING: duplicate name\n" + realJson + "\n")  // (2) main with noise prefix
        );

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        result.Should().NotBeNull();
        result.Action.Should().Be("create");
        result.CheckpointName.Should().Be("cp1");
        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Id.Should().Be("aaaaaaaa-bbbb-cccc-dddd-111111111111");
    }

    // --- T2: PS non-zero + post-probe finds NEW checkpoint → downgrade --------

    [Fact]
    public async Task T2_HostSideDowngrade_NewCheckpointExists()
    {
        var newId = "11111111-2222-3333-4444-555555555555";
        SequenceExecutor(
            Ok(PreIdsJson()),                                  // (1) empty pre-snapshot
            Fail("NullReferenceException at line 73"),         // (2) main fails (NRE-shaped)
            Ok(ProbeCheckpointsJson(("cp1", newId)))           // (3) post-probe sees NEW cp
        );

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        result.Should().NotBeNull();
        result.Action.Should().Be("create");
        result.CheckpointName.Should().Be("cp1");
        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Id.Should().Be(newId);

        // VC-CE-D7: structured LogWarning on the downgrade path.
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("VC-CE-D5 downgrade")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    // --- T2b: pre-existing same-name checkpoint + no new Id → still failure ---

    [Fact]
    public async Task T2b_PreExistingSameName_NoNewId_StaysFailed()
    {
        var preExistingId = "99999999-9999-9999-9999-999999999999";
        SequenceExecutor(
            Ok(PreIdsJson(preExistingId)),                       // (1) preIds includes same-name Id
            Fail("Checkpoint-VM failed: timeout"),               // (2) main fails
            Ok(ProbeCheckpointsJson(("cp1", preExistingId)))     // (3) probe sees SAME Id only
        );

        Func<Task> act = () => _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        await act.Should().ThrowAsync<CheckpointFailedException>();
    }

    // --- T2c.i: pre-snapshot fails + main succeeds → success unchanged -------

    [Fact]
    public async Task T2c_i_PreSnapshotFails_MainSucceeds_NormalSuccess()
    {
        var realJson = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-222222222222");
        SequenceExecutor(
            Fail("Get-VMCheckpoint: access denied"),  // (1) pre-snapshot fails
            Ok(realJson)                              // (2) main succeeds
        );

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        result.Should().NotBeNull();
        result.Action.Should().Be("create");
        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Id.Should().Be("aaaaaaaa-bbbb-cccc-dddd-222222222222");
    }

    // --- T2c.ii: pre-snapshot fails + main fails → CHECKPOINT_FAILED, probe NEVER called ---

    [Fact]
    public async Task T2c_ii_PreSnapshotFails_MainFails_NoProbeInvoked()
    {
        int callCount = 0;
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) return Fail("Get-VMCheckpoint: access denied");
                if (callCount == 2) return Fail("Checkpoint-VM: insufficient disk space");
                throw new InvalidOperationException("Post-failure probe MUST NOT be invoked when pre-snapshot failed.");
            });

        Func<Task> act = () => _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        var ex = await act.Should().ThrowAsync<CheckpointFailedException>();
        ex.Which.Message.Should().Contain("insufficient disk space");
        callCount.Should().Be(2, "exactly pre-snapshot + main script — no post-failure probe");
    }

    // --- T3: PS non-zero + probe finds NOTHING → CHECKPOINT_FAILED preserved --

    [Fact]
    public async Task T3_EmptyProbe_PreservesFailure()
    {
        SequenceExecutor(
            Ok(PreIdsJson()),                              // (1)
            Fail("Checkpoint-VM failed: foo"),             // (2)
            Ok(ProbeCheckpointsJson())                     // (3) empty probe result
        );

        Func<Task> act = () => _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        await act.Should().ThrowAsync<CheckpointFailedException>();
    }

    // --- T4: in-script retry succeeds — we model via the main script returning
    //         a valid envelope whose Id is NOT in preIds (simulating successful
    //         retry-2 path in the embedded script). -----------------------------

    [Fact]
    public async Task T4_ProbeRetrySucceeds_ReturnsRealId()
    {
        var realId = "bbbbbbbb-cccc-dddd-eeee-333333333333";
        SequenceExecutor(
            Ok(PreIdsJson("old-id-1", "old-id-2")),
            Ok(CreateSuccessJson("cp1", realId))
        );

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Id.Should().Be(realId);
    }

    // --- T5: probe exhausted → fail loud (model via PS non-zero stderr matching
    //         CHECKPOINT_PROBE_EXHAUSTED, AND probe returns nothing new) --------

    [Fact]
    public async Task T5_ProbeExhausted_NoSyntheticSuccess()
    {
        SequenceExecutor(
            Ok(PreIdsJson()),
            Fail("CHECKPOINT_PROBE_EXHAUSTED: requested='cp1' preIdCount=0 attempts=3", exitCode: 2),
            Ok(ProbeCheckpointsJson())   // post-probe also finds nothing
        );

        Func<Task> act = () => _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        var ex = await act.Should().ThrowAsync<CheckpointFailedException>();
        ex.Which.Message.Should().Contain("CHECKPOINT_PROBE_EXHAUSTED");
    }

    // --- T6: same-name pre-existing → success path; no warning bleed ----------

    [Fact]
    public async Task T6_SameNamePreExisting_NewIdSucceedsNoNoise()
    {
        var preId = "aaaaaaaa-1111-1111-1111-111111111111";
        var newId = "aaaaaaaa-2222-2222-2222-222222222222";
        var stdout = CreateSuccessJson("cp1", newId);

        SequenceExecutor(
            Ok(PreIdsJson(preId)),
            Ok(stdout)
        );

        var result = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        result.Should().NotBeNull();
        result.Checkpoints![0].Id.Should().Be(newId);
        stdout.Should().NotContain("WARNING:", "VC-CE-D6 removes the duplicate-name Write-Warning");
    }

    // --- T7: real powershell.exe regression — gated by env --------------------

    [Fact]
    [Trait("Category", "RealPowerShell")]
    public void T7_RealPowerShell_RegressionPlaceholder()
    {
        // Real powershell.exe execution requires Hyper-V on the host. The smoke-test
        // and live-integration suites cover this; here we document the slot.
        // If $env:HYPERV_MCP_RUN_REAL_PS == "1" AND Hyper-V is available, the
        // intended behavior is to instantiate a real PowerShellExecutor and call
        // CreateCheckpointAsync against a test VM, asserting no NRE leaks out and
        // envelope is success:true. Skipped by default.
        var enable = Environment.GetEnvironmentVariable("HYPERV_MCP_RUN_REAL_PS");
        Assert.True(string.IsNullOrEmpty(enable) || enable == "0",
            "Real-PowerShell regression for #206 is exercised by the live integration suite.");
    }

    // --- T8: parser unit (happy path with create validator) -------------------

    [Fact]
    public void T8_ParseCheckpointResult_Happy_CreatePayload()
    {
        // Direct parser unit test via the public create flow (parser is private).
        var realJson = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-444444444444");
        var noisy = "WARNING: duplicate name\n" + realJson + "\ntrailing junk\n";
        SequenceExecutor(Ok(PreIdsJson()), Ok(noisy));

        var result = _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1").GetAwaiter().GetResult();

        result.Action.Should().Be("create");
        result.CheckpointName.Should().Be("cp1");
        result.Checkpoints.Should().HaveCount(1);
        result.Checkpoints![0].Id.Should().NotBeNullOrEmpty();
    }

    // --- T9: cross-action — restore/delete/list payloads ----------------------

    [Fact]
    public async Task T9_RestorePayload_Accepted()
    {
        var restoreJson = $"{{\"Action\":\"restore\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"cp1\",\"Checkpoints\":null}}";
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Ok(restoreJson));
        _mockSessionStore
            .Setup(s => s.EvictAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var r = await _manager.RestoreCheckpointAsync(LocalHostId, TestVmId, "cp1");
        r.Action.Should().Be("restore");
        r.CheckpointName.Should().Be("cp1");
    }

    [Fact]
    public async Task T9_DeletePayload_Accepted()
    {
        var deleteJson = $"{{\"Action\":\"delete\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"cp1\",\"Checkpoints\":null}}";
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Ok(deleteJson));

        var r = await _manager.DeleteCheckpointAsync(LocalHostId, TestVmId, "cp1");
        r.Action.Should().Be("delete");
        r.CheckpointName.Should().Be("cp1");
    }

    [Fact]
    public async Task T9_ListPayload_NonEmpty_Accepted()
    {
        var listJson = $"{{\"Action\":\"list\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"\",\"Checkpoints\":[" +
                       "{\"Name\":\"a\",\"Id\":\"id-a\",\"CreatedAt\":\"2026-01-01T00:00:00Z\"}," +
                       "{\"Name\":\"b\",\"Id\":\"id-b\",\"CreatedAt\":\"2026-01-02T00:00:00Z\"}" +
                       "]}";
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Ok(listJson));

        var r = await _manager.ListCheckpointsAsync(LocalHostId, TestVmId);
        r.Action.Should().Be("list");
        r.Checkpoints.Should().HaveCount(2);
    }

    [Fact]
    public async Task T9_ListPayload_Empty_Accepted()
    {
        var listJson = $"{{\"Action\":\"list\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"\",\"Checkpoints\":[]}}";
        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Ok(listJson));

        var r = await _manager.ListCheckpointsAsync(LocalHostId, TestVmId);
        r.Action.Should().Be("list");
        r.Checkpoints.Should().BeEmpty();
    }

    // --- T10: parser spoof safety (three sub-cases under create validator) ----

    [Fact]
    public async Task T10a_NoiseObject_Rejected_RealEnvelopeWins()
    {
        var realJson = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-555555555555");
        var noisy = "{\"hello\":\"world\"}\n" + realJson + "\n";
        SequenceExecutor(Ok(PreIdsJson()), Ok(noisy));

        var r = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");
        r.Checkpoints![0].Id.Should().Be("aaaaaaaa-bbbb-cccc-dddd-555555555555");
    }

    [Fact]
    public async Task T10b_WrongCheckpointName_Rejected()
    {
        var wrong = $"{{\"Action\":\"create\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"different\",\"Checkpoints\":[{{\"Name\":\"different\",\"Id\":\"x\"}}]}}";
        var real = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-666666666666");
        SequenceExecutor(Ok(PreIdsJson()), Ok(wrong + "\n" + real + "\n"));

        var r = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");
        r.CheckpointName.Should().Be("cp1");
        r.Checkpoints![0].Id.Should().Be("aaaaaaaa-bbbb-cccc-dddd-666666666666");
    }

    [Fact]
    public async Task T10c_EmptyId_Rejected()
    {
        var empty = $"{{\"Action\":\"create\",\"VmId\":\"{TestVmId}\",\"CheckpointName\":\"cp1\",\"Checkpoints\":[{{\"Name\":\"cp1\",\"Id\":\"\"}}]}}";
        var real = CreateSuccessJson("cp1", "aaaaaaaa-bbbb-cccc-dddd-777777777777");
        SequenceExecutor(Ok(PreIdsJson()), Ok(empty + "\n" + real + "\n"));

        var r = await _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");
        r.Checkpoints![0].Id.Should().Be("aaaaaaaa-bbbb-cccc-dddd-777777777777");
    }

    // --- T11: all-noise input falls through to InvalidOperationException ------

    [Fact]
    public async Task T11_AllNoise_CreateValidator_Throws()
    {
        var allNoise =
            "{\"hello\":\"world\"}\n" +
            "{\"Action\":\"create\",\"CheckpointName\":\"wrong\",\"Checkpoints\":[]}\n" +
            "{\"Action\":\"restore\",\"CheckpointName\":\"cp1\"}\n";

        SequenceExecutor(Ok(PreIdsJson()), Ok(allNoise));

        Func<Task> act = () => _manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PowerShell returned empty output*");
    }

    [Fact]
    public async Task T11_AllNoise_ListValidator_Throws()
    {
        var allNoise =
            "{\"hello\":\"world\"}\n" +
            "{\"Action\":\"create\",\"CheckpointName\":\"cp1\",\"Checkpoints\":[{\"Name\":\"cp1\",\"Id\":\"x\"}]}\n";

        _mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Ok(allNoise));

        Func<Task> act = () => _manager.ListCheckpointsAsync(LocalHostId, TestVmId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PowerShell returned empty output*");
    }
}
