using System.Management.Automation.Runspaces;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>Real-runspace smoke tests need the Hyper-V module because initialization probes Get-VMHost. Without it, return early: this
/// project's xUnit v2 does not support Assert.Skip.</summary>
[Trait("Category", "Runtime")]
public class PowerShellHostTests
{
    /// <summary>Probe the module manifest to avoid heavyweight runspace initialization before deciding to skip.</summary>
    private static bool HyperVModuleAvailable()
    {
        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var manifest = Path.Combine(systemRoot,
                "System32", "WindowsPowerShell", "v1.0", "Modules", "Hyper-V", "Hyper-V.psd1");
            return File.Exists(manifest);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task EnsureInitialized_WhenHyperVAvailable_SetsEdition()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        await host.EnsureInitializedAsync(CancellationToken.None);

        new[] { PowerShellEdition.PowerShell7, PowerShellEdition.WindowsPowerShell51 }
            .Should().Contain(host.Edition,
                "the host must select either PS7 in-process or PS5.1 out-of-process");
    }

    [Fact]
    public async Task InvokeAsync_HelloWorld_ReturnsOutputAndSuccess()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var result = await host.InvokeAsync("'hello'", args: null, ct: CancellationToken.None);

        result.Success.Should().BeTrue($"InvokeAsync should succeed; stderr was: {result.Stderr}");
        result.Output.Should().NotBeNull();
        result.Output.Should().ContainSingle().Which.Should().Be("hello");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_WithArgs_BindsArgsAsSessionVariables()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var args = new Dictionary<string, object?>
        {
            ["x"] = 7,
            ["y"] = 35,
        };

        var result = await host.InvokeAsync("$x + $y", args, CancellationToken.None);

        result.Success.Should().BeTrue($"stderr: {result.Stderr}");
        result.Output.Should().ContainSingle().Which.Should().Be(42);
    }

    [Fact]
    public async Task InvokeAsync_WriteError_ReportsStderrAndFailure()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var result = await host.InvokeAsync(
            "Write-Error 'kaboom-marker'",
            args: null,
            ct: CancellationToken.None);

        result.Success.Should().BeFalse("Write-Error must surface as a failed invocation");
        result.Stderr.Should().Contain("kaboom-marker");
        result.ExitCode.Should().Be(1);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        if (!HyperVModuleAvailable()) return;

        var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        Action act = () =>
        {
            host.Dispose();
            host.Dispose();
        };

        act.Should().NotThrow("Dispose must be safe to call multiple times");
    }

    [Fact]
    public async Task InvokeAsync_AfterDispose_ThrowsObjectDisposed()
    {
        if (!HyperVModuleAvailable()) return;

        var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);
        host.Dispose();

        Func<Task> act = async () =>
            await host.InvokeAsync("'noop'", args: null, ct: CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }


    /// <summary>Distinct concurrent arguments must remain isolated: the global semaphore prevents SessionStateProxy variable
    /// clobbering.</summary>
    [Fact]
    public async Task InvokeAsync_ConcurrentCalls_EachInvocationObservesItsOwnArgs()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        const int N = 12;
        var tasks = Enumerable.Range(0, N).Select(i =>
        {
            var args = new Dictionary<string, object?>
            {
                ["payload"] = $"caller-{i}",
            };
            return host.InvokeAsync("$payload", args, CancellationToken.None);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        for (int i = 0; i < N; i++)
        {
            results[i].Success.Should().BeTrue($"call {i} stderr: {results[i].Stderr}");
            results[i].Output.Should().ContainSingle()
                .Which.Should().Be($"caller-{i}",
                    $"call {i} must observe its own bound 'payload' value — " +
                    "no cross-call clobbering of SessionStateProxy variables (Gate 6 Fix #1)");
        }
    }


    [Fact]
    public async Task InvokeWithTimeoutAsync_ScriptExceedsTimeout_ThrowsTimeoutException()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = () => host.InvokeWithTimeoutAsync(
            script: "Start-Sleep -Seconds 30; 'never-returned'",
            args: null,
            timeoutSeconds: 1,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>(
            "host must surface command timeout as TimeoutException, distinct from caller cancellation");
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "timeout must fire within a small multiple of the configured budget");
    }

    /// <summary>Caller cancellation throws OperationCanceledException, not command-timeout TimeoutException.</summary>
    [Fact]
    public async Task InvokeWithTimeoutAsync_CallerCancellation_ThrowsOperationCanceledNotTimeout()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        using var cts = new CancellationTokenSource();
        var task = host.InvokeWithTimeoutAsync(
            script: "Start-Sleep -Seconds 30; 'never-returned'",
            args: null,
            timeoutSeconds: 60,
            ct: cts.Token);

        // Give the pipeline a moment to actually start, then cancel.
        await Task.Delay(200);
        cts.Cancel();

        var thrown = await Record.ExceptionAsync(() => task);
        thrown.Should().NotBeNull();
        thrown.Should().BeAssignableTo<OperationCanceledException>(
            "caller-cancellation surfaces as OperationCanceledException (NOT TimeoutException)");
        thrown.Should().NotBeOfType<TimeoutException>();
    }


    /// <summary>Require ArgumentNullException or qualifying ParameterBindingException plus a message starting "Value cannot be null".
    /// Substring-only matching masked real PS5.1 fallback module-load failures.</summary>
    [Theory]
    [InlineData("Value cannot be null. (Parameter 'serverName')", true)]
    [InlineData("value cannot be null", true)]
    [InlineData("Argument null: VALUE CANNOT BE NULL", false)] // does not start with the phrase → reject
    [InlineData("Some other PowerShell error", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesValueCannotBeNullSignature_Classifies(string? message, bool expected)
    {
        // Satisfy the type check to isolate message-shape matching.
        var ex = message is null ? null : new ArgumentNullException("name", message);
        PowerShellHost.MatchesValueCannotBeNullSignature(ex).Should().Be(expected);
    }

    [Fact]
    public void MatchesValueCannotBeNullSignature_NullException_ReturnsFalse()
    {
        PowerShellHost.MatchesValueCannotBeNullSignature(null).Should().BeFalse();
    }

    /// <summary>A matching message on an unrelated exception must not trigger PS7 bug classification; only qualifying types in the inner
    /// chain may match.</summary>
    [Fact]
    public void MatchesValueCannotBeNullSignature_NonArgumentNull_DoesNotMatch()
    {
        var ex = new InvalidOperationException("Value cannot be null. (Parameter 'serverName')");
        PowerShellHost.MatchesValueCannotBeNullSignature(ex).Should().BeFalse(
            "the matcher must not classify generic exceptions whose message merely contains " +
            "the phrase — the type chain must include ArgumentNullException (RC-3).");
    }

    /// <summary>A CommandNotFoundException-style failure with an inner ArgumentNullException lacking the required prefix must not match:
    /// that misclassification masked live module-load failures.</summary>
    [Fact]
    public void MatchesValueCannotBeNullSignature_CommandNotFoundWithEmbeddedAne_DoesNotMatch()
    {
        var inner = new ArgumentNullException("name", "name");
        var outer = new InvalidOperationException(
            "The term 'Get-VMxyz' is not recognized as the name of a cmdlet, function, " +
            "script file, or operable program.",
            inner);

        PowerShellHost.MatchesValueCannotBeNullSignature(outer).Should().BeFalse(
            "missing-cmdlet failures with an unrelated embedded ArgumentNullException " +
            "must not be classified as the PS7 Hyper-V autoload bug (RC-3).");
    }

    /// <summary>Walk inner exceptions: a wrapped ArgumentNullException still qualifies when the top-level message has the required
    /// prefix.</summary>
    [Fact]
    public void MatchesValueCannotBeNullSignature_InnerArgumentNull_Matches()
    {
        var inner = new ArgumentNullException("name");
        var outer = new InvalidOperationException(
            "Value cannot be null. (Parameter 'name')",
            inner);

        PowerShellHost.MatchesValueCannotBeNullSignature(outer).Should().BeTrue(
            "wrapped ArgumentNullException with a message that starts with the canonical " +
            "phrase must match (RC-3 walks the inner-exception chain).");
    }


    /// <summary>Cached initialization failure must rethrow without repeating slow PS7/PS5.1 probes. This test only checks the success fast
    /// path and disposal because public-API failure is hard to induce where Hyper-V loads; integration tests without Hyper-V cover failure
    /// caching.</summary>
    [Fact]
    public async Task EnsureInitializedAsync_AfterSuccess_FastPathDoesNotRePr()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);
        await host.EnsureInitializedAsync(CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await host.EnsureInitializedAsync(CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100),
            "second call must hit the _initialized fast-path, not re-run probes (RC-4).");
    }

    /// <summary>The success-path test cannot catch repeated probing after failure. Override ProbeAndOpenRunspaceAsync to count
    /// deterministic failures and verify the same exception is cached, without real startup, Hyper-V or timing dependence.</summary>
    [Fact]
    public async Task EnsureInitializedAsync_AfterFailure_RethrowsCachedFailure_DoesNotReProbe()
    {
        using var host = new ThrowingProbeHost(NullLogger<PowerShellHost>.Instance);

        var first = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));
        first.Should().NotBeNull("the test seam forces a probe failure on first call");
        host.ProbeCallCount.Should().Be(1, "probe must run exactly once on first call");

        var second = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));
        second.Should().NotBeNull(
            "cached init failure must be rethrown, not silently swallowed (RC-4).");
        host.ProbeCallCount.Should().Be(1,
            "probe must NOT be invoked a second time — cached failure short-circuits " +
            "BEFORE acquiring the init lock or re-running the PS7/PS5.1 sequence (RC-4-fix-C).");

        // The original inner exception proves cached failure, not a fresh probe.
        second!.InnerException.Should().NotBeNull();
        second.InnerException!.Message.Should().Be(ThrowingProbeHost.ProbeErrorMessage,
            "the cached failure (InnerException) must be the original probe exception, " +
            "proving the host short-circuits without rerunning the probe.");

        await Record.ExceptionAsync(() => host.EnsureInitializedAsync(CancellationToken.None));
        host.ProbeCallCount.Should().Be(1,
            "the probe must remain at one invocation across N>=3 failed attempts.");
    }


    /// <summary>Cached failure messages must expose the underlying type/message rather than an opaque wrapper; retain the original
    /// InnerException.</summary>
    [Fact]
    public async Task EnsureInitializedAsync_AfterFailure_RethrowMessage_IncludesUnderlyingTypeAndMessage()
    {
        using var host = new ThrowingProbeHost(NullLogger<PowerShellHost>.Instance);

        var first = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));
        first.Should().NotBeNull();

        var second = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));

        second.Should().BeOfType<InvalidOperationException>();
        second!.Message.Should().Contain(typeof(InvalidOperationException).FullName!,
            "the cached-rethrow message must surface the underlying exception's TYPE " +
            "(Issue #52 Phase 2 live-debug instrumentation).");
        second.Message.Should().Contain(ThrowingProbeHost.ProbeErrorMessage,
            "the cached-rethrow message must surface the underlying exception's MESSAGE " +
            "so live debugging can localize the root cause without reading inner exceptions.");
        second.InnerException.Should().NotBeNull(
            "the original cached failure must remain as InnerException for error mapping.");
    }

    /// <summary>A fresh Windows host reports Initialized=false, no error and a non-null PsModulePath.</summary>
    [Fact]
    public void GetInitDiagnostics_FreshHost_ReportsNotInitializedNoError()
    {
        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var diag = host.GetInitDiagnostics();

        diag.Initialized.Should().BeFalse();
        diag.Edition.Should().BeNull();
        diag.LastInitError.Should().BeNull();
        diag.LastInitErrorType.Should().BeNull();
        diag.LastInitErrorTrace.Should().BeNull();
    }

    /// <summary>Cached failure diagnostics expose the full type/message/trace chain used by vm_diag.phase2Host.</summary>
    [Fact]
    public async Task GetInitDiagnostics_AfterFailure_ReportsCachedFailureDetail()
    {
        using var host = new ThrowingProbeHost(NullLogger<PowerShellHost>.Instance);

        await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));

        var diag = host.GetInitDiagnostics();

        diag.Initialized.Should().BeFalse();
        diag.Edition.Should().BeNull();
        diag.LastInitErrorType.Should().Be(typeof(InvalidOperationException).FullName);
        diag.LastInitError.Should().NotBeNull();
        diag.LastInitError!.Should().Contain(ThrowingProbeHost.ProbeErrorMessage);
        diag.LastInitErrorTrace.Should().NotBeNull();
        diag.LastInitErrorTrace!.Should().Contain(typeof(InvalidOperationException).FullName!);
        diag.LastInitErrorTrace.Should().Contain(ThrowingProbeHost.ProbeErrorMessage);
    }


    [Fact]
    public void AugmentPsModulePath_PrependsWindowsPowerShellRoots_WhenMissing()
    {
        // Reproduce the observed missing System32 and Program Files module roots.
        const string current =
            @"C:\Users\test\OneDrive\Documents\PowerShell\Modules;" +
            @"C:\Program Files\PowerShell\Modules;" +
            @"C:\some\other\path";

        string augmented = PowerShellHost.AugmentPsModulePath(current);

        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Length.Should().BeGreaterThanOrEqualTo(5);
        entries[0].Should().Be(PowerShellHost.WindowsPowerShellModuleRoots[0]);
        entries[1].Should().Be(PowerShellHost.WindowsPowerShellModuleRoots[1]);
        entries.Should().Contain(@"C:\Users\test\OneDrive\Documents\PowerShell\Modules");
        entries.Should().Contain(@"C:\Program Files\PowerShell\Modules");
        entries.Should().Contain(@"C:\some\other\path");
    }

    [Fact]
    public void AugmentPsModulePath_DeduplicatesCaseInsensitive_WhenAlreadyPresent()
    {
        // Alternate casing and duplicate roots must not create duplicate entries.
        string system32Root = PowerShellHost.WindowsPowerShellModuleRoots[0];
        string programFilesRoot = PowerShellHost.WindowsPowerShellModuleRoots[1];
        string current =
            system32Root.ToUpperInvariant() + ";" +
            programFilesRoot + ";" +
            @"C:\extra\path";

        string augmented = PowerShellHost.AugmentPsModulePath(current);

        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Count(e => string.Equals(e, system32Root, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "system32 root must be deduplicated case-insensitively");
        entries.Count(e => string.Equals(e, programFilesRoot, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "Program Files root must be deduplicated case-insensitively");
        entries.Should().Contain(@"C:\extra\path");
        // First occurrence wins: prepended canonical casing replaces the duplicate from current.
        entries[1].Should().Be(programFilesRoot);
    }

    [Fact]
    public void AugmentPsModulePath_HandlesNullAndEmpty()
    {
        string fromNull = PowerShellHost.AugmentPsModulePath(null);
        string[] nullEntries = fromNull.Split(';', StringSplitOptions.RemoveEmptyEntries);
        nullEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);

        string fromEmpty = PowerShellHost.AugmentPsModulePath(string.Empty);
        string[] emptyEntries = fromEmpty.Split(';', StringSplitOptions.RemoveEmptyEntries);
        emptyEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);

        string fromWhitespace = PowerShellHost.AugmentPsModulePath("   ;  ;");
        string[] wsEntries = fromWhitespace.Split(';', StringSplitOptions.RemoveEmptyEntries);
        wsEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);
    }

    [Fact]
    public void AugmentPsModulePath_PreservesOrderOfExistingEntries()
    {
        // PSModulePath resolves left-to-right; preserve caller search order after prepended roots.
        const string current = @"C:\a;C:\b;C:\c";
        string augmented = PowerShellHost.AugmentPsModulePath(current);
        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        int aIdx = Array.IndexOf(entries, @"C:\a");
        int bIdx = Array.IndexOf(entries, @"C:\b");
        int cIdx = Array.IndexOf(entries, @"C:\c");
        aIdx.Should().BeGreaterThan(-1);
        bIdx.Should().BeGreaterThan(aIdx);
        cIdx.Should().BeGreaterThan(bIdx);
    }

    // Strip the SDK bin-local runtimes\win\lib\net*\Modules subtree: its unsigned .types.ps1xml files fail Code Integrity/catalog policy,
    // shadow system Security cmdlets such as ConvertTo-SecureString and break New-PSSession credential binding.

    [Fact]
    public void AugmentPsModulePath_StripsBinLocalSdkModuleSubtree_Net8()
    {
        // Reproduce the bin-local module path inserted by SDK static initialization.
        const string binLocal =
            @"C:\git\example-project\src\hyperv.mcp.server\bin\release\net8.0-windows\runtimes\win\lib\net8.0\Modules";
        string current =
            @"C:\Users\test\OneDrive\Documents\PowerShell\Modules;" +
            @"C:\Program Files\PowerShell\Modules;" +
            binLocal + ";" +
            @"C:\some\other\path";

        string augmented = PowerShellHost.AugmentPsModulePath(current);

        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var sdkPattern = new System.Text.RegularExpressions.Regex(
            @"\\runtimes\\win\\lib\\net\d+(\.\d+)?(-[a-z]+)?\\Modules\\?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        entries.Should().NotContain(e => sdkPattern.IsMatch(e),
            "the bin-local PS7 SDK module subtree must be excluded — it shadows " +
            "system Microsoft.PowerShell.Security and fails AuthorizationManager " +
            "catalog signing under Code Integrity policy (RC-10.2)");
        entries.Should().NotContain(e =>
            e.IndexOf(@"\runtimes\win\lib\", StringComparison.OrdinalIgnoreCase) >= 0,
            "no SDK runtimes subtree entry should survive augmentation");
        entries.Should().Contain(@"C:\some\other\path");
        entries[0].Should().Be(PowerShellHost.WindowsPowerShellModuleRoots[0]);
    }

    [Fact]
    public void AugmentPsModulePath_StripsBinLocalSdkModuleSubtree_AcrossNetVersions()
    {
        const string current =
            @"C:\app\bin\Release\net9.0-windows\runtimes\win\lib\net9.0\Modules;" +
            @"C:\app\bin\Debug\net8.0-windows\Runtimes\Win\Lib\Net8.0\MODULES;" +
            @"C:\app\bin\Release\net10.0-windows\runtimes\win\lib\net10.0\Modules\;" +
            @"C:\legit\path";

        string augmented = PowerShellHost.AugmentPsModulePath(current);

        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Should().NotContain(e =>
            e.IndexOf(@"\runtimes\win\lib\", StringComparison.OrdinalIgnoreCase) >= 0,
            "all net*\\Modules SDK subtree variants must be stripped (case-insensitive)");
        entries.Should().Contain(@"C:\legit\path");
    }

    [Fact]
    public void AugmentPsModulePath_DoesNotStrip_NonSdkPathsContainingNetSubstring()
    {
        // A legitimate path containing net is not the SDK subtree and must survive.
        const string benign1 = @"C:\Tools\dotnet\Modules";
        const string benign2 = @"C:\Program Files\WindowsPowerShell\Modules\NetCore";
        string current = benign1 + ";" + benign2;

        string augmented = PowerShellHost.AugmentPsModulePath(current);

        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Should().Contain(benign1);
        entries.Should().Contain(benign2);
    }

    /// <summary>Override the PS7/PS5.1 probe to force and count failures without starting the SDK.</summary>
    private sealed class ThrowingProbeHost : PowerShellHost
    {
        public const string ProbeErrorMessage = "probe-failure-marker (RC-4-fix-C test seam)";

        public int ProbeCallCount { get; private set; }

        public ThrowingProbeHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Task<(Runspace Runspace, PowerShellEdition Edition)>
            ProbeAndOpenRunspaceAsync(CancellationToken ct)
        {
            ProbeCallCount++;
            throw new InvalidOperationException(ProbeErrorMessage);
        }
    }


    /// <summary>Capture outer and immediate-inner exception fields plus every level in FullExceptionToString.</summary>
    [Fact]
    public void PowerShellEditionAttempt_RecordExceptionFlattensInnerChain()
    {
        // Three exception levels distinguish immediate-inner fields from full-chain text.
        Exception inner2;
        try { throw new InvalidOperationException("inner2"); }
        catch (Exception ex) { inner2 = ex; }

        Exception inner1;
        try { throw new ApplicationException("inner1", inner2); }
        catch (Exception ex) { inner1 = ex; }

        Exception outer;
        try { throw new InvalidOperationException("outer", inner1); }
        catch (Exception ex) { outer = ex; }

        var builder = new PowerShellEditionAttemptBuilder
        {
            Attempted = true,
            FailureStage = "test.stage",
        };

        builder.RecordException(outer);
        var attempt = builder.Build();

        attempt.Attempted.Should().BeTrue();
        attempt.Succeeded.Should().BeFalse();
        attempt.FailureStage.Should().Be("test.stage");
        attempt.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        attempt.ExceptionMessage.Should().Be("outer");

        attempt.InnerExceptionType.Should().Be(typeof(ApplicationException).FullName);
        attempt.InnerExceptionMessage.Should().Be("inner1");
        attempt.InnerExceptionStackTrace.Should().NotBeNullOrEmpty(
            "the immediate inner exception's stack trace is the missing signal RC-8 captures");

        attempt.FullExceptionToString.Should().NotBeNullOrEmpty();
        attempt.FullExceptionToString!.Should().Contain("outer");
        attempt.FullExceptionToString.Should().Contain("inner1");
        attempt.FullExceptionToString.Should().Contain("inner2");
    }

    [Fact]
    public void GetInitDiagnostics_BeforeAnyAttempt_ReportsNullEditionAttempts()
    {
        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var diag = host.GetInitDiagnostics();

        diag.Ps7Attempt.Should().BeNull();
        diag.Ps51Attempt.Should().BeNull();
    }

    [Fact]
    public async Task GetInitDiagnostics_AfterFailure_ReportsPerEditionAttempts()
    {
        using var host = new EditionAttemptRecordingHost(NullLogger<PowerShellHost>.Instance);

        await Record.ExceptionAsync(() => host.EnsureInitializedAsync(CancellationToken.None));

        var diag = host.GetInitDiagnostics();

        diag.Ps7Attempt.Should().NotBeNull();
        diag.Ps7Attempt!.Attempted.Should().BeTrue();
        diag.Ps7Attempt.Succeeded.Should().BeFalse();
        diag.Ps7Attempt.FailureStage.Should().Be("iss.ImportPSModule");
        diag.Ps7Attempt.InnerExceptionStackTrace.Should().NotBeNullOrEmpty();

        diag.Ps51Attempt.Should().NotBeNull();
        diag.Ps51Attempt!.Attempted.Should().BeTrue();
        diag.Ps51Attempt.Succeeded.Should().BeFalse();
        diag.Ps51Attempt.FailureStage.Should().Be("runspace.Open");
        diag.Ps51Attempt.InnerExceptionStackTrace.Should().NotBeNullOrEmpty();
    }

    /// <summary>Populate both edition-attempt records through the protected hook, then throw to simulate both editions failing without SDK
    /// calls.</summary>
    private sealed class EditionAttemptRecordingHost : PowerShellHost
    {
        public EditionAttemptRecordingHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Task<(Runspace Runspace, PowerShellEdition Edition)>
            ProbeAndOpenRunspaceAsync(CancellationToken ct)
        {
            // Throw a two-level exception chain so the builder can capture the inner stack trace.
            Exception innerPs7;
            try { throw new ArgumentNullException("name"); }
            catch (Exception ex) { innerPs7 = ex; }

            var ps7Builder = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "iss.ImportPSModule",
            };
            ps7Builder.RecordException(
                new InvalidOperationException("PS7 simulated wrap", innerPs7));

            Exception innerPs51;
            try { throw new InvalidOperationException("PS5.1 inner: spawn failed"); }
            catch (Exception ex) { innerPs51 = ex; }

            var ps51Builder = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "runspace.Open",
            };
            ps51Builder.RecordException(
                new InvalidOperationException("PS5.1 simulated wrap", innerPs51));

            SetEditionAttemptsForTesting(ps7Builder.Build(), ps51Builder.Build());
            throw new InvalidOperationException(
                "Failed to initialize PowerShell host: neither edition could load Hyper-V.");
        }
    }

    // A successful PS5.1 open must not be discarded by an aggregate "neither worked" exception after PS7 fails.

    /// <summary>Adopt the working PS5.1 runspace after PS7 failure and report Initialized=true, Edition=WindowsPowerShell51, not aggregate
    /// failure.</summary>
    [Fact]
    public async Task ProbeAndOpenRunspaceAsync_Ps7Fails_Ps51Succeeds_AdoptsPs51Runspace()
    {
        using var host = new Ps7FailsPs51SucceedsHost(NullLogger<PowerShellHost>.Instance);

        Func<Task> act = () => host.EnsureInitializedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "RC-9: when PS7 fails but PS5.1 succeeds, the working PS5.1 runspace " +
            "must be adopted, not discarded with an aggregate failure.");

        host.Edition.Should().Be(PowerShellEdition.WindowsPowerShell51,
            "the host must be initialized using the PS5.1 fallback edition.");

        var diag = host.GetInitDiagnostics();
        diag.Initialized.Should().BeTrue();
        diag.Edition.Should().Be(PowerShellEdition.WindowsPowerShell51);
        diag.Ps7Attempt.Should().NotBeNull();
        diag.Ps7Attempt!.Succeeded.Should().BeFalse();
        diag.Ps51Attempt.Should().NotBeNull();
        diag.Ps51Attempt!.Succeeded.Should().BeTrue(
            "_ps51Attempt.Succeeded must be true on the success-via-PS5.1 path.");
        diag.LastInitError.Should().BeNull(
            "no init failure should be cached when PS5.1 succeeded.");
    }

    /// <summary>When both editions fail, preserve the "neither PowerShell 7 nor Windows PowerShell 5.1" wording for log/test consumers and
    /// populate both attempt records.</summary>
    [Fact]
    public async Task ProbeAndOpenRunspaceAsync_BothFail_ThrowsAggregateWithBothEditionAttempts()
    {
        using var host = new BothEditionsFailHost(NullLogger<PowerShellHost>.Instance);

        var thrown = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));

        thrown.Should().NotBeNull();
        thrown.Should().BeOfType<InvalidOperationException>();

        // Walk the inner-exception chain for the canonical phrase, since the outer wrapper prepends "PowerShell host previously failed to
        // initialize:".
        bool foundCanonicalPhrase = false;
        Exception? cursor = thrown;
        while (cursor is not null)
        {
            if (cursor.Message.Contains(
                "neither PowerShell 7 nor Windows PowerShell 5.1",
                StringComparison.Ordinal))
            {
                foundCanonicalPhrase = true;
                break;
            }
            cursor = cursor.InnerException;
        }
        foundCanonicalPhrase.Should().BeTrue(
            "the aggregate failure must preserve the canonical 'neither PowerShell 7 " +
            "nor Windows PowerShell 5.1' wording for log/test consumer compatibility.");

        var diag = host.GetInitDiagnostics();
        diag.Ps7Attempt.Should().NotBeNull();
        diag.Ps7Attempt!.Succeeded.Should().BeFalse();
        diag.Ps51Attempt.Should().NotBeNull();
        diag.Ps51Attempt!.Succeeded.Should().BeFalse();
        diag.LastInitError.Should().NotBeNull();
    }

    /// <summary>Preserve the PS7 "Value cannot be null" signature exception as InnerException so attempt type/stack fields remain available
    /// for triage; both were previously null.</summary>
    [Fact]
    public async Task Ps7CatchBlock_PreservesOriginalExceptionInInnerException()
    {
        using var host = new Ps7NonInteractiveBugHost(NullLogger<PowerShellHost>.Instance);

        await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));

        var diag = host.GetInitDiagnostics();
        diag.Ps7Attempt.Should().NotBeNull();
        diag.Ps7Attempt!.Succeeded.Should().BeFalse();
        diag.Ps7Attempt.InnerExceptionType.Should().NotBeNull(
            "RC-9 secondary: PS7 non-interactive bug detection must preserve " +
            "the original ANE via InvalidOperationException(message, ex). " +
            "Tester probe #5 saw InnerExceptionType=null - that is the regression.");
        diag.Ps7Attempt.InnerExceptionStackTrace.Should().NotBeNullOrEmpty(
            "the original exception's stack trace must propagate into the diagnostic record.");
    }


    /// <summary>Use a working in-process runspace as the PS5.1 stand-in after PS7 fails.</summary>
    private sealed class Ps7FailsPs51SucceedsHost : PowerShellHost
    {
        public Ps7FailsPs51SucceedsHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Runspace? TryOpenPowerShell7ForTesting(out string? failureReason)
        {
            var b = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "post-open.ProbeHyperV(Get-VMHost)",
            };
            try { throw new ArgumentNullException("name"); }
            catch (Exception ex)
            {
                b.RecordException(new InvalidOperationException(
                    "PS7 Hyper-V non-interactive bug detected: 'Value cannot be null'.",
                    ex));
            }
            SetPs7AttemptForTesting(b.Build());
            failureReason = "PS7 simulated failure (test seam)";
            return null;
        }

        protected override Runspace OpenWindowsPowerShell51ForTesting()
        {
            // The in-process PS5.1 stand-in throws from Get-VMHost so obsolete post-open probing fails regardless of installed Hyper-V.
            var rs = RunspaceFactory.CreateRunspace();
            rs.Open();
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                ps.Runspace = rs;
                ps.AddScript(
                    "function Get-VMHost { throw 'RC-9 test seam: simulated post-open probe failure' }");
                ps.Invoke();
            }

            var b = new PowerShellEditionAttemptBuilder { Attempted = true, Succeeded = true };
            SetPs51AttemptForTesting(b.Build());
            return rs;
        }
    }

    private sealed class BothEditionsFailHost : PowerShellHost
    {
        public BothEditionsFailHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Runspace? TryOpenPowerShell7ForTesting(out string? failureReason)
        {
            var b = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "runspace.Open",
            };
            try { throw new InvalidOperationException("PS7 simulated open failure"); }
            catch (Exception ex) { b.RecordException(ex); }
            SetPs7AttemptForTesting(b.Build());
            failureReason = "PS7 simulated open failure (test seam)";
            return null;
        }

        protected override Runspace OpenWindowsPowerShell51ForTesting()
        {
            var b = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "PowerShellProcessInstance.ctor",
            };
            try { throw new InvalidOperationException("PS5.1 simulated spawn failure"); }
            catch (Exception ex) { b.RecordException(ex); }
            SetPs51AttemptForTesting(b.Build());
            throw new InvalidOperationException("PS5.1 simulated spawn failure (test seam)");
        }
    }

    /// <summary>Simulate the PS7 non-interactive bug and fail PS5.1 too, caching failure so the PS7 diagnostic record can be
    /// inspected.</summary>
    private sealed class Ps7NonInteractiveBugHost : PowerShellHost
    {
        public Ps7NonInteractiveBugHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Runspace? TryOpenPowerShell7ForTesting(out string? failureReason)
        {
            var b = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "post-open.ProbeHyperV(Get-VMHost)",
            };
            try
            {
                ArgumentNullException original;
                try { throw new ArgumentNullException("name"); }
                catch (ArgumentNullException ex) { original = ex; }

                throw new InvalidOperationException(
                    "PS7 Hyper-V non-interactive bug detected: 'Value cannot be null'.",
                    original);
            }
            catch (Exception wrapped)
            {
                b.RecordException(wrapped);
            }
            SetPs7AttemptForTesting(b.Build());
            failureReason = "PS7 non-interactive bug (test seam)";
            return null;
        }

        protected override Runspace OpenWindowsPowerShell51ForTesting()
        {
            var b = new PowerShellEditionAttemptBuilder
            {
                Attempted = true,
                FailureStage = "PowerShellProcessInstance.ctor",
            };
            try { throw new InvalidOperationException("PS5.1 not available in test env"); }
            catch (Exception ex) { b.RecordException(ex); }
            SetPs51AttemptForTesting(b.Build());
            throw new InvalidOperationException("PS5.1 not available in test env");
        }
    }

    // A terminating ps.Invoke() exception bypassed the success-only error drain, leaving session failures blank. Both prior non-terminating
    // errors and the terminating exception must appear in Stderr.

    [Fact]
    public async Task InvokeAsync_NonTerminatingThenTerminating_BothMarkersAppearInStderr()
    {
        // Use a bare runspace without Hyper-V: stderr assembly is module-independent.
        using var host = new BareRunspaceHost(NullLogger<PowerShellHost>.Instance);

        // Write a non-terminating error, then use ErrorActionPreference=Stop to raise ActionPreferenceStopException past the
        // PipelineStoppedException-only catch. Drain the first error and flatten the caught exception; the framed diagnostic prefix
        // distinguishes this from the old raw ErrorRecord.ToString() output.
        const string script =
            "Write-Error 'RC103a_NONTERMINATING_MARKER'; " +
            "$ErrorActionPreference = 'Stop'; " +
            "Write-Error 'RC103a_TERMINATING_MARKER'";

        var result = await host.InvokeAsync(script, args: null, ct: CancellationToken.None);

        result.Success.Should().BeFalse(
            "a script that triggers a terminating Write-Error must report failure");

        // Both markers must survive: one from Streams.Error, one from the terminating exception.
        result.Stderr.Should().Contain("RC103a_NONTERMINATING_MARKER",
            "the non-terminating ErrorRecord MUST be drained from " +
            "ps.Streams.Error into result.Stderr even when ps.Invoke() throws " +
            "(RC-10.3a Layer 1)");
        result.Stderr.Should().Contain("RC103a_TERMINATING_MARKER",
            "the terminating exception text MUST be flattened into " +
            "result.Stderr instead of unwinding silently past the drain " +
            "(RC-10.3a Layer 1)");

        // The framed prefix detects the new diagnostic path; the old raw ErrorRecord.ToString() drain cannot satisfy it.
        result.Stderr.Should().Contain("[RC103a:Stream]",
            "the RC-10.3a Layer 1 drain MUST tag each Streams.Error record " +
            "it appends with the [RC103a:Stream] frame so they're greppable " +
            "in logs (this assertion forces the test to fail pre-fix even " +
            "when the existing partial drain happens to capture the markers)");
        result.Stderr.Should().Contain("[RC103a:Exception]",
            "the RC-10.3a Layer 1 fix MUST tag any caught CLR exception " +
            "appended to result.Stderr with the [RC103a:Exception] frame " +
            "so the source of the diagnostic is unambiguous in logs");
    }

    /// <summary>A bare runspace bypasses Hyper-V import/probing so module-independent InvokeAsync diagnostics can run without the role
    /// installed.</summary>
    private sealed class BareRunspaceHost : PowerShellHost
    {
        public BareRunspaceHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Task<(Runspace Runspace, PowerShellEdition Edition)>
            ProbeAndOpenRunspaceAsync(CancellationToken ct)
        {
            var rs = RunspaceFactory.CreateRunspace();
            rs.Open();
            return Task.FromResult((rs, PowerShellEdition.PowerShell7));
        }
    }

    // Hosted PS5.1 defaults to MTA, unlike the working stock STA console. Hyper-V GetServers/GetServer needs STA for local WMI; MTA yielded
    // a null-name exception inside New-PSSession's Get-VM call. Pin STA before Open(), while runspace properties remain settable, to
    // prevent regression.
    [Fact]
    public void OpenWindowsPowerShell51_ForcesStaApartmentForHyperVWmiProxyCompatibility()
    {
        var sourcePath = Path.Combine(TestPaths.RepositoryRoot(),
            "src", "HyperV.Mcp.Server", "Infrastructure", "PowerShellHost.cs");
        File.Exists(sourcePath).Should().BeTrue($"the source guard requires '{sourcePath}'.");

        string source = File.ReadAllText(sourcePath!);

        // Anchor on the method definition, not comment/cref mentions or unrelated runspace.Open() calls.
        int methodIdx = source.IndexOf(
            "private Runspace OpenWindowsPowerShell51(",
            StringComparison.Ordinal);
        methodIdx.Should().BeGreaterThan(-1,
            "RC-11.8: the OpenWindowsPowerShell51 method definition " +
            "(`private Runspace OpenWindowsPowerShell51(...)`) must exist in " +
            "PowerShellHost.cs.");

        // The next private member or EOF bounds this literal-scan window closely enough to isolate runspace.Open().
        int openIdx = source.IndexOf(
            "runspace.Open();", methodIdx, StringComparison.Ordinal);
        openIdx.Should().BeGreaterThan(-1,
            "RC-11.8: OpenWindowsPowerShell51 must still call runspace.Open().");

        int apartmentIdx = source.IndexOf(
            "runspace.ApartmentState = System.Threading.ApartmentState.STA",
            methodIdx,
            StringComparison.Ordinal);
        apartmentIdx.Should().BeGreaterThan(-1,
            "RC-11.8: OpenWindowsPowerShell51 MUST force " +
            "`runspace.ApartmentState = System.Threading.ApartmentState.STA` " +
            "so the hosted Windows PowerShell 5.1 runspace matches the STA " +
            "apartment of the stock PS5.1 console. Under the default MTA, " +
            "the Hyper-V WMI proxy " +
            "(Microsoft.Virtualization.Client.Management.Server.GetServer) " +
            "resolves the server name to null and throws " +
            "ArgumentNullException, which surfaces as LF-D7's " +
            "`Get-VM : Value cannot be null. Parameter name: name` inside " +
            "`New-PSSession -VMId <Guid>`. See harness " +
            "(formerly scripts/harness-rc117-newpssession-variants.ps1; removed in Phase E — recoverable from git history) for the proof.");

        apartmentIdx.Should().BeLessThan(openIdx,
            "RC-11.8: the ApartmentState assignment MUST appear BEFORE " +
            "runspace.Open() — once the runspace transitions out of " +
            "BeforeOpen, the property is read-only and assignment throws " +
            "InvalidRunspaceStateException.");

        int threadOptsIdx = source.IndexOf(
            "PSThreadOptions.UseNewThread", methodIdx, StringComparison.Ordinal);
        threadOptsIdx.Should().BeGreaterThan(-1,
            "RC-11.8: OpenWindowsPowerShell51 MUST set " +
            "`runspace.ThreadOptions = " +
            "System.Management.Automation.Runspaces.PSThreadOptions.UseNewThread` " +
            "so each pipeline invocation runs on a freshly-created STA thread " +
            "rather than reusing the calling thread's apartment (which would " +
            "negate the ApartmentState=STA setting).");

        threadOptsIdx.Should().BeLessThan(openIdx,
            "RC-11.8: the ThreadOptions assignment MUST appear BEFORE " +
            "runspace.Open() for the same BeforeOpen-state reason as " +
            "ApartmentState.");

    }

    // New-PSSession synthesizes Get-VM without ComputerName, triggering the null-server bug. Process-wide defaults complement
    // SessionStore's local injection if a caller omits it. Set defaults after Import-Module Hyper-V so the cmdlet-qualified keys bind.
    [Fact]
    public void Ps51InitializationScript_HasRc1110PSDefaultParameterValuesInjection()
    {
        string script = PowerShellHost.Ps51InitializationScript;

        script.Should().Contain(
            "$PSDefaultParameterValues['Get-VM:ComputerName']       = 'localhost'",
            "RC-11.10: Ps51InitializationScript must inject Get-VM:Computer" +
            "Name='localhost' so EVERY Get-VM invocation in the OOP runspace " +
            "(including the synthesized internal `Get-VM -Name $args` call " +
            "inside `New-PSSession -VMName`'s parameter resolver) inherits " +
            "the LF-D7 -ComputerName localhost workaround. This is the " +
            "process-wide belt-and-suspenders complement to the SessionStore " +
            "script's local injection.");

        script.Should().Contain(
            "$PSDefaultParameterValues['New-PSSession:ComputerName'] = 'localhost'",
            "RC-11.10: Ps51InitializationScript must also inject " +
            "New-PSSession:ComputerName='localhost' so the cmdlet itself " +
            "binds the same -ComputerName default.");

        script.Should().Contain(
            "if (-not $PSDefaultParameterValues) { $PSDefaultParameterValues = @{} }",
            "RC-11.10: Ps51InitializationScript must use the ADDITIVE form " +
            "(init-if-null + indexer assignment) instead of replacing the " +
            "whole hashtable, so any defaults set upstream in the runspace " +
            "are preserved.");

        // Get-VM:ComputerName binds only after Hyper-V import; setting it earlier silently does nothing for module-resolved cmdlets.
        int importIdx = script.IndexOf(
            "Import-Module -Name 'Hyper-V' -ErrorAction Stop", StringComparison.Ordinal);
        int defaultsIdx = script.IndexOf(
            "$PSDefaultParameterValues['Get-VM:ComputerName']", StringComparison.Ordinal);

        importIdx.Should().BeGreaterThan(-1,
            "Ps51InitializationScript must still import the Hyper-V module " +
            "(retained from RC-6/RC-7 — anchors the ordering invariant).");
        defaultsIdx.Should().BeGreaterThan(-1,
            "RC-11.10: the Get-VM:ComputerName default-parameter assignment " +
            "must be present in Ps51InitializationScript.");

        defaultsIdx.Should().BeGreaterThan(importIdx,
            "RC-11.10 invariant: the $PSDefaultParameterValues injection " +
            "MUST appear AFTER Import-Module -Name 'Hyper-V' so the " +
            "cmdlet-qualified default keys ('Get-VM:ComputerName', etc.) " +
            "bind against the loaded Hyper-V module's cmdlets. Setting them " +
            "before the import would silently no-op for module-resolved " +
            "cmdlets.");
    }
}
