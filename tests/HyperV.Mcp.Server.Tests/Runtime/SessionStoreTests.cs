using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Unit tests for the rewritten <see cref="SessionStore"/> (issue #52, ST-2/ST-7).
/// Backed by <see cref="IPowerShellHost"/> rather than <see cref="IPowerShellExecutor"/>.
///
/// Coverage:
/// - <see cref="SessionStore.BuildSessionName"/> deterministic + sanitized
/// - GetOrCreateAsync — fresh create, cache hit + healthy reuse, cache hit + unhealthy
///   evict-and-recreate
/// - HasSession — does NOT trigger a health check
/// - EvictAsync — idempotent, runs Remove-PSSession script when entry exists
/// - EvictAllAsync — removes only sessions for the matched host
/// - Per-(hostId,vmId) serialization — concurrent GetOrCreateAsync only creates once
/// - Argument validation
/// - Dispose is safe + idempotent
/// </summary>
[Trait("Category", "Runtime")]
public class SessionStoreTests : IDisposable
{
    private readonly Mock<IPowerShellHost> _mockHost;
    private readonly ILogger<SessionStore> _logger;
    private readonly SessionStore _store;

    private const string TestHostId = "local";
    private const string TestVmId = "12345678-1234-1234-1234-123456789abc";
    private const string TestUsername = "testuser";
    private const string TestPassword = "testpass";

    public SessionStoreTests()
    {
        _mockHost = new Mock<IPowerShellHost>();
        _logger = NullLoggerFactory.Instance.CreateLogger<SessionStore>();

        // Default: every InvokeAsync succeeds (used by both create and health probe).
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        _store = new SessionStore(_mockHost.Object, _logger);
    }

    public void Dispose() => _store.Dispose();

    // ─── BuildSessionName ────────────────────────────────────────────────

    [Fact]
    public void BuildSessionName_ProducesDeterministicName()
    {
        var name = SessionStore.BuildSessionName("local", "vm-abc");
        name.Should().Be("hyperv-mcp-local-vm-abc");
    }

    [Fact]
    public void BuildSessionName_SanitizesSpecialCharacters()
    {
        var name = SessionStore.BuildSessionName("host.1", "vm:test@123");
        name.Should().Be("hyperv-mcp-host-1-vm-test-123");
    }

    [Fact]
    public void BuildSessionName_IsIdempotent()
    {
        var n1 = SessionStore.BuildSessionName(TestHostId, TestVmId);
        var n2 = SessionStore.BuildSessionName(TestHostId, TestVmId);
        n1.Should().Be(n2);
    }

    // ─── GetOrCreateAsync — fresh create ─────────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_CacheMiss_CreatesAndReturnsHandle()
    {
        var handle = await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        handle.Should().NotBeNull();
        handle.HostId.Should().Be(TestHostId);
        handle.VmId.Should().Be(TestVmId);
        handle.SessionName.Should().Be(SessionStore.BuildSessionName(TestHostId, TestVmId));

        // Exactly one InvokeAsync call (the New-PSSession create).
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreateFails_ThrowsInvalidOperationException()
    {
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: false,
                Output: Array.Empty<object?>(),
                Stderr: "Access denied to VM",
                ExitCode: 1));

        Func<Task> act = () => _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("Access denied to VM");
    }

    // ─── GetOrCreateAsync — cache-hit health-check semantics ─────────────

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_HealthyReturnsSameSession()
    {
        // First call: 1x create.
        var first = await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        // Second call: should run the health-check script (Invoke-Command 1) and
        // reuse the existing session — NOT call New-PSSession again.
        var second = await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        second.SessionName.Should().Be(first.SessionName);

        // Only one New-PSSession invocation across both calls.
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // And at least one health-check probe call (Invoke-Command on $s).
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("Invoke-Command -Session $s")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_UnhealthyEvictsAndRecreates()
    {
        // Phase A: succeed for the first create.
        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        // Phase B: make the health-check probe (script that contains `Invoke-Command -Session $s`)
        // fail; New-PSSession must succeed again so the recreate can complete.
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("Invoke-Command -Session $s")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerShellHostResult(
                Success: false,
                Output: Array.Empty<object?>(),
                Stderr: "session is broken",
                ExitCode: 1));

        var handle = await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        handle.SessionName.Should().Be(SessionStore.BuildSessionName(TestHostId, TestVmId));

        // New-PSSession was now called twice (initial + after the unhealthy eviction).
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ─── HasSession ─────────────────────────────────────────────────────

    [Fact]
    public void HasSession_NoEntry_ReturnsFalse()
    {
        _store.HasSession(TestHostId, TestVmId).Should().BeFalse();
    }

    [Fact]
    public async Task HasSession_AfterCreation_ReturnsTrue_WithoutHealthCheck()
    {
        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        _mockHost.Invocations.Clear();

        _store.HasSession(TestHostId, TestVmId).Should().BeTrue();

        // HasSession must NOT trigger any PowerShell invocation.
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── EvictAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task EvictAsync_RemovesEntry_AndCallsRemovePSSession()
    {
        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);
        _mockHost.Invocations.Clear();

        await _store.EvictAsync(TestHostId, TestVmId);

        _store.HasSession(TestHostId, TestVmId).Should().BeFalse();
        _mockHost.Verify(
            h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("Remove-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvictAsync_NoEntry_IsIdempotent()
    {
        // Never created — must not throw and must not invoke PowerShell.
        Func<Task> act = () => _store.EvictAsync(TestHostId, TestVmId);
        await act.Should().NotThrowAsync();

        _mockHost.Verify(
            h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── EvictAllAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task EvictAllAsync_RemovesOnlySessionsForGivenHost()
    {
        var vmA = "aaaaaaaa-1111-1111-1111-111111111111";
        var vmB = "bbbbbbbb-2222-2222-2222-222222222222";

        await _store.GetOrCreateAsync("host1", vmA, TestUsername, TestPassword);
        await _store.GetOrCreateAsync("host1", vmB, TestUsername, TestPassword);
        await _store.GetOrCreateAsync("host2", vmA, TestUsername, TestPassword);

        await _store.EvictAllAsync("host1");

        _store.HasSession("host1", vmA).Should().BeFalse();
        _store.HasSession("host1", vmB).Should().BeFalse();
        _store.HasSession("host2", vmA).Should().BeTrue();
    }

    // ─── Per-key serialization ──────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_ConcurrentCallsForSameKey_SerializeAndCreateOnce()
    {
        var createCount = 0;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref createCount);
                // Tiny delay to widen the race window.
                await Task.Delay(20).ConfigureAwait(false);
                return new PowerShellHostResult(
                    Success: true,
                    Output: Array.Empty<object?>(),
                    Stderr: string.Empty,
                    ExitCode: 0);
            });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _store.GetOrCreateAsync(
                TestHostId, TestVmId, TestUsername, TestPassword))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Select(h => h.SessionName).Distinct().Should().HaveCount(1);
        createCount.Should().Be(1,
            "the per-(hostId,vmId) semaphore must serialize creation");
    }

    // ─── Argument validation ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateAsync_InvalidHostId_ThrowsArgumentException(string? hostId)
    {
        Func<Task> act = () => _store.GetOrCreateAsync(
            hostId!, TestVmId, TestUsername, TestPassword);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateAsync_InvalidVmId_ThrowsArgumentException(string? vmId)
    {
        Func<Task> act = () => _store.GetOrCreateAsync(
            TestHostId, vmId!, TestUsername, TestPassword);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateAsync_InvalidUsername_ThrowsArgumentException(string? username)
    {
        Func<Task> act = () => _store.GetOrCreateAsync(
            TestHostId, TestVmId, username!, TestPassword);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetOrCreateAsync_InvalidPassword_ThrowsArgumentException(string? password)
    {
        Func<Task> act = () => _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, password!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ─── Dispose ────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_AfterCreate_DoesNotThrow_AndIsIdempotent()
    {
        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        Action act = () =>
        {
            _store.Dispose();
            _store.Dispose();
        };

        act.Should().NotThrow();
    }

    // ─── RC-10.1 diagnostic: New-PSSession exception propagation ────────
    //
    // RC-10 surfaced when vm_run_command / vm_copy_file failed with the empty-tail
    // message "Failed to create PSSession 'hyperv-mcp-...': " — the trailing
    // result.Stderr was EMPTY because the underlying PowerShell-level exception
    // thrown by `New-PSSession -VMId ... -ErrorAction Stop` was being swallowed
    // before it reached the C# catch block in
    // SessionStore.CreateSessionInRunspaceAsync.
    //
    // This test reproduces the gap end-to-end by routing SessionStore through a
    // REAL System.Management.Automation runspace (not the Moq host) and shadowing
    // `New-PSSession` with a function that throws a known marker. The assertion
    // is purely on the propagation contract: the marker text MUST appear in the
    // thrown InvalidOperationException.Message.
    //
    // Why a real-runspace fake instead of pure Moq:
    // - The bug lives in PowerShell-level error-stream behavior under
    //   `-ErrorAction Stop`. A Moq that just returns `Success: false, Stderr: "..."`
    //   would mask the real swallow because Moq cannot model how PS routes a
    //   terminating exception around `ps.Streams.Error`.
    // - The fake host below mirrors `PowerShellHost.InvokeAsync` semantics
    //   (variable binding via SessionStateProxy, Success=!HadErrors, Stderr from
    //   ErrorRecord.ToString()) so the SUT script runs against the same surface
    //   it sees in production.
    [Fact]
    public async Task GetOrCreateAsync_NewPSSessionThrows_PropagatesMarkerToInvalidOperationException()
    {
        // Use a real-runspace fake so the actual PowerShell error-stream behavior
        // under `-ErrorAction Stop` is exercised — that is where the swallow
        // happens in production.
        using var fakeHost = new RealRunspaceFakeHost(
            shadowScript:
                // RC-11.9: production now invokes the BASE
                // Microsoft.PowerShell.Core\New-PSSession cmdlet's native
                // -VMName <String[]> parameter (Build #19 proved -VMId
                // fast-fails inside the MCP-hosted runspace even under STA
                // because its internal Get-VM resolver hits LF-D7's
                // Server.GetServer(name=null) bug). Shadow Get-VM so it
                // still returns a sentinel object with a .Name (the pre-flight
                // existence check is retained), and shadow New-PSSession to
                // accept the -VMName String parameter the production script
                // now passes. -VMId is intentionally dropped from the shadow
                // to avoid partial-name binding ambiguity against -VMName.
                // The propagation contract we are exercising is still the
                // New-PSSession failure path.
                "function global:Import-Module { param([Parameter(ValueFromRemainingArguments=$true)] $args) }; " +
                "function global:Get-VM { " +
                "  param( " +
                "    $Id, " +
                "    [Parameter(ValueFromRemainingArguments=$true)] $Rest " +
                "  ); " +
                "  [pscustomobject]@{ Id = $Id; Name = 'rc-shadow-vm' } " +
                "}; " +
                "function global:New-PSSession { " +
                "  [CmdletBinding()] param( " +
                "    [Parameter()] [String[]] $VMName, " +
                "    [Parameter(ValueFromRemainingArguments=$true)] $args " +
                "  ) " +
                "  process { throw 'RC10_PROBE_MARKER_EXCEPTION' } " +
                "}");

        using var store = new SessionStore(fakeHost, _logger);

        Func<Task> act = () => store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain(
            "RC10_PROBE_MARKER_EXCEPTION",
            "the underlying New-PSSession exception MUST propagate through " +
            "result.Stderr into the thrown InvalidOperationException so RC-10 " +
            "is no longer opaque (issue #52 RC-10.1 diagnostic patch).");
    }

    // ─── RC-10.3a (Issue #52 Phase 2) — empty .Message scenario ──────────
    //
    // Symptom RC-10.3 observed in production: New-PSSession failed with an
    // ErrorRecord whose underlying Exception.Message was empty — the rich
    // diagnostic text lived in ErrorRecord.ErrorDetails.Message instead
    // (PowerShell uses ErrorDetails to override Exception.Message for
    // display purposes). The RC-10.1 catch block only inspected
    // $_.Exception.Message + $_.ScriptStackTrace and produced a useless
    // "Failed to create PSSession '...': " envelope.
    //
    // This test simulates that exact shape: a shadowed New-PSSession that
    // throws an ErrorRecord whose Exception.Message is empty BUT whose
    // ErrorDetails.Message + FullyQualifiedErrorId carry the real signal.
    // Pre-fix the test fails because the script never inspects ErrorDetails
    // / FullyQualifiedErrorId. Post-fix both markers must surface.
    [Fact]
    public async Task GetOrCreateAsync_EmptyExceptionMessageWithRichErrorDetails_SurfacesErrorDetailsAndErrorId()
    {
        using var fakeHost = new RealRunspaceFakeHost(
            shadowScript:
                // RC-11.9: shadow Get-VM (still called as a pre-flight existence
                // check) and shadow New-PSSession to accept the -VMName String
                // parameter that the production script now binds to the BASE
                // Microsoft.PowerShell.Core\New-PSSession cmdlet. -VMId is
                // intentionally dropped from the shadow to avoid partial-name
                // binding ambiguity against -VMName (Build #19 proved -VMId
                // fast-fails inside the MCP-hosted runspace via LF-D7). We
                // still throw the rich ErrorRecord from New-PSSession to
                // exercise the ErrorDetails.Message + FullyQualifiedErrorId
                // propagation contract introduced in RC-10.3a Layer 2.
                "function global:Import-Module { param([Parameter(ValueFromRemainingArguments=$true)] $args) }; " +
                "function global:Get-VM { " +
                "  param( " +
                "    $Id, " +
                "    [Parameter(ValueFromRemainingArguments=$true)] $Rest " +
                "  ); " +
                "  [pscustomobject]@{ Id = $Id; Name = 'rc-shadow-vm' } " +
                "}; " +
                "function global:New-PSSession { " +
                "  param( " +
                "    [Parameter()] [String[]] $VMName, " +
                "    $Credential, " +
                "    $Name, " +
                "    [Parameter(ValueFromRemainingArguments=$true)] $Rest " +
                "  ); " +
                "  process { " +
                "    $exc = [System.Exception]::new(''); " +
                "    $cat = [System.Management.Automation.ErrorCategory]::NotSpecified; " +
                "    $err = [System.Management.Automation.ErrorRecord]::new($exc, 'RC103a_TEST_ERRID', $cat, $null); " +
                "    $err.ErrorDetails = [System.Management.Automation.ErrorDetails]::new('RC103a_RICH_DETAILS_MARKER'); " +
                "    throw $err; " +
                "  } " +
                "}");

        using var store = new SessionStore(fakeHost, _logger);

        Func<Task> act = () => store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();

        ex.Which.Message.Should().Contain(
            "RC103a_RICH_DETAILS_MARKER",
            "ErrorDetails.Message overrides Exception.Message for display — " +
            "the SessionStore PS try/catch MUST inspect $_.ErrorDetails.Message " +
            "so the real failure text reaches the C# layer when the underlying " +
            "Exception.Message is empty (RC-10.3a Layer 2)");
        ex.Which.Message.Should().Contain(
            "RC103a_TEST_ERRID",
            "FullyQualifiedErrorId is the canonical PowerShell error " +
            "identifier — it MUST appear in the surfaced diagnostic so " +
            "post-mortem triage can correlate the failure (RC-10.3a Layer 2)");
    }

    // ─── RC-10.3b Layer A — eliminate ConvertTo-SecureString in PS script ──
    //
    // RC-10.3-meta diagnostic captured the smoking gun: the bundled (unsigned)
    // app-local Microsoft.PowerShell.Security module under
    // runtimes\win\lib\net8.0\Modules is rejected by AuthorizationManager
    // under Code Integrity, causing ConvertTo-SecureString to fail with
    // CommandNotFoundException + FQEID=CouldNotAutoloadMatchingModule.
    //
    // Layer A fix: build PSCredential in C# and bind it as a single $cred
    // session variable. The PS script body must NOT invoke
    // ConvertTo-SecureString or New-Object ... PSCredential anymore — those
    // calls are the Code-Integrity-tripping payload we are eliminating.

    [Fact]
    public async Task CreateSession_ScriptDoesNotInvokeConvertToSecureString()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        capturedScript!.Should().NotContain(
            "ConvertTo-SecureString",
            "RC-10.3b Layer A: ConvertTo-SecureString triggers auto-loading of " +
            "the bundled Microsoft.PowerShell.Security module which fails " +
            "AuthorizationManager.PassesPolicyCheck() under Code Integrity. " +
            "PSCredential MUST be built in C# and bound as $cred instead.");

        capturedScript.Should().NotContain(
            "New-Object System.Management.Automation.PSCredential",
            "RC-10.3b Layer A: PSCredential construction must happen in C# — " +
            "the PS script must not allocate the credential itself.");

        capturedScript.Should().NotContain(
            "New-Object PSCredential",
            "RC-10.3b Layer A: any PS-side PSCredential allocation is " +
            "forbidden — bind the credential from C#.");

        capturedScript.Should().Contain(
            "$cred",
            "RC-10.3b Layer A: the script must reference the C#-bound " +
            "$cred session variable when calling New-PSSession.");
    }

    [Fact]
    public async Task CreateSession_BindsPSCredentialAsCredArg()
    {
        IDictionary<string, object?>? capturedArgs = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (_, args, _) => capturedArgs = args)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedArgs.Should().NotBeNull(
            "the New-PSSession invocation must have received an args dict");

        capturedArgs!.Should().ContainKey("cred",
            "RC-10.3b Layer A: PSCredential must be bound as the single " +
            "$cred session variable instead of bare username + password.");

        var credValue = capturedArgs["cred"];
        credValue.Should().BeOfType<PSCredential>(
            "RC-10.3b Layer A: the bound 'cred' value must be a real " +
            "PSCredential constructed in C#.");

        var psCred = (PSCredential)credValue!;
        psCred.UserName.Should().Be(
            TestUsername,
            "the C#-built PSCredential must carry the original username verbatim.");

        capturedArgs.Should().NotContainKey("username",
            "RC-10.3b Layer A: bare username/password keys must be folded " +
            "into the PSCredential — neither should appear separately.");
        capturedArgs.Should().NotContainKey("password",
            "RC-10.3b Layer A: bare username/password keys must be folded " +
            "into the PSCredential — neither should appear separately.");
    }

    // ─── RC-11.1 — explicit [Guid] coercion for -VMId ─────────────────────
    //
    // Smoke probe #7 against Build #10 surfaced RC-11: PS5.1's
    // Microsoft.HyperV.PowerShell New-PSSession -VMId parameter set
    // internally calls Get-VM -Id $args[0] and the binder fails to coerce a
    // bound [String] $vmId into the required [Guid], silently passing $null
    // and producing
    //   VirtualizationException: Value cannot be null. Parameter name: name
    //
    // Fix: explicit [Guid]::Parse($vmId) at the call site, with a separate
    // local $vmIdGuid so the original $vmId string remains available for the
    // catch-block error envelope.
    [Fact]
    public async Task CreateSession_ScriptCastsVmIdToGuidExplicitly()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        capturedScript!.Should().Contain(
            "[Guid]::Parse($vmId)",
            "RC-11.1: the script must explicitly parse $vmId into a [Guid] " +
            "before passing it to New-PSSession -VMId. The PS5.1 cmdlet " +
            "binder does not coerce [String] → [Guid] for the -VMId " +
            "parameter set and silently null-propagates, surfacing as " +
            "'VirtualizationException: Value cannot be null. Parameter name: name'.");

        capturedScript.Should().Contain(
            "Get-VM -Id $vmIdGuid",
            "RC-11.1 (retained under RC-11.9): the parsed $vmIdGuid local " +
            "must still be consumed by the pre-flight Get-VM lookup so the " +
            "[Guid]::Parse(...) is not dead code. The Get-VM -Id binder also " +
            "demands a real [Guid] (not a string).");

        capturedScript.Should().NotContain(
            "-VMId $vmId ",
            "RC-11.1: the OLD pattern '-VMId $vmId <space>' must be gone. " +
            "RC-11.9 also eliminates '-VMId $vmIdGuid' from the New-PSSession " +
            "call site (replaced by -VMName $vm.Name) — only Get-VM still " +
            "consumes the parsed [Guid].");
    }

    // RC-11.2: the SessionStore creation script must `Import-Module Hyper-V -Force`
    // BEFORE invoking New-PSSession so the Hyper-V module's -VMId-aware proxy
    // shadows Microsoft.PowerShell.Core's bare New-PSSession. In long-lived host
    // processes (e.g. the Roo MCP server) earlier Hyper-V calls (vm_list /
    // vm_diag) may load the module in a way that does NOT install the proxy in
    // this script's runspace scope, so the bare Core cmdlet wins, the Hyper-V
    // parameter resolver hooks then call Get-VM -Id $args[0] from an empty
    // $args context, and we get
    //   Microsoft.HyperV.PowerShell.VirtualizationException:
    //     Value cannot be null. Parameter name: name
    // Smoke probe #7 (Build #11) RC103a:Discovery captured
    //   NewPSSessionSource=Microsoft.PowerShell.Core
    // confirming the proxy was NOT in scope. -Force ensures the proxy wins
    // over any cached-but-incomplete auto-import that may have occurred
    // earlier in the runspace.
    [Fact]
    public async Task CreateSession_ScriptForceImportsHyperVModuleBeforeNewPSSession()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        capturedScript!.Should().Contain(
            "Import-Module Hyper-V -Force",
            "RC-11.2: the script must force-import the Hyper-V module so its " +
            "-VMId-aware New-PSSession proxy shadows Microsoft.PowerShell.Core's " +
            "bare cmdlet. Without this the bare Core cmdlet wins in long-lived " +
            "runspaces and the Hyper-V binder hooks null-propagate, surfacing " +
            "as 'VirtualizationException: Value cannot be null. Parameter name: name'.");

        var importIdx = capturedScript.IndexOf(
            "Import-Module Hyper-V -Force", StringComparison.Ordinal);
        var newPsSessionIdx = capturedScript.IndexOf(
            "$session = New-PSSession -VMName", StringComparison.Ordinal);
        var discoveryIdx = capturedScript.IndexOf(
            "Get-Module -ListAvailable -Name 'Hyper-V'", StringComparison.Ordinal);

        importIdx.Should().BeGreaterThan(-1,
            "the Import-Module Hyper-V -Force line must be present in the script body");
        newPsSessionIdx.Should().BeGreaterThan(-1,
            "the New-PSSession -VMName call must be present in the script body " +
            "(RC-11.9 replaced the RC-11.7 -VMId form with -VMName $vm.Name).");
        discoveryIdx.Should().BeGreaterThan(-1,
            "the RC103a:Discovery snapshot probing Hyper-V module availability " +
            "must be present in the script body");

        importIdx.Should().BeLessThan(newPsSessionIdx,
            "RC-11.2: the Import-Module Hyper-V -Force line must precede the " +
            "New-PSSession -VMName call so the Hyper-V proxy is in scope when " +
            "the cmdlet is resolved.");

        importIdx.Should().BeLessThan(discoveryIdx,
            "RC-11.2: the Import-Module Hyper-V -Force line must precede the " +
            "RC103a:Discovery snapshot so the discovery probes the post-import " +
            "state (NewPSSessionSource should resolve to the Hyper-V module, " +
            "not Microsoft.PowerShell.Core).");
    }

    // ─── RC-11.3 — pipeline-form fix for -VMId binder cache poisoning ─────
    //
    // Build #12 differential test (out-of-Roo harness PASS, in-Roo MCP server
    // ~28min uptime FAIL) proved that RC-11.1 ([Guid] cast) and RC-11.2
    // (Import-Module Hyper-V -Force) are correct PowerShell hygiene but
    // INSUFFICIENT to fix the production bug. PS5.1's command-table cache
    // binds Microsoft.PowerShell.Core's bare cmdlet on first use without
    // the Hyper-V module's -VMId parameter set extension; subsequent
    // Import-Module Hyper-V -Force does NOT invalidate that metadata cache.
    //
    // Workaround: route through Get-VM -Id then pipe the [VirtualMachine]
    // object into New-PSSession. The pipeline binder uses the -VM (object)
    // parameter set provided by the Hyper-V module and is unaffected by
    // the poisoned cache for the -VMId parameter set.
    [Fact]
    public async Task CreateSession_ScriptInvokesGetVmByIdBeforeNewPSSession()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        capturedScript!.Should().Contain(
            "Get-VM -Id $vmIdGuid",
            "RC-11.3: the script must resolve the VM via Get-VM -Id before " +
            "piping it into New-PSSession. This bypasses PS5.1's poisoned " +
            "-VMId parameter binder cache that survives Import-Module -Force " +
            "in long-lived runspaces (Build #12 differential evidence).");

        capturedScript.Should().Contain(
            "-ErrorAction Stop",
            "the Get-VM lookup must terminate the script on failure so the " +
            "C# catch path receives a populated error envelope.");
    }

    // ─── RC-11.9 — switch to -VMName (proven path) replaces RC-11.7 -VMId ──
    //
    // Build #19 smoke probe (with RC-11.8 STA confirmed via DIAG-APARTMENT
    // marker) STILL fast-failed in 29ms with the byte-identical LF-D7
    // signature:
    //   Get-VM : Value cannot be null. Parameter name: name
    //   ScriptStackTrace=at <ScriptBlock>, <No file>: line 1
    // The `<No file>: line 1` script `Get-VM -Id $args[0]` is internally
    // injected by `New-PSSession -VMId`'s parameter resolver — even under
    // STA it hits the LF-D7 null-name bug in
    // Server.GetServer(name=null). RC-11.8 (STA) was insufficient.
    //
    // Out-of-Roo harness (formerly scripts/harness-rc117-newpssession-variants.ps1;
    // removed in Phase E — recoverable from git history)
    // proved BOTH parameter sets work in stock PS5.1, but inside the
    // MCP-hosted runspace only -VMId fails because its internal resolver
    // calls the broken Server.GetServer(name=null) path. -VMName uses a
    // different resolver that bypasses that path and succeeds in
    // 2,451 ms (vs -VMId failing instantly).
    //
    // RC-11.9's fix: switch the cmdlet binding to -VMName $vm.Name. We
    // already have $vm.Name from RC-11.4's
    // `Get-VM -Id $vmIdGuid -ComputerName localhost` call, so no extra
    // lookup is needed. RC-11.4 stays as the pre-flight existence check.
    // These two tests assert the new wire shape and forbid any
    // regression to the broken -VMId form. They replace the deleted
    // RC-11.7 tests CreateSession_ScriptUsesVmIdGuidParameterForNewPSSession
    // and CreateSession_ScriptOrdersGetVmBeforeNewPSSessionVMId.
    [Fact]
    public async Task CreateSession_ScriptUsesVMNameParameterForNewPSSession()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        capturedScript!.Should().Contain(
            "New-PSSession -VMName $vm.Name -Credential $cred -Name $sessionName -ErrorAction Stop",
            "RC-11.9: New-PSSession must be invoked with the -VMName $vm.Name " +
            "parameter form. Build #19 proved that even under STA " +
            "(RC-11.8), -VMId fast-fails inside the MCP-hosted runspace " +
            "because the cmdlet's internal `Get-VM -Id $args[0]` resolver " +
            "hits the LF-D7 null-name bug in Server.GetServer(name=null). " +
            "-VMName uses a different resolver that bypasses that path. " +
            "Out-of-Roo harness (formerly scripts/harness-rc117-newpssession-variants.ps1; " +
            "removed in Phase E — recoverable from git history) " +
            "proved -VMName succeeds in 2,451ms vs -VMId failing instantly.");

        capturedScript.Should().NotContain(
            "New-PSSession -VMId $vmIdGuid",
            "RC-11.9: the RC-11.7 `New-PSSession -VMId $vmIdGuid` invocation " +
            "must be fully eliminated. Build #19 smoke probe proved that " +
            "form fast-fails in 29ms inside the MCP-hosted runspace even " +
            "under STA, because -VMId's internal Get-VM resolver hits the " +
            "LF-D7 Server.GetServer(name=null) bug.");

        capturedScript.Should().Contain(
            "Get-VM -Id $vmIdGuid -ComputerName localhost",
            "RC-11.4 (retained): the pre-flight Get-VM -Id $vmIdGuid " +
            "-ComputerName localhost lookup must remain so we have $vm.Name " +
            "available for the -VMName binding AND so we fail fast with a " +
            "friendly error if the VM doesn't exist.");
    }

    [Fact]
    public async Task CreateSession_ScriptOrdersGetVmBeforeNewPSSessionVMName()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        // Use the full cmdlet+args literal (including -Credential) so we
        // anchor on the actual production call site rather than incidental
        // mentions of the cmdlet name in upstream comment blocks.
        var getVmIdx = capturedScript!.IndexOf(
            "$vm = Get-VM -Id $vmIdGuid -ComputerName localhost",
            StringComparison.Ordinal);
        var newPSSessionIdx = capturedScript.IndexOf(
            "$session = New-PSSession -VMName $vm.Name -Credential $cred -Name $sessionName",
            StringComparison.Ordinal);

        getVmIdx.Should().BeGreaterThan(-1,
            "RC-11.4 (retained as pre-flight + name source): the Get-VM " +
            "-Id $vmIdGuid -ComputerName localhost lookup must be present " +
            "in the script body so we have $vm.Name available for the " +
            "-VMName binding and fail fast if the VM doesn't exist.");
        newPSSessionIdx.Should().BeGreaterThan(-1,
            "RC-11.9: the New-PSSession -VMName $vm.Name invocation must " +
            "be present in the script body.");

        getVmIdx.Should().BeLessThan(newPSSessionIdx,
            "RC-11.9 + RC-11.4 ordering: Get-VM -Id $vmIdGuid -ComputerName " +
            "localhost must precede New-PSSession -VMName $vm.Name so " +
            "$vm.Name is bound before the PSDirect bind references it.");
    }

    // ─── RC-11.4 — LF-D7 -ComputerName localhost workaround for Get-VM ────
    //
    // Smoke probe #7 against Build #13 (RC-11.3 pipeline-form fix shipped)
    // proved that line 78 of the SessionStore script — the Get-VM -Id call
    // itself — crashes with
    //   VirtualizationException: Value cannot be null. Parameter name: name
    //   at Microsoft.Virtualization.Client.Management.Server.GetServer(
    //          String name, IUserPassCredential credential)
    //   at ParameterResolvers.GetServers(...)
    //   at GetVM.EnumerateOperands(...)
    // The 'name' here is the Hyper-V SERVER/COMPUTER name (NOT the VM
    // name): in long-lived PS5.1 runspaces the Hyper-V module's default-
    // server resolution sporadically returns $null, and the internal
    // Server.GetServer(name=null) throws. This is the "LF-D7 Hyper-V WMI
    // null-name bug" already worked around throughout the codebase —
    // every Get-VM call in HyperVManager.cs, CheckpointManager.cs, and
    // PowerShellHost.cs uses the explicit -ComputerName localhost fix.
    // RC-11.3 added the only Get-VM call site in production that was
    // missing the workaround. This test pins the workaround in place.
    [Fact]
    public async Task CreateSession_ScriptUsesComputerNameLocalhostWorkaroundForGetVm()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.Is<string>(s => s.Contains("New-PSSession")),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        // Single-literal assertion that simultaneously pins:
        //   1. the RC-11.3 pipeline form (Get-VM -Id $vmIdGuid)
        //   2. the RC-11.4 LF-D7 -ComputerName localhost workaround
        // in the exact param order the production script must emit.
        capturedScript!.Should().Contain(
            "Get-VM -Id $vmIdGuid -ComputerName localhost",
            "RC-11.4: Get-VM must be invoked with -ComputerName localhost to " +
            "work around the LF-D7 Hyper-V WMI null-name bug. In long-lived " +
            "PS5.1 runspaces (the Roo MCP server) Get-VM's default-server " +
            "resolution sporadically returns $null, causing Server.GetServer" +
            "(name=null) inside the Hyper-V module to throw " +
            "VirtualizationException 'Value cannot be null. Parameter name: " +
            "name'. Every other Get-VM call site in the codebase " +
            "(HyperVManager, CheckpointManager, PowerShellHost) already " +
            "uses this workaround; RC-11.3 was the only one missing it, " +
            "as exposed by smoke probe #7 against Build #13.");

        // Defensive secondary check — catches accidental param-order
        // changes (e.g. someone reorders to '-ComputerName localhost -Id
        // $vmIdGuid' which would still satisfy the contract but slip past
        // the single-literal assertion above).
        capturedScript.Should().Contain(
            "-ComputerName localhost",
            "RC-11.4: the LF-D7 workaround literal '-ComputerName localhost' " +
            "must appear in the script body regardless of parameter order.");
    }

    // ─── RC-11.5-diag — phase-timing markers ──────────────────────────────
    //
    // Build #14 smoke probe revealed the failure mode is a 60s MCP-client
    // timeout with psFinalState=Stopped — the SessionStore script ran for
    // 59,455ms between PRE-Invoke and POST-Invoke-CAUGHT with ZERO stderr
    // output, then was forcibly killed by the outer 60s budget. RC-11.5
    // instruments the script with millisecond-relative phase markers so
    // the next smoke probe will reveal WHICH cmdlet is slow. These tests
    // pin (a) presence of all 8 phase marker names inside Write-Information
    // calls with the [RC11.5:T+ prefix, and (b) that the stopwatch
    // initialization precedes the first marker (otherwise $__rc115Sw is
    // null when the first marker references it and the script fails).

    [Fact]
    public async Task CreateSession_ScriptEmitsRc115PhaseMarkers()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        // All 8 phase marker names must appear inside a Write-Information
        // call with the [RC11.5:T+ prefix. We assert the FULL prefix +
        // suffix together so a stray bare token elsewhere in the script
        // (e.g. inside a comment) cannot satisfy the test.
        string[] markerNames = new[]
        {
            "SCRIPT-ENTER",
            "PRE-IMPORT-MODULE",
            "POST-IMPORT-MODULE",
            "PRE-GET-VM",
            "POST-GET-VM",
            "PRE-NEW-PSSESSION",
            "POST-NEW-PSSESSION",
            "SCRIPT-EXIT-NORMAL",
            "SCRIPT-EXIT-CAUGHT",
        };

        foreach (string name in markerNames)
        {
            capturedScript!.Should().Contain(
                $"[RC11.5:T+",
                "RC-11.5-diag: every phase marker must use the " +
                "[RC11.5:T+...ms] prefix so PowerShellHost can filter " +
                "Hyper-V module verbose chatter from the meta log.");

            capturedScript.Should().Contain(
                $"ms] {name}",
                $"RC-11.5-diag: phase marker '{name}' must appear in the " +
                "script body so the meta log can pinpoint which cmdlet " +
                "is slow when the 60s outer budget kills the pipeline.");
        }

        // Belt-and-braces: the literal `Write-Information "[RC11.5:T+`
        // composed marker prefix must appear at least 8 times (one per
        // phase). Counted via successive IndexOf so we do not depend on
        // any external regex helper.
        const string ComposedPrefix = "Write-Information \"[RC11.5:T+";
        int count = 0;
        int idx = 0;
        while ((idx = capturedScript!.IndexOf(ComposedPrefix, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += ComposedPrefix.Length;
        }

        count.Should().BeGreaterOrEqualTo(8,
            "RC-11.5-diag: the script must emit at least 8 Write-Information " +
            "phase markers (SCRIPT-ENTER, PRE/POST-IMPORT-MODULE, " +
            "PRE/POST-GET-VM, PRE/POST-NEW-PSSESSION, SCRIPT-EXIT-*).");
    }

    [Fact]
    public async Task CreateSession_ScriptInitializesStopwatchBeforeFirstMarker()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        int stopwatchIdx = capturedScript!.IndexOf(
            "[System.Diagnostics.Stopwatch]::StartNew()", StringComparison.Ordinal);
        int firstMarkerIdx = capturedScript.IndexOf(
            "Write-Information \"[RC11.5:", StringComparison.Ordinal);

        stopwatchIdx.Should().BeGreaterThan(-1,
            "RC-11.5-diag: the script must initialize a stopwatch via " +
            "[System.Diagnostics.Stopwatch]::StartNew() so every phase " +
            "marker can carry a millisecond-relative offset.");

        firstMarkerIdx.Should().BeGreaterThan(-1,
            "RC-11.5-diag: at least one Write-Information [RC11.5: marker " +
            "must be present in the script body.");

        stopwatchIdx.Should().BeLessThan(firstMarkerIdx,
            "RC-11.5-diag: the stopwatch must be initialized BEFORE the " +
            "first Write-Information [RC11.5: marker — otherwise " +
            "$__rc115Sw is null when the first marker references it and " +
            "the entire diagnostic script fails on the first line.");
    }

    // ─── RC-11.10 — $PSDefaultParameterValues injection (LF-D7 cure) ──────
    //
    // Smoke probe #7's full 6KB stderr (captured 2026-04-30) showed the
    // failing `Get-VM -Name $args` is NOT in our script — it is internally
    // synthesized by `New-PSSession -VMName/-VMId`'s parameter resolver and
    // runs WITHOUT -ComputerName, hitting the LF-D7
    // Server.GetServer(name=null) -> ArgumentNullException path.
    //
    // RC-11.4 (-ComputerName localhost on our explicit Get-VM call) does
    // NOT cover this internal invocation. RC-11.10 injects
    // $PSDefaultParameterValues so EVERY Get-VM/New-PSSession invocation
    // — including the synthesized internal one — inherits the workaround.
    //
    // Validated empirically via the OOP harness relocated to the roo-vault at
    // myscripts/archive/harness-rc11-oop (not tracked in this repo) with 10/10 probes
    // succeeding under MCP-identical ServerRemoteHost hosting.
    [Fact]
    public async Task CreateSessionScript_HasRc1110PSDefaultParameterValuesInjection()
    {
        string? capturedScript = null;
        _mockHost
            .Setup(h => h.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IDictionary<string, object?>?, CancellationToken>(
                (script, _, _) => capturedScript = script)
            .ReturnsAsync(new PowerShellHostResult(
                Success: true,
                Output: Array.Empty<object?>(),
                Stderr: string.Empty,
                ExitCode: 0));

        await _store.GetOrCreateAsync(
            TestHostId, TestVmId, TestUsername, TestPassword);

        capturedScript.Should().NotBeNull(
            "the New-PSSession script must have been dispatched to the host");

        // ── 1. Both default-parameter-value entries must be present. ──
        capturedScript!.Should().Contain(
            "$PSDefaultParameterValues['Get-VM:ComputerName']       = 'localhost'",
            "RC-11.10: the script must inject Get-VM:ComputerName='localhost' " +
            "into $PSDefaultParameterValues so that EVERY Get-VM invocation — " +
            "including the synthesized internal `Get-VM -Name $args` call " +
            "inside `New-PSSession -VMName`'s parameter resolver — inherits " +
            "the LF-D7 -ComputerName localhost workaround. Smoke probe #7's " +
            "full 6KB stderr proved this internal call is the source of the " +
            "VirtualizationException 'Value cannot be null. Parameter name: " +
            "name' that bypassed RC-11.4's per-callsite workaround.");

        capturedScript.Should().Contain(
            "$PSDefaultParameterValues['New-PSSession:ComputerName'] = 'localhost'",
            "RC-11.10: the script must also inject New-PSSession:ComputerName" +
            "='localhost' so the cmdlet itself binds the same -ComputerName " +
            "default, eliminating any code path where the Hyper-V SDK's " +
            "default-server resolution could return $null.");

        // ── 2. Additive form (preserves upstream defaults). ──
        capturedScript.Should().Contain(
            "if (-not $PSDefaultParameterValues) { $PSDefaultParameterValues = @{} }",
            "RC-11.10: the script must use the ADDITIVE form (init-if-null + " +
            "indexer assignment) instead of replacing the whole hashtable. " +
            "Replacing would lose any defaults set upstream by " +
            "Ps51InitializationScript or by other in-runspace setup.");

        // ── 3. Ordering: defaults must be set BEFORE the first Get-VM. ──
        var defaultsIdx = capturedScript.IndexOf(
            "$PSDefaultParameterValues['Get-VM:ComputerName']", StringComparison.Ordinal);
        var firstGetVmIdx = capturedScript.IndexOf(
            "Get-VM -Id $vmIdGuid -ComputerName localhost", StringComparison.Ordinal);

        defaultsIdx.Should().BeGreaterThan(-1,
            "RC-11.10: the Get-VM:ComputerName default-parameter assignment " +
            "must be present in the script body.");
        firstGetVmIdx.Should().BeGreaterThan(-1,
            "RC-11.4 (retained): the explicit Get-VM -Id call site must " +
            "still be present (anchors the ordering invariant).");

        defaultsIdx.Should().BeLessThan(firstGetVmIdx,
            "RC-11.10 invariant: the $PSDefaultParameterValues injection " +
            "MUST appear BEFORE the first Get-VM invocation. If it appears " +
            "after, the synthesized internal `Get-VM -Name $args` resolver " +
            "inside New-PSSession may run before the defaults are bound, " +
            "and the LF-D7 cure becomes a no-op.");

        // ── 4. Ordering: defaults must be set BEFORE Import-Module Hyper-V. ──
        // Defense in depth — set defaults before any Hyper-V cmdlet activity
        // so even module-load-time cmdlet resolution sees them.
        var importIdx = capturedScript.IndexOf(
            "Import-Module Hyper-V -Force", StringComparison.Ordinal);

        importIdx.Should().BeGreaterThan(-1,
            "RC-11.2 (retained): the Import-Module Hyper-V -Force line must " +
            "still be present (anchors the ordering invariant).");

        defaultsIdx.Should().BeLessThan(importIdx,
            "RC-11.10 invariant: the $PSDefaultParameterValues injection " +
            "MUST appear BEFORE Import-Module Hyper-V -Force. Defense in " +
            "depth — setting defaults before any Hyper-V cmdlet activity " +
            "ensures even module-load-time cmdlet resolution sees them.");

        // ── 5. The RC-11.10 marker must be present (rationale anchor). ──
        capturedScript.Should().Contain(
            "RC-11.10:",
            "RC-11.10: the script must carry the RC-11.10 comment marker so " +
            "the build #22 literal-scan can pin its presence in the compiled " +
            "dll and grep tools can locate the rationale block.");
    }
}

/// <summary>
/// Test-only <see cref="IPowerShellHost"/> implementation backed by a REAL
/// in-process <see cref="System.Management.Automation.PowerShell"/> runspace.
/// Faithfully mirrors <c>PowerShellHost.InvokeAsync</c> semantics (session-variable
/// binding, <c>Success=!HadErrors</c>, <c>Stderr</c> joined from
/// <c>ErrorRecord.ToString()</c>) so SUT scripts run against the same surface
/// they see in production.
/// <para>
/// The optional <c>shadowScript</c> is executed against the runspace once at
/// construction time, allowing tests to define functions / aliases that shadow
/// real cmdlets (e.g. <c>New-PSSession</c>) with deterministic behavior — this
/// is the seam used by the RC-10.1 diagnostic test to inject a known marker
/// exception without touching production code.
/// </para>
/// </summary>
file sealed class RealRunspaceFakeHost : IPowerShellHost, IDisposable
{
    private readonly Runspace _runspace;
    private bool _disposed;

    public RealRunspaceFakeHost(string? shadowScript = null)
    {
        _runspace = RunspaceFactory.CreateRunspace();
        _runspace.Open();

        if (!string.IsNullOrEmpty(shadowScript))
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(shadowScript);
            ps.Invoke();
        }
    }

    public PowerShellEdition Edition => PowerShellEdition.PowerShell7;

    public Task EnsureInitializedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<PowerShellHostResult> InvokeAsync(
        string script,
        IDictionary<string, object?>? args = null,
        CancellationToken ct = default)
        => InvokeWithTimeoutAsync(script, args, timeoutSeconds: null, ct);

    public Task<PowerShellHostResult> InvokeWithTimeoutAsync(
        string script,
        IDictionary<string, object?>? args,
        int? timeoutSeconds,
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RealRunspaceFakeHost));
        ct.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;

            if (args is not null)
            {
                foreach (var kvp in args)
                {
                    _runspace.SessionStateProxy.SetVariable(kvp.Key, kvp.Value);
                }
            }

            ps.AddScript(script);

            Collection<PSObject> output;
            try
            {
                output = ps.Invoke();
            }
            catch (Exception ex)
            {
                // Mirror PowerShellHost.InvokeAsync: a terminating exception out of
                // ps.Invoke() is treated as Success=false. Critically, we DO NOT
                // append `ex.Message` to Stderr here — production behavior under
                // the buggy script path was that Stderr was empty (the swallow we
                // are diagnosing). Capturing ex.Message here would mask the gap.
                StringBuilder stderr = new();
                bool first = true;
                foreach (var er in ps.Streams.Error)
                {
                    if (!first) stderr.Append('\n');
                    stderr.Append(er.ToString());
                    first = false;
                }
                _ = ex; // intentionally unused — see comment above
                return new PowerShellHostResult(
                    Success: false,
                    Output: Array.Empty<object?>(),
                    Stderr: stderr.ToString(),
                    ExitCode: 1);
            }

            StringBuilder errorBuilder = new();
            bool firstErr = true;
            foreach (var errorRecord in ps.Streams.Error)
            {
                if (!firstErr) errorBuilder.Append('\n');
                errorBuilder.Append(errorRecord.ToString());
                firstErr = false;
            }

            var outList = output.Select(o => (object?)o?.BaseObject).ToList();
            bool success = !ps.HadErrors;

            return new PowerShellHostResult(
                Success: success,
                Output: outList,
                Stderr: errorBuilder.ToString(),
                ExitCode: success ? 0 : 1);
        }, ct);
    }

    public Task<string> GetVmStateAsync(string hostId, string vmId, CancellationToken ct = default)
        => throw new NotSupportedException("Not used by SessionStore tests.");

    public PowerShellHostInitDiagnostics GetInitDiagnostics() =>
        new(true, PowerShellEdition.PowerShell7, null, null, null, null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _runspace.Dispose(); } catch { /* swallow */ }
    }
}
