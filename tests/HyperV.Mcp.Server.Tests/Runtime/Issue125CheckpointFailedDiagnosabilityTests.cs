using System.Text.Json;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #125 — CHECKPOINT_FAILED diagnosability, unit-level seams for:
///  • Part A (ErrorMapper): forward the sanitized diagnostic (CPF-D1), keep the VM
///    identifier (CPF-D2), via the generic arm's sanitizer chain (Constraint #4), with
///    no taxonomy change (Constraint #3).
///  • CPF-D6 (CheckpointManager.HandleError): LogWarning the exit code + stderr preview
///    before throwing, to the logger sink only (never stdout — owned by the JSON envelope).
/// The live Part B / CPF-D7 path is covered separately (Category=Integration).
/// See /myplans/vm-management/checkpoints/vm-checkpoint-failed-diagnosability-design.md.
/// </summary>
[Trait("Category", "Runtime")]
public class Issue125CheckpointFailedDiagnosabilityTests
{
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";
    private const string LocalHostId = "local";

    private readonly ErrorMapper _mapper = new();

    private static JsonElement Roundtrip(McpToolResponse response)
    {
        var json = JsonSerializer.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── Part A / CPF-D1 + CPF-D2: ErrorMapper checkpoint arm ──────────────────

    /// <summary>
    /// CPF-D1: the real "(exit code N): &lt;stderr&gt;" diagnostic from HandleError must
    /// reach the envelope's `error` instead of being masked by a generic rebuild.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_ForwardsExitCodeAndStderr_OntoWire()
    {
        var ex = new CheckpointFailedException(
            LocalHostId, TestVmId,
            "Checkpoint operation failed (exit code 1): Checkpoint-VM : Value cannot be null. Parameter name: name");

        var response = _mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("exit code 1",
            "CPF-D1: the underlying exit code must reach the wire (was masked before)");
        response.Error.Should().Contain("Value cannot be null",
            "CPF-D1: the underlying stderr must reach the wire (was masked before)");
    }

    /// <summary>
    /// CPF-D2: the VM identifier must remain recoverable in the envelope after the
    /// Part A reshaping. The later regression suite asserts vmId is still present.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_PreservesVmId_InErrorAndDetails()
    {
        var ex = new CheckpointFailedException(
            LocalHostId, TestVmId,
            "Checkpoint operation failed (exit code 1): some stderr",
            checkpointName: "nightly");

        var response = _mapper.MapException(ex);
        var root = Roundtrip(response);

        // Recoverable in the human-readable `error` string (CPF-D2 acceptable shape).
        response.Error.Should().Contain(TestVmId,
            "CPF-D2: vmId must remain recoverable in the error text");
        response.Error.Should().Contain("nightly",
            "CPF-D2: checkpointName must remain recoverable when present");

        // Also recoverable as a structured field for machine consumers.
        var details = root.GetProperty("details");
        details.GetProperty("vmId").GetString().Should().Be(TestVmId);
        details.GetProperty("hostId").GetString().Should().Be(LocalHostId);
        details.GetProperty("checkpointName").GetString().Should().Be("nightly");
        details.GetProperty("diagnostic").GetString().Should().Contain("exit code 1");
    }

    /// <summary>
    /// Constraint #3: the error-code taxonomy is unchanged — Part A enriches the
    /// message/details, it does not add or repurpose a code.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_KeepsErrorCode_Unchanged()
    {
        var ex = new CheckpointFailedException(
            LocalHostId, TestVmId, "Checkpoint operation failed (exit code 2): boom");

        var response = _mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.CheckpointFailed,
            "Constraint #3: errorCode stays CHECKPOINT_FAILED");
    }

    /// <summary>
    /// CPF-D7(a) — consolidated wire-unmasking regression. Pins all four post-fix
    /// conditions on the serialized envelope as one conjunction so any single regression
    /// fails loudly: (1) the diagnostic (exit code + stderr) is in `error`, (2) `vmId` is
    /// recoverable, (3) `errorCode` stays CHECKPOINT_FAILED, (4) `details` is populated.
    /// The pre-fix envelope masked the diagnostic and left details:null (failing 1 and 4).
    /// See /myplans/vm-management/checkpoints/vm-checkpoint-failed-diagnosability-design.md.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_PreFixFailingEnvelope_AllFourConditionsHold()
    {
        var ex = new CheckpointFailedException(
            LocalHostId, TestVmId,
            "Checkpoint operation failed (exit code 1): Checkpoint-VM : Value cannot be null. Parameter name: name",
            checkpointName: "nightly");

        var response = _mapper.MapException(ex);
        var root = Roundtrip(response);

        // Leg 0: it is an error envelope.
        root.GetProperty("success").GetBoolean().Should().BeFalse();

        // Leg 1: the real diagnostic (exit code + stderr) reaches the wire `error`.
        var error = root.GetProperty("error").GetString();
        error.Should().NotBeNull();
        error!.Should().Contain("exit code 1",
            "CPF-D1: the underlying exit code must reach the wire");
        error.Should().Contain("Value cannot be null",
            "CPF-D1: the underlying stderr must reach the wire");

        // Leg 2: vmId is still recoverable in the human-readable `error` (CPF-D2).
        error.Should().Contain(TestVmId,
            "CPF-D2: vmId must remain recoverable in the error text");

        // Leg 3: the taxonomy code is unchanged.
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.CheckpointFailed,
            "Constraint #3: errorCode stays CHECKPOINT_FAILED");

        // Leg 4: `details` is populated (pre-fix was null) with the sanitized diagnostic
        // and preserved identifiers.
        var details = root.GetProperty("details");
        details.ValueKind.Should().NotBe(JsonValueKind.Null,
            "CPF-D1/D2: the pre-fix envelope had details:null; the fix must populate it");
        details.GetProperty("vmId").GetString().Should().Be(TestVmId,
            "CPF-D2: vmId must be machine-readable in details");
        details.GetProperty("hostId").GetString().Should().Be(LocalHostId);
        details.GetProperty("checkpointName").GetString().Should().Be("nightly");
        details.GetProperty("diagnostic").GetString().Should()
            .Contain("exit code 1", "CPF-D1: the structured diagnostic carries the exit code")
            .And.Contain("Value cannot be null", "CPF-D1: the structured diagnostic carries the stderr");
    }

    /// <summary>
    /// Constraint #4: the forwarded text passes through the same PS-noise sanitizer
    /// the generic InvalidOperationException arm uses, so positional tokens
    /// ("At &lt;path&gt;:&lt;line&gt; char:&lt;col&gt;") never leak onto the wire.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_StripsPowerShellPositionalNoise()
    {
        var noisy =
            "Checkpoint operation failed (exit code 1): Checkpoint-VM : boom\r\n" +
            "At C:\\Program Files\\hvmcp\\script.ps1:42 char:5\r\n" +
            "+ CategoryInfo          : InvalidOperation: (:) [Checkpoint-VM]\r\n";
        var ex = new CheckpointFailedException(LocalHostId, TestVmId, noisy);

        var response = _mapper.MapException(ex);

        response.Error.Should().NotContain("char:5",
            "Constraint #4: PS positional tokens must be stripped before emission");
        response.Error.Should().NotContain("CategoryInfo",
            "Constraint #4: PS CategoryInfo noise must be stripped before emission");
        response.Error.Should().Contain("boom",
            "the human-readable failure summary must survive sanitization");
    }

    /// <summary>
    /// Constraint #4 / Assumption 3: credential text in the message is redacted by the
    /// same RedactCredentials backstop the generic arm relies on.
    /// </summary>
    [Fact]
    public void MapException_CheckpointFailed_RedactsCredentialText()
    {
        var withSecret =
            "Checkpoint operation failed (exit code 1): " +
            "ConvertTo-SecureString 'SuperSecretP@ss' -AsPlainText -Force";
        var ex = new CheckpointFailedException(LocalHostId, TestVmId, withSecret);

        var response = _mapper.MapException(ex);
        var root = Roundtrip(response);

        response.Error.Should().NotContain("SuperSecretP@ss",
            "Constraint #4: ConvertTo-SecureString plaintext must be redacted on the wire");
        root.GetProperty("details").GetProperty("diagnostic").GetString()
            .Should().NotContain("SuperSecretP@ss",
                "Constraint #4: the structured diagnostic must also be redacted");
    }

    // ── CPF-D6: HandleError host-side LogWarning ──────────────────────────────

    /// <summary>
    /// CPF-D6: CheckpointManager.HandleError must emit a host-side LogWarning capturing
    /// the exit code and a truncated stderr preview before throwing. Verified through the
    /// public create flow with a mocked executor whose main script fails.
    /// </summary>
    [Fact]
    public async Task HandleError_OnCheckpointFailure_LogsExitCodeAndStderr_Warning()
    {
        var mockLogger = await RunCreateFailureAsync(
            "Checkpoint-VM : Value cannot be null. Parameter name: name", exitCode: 1);

        VerifyCheckpointFailedWarning(
            mockLogger,
            text => text.Contains("Value cannot be null"),
            "CPF-D6: HandleError must LogWarning the exit code + stderr preview before throwing");
    }

    /// <summary>
    /// 🟡 #2 (Gate 7 follow-up): the CPF-D6 LogWarning must record WHICH checkpoint
    /// action failed. The create flow exercised here must log the "create" action.
    /// </summary>
    [Fact]
    public async Task HandleError_OnCheckpointFailure_LogsFailingAction()
    {
        var mockLogger = await RunCreateFailureAsync("Checkpoint-VM : boom", exitCode: 1);

        VerifyCheckpointFailedWarning(
            mockLogger,
            text => text.Contains("create"),
            "🟡 #2: the operator log must name the failing checkpoint action (create)");
    }

    /// <summary>
    /// 🟡 #1 (Gate 7 follow-up): the CPF-D6 log stderr preview must be sanitized with the
    /// SAME chain the wire path uses, so a credential token in stderr is NOT logged raw —
    /// the log must be no weaker than the client envelope.
    /// </summary>
    [Fact]
    public async Task HandleError_OnCheckpointFailure_SanitizesCredentialInLoggedPreview()
    {
        var withSecret =
            "Checkpoint-VM : ConvertTo-SecureString 'SuperSecretP@ss' -AsPlainText -Force";

        var mockLogger = await RunCreateFailureAsync(withSecret, exitCode: 1);

        VerifyCheckpointFailedWarning(
            mockLogger,
            text => !text.Contains("SuperSecretP@ss"),
            "🟡 #1: ConvertTo-SecureString plaintext must be redacted in the logged preview, not just on the wire");
    }

    /// <summary>
    /// 🟡 #1 (Gate 7 follow-up): PowerShell positional noise must also be stripped from the
    /// logged preview (same SanitizePowerShellErrorText pass the wire path applies).
    /// </summary>
    [Fact]
    public async Task HandleError_OnCheckpointFailure_StripsPowerShellNoiseFromLoggedPreview()
    {
        var noisy =
            "Checkpoint-VM : boom\r\n" +
            "At C:\\Program Files\\hvmcp\\script.ps1:42 char:5\r\n" +
            "+ CategoryInfo          : InvalidOperation: (:) [Checkpoint-VM]\r\n";

        var mockLogger = await RunCreateFailureAsync(noisy, exitCode: 1);

        VerifyCheckpointFailedWarning(
            mockLogger,
            text => !text.Contains("char:5") && !text.Contains("CategoryInfo") && text.Contains("boom"),
            "🟡 #1: PS positional/CategoryInfo noise must be stripped from the logged preview while the summary survives");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the public create flow so HandleError runs, returning the logger mock.
    /// The mocked executor returns: (1) pre-snapshot OK with empty Ids, (2) main script
    /// fails, (3) post-probe finds nothing new → original failure preserved.
    /// </summary>
    private static async Task<Mock<ILogger<CheckpointManager>>> RunCreateFailureAsync(
        string stderr, int exitCode)
    {
        var mockExecutor = new Mock<IPowerShellExecutor>();
        var mockSessionStore = new Mock<ISessionStore>();
        var mockLogger = new Mock<ILogger<CheckpointManager>>();
        var hostResolver = BuildLocalHostResolver();

        int call = 0;
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(() =>
            {
                call++;
                if (call == 1) return Ok("{\"Ids\":[]}");
                if (call == 2) return Fail(stderr, exitCode);
                return Ok("{\"Checkpoints\":[]}");
            });

        var manager = new CheckpointManager(
            mockExecutor.Object, hostResolver, mockSessionStore.Object, mockLogger.Object);

        Func<Task> act = () => manager.CreateCheckpointAsync(LocalHostId, TestVmId, "cp1");
        await act.Should().ThrowAsync<CheckpointFailedException>();

        return mockLogger;
    }

    /// <summary>
    /// Verifies a Warning-level log entry whose rendered text contains "CHECKPOINT_FAILED"
    /// AND satisfies <paramref name="textPredicate"/> was emitted at least once. Uses the
    /// same Mock&lt;ILogger&gt; <c>v.ToString()</c> verification style already established in
    /// this file (asserting on the rendered FormattedLogValues state).
    /// </summary>
    private static void VerifyCheckpointFailedWarning(
        Mock<ILogger<CheckpointManager>> mockLogger,
        Func<string, bool> textPredicate,
        string because)
    {
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString()!.Contains("CHECKPOINT_FAILED") &&
                    textPredicate(v.ToString()!)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            because);
    }

    private static IHostResolver BuildLocalHostResolver()
    {
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
        return new HostResolver(options);
    }

    private static PowerShellResult Ok(string stdout) => new()
    {
        ExitCode = 0, Stdout = stdout, Stderr = string.Empty, DurationMs = 1,
    };

    private static PowerShellResult Fail(string stderr, int exitCode = 1) => new()
    {
        ExitCode = exitCode, Stdout = string.Empty, Stderr = stderr, DurationMs = 1,
    };
}
