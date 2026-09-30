using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #266: a guest that refuses the resolved credential at PowerShell Direct
/// session-open must surface a distinguishable, actionable <c>AUTH_FAILED</c> envelope.
///
/// <para>The fake host ACTUALLY fails the session open instead of capturing script text, so a
/// defective classification cannot pass by inspection of a masked mock.
/// See myplans/remoting/session-management/guest-credential-rejection-design.md — GCR-D5.</para>
/// </summary>
[Collection("EnvVarMutating")]
public class Issue266GuestCredentialRejectionTests
{
    /// <summary>The REAL mapper — never a stub. Its arm ordering is what these tests pin.</summary>
    private readonly ErrorMapper _mapper = new();

    private const string TestHostId = "localhost";
    private const string TestVmId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string TestUsername = "TestAdmin";

    /// <summary>
    /// URL-significant characters make the URL-encoded rendering distinct from the literal —
    /// an alphanumeric sentinel would make that FR-9 check vacuous.
    /// </summary>
    private const string UrlSignificantPassword = "P@ss w/rd&x=1%z+Q";

    /// <summary>Platform text shaped like a real credential rejection, with a secret rendering embedded.</summary>
    private static string CredentialRejectionStderr(string injectedSecretRendering) =>
        "New-PSSession : [3f2504e0] Connecting to remote server failed: The credential is invalid. " +
        $"Supplied secret was '{injectedSecretRendering}'. " +
        "+ CategoryInfo : OpenError: (System.Manageme...RemoteRunspace:RemoteRunspace)";

    private static string UnrelatedSessionFailureStderr() =>
        "New-PSSession : The background process reported an error: " +
        "vmhypervsocketclient PSSessionOpenFailed cannot find path 'HvSocket'.";

    private static SessionStore CreateStore(FakePowerShellHost host, ILogger<SessionStore>? logger = null) =>
        new(host, logger ?? NullLogger<SessionStore>.Instance);

    /// <summary>
    /// Drives the production session-open over the fake and returns the thrown exception.
    /// Fails if nothing is thrown — a silent success would be the defect.
    /// </summary>
    private static async Task<SessionOpenFailedException> OpenAndCaptureAsync(
        FakePowerShellHost host,
        string password,
        string vmId = TestVmId,
        string hostId = TestHostId,
        string username = TestUsername,
        ILogger<SessionStore>? logger = null)
    {
        var store = CreateStore(host, logger);
        var act = async () => await store.GetOrCreateAsync(hostId, vmId, username, password);
        var thrown = await act.Should().ThrowAsync<SessionOpenFailedException>();
        return thrown.Subject.First();
    }

    // ── AC-1 / AC-3 / AC-7: credential rejection ⇒ AUTH_FAILED, success:false ──

    [Fact]
    public async Task CredentialRejection_MapsToAuthFailed_AndFailsClosed()
    {
        const string password = "SentinelPassword123";
        var host = new FakePowerShellHost(CredentialRejectionStderr(password));

        var thrown = await OpenAndCaptureAsync(host, password);
        var envelope = _mapper.MapException(thrown);

        thrown.CredentialRejected.Should().BeTrue();
        envelope.ErrorCode.Should().Be(ErrorCodes.AuthFailed);
        envelope.Success.Should().BeFalse();
        envelope.Data.Should().BeNull("AC-7: a failed open carries no result payload.");
    }

    /// <summary>
    /// AC-2 arm-ordering guard. Both mapper arms run in the SAME test, so a future reorder or
    /// merge of the guarded and unconditional arms fails here instead of silently regressing
    /// (the #204 hazard).
    /// </summary>
    [Fact]
    public async Task BothMapperArms_ArePinnedInTheSameRun()
    {
        const string password = "SentinelPassword123";

        var rejectingHost = new FakePowerShellHost(CredentialRejectionStderr(password));
        var rejected = await OpenAndCaptureAsync(rejectingHost, password);

        var unrelatedHost = new FakePowerShellHost(UnrelatedSessionFailureStderr());
        var unrelated = await OpenAndCaptureAsync(unrelatedHost, password);

        unrelated.CredentialRejected.Should().BeFalse("GCR-A1: an unrecognized signal must fail closed.");

        _mapper.MapException(rejected).ErrorCode.Should().Be(ErrorCodes.AuthFailed);
        _mapper.MapException(unrelated).ErrorCode.Should().Be(
            ErrorCodes.SessionFailed,
            "AC-2: every other session-open failure keeps its existing code. The unrelated stderr " +
            "also contains 'cannot find path', so this pins the pair above the FILE_NOT_FOUND arm.");
    }

    // ── AC-4 / AC-5 / AC-10 / FR-4..FR-7: actionable message content ──

    /// <summary>Every FR-4..FR-7 element the operator needs, asserted individually.</summary>
    private static void AssertActionableMessageElements(string? error)
    {
        error.Should().NotBeNull();
        error.Should().Contain(TestUsername, "FR-4: the attempted username must be named.");
        error.Should().Contain("PowerShell Direct", "FR-5: the guest channel must be named.");
        error.Should().Contain("operation", "FR-5: the attempted operation must be named.");
        error.Should().Contain("wrong for this image", "FR-6: cause 1.");
        error.Should().Contain("not yet accepting logons", "FR-6: cause 2.");
        error.Should().Contain("verify the username and password", "FR-7: next step for cause 1.");
        error.Should().Contain("wait for it to", "FR-7: next step for cause 2.");
    }

    [Fact]
    public async Task Message_NamesUsernameChannelOperation_AndBothCausesWithNextSteps()
    {
        const string password = "SentinelPassword123";
        var host = new FakePowerShellHost(CredentialRejectionStderr(password));

        var envelope = _mapper.MapException(await OpenAndCaptureAsync(host, password));

        AssertActionableMessageElements(envelope.Error);
    }

    /// <summary>
    /// The production path: <c>vm_run_command</c> / <c>vm_run_script</c> / <c>vm_copy_file</c> /
    /// <c>vm_get_file</c> all reach the mapper through the channel wrapper, which historically
    /// replaced the composed message with raw platform text. Asserting the same FR-4..FR-7
    /// elements here, not only on the direct mapping seam, is what makes the guarantee real.
    /// </summary>
    [Fact]
    public async Task ChannelWrappedPath_PreservesActionableMessage_ForEveryGuestOperation()
    {
        const string password = "SentinelPassword123";

        foreach (var invokeOperation in ChannelOperations())
        {
            var host = new FakePowerShellHost(CredentialRejectionStderr(password));
            var channel = new PowerShellDirectChannel(
                host, CreateStore(host), NullLogger<PowerShellDirectChannel>.Instance);

            var act = async () => await invokeOperation(channel, password);
            var thrown = await act.Should().ThrowAsync<Exception>();

            var envelope = _mapper.MapException(thrown.Subject.First());

            envelope.Success.Should().BeFalse();
            envelope.ErrorCode.Should().Be(ErrorCodes.AuthFailed);
            AssertActionableMessageElements(envelope.Error);
            envelope.Error.Should().NotContain(password, "FR-9 holds on the channel path too.");
        }
    }

    /// <summary>The four guest entry points that funnel through the channel's retry wrapper.</summary>
    private static IEnumerable<Func<PowerShellDirectChannel, string, Task>> ChannelOperations()
    {
        yield return (channel, password) => channel.InvokeScriptAsync(
            TestHostId, TestVmId, TestUsername, password, "Get-Date");
        yield return (channel, password) => channel.InvokeScriptWithTimeoutAsync(
            TestHostId, TestVmId, TestUsername, password, "whoami", args: null, timeoutSeconds: 30);
        yield return (channel, password) => channel.CopyToSessionAsync(
            TestHostId, TestVmId, TestUsername, password, @"C:\host\file.txt", @"C:\guest\file.txt");
        yield return (channel, password) => channel.CopyFromSessionAsync(
            TestHostId, TestVmId, TestUsername, password, @"C:\guest\file.txt", @"C:\host\file.txt");
    }

    /// <summary>AC-11 / FR-8: the platform text is retained, with the password redacted out of it.</summary>
    [Fact]
    public async Task Message_RetainsPlatformText_WithPasswordRedacted()
    {
        const string password = "SentinelPassword123";
        var host = new FakePowerShellHost(CredentialRejectionStderr(password));

        var envelope = _mapper.MapException(await OpenAndCaptureAsync(host, password));

        envelope.Error.Should().Contain("Connecting to remote server failed");
        envelope.Error.Should().Contain("***REDACTED***");
        envelope.Error.Should().NotContain(password);
    }

    // ── AC-6 / FR-9: no password-derived representation in the envelope OR the diagnostics ──

    public static TheoryData<string> ProhibitedFormNames() =>
        new("literal", "reversed", "base64", "url-encoded");

    /// <summary>
    /// Injects ONE prohibited rendering per case — injecting only the literal would make the
    /// reversed and Base64 assertions trivially green — and proves that rendering absent from
    /// the envelope, every emitted log record, and the spill file on disk.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProhibitedFormNames))]
    public async Task EachProhibitedRepresentation_IsAbsentFromEnvelopeAndDiagnostics(string formName)
    {
        var password = UrlSignificantPassword;
        var injectedRendering = RenderProhibitedForm(password, formName);

        injectedRendering.Should().NotBe(
            formName == "literal" ? "\u0000never" : password,
            "the injected rendering must be genuinely distinct from the literal password, " +
            "otherwise this case proves nothing.");

        var recordingLogger = new RecordingLogger<SessionStore>();
        var spillDirectory = CreateIsolatedSpillDirectory();
        var originalProvider = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDirectory;

        try
        {
            var host = new FakePowerShellHost(CredentialRejectionStderr(injectedRendering));
            var envelope = _mapper.MapException(
                await OpenAndCaptureAsync(host, password, logger: recordingLogger));

            var envelopeText = Serialize(envelope);
            var diagnosticsText = string.Join("\n", recordingLogger.Records)
                + "\n" + ReadAllSpillContent(spillDirectory);

            recordingLogger.Records.Should().NotBeEmpty(
                "AC-6 covers diagnostic output, so there must be records to inspect.");

            envelopeText.Should().NotContain(
                injectedRendering, $"FR-9: the {formName} rendering must not reach the envelope.");
            diagnosticsText.Should().NotContain(
                injectedRendering, $"FR-9: the {formName} rendering must not reach logs or the spill file.");

            foreach (var otherForm in new[] { "literal", "reversed", "base64", "url-encoded" })
            {
                var otherRendering = RenderProhibitedForm(password, otherForm);
                envelopeText.Should().NotContain(otherRendering);
                diagnosticsText.Should().NotContain(otherRendering);
            }
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = originalProvider;
            try { Directory.Delete(spillDirectory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// AC-6 / FR-9 item 5: output must not vary with password length. Envelope AND diagnostics are
    /// compared; the spill path is masked because its filename carries a timestamp and sequence
    /// number that vary between runs for reasons unrelated to length.
    /// </summary>
    [Fact]
    public async Task EnvelopeAndDiagnostics_AreInvariantToPasswordLength()
    {
        const string shortPassword = "Xy7";
        const string longPassword = "SentinelPassword123456789ABCDEFGH";

        var (shortEnvelope, shortDiagnostics) = await CaptureWithDiagnosticsAsync(shortPassword);
        var (longEnvelope, longDiagnostics) = await CaptureWithDiagnosticsAsync(longPassword);

        Serialize(longEnvelope).Should().Be(
            Serialize(shortEnvelope),
            "FR-9 item 5: any length-varying mask would make these differ.");

        MaskSpillPaths(longDiagnostics).Should().Be(
            MaskSpillPaths(shortDiagnostics),
            "FR-9 item 5 also binds diagnostic output.");
    }

    private async Task<(McpToolResponse Envelope, string Diagnostics)> CaptureWithDiagnosticsAsync(string password)
    {
        var recordingLogger = new RecordingLogger<SessionStore>();
        var spillDirectory = CreateIsolatedSpillDirectory();
        var originalProvider = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDirectory;

        try
        {
            var host = new FakePowerShellHost(CredentialRejectionStderr(password));
            var envelope = _mapper.MapException(
                await OpenAndCaptureAsync(host, password, logger: recordingLogger));

            var diagnostics = string.Join("\n", recordingLogger.Records)
                + "\n" + ReadAllSpillContent(spillDirectory);
            return (envelope, diagnostics);
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = originalProvider;
            try { Directory.Delete(spillDirectory, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── AC-8 / FR-11 / FR-12: classification is general ──

    [Fact]
    public async Task Classification_IsIndependentOfVmImageAndHost()
    {
        const string password = "SentinelPassword123";

        var first = _mapper.MapException(await OpenAndCaptureAsync(
            new FakePowerShellHost(CredentialRejectionStderr(password)),
            password,
            vmId: "11111111-1111-1111-1111-111111111111",
            hostId: "hostAlpha"));

        var second = _mapper.MapException(await OpenAndCaptureAsync(
            new FakePowerShellHost(CredentialRejectionStderr(password)),
            password,
            vmId: "22222222-2222-2222-2222-222222222222",
            hostId: "hostBravo"));

        second.ErrorCode.Should().Be(first.ErrorCode);
        second.Success.Should().Be(first.Success);

        AssertActionableMessageElements(first.Error);
        AssertActionableMessageElements(second.Error);
    }

    // ── classifier: direct positive and negative coverage ──

    public static TheoryData<string?, bool> ClassifierCases() => new()
    {
        { null, false },
        { string.Empty, false },
        { "   \t\r\n ", false },
        { "The credential is invalid.", true },
        { "Invalid credential supplied for the guest.", true },
        { "Logon failure: unknown user name or bad password.", true },
        { "The user name or password is incorrect.", true },
        { "Access is denied due to invalid credentials.", true },
        { "Fehler bei der Anmeldung. Statuscode 0x8009030c.", true },
        { "AuthenticationException: the guest refused the logon.", true },
        { "System.Security.Authentication.AuthenticationException: the remote certificate is invalid.", false },
        { "AuthenticationException: Kerberos SPN could not be resolved.", false },
        { "The session is broken and cannot be reused.", false },
        { "cannot find path 'HvSocket' on the host.", false },
        { "credential is valid but the guest is shutting down.", false },
    };

    /// <summary>
    /// Direct classifier coverage. The negative cases matter most: the broad
    /// <c>authenticationexception</c> token must not turn a transport / certificate / Kerberos
    /// failure into a wrong-password verdict. The localized case proves the stable status code
    /// alone suffices.
    /// </summary>
    [Theory]
    [MemberData(nameof(ClassifierCases))]
    public void Classifier_MatchesOnlyCredentialRejectionSignals(string? redactedStderr, bool expected)
    {
        SessionStore.IsCredentialRejectionSignal(redactedStderr).Should().Be(expected);
    }

    // ── AC-12 / FR-10: exactly one attempt, over the full retry path ──

    /// <summary>
    /// Drives <c>PowerShellDirectChannel</c> (whose ExecuteWithRetryAsync owns the
    /// evict-and-retry) rather than <c>GetOrCreateAsync</c> alone, so a broken-session matcher
    /// that ever accepted a credential rejection shows up here as a second host invocation.
    /// See myplans/remoting/session-management/guest-credential-rejection-design.md — GCR-D6.
    /// </summary>
    [Fact]
    public async Task FullRetryPath_MakesExactlyOneSessionOpenAttempt()
    {
        const string password = "SentinelPassword123";
        var host = new FakePowerShellHost(CredentialRejectionStderr(password));
        var store = CreateStore(host);
        var channel = new PowerShellDirectChannel(
            host, store, NullLogger<PowerShellDirectChannel>.Instance);

        var act = async () => await channel.InvokeScriptAsync(
            TestHostId, TestVmId, TestUsername, password, "Get-Date");

        var thrown = await act.Should().ThrowAsync<Exception>();

        host.InvokeCount.Should().Be(
            1,
            "AC-12: a credential rejection must not be retried — the throw precedes runOperation " +
            "and shares no signature with the broken-session matchers.");

        var envelope = _mapper.MapException(thrown.Subject.First());
        envelope.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be(
            ErrorCodes.AuthFailed,
            "the channel wrapper preserves the inner classification.");
        Serialize(envelope).Should().NotContain(password, "FR-9 holds on the channel path too.");
    }

    // ── helpers ──

    private static string Serialize(McpToolResponse envelope) =>
        string.Join("\u001f", envelope.Success, envelope.ErrorCode, envelope.Error,
            envelope.State, envelope.Data, envelope.Details);

    private static string RenderProhibitedForm(string password, string formName) => formName switch
    {
        "literal" => password,
        "reversed" => new string(password.Reverse().ToArray()),
        "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
        "url-encoded" => Uri.EscapeDataString(password),
        _ => throw new ArgumentOutOfRangeException(nameof(formName), formName, "unknown FR-9 form"),
    };

    private static string CreateIsolatedSpillDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "hvmcp-issue266-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadAllSpillContent(string spillDirectory)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(spillDirectory))
        {
            try { builder.AppendLine(File.ReadAllText(file)); }
            catch { /* best-effort */ }
        }
        return builder.ToString();
    }

    /// <summary>Removes spill file paths, whose timestamp and sequence suffix vary per run.</summary>
    private static string MaskSpillPaths(string diagnostics) =>
        string.Join("\n", diagnostics
            .Split('\n')
            .Select(line => line.Contains("Spilled=", StringComparison.Ordinal)
                ? MaskFromSpillMarker(line)
                : line));

    /// <summary>
    /// Masks ONLY the spill file path token. Truncating at the marker would discard the trailing
    /// byte count and preview — the fields most likely to carry the password-length-dependent
    /// artifact this invariance assertion exists to catch.
    /// </summary>
    private static string MaskFromSpillMarker(string line) =>
        SpillPathTokenRegex.Replace(line, "Spilled=<masked>");

    private static readonly System.Text.RegularExpressions.Regex SpillPathTokenRegex = new(
        @"Spilled=.*?" + StderrSpillHelper.SpillFilePrefix + @"\S*?" + @"\.log",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>Captures formatted log records so the AC-6 diagnostics scope is actually observed.</summary>
    private sealed class RecordingLogger<TCategory> : ILogger<TCategory>
    {
        public List<string> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Records.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    /// <summary>
    /// Fake host that ACTUALLY fails the session open in the shape under test and counts
    /// invocations. It deliberately does not model script text: a fake that only records
    /// composed script would let a defective classification pass (the #278 trap).
    /// </summary>
    private sealed class FakePowerShellHost : IPowerShellHost
    {
        private readonly string _failureStderr;

        public FakePowerShellHost(string failureStderr) => _failureStderr = failureStderr;

        public int InvokeCount { get; private set; }

        public PowerShellEdition Edition => PowerShellEdition.PowerShell7;

        public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<PowerShellHostResult> InvokeAsync(
            string script, IDictionary<string, object?>? args = null, CancellationToken ct = default)
        {
            InvokeCount++;
            return Task.FromResult(new PowerShellHostResult(
                Success: false, Output: Array.Empty<object?>(), Stderr: _failureStderr, ExitCode: 1));
        }

        public Task<PowerShellHostResult> InvokeWithTimeoutAsync(
            string script, IDictionary<string, object?>? args, int? timeoutSeconds,
            CancellationToken ct = default)
            => InvokeAsync(script, args, ct);

        public Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
            => Task.FromResult("Running");

        public PowerShellHostInitDiagnostics GetInitDiagnostics()
            => new(true, PowerShellEdition.PowerShell7, null, null, null, null);
    }
}
