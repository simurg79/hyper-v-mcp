using System.Management.Automation.Runspaces;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Smoke tests for the real <see cref="PowerShellHost"/>. These exercise the in-process
/// PowerShell runspace, so they require the Hyper-V module to be installed (the host's
/// own <c>EnsureInitializedAsync</c> probes <c>Get-VMHost</c>). Tests detect that
/// constraint up front and skip themselves (return early) when it is not satisfied —
/// the test project targets xUnit v2 which does not support <c>Assert.Skip</c>.
///
/// See PSD-D1, PSD-D2 in /myplans/issue-52/phase-2/powershell-direct-channel-design.md.
/// </summary>
[Trait("Category", "Runtime")]
public class PowerShellHostTests
{
    /// <summary>
    /// Returns true when the Hyper-V PowerShell module appears available. We use the
    /// presence of the module manifest as a lightweight probe so we avoid actually
    /// initializing the heavyweight runspace before deciding to skip.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #1 — runspace-global serialization
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Concurrent <see cref="PowerShellHost.InvokeAsync"/> calls with DISTINCT bound
    /// arguments must each observe their own value — proving the runspace-global
    /// semaphore prevents cross-call clobbering of <c>SessionStateProxy</c> variables.
    /// (Gate 6 Fix #1.)
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ConcurrentCalls_EachInvocationObservesItsOwnArgs()
    {
        if (!HyperVModuleAvailable()) return;

        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        const int N = 12;
        // Script echoes the bound variable so we can pair input -> output.
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

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #2 — timeout enforcement
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <see cref="PowerShellHost.InvokeWithTimeoutAsync"/> with a tight timeout against a
    /// long-sleeping script must throw <see cref="TimeoutException"/> within the budget.
    /// (Gate 6 Fix #2.)
    /// </summary>
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

    /// <summary>
    /// When the caller cancels their token, <see cref="OperationCanceledException"/> is
    /// thrown — NOT <see cref="TimeoutException"/>. Distinguishes caller-cancel from
    /// command-timeout per Gate 6 Fix #2.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52, Gate 6 Fix #6 + Phase 2 Gate 3 RC-3 — probe classifier
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RC-3 (Issue #52 Phase 2 Gate 3): the matcher now requires BOTH an
    /// <see cref="ArgumentNullException"/> (or compatible <c>ParameterBindingException</c>)
    /// AND a message that <b>starts with</b> "Value cannot be null". The previous
    /// implementation accepted any exception whose message merely contained the substring
    /// — which falsely matched unrelated <see cref="ArgumentNullException"/>s and masked
    /// real PS5.1-fallback module-load failures (the original RC-2 root cause).
    /// </summary>
    [Theory]
    [InlineData("Value cannot be null. (Parameter 'serverName')", true)]
    [InlineData("value cannot be null", true)]
    [InlineData("Argument null: VALUE CANNOT BE NULL", false)] // does not start with the phrase → reject
    [InlineData("Some other PowerShell error", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesValueCannotBeNullSignature_Classifies(string? message, bool expected)
    {
        // Use ArgumentNullException so the type-match leg is satisfied; the test is then
        // exercising the message-shape check exclusively.
        var ex = message is null ? null : new ArgumentNullException("name", message);
        PowerShellHost.MatchesValueCannotBeNullSignature(ex).Should().Be(expected);
    }

    [Fact]
    public void MatchesValueCannotBeNullSignature_NullException_ReturnsFalse()
    {
        PowerShellHost.MatchesValueCannotBeNullSignature(null).Should().BeFalse();
    }

    /// <summary>
    /// RC-3 false-positive guard: an unrelated exception (e.g.
    /// <see cref="InvalidOperationException"/>) whose message happens to start with
    /// "Value cannot be null" must NOT be classified as the PS7 Hyper-V bug. Only
    /// <see cref="ArgumentNullException"/> / qualifying <see cref="ParameterBindingException"/>
    /// in the inner-exception chain may match.
    /// </summary>
    [Fact]
    public void MatchesValueCannotBeNullSignature_NonArgumentNull_DoesNotMatch()
    {
        var ex = new InvalidOperationException("Value cannot be null. (Parameter 'serverName')");
        PowerShellHost.MatchesValueCannotBeNullSignature(ex).Should().BeFalse(
            "the matcher must not classify generic exceptions whose message merely contains " +
            "the phrase — the type chain must include ArgumentNullException (RC-3).");
    }

    /// <summary>
    /// RC-3 false-positive guard: a <c>CommandNotFoundException</c>-style failure with
    /// an embedded <see cref="ArgumentNullException"/> whose message does not start with
    /// "Value cannot be null" must NOT be classified as the PS7 Hyper-V bug. This is the
    /// exact misclassification path that masked RC-2 in live testing.
    /// </summary>
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

    /// <summary>
    /// RC-3 positive case: an <see cref="ArgumentNullException"/> wrapped inside another
    /// exception whose top-level message starts with "Value cannot be null" must still
    /// match — we walk the inner-exception chain.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52 Phase 2 Gate 3 RC-4 — cached init failure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RC-4: when the host has a cached init failure, subsequent
    /// <see cref="PowerShellHost.EnsureInitializedAsync"/> calls must re-throw the cached
    /// failure WITHOUT re-running the (slow) PS7 + PS5.1 probe sequence.
    ///
    /// We can't easily simulate an init failure via the public API on a machine where
    /// Hyper-V actually loads. Instead, this test asserts the contract: after a successful
    /// initialization, repeated calls return immediately (the existing fast-path) and after
    /// dispose the host throws <see cref="ObjectDisposedException"/>.
    /// The pure-failure-cache path is exercised by integration tests on machines where
    /// Hyper-V is unavailable.
    /// </summary>
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

    /// <summary>
    /// RC-4-fix-C (🟡): when the host has a cached init failure, subsequent
    /// <see cref="PowerShellHost.EnsureInitializedAsync"/> calls must rethrow the SAME
    /// cached exception WITHOUT re-running the probe sequence. This is the
    /// fail-fast-on-cached-failure path that the original RC-4 fast-path test did NOT
    /// exercise. A regression that re-probed on every failed call would not have been
    /// caught by the success-path test alone.
    ///
    /// Uses a tiny test-seam subclass that overrides
    /// <see cref="PowerShellHost.ProbeAndOpenRunspaceAsync"/> to throw deterministically
    /// and count invocations — no real PowerShell startup, no Hyper-V dependency, no
    /// timing flakiness.
    /// </summary>
    [Fact]
    public async Task EnsureInitializedAsync_AfterFailure_RethrowsCachedFailure_DoesNotReProbe()
    {
        using var host = new ThrowingProbeHost(NullLogger<PowerShellHost>.Instance);

        // First call: probe runs, throws, host caches the failure.
        var first = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));
        first.Should().NotBeNull("the test seam forces a probe failure on first call");
        host.ProbeCallCount.Should().Be(1, "probe must run exactly once on first call");

        // Second call: must rethrow WITHOUT re-probing.
        var second = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));
        second.Should().NotBeNull(
            "cached init failure must be rethrown, not silently swallowed (RC-4).");
        host.ProbeCallCount.Should().Be(1,
            "probe must NOT be invoked a second time — cached failure short-circuits " +
            "BEFORE acquiring the init lock or re-running the PS7/PS5.1 sequence (RC-4-fix-C).");

        // The thrown failure must reference the original probe exception (chained as
        // InnerException by EnsureInitializedAsync) — proving it's the cached one and
        // not a fresh exception from a re-probe.
        second!.InnerException.Should().NotBeNull();
        second.InnerException!.Message.Should().Be(ThrowingProbeHost.ProbeErrorMessage,
            "the cached failure (InnerException) must be the original probe exception, " +
            "proving the host short-circuits without rerunning the probe.");

        // Third call: still exactly one probe.
        await Record.ExceptionAsync(() => host.EnsureInitializedAsync(CancellationToken.None));
        host.ProbeCallCount.Should().Be(1,
            "the probe must remain at one invocation across N>=3 failed attempts.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #52 Phase 2 live-debug instrumentation — diagnostics surface
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Phase 2 diag: when init fails, the cached-rethrow's <c>Message</c> must include
    /// the underlying exception's TYPE and MESSAGE — not the previous opaque string.
    /// The original exception remains as <c>InnerException</c>.
    /// </summary>
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

    /// <summary>
    /// Phase 2 diag: <see cref="IPowerShellHost.GetInitDiagnostics"/> on a fresh host
    /// (never initialized) reports <c>Initialized=false</c>, no error, and a non-null
    /// <c>PsModulePath</c> (the dotnet host process always has one on Windows).
    /// </summary>
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

    /// <summary>
    /// Phase 2 diag: after a cached init failure,
    /// <see cref="IPowerShellHost.GetInitDiagnostics"/> reports the full failure chain
    /// (type, message, trace) — the data <c>vm_diag.phase2Host</c> surfaces.
    /// </summary>
    [Fact]
    public async Task GetInitDiagnostics_AfterFailure_ReportsCachedFailureDetail()
    {
        using var host = new ThrowingProbeHost(NullLogger<PowerShellHost>.Instance);

        // Drive a failed init so the failure cache is populated.
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

    // ---------------------------------------------------------------------
    // RC-6 (Issue #52 Phase 2 Gate 3 Loopback #3) — AugmentPsModulePath helper.
    // ---------------------------------------------------------------------

    [Fact]
    public void AugmentPsModulePath_PrependsWindowsPowerShellRoots_WhenMissing()
    {
        // Arrange — a PSModulePath that omits the System32 + Program Files roots
        // (this is the exact failure mode observed in vm_diag.phase2Host smoke v8).
        const string current =
            @"C:\Users\test\OneDrive\Documents\PowerShell\Modules;" +
            @"C:\Program Files\PowerShell\Modules;" +
            @"C:\some\other\path";

        // Act
        string augmented = PowerShellHost.AugmentPsModulePath(current);

        // Assert — both Windows PowerShell roots are prepended (in declared order)
        // and the original entries are preserved after them.
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
        // Arrange — supply the System32 root with an alternate casing PLUS a
        // duplicate of the Program Files root in the original casing. The helper
        // must not double-list either.
        string system32Root = PowerShellHost.WindowsPowerShellModuleRoots[0];
        string programFilesRoot = PowerShellHost.WindowsPowerShellModuleRoots[1];
        string current =
            system32Root.ToUpperInvariant() + ";" +
            programFilesRoot + ";" +
            @"C:\extra\path";

        // Act
        string augmented = PowerShellHost.AugmentPsModulePath(current);

        // Assert — each unique (case-insensitive) entry appears exactly once.
        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Count(e => string.Equals(e, system32Root, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "system32 root must be deduplicated case-insensitively");
        entries.Count(e => string.Equals(e, programFilesRoot, StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "Program Files root must be deduplicated case-insensitively");
        entries.Should().Contain(@"C:\extra\path");
        // The first occurrence wins — since the helper prepends roots first using
        // their canonical casing, the Program-Files duplicate from `current` is
        // dropped, leaving the canonical-cased root in position [1].
        entries[1].Should().Be(programFilesRoot);
    }

    [Fact]
    public void AugmentPsModulePath_HandlesNullAndEmpty()
    {
        // Null input — must still produce both Windows PowerShell roots.
        string fromNull = PowerShellHost.AugmentPsModulePath(null);
        string[] nullEntries = fromNull.Split(';', StringSplitOptions.RemoveEmptyEntries);
        nullEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);

        // Empty input — same.
        string fromEmpty = PowerShellHost.AugmentPsModulePath(string.Empty);
        string[] emptyEntries = fromEmpty.Split(';', StringSplitOptions.RemoveEmptyEntries);
        emptyEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);

        // Whitespace-only entries are dropped from the existing portion but the
        // roots are still emitted.
        string fromWhitespace = PowerShellHost.AugmentPsModulePath("   ;  ;");
        string[] wsEntries = fromWhitespace.Split(';', StringSplitOptions.RemoveEmptyEntries);
        wsEntries.Should().Equal(PowerShellHost.WindowsPowerShellModuleRoots);
    }

    [Fact]
    public void AugmentPsModulePath_PreservesOrderOfExistingEntries()
    {
        // Defensive — order matters because PSModulePath resolution is left-to-right
        // and we must not silently reshuffle a caller-supplied search order.
        const string current = @"C:\a;C:\b;C:\c";
        string augmented = PowerShellHost.AugmentPsModulePath(current);
        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        // After the prepended roots, the original entries must appear in the same order.
        int aIdx = Array.IndexOf(entries, @"C:\a");
        int bIdx = Array.IndexOf(entries, @"C:\b");
        int cIdx = Array.IndexOf(entries, @"C:\c");
        aIdx.Should().BeGreaterThan(-1);
        bIdx.Should().BeGreaterThan(aIdx);
        cIdx.Should().BeGreaterThan(bIdx);
    }

    // ---------------------------------------------------------------------
    // RC-10.2 (Issue #52 Phase 2) — AugmentPsModulePath must STRIP the
    // bin-local PS7 SDK module subtree (runtimes\win\lib\net*\Modules).
    // That subtree contains a full copy of PS7's intrinsic modules
    // (Microsoft.PowerShell.Security, etc.) shipped by Microsoft.PowerShell.SDK.
    // PS7 finds e.g. ConvertTo-SecureString there first, but
    // AuthorizationManager.PassesPolicyCheck() rejects loading the unsigned
    // bin-path .types.ps1xml under Code Integrity / catalog signing policy,
    // shadowing the system Microsoft.PowerShell.Security and breaking
    // New-PSSession credential parameter binding.
    // ---------------------------------------------------------------------

    [Fact]
    public void AugmentPsModulePath_StripsBinLocalSdkModuleSubtree_Net8()
    {
        // Arrange — the exact poison entry observed in the harness Variant A
        // failure (RC-10.1 re-run): a bin-local SDK runtimes\win\lib\net8.0\Modules
        // path inserted by Microsoft.PowerShell.SDK static init.
        const string binLocal =
            @"c:\git\hyper-v-mcp-server\src\hyperv.mcp.server\bin\release\net8.0-windows\runtimes\win\lib\net8.0\Modules";
        string current =
            @"C:\Users\test\OneDrive\Documents\PowerShell\Modules;" +
            @"C:\Program Files\PowerShell\Modules;" +
            binLocal + ";" +
            @"C:\some\other\path";

        // Act
        string augmented = PowerShellHost.AugmentPsModulePath(current);

        // Assert — no entry in the result matches the SDK-shipped subtree shape.
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
        // Sanity — legitimate entries remain.
        entries.Should().Contain(@"C:\some\other\path");
        entries[0].Should().Be(PowerShellHost.WindowsPowerShellModuleRoots[0]);
    }

    [Fact]
    public void AugmentPsModulePath_StripsBinLocalSdkModuleSubtree_AcrossNetVersions()
    {
        // Arrange — multiple net*\Modules variants (case + version permutations).
        const string current =
            @"C:\app\bin\Release\net9.0-windows\runtimes\win\lib\net9.0\Modules;" +
            @"C:\app\bin\Debug\net8.0-windows\Runtimes\Win\Lib\Net8.0\MODULES;" +
            @"C:\app\bin\Release\net10.0-windows\runtimes\win\lib\net10.0\Modules\;" +
            @"C:\legit\path";

        // Act
        string augmented = PowerShellHost.AugmentPsModulePath(current);

        // Assert
        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Should().NotContain(e =>
            e.IndexOf(@"\runtimes\win\lib\", StringComparison.OrdinalIgnoreCase) >= 0,
            "all net*\\Modules SDK subtree variants must be stripped (case-insensitive)");
        entries.Should().Contain(@"C:\legit\path");
    }

    [Fact]
    public void AugmentPsModulePath_DoesNotStrip_NonSdkPathsContainingNetSubstring()
    {
        // Arrange — defensive: a legitimate path that happens to contain "net" in a
        // segment must NOT be stripped. Only the specific SDK shape matches.
        const string benign1 = @"C:\Tools\dotnet\Modules";
        const string benign2 = @"C:\Program Files\WindowsPowerShell\Modules\NetCore";
        string current = benign1 + ";" + benign2;

        // Act
        string augmented = PowerShellHost.AugmentPsModulePath(current);

        // Assert — both benign paths survive.
        string[] entries = augmented.Split(';', StringSplitOptions.RemoveEmptyEntries);
        entries.Should().Contain(benign1);
        entries.Should().Contain(benign2);
    }

    /// <summary>
    /// Test seam — production constructor uses the default PS7 → PS5.1 probe.
    /// This subclass overrides <see cref="PowerShellHost.ProbeAndOpenRunspaceAsync"/> so
    /// tests can deterministically force a probe failure and count how many times the
    /// probe is invoked, without spinning up the real PowerShell SDK.
    /// </summary>
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

    // ---------------------------------------------------------------------
    // RC-8 (Issue #52 Phase 2 Gate 3 Loopback #4) — per-edition attempt
    // diagnostics + builder.
    // ---------------------------------------------------------------------

    /// <summary>
    /// RC-8: <see cref="PowerShellEditionAttemptBuilder.RecordException"/> must
    /// flatten the outer + immediate-inner exception payload AND populate
    /// <c>FullExceptionToString</c> with every level via <see cref="Exception.ToString"/>.
    /// </summary>
    [Fact]
    public void PowerShellEditionAttempt_RecordExceptionFlattensInnerChain()
    {
        // Arrange — a 3-level exception chain so we can confirm the immediate
        // inner is captured AND that FullExceptionToString contains all three.
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

        // Act
        builder.RecordException(outer);
        var attempt = builder.Build();

        // Assert — outer
        attempt.Attempted.Should().BeTrue();
        attempt.Succeeded.Should().BeFalse();
        attempt.FailureStage.Should().Be("test.stage");
        attempt.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        attempt.ExceptionMessage.Should().Be("outer");

        // Assert — immediate inner (inner1, NOT inner2)
        attempt.InnerExceptionType.Should().Be(typeof(ApplicationException).FullName);
        attempt.InnerExceptionMessage.Should().Be("inner1");
        attempt.InnerExceptionStackTrace.Should().NotBeNullOrEmpty(
            "the immediate inner exception's stack trace is the missing signal RC-8 captures");

        // Assert — FullExceptionToString must contain ALL three levels' messages.
        attempt.FullExceptionToString.Should().NotBeNullOrEmpty();
        attempt.FullExceptionToString!.Should().Contain("outer");
        attempt.FullExceptionToString.Should().Contain("inner1");
        attempt.FullExceptionToString.Should().Contain("inner2");
    }

    /// <summary>
    /// RC-8: a fresh host (no probe yet) must report both edition-attempt fields
    /// as <c>null</c> (i.e. neither path has been entered).
    /// </summary>
    [Fact]
    public void GetInitDiagnostics_BeforeAnyAttempt_ReportsNullEditionAttempts()
    {
        using var host = new PowerShellHost(NullLogger<PowerShellHost>.Instance);

        var diag = host.GetInitDiagnostics();

        diag.Ps7Attempt.Should().BeNull();
        diag.Ps51Attempt.Should().BeNull();
    }

    /// <summary>
    /// RC-8: after a probe failure that records both PS7 and PS5.1 attempts via
    /// the test seam, <see cref="IPowerShellHost.GetInitDiagnostics"/> must
    /// surface both per-edition records with their <c>FailureStage</c> and
    /// <c>InnerExceptionStackTrace</c> populated.
    /// </summary>
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

    /// <summary>
    /// Test seam — overrides the probe to populate both per-edition attempt
    /// records (via the protected <c>SetEditionAttemptsForTesting</c> hook) and
    /// then throw, simulating the production "both editions failed" path
    /// without requiring real PowerShell SDK calls.
    /// </summary>
    private sealed class EditionAttemptRecordingHost : PowerShellHost
    {
        public EditionAttemptRecordingHost(ILogger<PowerShellHost> logger) : base(logger) { }

        protected override Task<(Runspace Runspace, PowerShellEdition Edition)>
            ProbeAndOpenRunspaceAsync(CancellationToken ct)
        {
            // Build a realistic 2-level exception chain so InnerExceptionStackTrace
            // can be populated by the builder.
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

    // ===================================================================
    // Issue #52 Phase 2 Gate 3 Loopback #5 - RC-9: adopt PS5.1 when PS7 fails.
    // Regression tests for the bug where ps51Attempt.Succeeded == true was
    // discarded by an aggregate "neither worked" InvalidOperationException.
    // ===================================================================

    /// <summary>
    /// RC-9 PRIMARY FIX (Tester smoke probe #5 regression): when the PS7 probe
    /// fails BUT the PS5.1 open succeeds, the host MUST adopt the PS5.1 runspace
    /// and report Initialized=true, Edition=WindowsPowerShell51 - instead of
    /// throwing the aggregate "neither PS7 nor PS5.1 could load" exception that
    /// discards the working PS5.1 runspace.
    /// </summary>
    [Fact]
    public async Task ProbeAndOpenRunspaceAsync_Ps7Fails_Ps51Succeeds_AdoptsPs51Runspace()
    {
        using var host = new Ps7FailsPs51SucceedsHost(NullLogger<PowerShellHost>.Instance);

        Func<Task> act = () => host.EnsureInitializedAsync(CancellationToken.None);

        // BEFORE RC-9: this assertion fails - host throws the aggregate exception.
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

    /// <summary>
    /// RC-9 companion: when BOTH PS7 and PS5.1 fail, the host must throw an
    /// InvalidOperationException whose message preserves the existing
    /// "neither PowerShell 7 nor Windows PowerShell 5.1" wording for log/test
    /// consumer compatibility, and both per-edition attempt records must be
    /// populated.
    /// </summary>
    [Fact]
    public async Task ProbeAndOpenRunspaceAsync_BothFail_ThrowsAggregateWithBothEditionAttempts()
    {
        using var host = new BothEditionsFailHost(NullLogger<PowerShellHost>.Instance);

        var thrown = await Record.ExceptionAsync(() =>
            host.EnsureInitializedAsync(CancellationToken.None));

        thrown.Should().NotBeNull();
        thrown.Should().BeOfType<InvalidOperationException>();

        // Walk the inner-exception chain for the canonical phrase, since the
        // outer wrapper prepends "PowerShell host previously failed to initialize:".
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

    /// <summary>
    /// RC-9 SECONDARY FIX: when the PS7 probe detects the "Value cannot be null"
    /// non-interactive bug, the resulting wrapper exception MUST preserve the
    /// original signature exception via Exception.InnerException so
    /// Ps7Attempt.InnerExceptionType and Ps7Attempt.InnerExceptionStackTrace
    /// are populated for triage. Tester probe #5 saw both as null - that is
    /// the regression.
    /// </summary>
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

    // ----- RC-9 test seams -----

    /// <summary>
    /// Test seam: PS7 returns null (failure), PS5.1 returns a working in-process
    /// runspace. Drives the RC-9 primary regression test.
    /// </summary>
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
            // In-process default runspace stand-in for the real OOP PS5.1 child.
            // Inject a Get-VMHost stub that THROWS, so any pre-fix orchestration
            // logic which gates adoption on a post-open ProbeHyperV(Get-VMHost)
            // call deterministically fails — proving the regression is caught
            // regardless of whether the dev box has Hyper-V installed.
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

    /// <summary>
    /// Test seam: both PS7 and PS5.1 fail. Drives the aggregate-failure regression test.
    /// </summary>
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

    /// <summary>
    /// Test seam: simulates the PS7 non-interactive "Value cannot be null" bug
    /// path so the catch/diagnostic capture is exercised end-to-end. PS5.1 also
    /// fails so a failure is cached and the captured PS7 record can be
    /// inspected.
    /// </summary>
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

    // ═══════════════════════════════════════════════════════════════════════
    // RC-10.3a (Issue #52 Phase 2) — diagnostic surfacing of failure detail
    //
    // Problem: when a script writes a non-terminating ErrorRecord to
    // ps.Streams.Error AND then raises a terminating exception (e.g. via
    // `throw` or `-ErrorAction Stop`), the C# `ps.Invoke()` call throws
    // BEFORE the existing post-Invoke drain runs. Net effect in production
    // (RC-10.2 → RC-10.3): result.Stderr is empty, the SessionStore wraps
    // it as "Failed to create PSSession '...': " with nothing after the
    // colon, and the real failure is invisible.
    //
    // This test exercises both paths simultaneously and asserts BOTH the
    // non-terminating marker AND the terminating marker appear in
    // result.Stderr. PRE-FIX the assertion fails because the drain only
    // runs on the success path.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvokeAsync_NonTerminatingThenTerminating_BothMarkersAppearInStderr()
    {
        // Use a bare-runspace test seam so the test runs on machines without
        // Hyper-V installed (the production probe gates on Get-VMHost). The
        // RC-10.3a fix lives entirely in InvokeAsync's post-execution stderr
        // assembly, so the runspace's module set is irrelevant.
        using var host = new BareRunspaceHost(NullLogger<PowerShellHost>.Instance);

        // Repro of the RC-10.3 production gap: a non-terminating ErrorRecord
        // lands in ps.Streams.Error, then a CLR-level terminating exception
        // ESCAPES ps.Invoke() (via $ErrorActionPreference = 'Stop' which makes
        // Write-Error promote to ActionPreferenceStopException — that's the
        // path that bypasses the post-Invoke drain entirely because the C#
        // catch only handles PipelineStoppedException).
        //
        // The non-terminating marker must be drained from ps.Streams.Error
        // (it landed there before $ErrorActionPreference was switched). The
        // terminating marker must be flattened from the CLR exception caught
        // by InvokeAsync. Pre-fix, the load-bearing assertion is the
        // [RC103a:...] prefix check — only the post-fix code path emits
        // that frame when flattening drained errors / caught exceptions.
        const string script =
            "Write-Error 'RC103a_NONTERMINATING_MARKER'; " +
            "$ErrorActionPreference = 'Stop'; " +
            "Write-Error 'RC103a_TERMINATING_MARKER'";

        var result = await host.InvokeAsync(script, args: null, ct: CancellationToken.None);

        result.Success.Should().BeFalse(
            "a script that triggers a terminating Write-Error must report failure");

        // Both markers must be visible — the non-terminating one came from
        // ps.Streams.Error (drain) and the terminating one came from the
        // ActionPreferenceStopException raised by ps.Invoke() (flattened).
        result.Stderr.Should().Contain("RC103a_NONTERMINATING_MARKER",
            "the non-terminating ErrorRecord MUST be drained from " +
            "ps.Streams.Error into result.Stderr even when ps.Invoke() throws " +
            "(RC-10.3a Layer 1)");
        result.Stderr.Should().Contain("RC103a_TERMINATING_MARKER",
            "the terminating exception text MUST be flattened into " +
            "result.Stderr instead of unwinding silently past the drain " +
            "(RC-10.3a Layer 1)");

        // Load-bearing pre-fix check: the unique tag is ONLY emitted by the
        // new InvokeAsync diagnostic path. Pre-fix, the existing drain
        // appends raw ErrorRecord.ToString() without any framing — so this
        // fails until Layer 1 lands.
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

    /// <summary>
    /// RC-10.3a test seam: opens a bare default runspace (no Hyper-V module
    /// import, no Get-VMHost probe) so InvokeAsync can be exercised on
    /// machines without the Hyper-V role installed. The production probe
    /// requires Hyper-V availability — we deliberately bypass it here
    /// because the RC-10.3a fix is module-agnostic.
    /// </summary>
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

    // ─── RC-11.8 — force STA apartment on hosted Windows PowerShell 5.1 runspace ─
    //
    // Build #18 (RC-11.5b-diag) hosted-runspace differentiator snapshot proved
    // the LF-D7 `Get-VM : Value cannot be null. Parameter name: name` failure
    // inside `New-PSSession -VMId <Guid>`'s internal `Get-VM -Id <guid>` call
    // has EXACTLY ONE discriminator versus a working stock PS5.1 console:
    //
    //     [RC11.5b:T+280ms] DIAG-APARTMENT MTA   ← MCP-hosted runspace (FAILS)
    //                       vs. STA in stock PS5.1 console (WORKS in harness)
    //
    // The Hyper-V `ParameterResolvers.GetServers` →
    // `Server.GetServer(name, credential)` proxy requires STA to talk to the
    // local WMI infrastructure. Under MTA, name resolves to null →
    // ArgumentNullException → VirtualizationException → `Get-VM` fails →
    // `New-PSSession -VMId` fails. Stock PS5.1 console is STA by default;
    // hosted runspaces created by RunspaceFactory default to MTA.
    //
    // RC-11.8's fix forces ApartmentState=STA on the PS5.1 runspace BEFORE
    // `runspace.Open()` (the Runspace properties are only settable in the
    // BeforeOpen state). This literal-scan test pins the assignment in
    // `OpenWindowsPowerShell51` so a future refactor cannot silently revert
    // the apartment back to MTA.
    [Fact]
    public void OpenWindowsPowerShell51_ForcesStaApartmentForHyperVWmiProxyCompatibility()
    {
        // Resolve src/HyperV.Mcp.Server/Infrastructure/PowerShellHost.cs by
        // walking up from the test assembly's BaseDirectory until we find the
        // repo root (the directory that contains the `src` folder).
        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? cursor = new DirectoryInfo(baseDir);
        string? sourcePath = null;
        while (cursor is not null)
        {
            var candidate = Path.Combine(
                cursor.FullName,
                "src",
                "HyperV.Mcp.Server",
                "Infrastructure",
                "PowerShellHost.cs");
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                break;
            }
            cursor = cursor.Parent;
        }

        sourcePath.Should().NotBeNull(
            "RC-11.8: literal-scan test must be able to locate " +
            "src/HyperV.Mcp.Server/Infrastructure/PowerShellHost.cs by walking " +
            "up from the test assembly's BaseDirectory.");

        string source = File.ReadAllText(sourcePath!);

        // Anchor the search to the OpenWindowsPowerShell51 METHOD DEFINITION
        // (not earlier doc-comment / cref mentions of the same identifier) so
        // the subsequent IndexOf calls scan only the method body, not the
        // unrelated `runspace.Open();` call sites in TryOpenPowerShell7 etc.
        int methodIdx = source.IndexOf(
            "private Runspace OpenWindowsPowerShell51(",
            StringComparison.Ordinal);
        methodIdx.Should().BeGreaterThan(-1,
            "RC-11.8: the OpenWindowsPowerShell51 method definition " +
            "(`private Runspace OpenWindowsPowerShell51(...)`) must exist in " +
            "PowerShellHost.cs.");

        // Find the method body's terminating `}` by scanning forward from the
        // method signature for the first `private` (or end of file) — close
        // enough for the literal-scan style. We just need a window that
        // unambiguously contains the runspace.Open() call.
        int openIdx = source.IndexOf(
            "runspace.Open();", methodIdx, StringComparison.Ordinal);
        openIdx.Should().BeGreaterThan(-1,
            "RC-11.8: OpenWindowsPowerShell51 must still call runspace.Open().");

        // ── 1. ApartmentState = STA assignment must be present, BEFORE Open(). ──
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

        // ── 2. ThreadOptions = UseNewThread assignment must be present. ──
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

    // ─── RC-11.10 — $PSDefaultParameterValues injection (LF-D7 cure) ──────
    //
    // Smoke probe #7 (full 6KB stderr, 2026-04-30) proved the failing
    // `Get-VM -Name $args` is internally synthesized by `New-PSSession
    // -VMName/-VMId`'s parameter resolver and runs WITHOUT -ComputerName,
    // hitting the LF-D7 Server.GetServer(name=null) bug. The SessionStore
    // script's local injection (also tagged RC-11.10) cures the per-session
    // create path; appending the SAME injection to Ps51InitializationScript
    // makes -ComputerName localhost a process-wide invariant for ALL
    // subsequent Get-VM/New-PSSession invocations in the OOP runspace —
    // belt-and-suspenders coverage in case any callsite forgets the
    // SessionStore script's local injection.
    //
    // The init script runs IMMEDIATELY after Import-Module Hyper-V, so the
    // defaults must appear AFTER the import in source order (otherwise the
    // module isn't loaded yet and the cmdlet-qualified key 'Get-VM:...'
    // won't bind).
    [Fact]
    public void Ps51InitializationScript_HasRc1110PSDefaultParameterValuesInjection()
    {
        string script = PowerShellHost.Ps51InitializationScript;

        // ── 1. Both default-parameter-value entries must be present. ──
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

        // ── 2. Additive form (preserves any defaults set upstream). ──
        script.Should().Contain(
            "if (-not $PSDefaultParameterValues) { $PSDefaultParameterValues = @{} }",
            "RC-11.10: Ps51InitializationScript must use the ADDITIVE form " +
            "(init-if-null + indexer assignment) instead of replacing the " +
            "whole hashtable, so any defaults set upstream in the runspace " +
            "are preserved.");

        // ── 3. Ordering: defaults must be AFTER Import-Module Hyper-V. ──
        // The cmdlet-qualified default key 'Get-VM:ComputerName' only binds
        // once the Hyper-V module's Get-VM cmdlet is resolvable. Setting it
        // before Import-Module would silently no-op for module-resolved
        // cmdlets.
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
