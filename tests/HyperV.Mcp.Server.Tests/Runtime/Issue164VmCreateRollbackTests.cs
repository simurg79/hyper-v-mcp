using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #164 / LF-D17: unit tests for the cancellation-safe rollback path in
/// <see cref="HyperVManager.CreateVmAsync"/>.
///
/// Covers the three invariants required by LF-D17:
/// 1. The rollback PowerShell call observes a CT that is NOT the cancelled inbound CT
///    (i.e. a fresh, detached <c>CancellationTokenSource</c>).
/// 2. The rollback runs under a configurable budget
///    (<c>HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS</c>); when the rollback
///    PowerShell exceeds that budget, <see cref="VmCreateRollbackException.Rollback.Succeeded"/>
///    is <c>false</c> and an error is logged.
/// 3. A clean rollback against a clean filesystem reports
///    <c>ResidualArtifacts.Length == 0</c> and <c>Succeeded == true</c>.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public sealed class Issue164VmCreateRollbackTests : IDisposable
{
    private const string LocalHostId = "local";
    private const string TestVmName = "issue164-vm";

    private readonly string? _originalBudget;

    public Issue164VmCreateRollbackTests()
    {
        _originalBudget = Environment.GetEnvironmentVariable("HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS");
        Environment.SetEnvironmentVariable("HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            "HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS", _originalBudget);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static ServerOptions BuildOptions(string baseVhdxPath, string storageRoot) =>
        new()
        {
            DefaultHostId = LocalHostId,
            Hosts = new Dictionary<string, HostProfile>
            {
                [LocalHostId] = new HostProfile
                {
                    HostId = LocalHostId,
                    ComputerName = "localhost",
                    TrustPolicy = "local",
                    BaseVhdxPath = baseVhdxPath,
                    StorageRoot = storageRoot,
                },
            },
        };

    /// <summary>
    /// Captures every <c>ExecuteAsync</c> call so tests can assert which CT was
    /// observed on the rollback (detached vs. inbound) and how many calls occurred.
    /// </summary>
    private sealed class RecordingExecutor : IPowerShellExecutor
    {
        public List<(string Script, CancellationToken Ct)> Calls { get; } = new();

        /// <summary>
        /// Issue #203 / LF-D19 pre-create existence probe handler. When the
        /// script contains the LF-D19 probe shape (Get-VM + 'present'/'absent'
        /// output), this handler is invoked. Defaults to authoritative
        /// 'absent' so the test progresses to the primary pipeline — preserving
        /// pre-#203 test behaviour. Override to simulate a duplicate-name probe
        /// hit.
        /// </summary>
        public Func<string, int, CancellationToken, Task<PowerShellResult>>? LfD19ProbeHandler { get; set; }

        public Func<string, int, CancellationToken, Task<PowerShellResult>>? PrimaryHandler { get; set; }
        public Func<string, int, CancellationToken, Task<PowerShellResult>>? RollbackHandler { get; set; }
        public Func<string, int, CancellationToken, Task<PowerShellResult>>? ProbeHandler { get; set; }

        private int _nonProbeCallCount;

        public Task<PowerShellResult> ExecuteAsync(string script, int timeoutSeconds = 300,
            CancellationToken ct = default, bool allowDump = true)
        {
            Calls.Add((script, ct));

            // LF-D19 pre-create probe is recognised by its distinctive shape:
            // a short read-only script that ONLY runs Get-VM and emits 'present'
            // or 'absent'. The primary create script imports Hyper-V, calls
            // New-VHD / New-VM / Set-VM, etc.
            if (IsLfD19ProbeScript(script))
            {
                if (LfD19ProbeHandler is not null)
                    return LfD19ProbeHandler(script, timeoutSeconds, ct);

                // Default: authoritative 'absent' so the test exercises the
                // primary pipeline as before.
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = "absent",
                    DurationMs = 1,
                });
            }

            var idx = _nonProbeCallCount++;

            if (idx == 0 && PrimaryHandler is not null)
                return PrimaryHandler(script, timeoutSeconds, ct);

            if (idx == 1 && RollbackHandler is not null)
                return RollbackHandler(script, timeoutSeconds, ct);

            if (idx >= 2 && ProbeHandler is not null)
                return ProbeHandler(script, timeoutSeconds, ct);

            return Task.FromResult(new PowerShellResult { ExitCode = 0, Stdout = "{}", DurationMs = 1 });
        }

        public static bool IsLfD19ProbeScript(string script)
        {
            // LF-D19 pre-create probe: short read-only Get-VM with 'present'/
            // 'absent' literals AND no 'probe-failed:' sentinel (which is the
            // residue-probe's distinguishing token — see
            // HyperVManager.TryAppendRegisteredVmResidueAsync). Excluding the
            // state-mutating verbs further protects against misclassifying the
            // primary create script.
            return script.Contains("Get-VM", StringComparison.OrdinalIgnoreCase) &&
                   script.Contains("'present'", StringComparison.OrdinalIgnoreCase) &&
                   script.Contains("'absent'", StringComparison.OrdinalIgnoreCase) &&
                   !script.Contains("probe-failed", StringComparison.OrdinalIgnoreCase) &&
                   !script.Contains("New-VHD", StringComparison.OrdinalIgnoreCase) &&
                   !script.Contains("New-VM", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static HyperVManager BuildManager(IPowerShellExecutor exec, ServerOptions options,
        ILogger<HyperVManager>? logger = null,
        IBaseImageHashCache? baseImageHashCache = null)
    {
        var resolver = new HostResolver(options);
        return new HyperVManager(
            exec,
            resolver,
            options,
            logger ?? NullLogger<HyperVManager>.Instance,
            new TestIsoInspector(),
            fileSystemProbe: null,
            baseImageHashCache: baseImageHashCache /* null ⇒ skip pre-hash so we don't need a real base VHDX */);
    }

    private static string CleanRollbackJson() => """
        {"removed":[],"failed":[],"residual":[]}
        """;

    // ─── Tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// LF-D17 (b): when the inbound CT is cancelled mid-create, the rollback call
    /// MUST run under a fresh, detached <see cref="CancellationToken"/>. We assert
    /// (a) the rollback was invoked, and (b) the CT the rollback observed is NOT
    /// the cancelled inbound CT.
    /// </summary>
    [Fact]
    public async Task RollbackCt_IsDetachedFromCancelledInboundCt()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        using var inboundCts = new CancellationTokenSource();
        var exec = new RecordingExecutor
        {
            // Primary: pretend the user's CT was cancelled mid-pipeline.
            PrimaryHandler = (_, _, ct) =>
            {
                inboundCts.Cancel();
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Cancelled = true,
                    Stderr = "user-initiated cancellation",
                    DurationMs = 10,
                });
            },
            RollbackHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = CleanRollbackJson(),
                DurationMs = 5,
            }),
        };

        var manager = BuildManager(exec, options);

        VmCreateRollbackException? thrown = null;
        try
        {
            await manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: inboundCts.Token);
        }
        catch (VmCreateRollbackException ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull("CreateVmAsync must throw VmCreateRollbackException on cancellation");
        thrown!.ErrorCode.Should().Be(ErrorCodes.OperationCanceled);
        thrown.Rollback.Performed.Should().BeTrue();
        thrown.Rollback.Succeeded.Should().BeTrue("clean filesystem ⇒ no residual artifacts");

        exec.Calls.Should().HaveCount(3, "LF-D19 probe + primary + rollback");
        // exec.Calls[0] is the LF-D19 pre-create existence probe (Issue #203 / VC-DUP-D1).
        var (_, primaryCt) = exec.Calls[1];
        var (_, rollbackCt) = exec.Calls[2];

        primaryCt.Should().Be(inboundCts.Token, "primary call must observe the inbound CT");
        rollbackCt.Should().NotBe(inboundCts.Token,
            "LF-D17 (b): rollback MUST observe a fresh, detached CT — not the cancelled inbound CT");
        rollbackCt.IsCancellationRequested.Should().BeFalse(
            "the detached CTS for the rollback budget must not already be cancelled");
    }

    /// <summary>
    /// LF-D17 (c): when the rollback PowerShell exceeds the budget configured via
    /// <c>HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS</c>, the budget CTS fires,
    /// the rollback reports <c>Succeeded=false</c>, and an error is logged.
    /// </summary>
    [Fact]
    public async Task RollbackBudget_Exceeded_ReportsSucceededFalse_AndLogsError()
    {
        Environment.SetEnvironmentVariable("HYPERV_MCP_VM_CREATE_ROLLBACK_BUDGET_SECONDS", "1");

        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var logger = new RecordingLogger<HyperVManager>();

        var exec = new RecordingExecutor
        {
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "New-VM : simulated failure",
                DurationMs = 5,
            }),
            // Rollback: block on the detached budget CT > 1s so the CTS fires.
            RollbackHandler = async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException)
                {
                    // Mirror real executor behaviour on cancellation.
                    return new PowerShellResult { Cancelled = true, DurationMs = 1100 };
                }
                return new PowerShellResult { ExitCode = 0, Stdout = CleanRollbackJson(), DurationMs = 1100 };
            },
        };

        var manager = BuildManager(exec, options, logger);

        var thrown = await Assert.ThrowsAsync<VmCreateRollbackException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.Rollback.Performed.Should().BeTrue();
        thrown.Rollback.Succeeded.Should().BeFalse(
            "rollback that exceeds its budget must report Succeeded=false (LF-D17 (c))");
        thrown.Rollback.ResidualArtifacts.Should().NotBeEmpty(
            "budget-exceeded rollback must surface residual artifacts");

        logger.AnyError().Should().BeTrue(
            "LF-D17 requires an error-level log when the rollback budget is exceeded");
    }

    /// <summary>
    /// LF-D17 (d): a clean rollback against a simulated clean filesystem returns
    /// <c>ResidualArtifacts.Length == 0</c> and <c>Succeeded == true</c>.
    /// </summary>
    [Fact]
    public async Task CleanRollback_AgainstCleanFilesystem_ReportsZeroResidualArtifacts()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        // Use a storage root under a non-existent directory so File.Exists(diffPath)
        // and Directory.Exists(vmDir) both return false (clean filesystem).
        var storage = Path.Combine(Path.GetTempPath(), "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var exec = new RecordingExecutor
        {
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "New-VHD : simulated failure",
                DurationMs = 5,
            }),
            RollbackHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = CleanRollbackJson(),
                DurationMs = 1,
            }),
        };

        var manager = BuildManager(exec, options);

        var thrown = await Assert.ThrowsAsync<VmCreateRollbackException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
            "non-cancel / non-timeout primary failure ⇒ COMMAND_FAILED");
        thrown.Rollback.Performed.Should().BeTrue();
        thrown.Rollback.Succeeded.Should().BeTrue(
            "clean filesystem + script-reported no residual ⇒ Succeeded=true");
        thrown.Rollback.ResidualArtifacts.Should().BeEmpty(
            "clean filesystem ⇒ ResidualArtifacts.Length == 0 (AC#2)");
        thrown.VmName.Should().Be(TestVmName);
        thrown.Phase.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILogger{T}"/> that records logged severities so
    /// tests can assert "an error was logged for budget exceeded".
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = new();
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            lock (Levels)
            {
                Levels.Add(logLevel);
                Entries.Add((logLevel, msg));
            }
        }

        public bool AnyError()
        {
            lock (Levels) return Levels.Any(l => l == LogLevel.Error || l == LogLevel.Critical);
        }

        public bool AnyWarningContaining(string substring)
        {
            lock (Levels) return Entries.Any(e => e.Level == LogLevel.Warning &&
                e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // ─── Fail-closed probe tests (post-#164 review fix) ─────────────────

    /// <summary>
    /// Fail-closed: when the rollback PowerShell call fails (non-zero exit), the
    /// host invokes the defensive Get-VM probe. If that probe ALSO fails
    /// (executor returns non-success), the host MUST append the
    /// <c>vm:&lt;vmName&gt;(probe-unknown)</c> sentinel and log a Warning so the
    /// envelope honestly reports uncertainty rather than silently asserting
    /// "no VM residue".
    /// </summary>
    [Fact]
    public async Task ProbeFailure_AppendsProbeUnknownSentinel_AndLogsWarning()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var logger = new RecordingLogger<HyperVManager>();

        var exec = new RecordingExecutor
        {
            // Post-registration failure (Start-VM phase) so the residue-probe
            // path is exercised. Issue #203 / VC-DUP-D3: the residue probe runs
            // ONLY when this call owned the VM registration — i.e. when stderr
            // signals the failure happened AFTER New-VM completed (Set-VM /
            // Start-VM phase).
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "Start-VM : simulated failure",
                DurationMs = 5,
            }),
            // Rollback PS fails (exit != 0) ⇒ triggers the probe fallback branch.
            RollbackHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "rollback script failed",
                DurationMs = 5,
            }),
            // Probe also fails (e.g. module-import error surfaced as non-zero exit).
            ProbeHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "Import-Module Hyper-V : The specified module 'Hyper-V' was not loaded",
                DurationMs = 2,
            }),
        };

        var manager = BuildManager(exec, options, logger);

        var thrown = await Assert.ThrowsAsync<VmCreateRollbackException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.Rollback.Performed.Should().BeTrue();
        thrown.Rollback.ResidualArtifacts.Should().Contain(
            $"vm:{TestVmName}(probe-unknown)",
            "fail-closed: an uncertain probe result MUST append the probe-unknown sentinel");
        thrown.Rollback.ResidualArtifacts.Should().NotContain($"vm:{TestVmName}",
            "the bare vm:<name> token is reserved for the authoritative 'present' outcome");

        logger.AnyWarningContaining("probe-unknown").Should().BeTrue(
            "fail-closed contract requires a Warning log explaining the probe-unknown outcome");

        exec.Calls.Should().HaveCount(4, "LF-D19 probe + primary + rollback + residue-probe");

        // ── Fail-closed probe script content assertions (Issue #164 loop-back #3) ──
        // The probe script itself MUST be fail-closed: no per-call error
        // suppression on Get-VM (which would override $ErrorActionPreference='Stop'
        // and let a non-terminating lookup failure produce 'absent'). It MUST also
        // contain the strict Stop preference AND a catch that maps unknown
        // failures to 'probe-failed:'.
        // Index 3 because Calls[0] is now the LF-D19 pre-create probe (Issue #203).
        var probeScript = exec.Calls[3].Script;

        probeScript.Should().NotMatch("*-ErrorAction*SilentlyContinue*",
            "Get-VM must not be invoked with -ErrorAction SilentlyContinue — that overrides the script-level Stop preference and breaks fail-closed");
        probeScript.Should().NotMatch("*-ErrorAction*Ignore*",
            "Get-VM must not be invoked with -ErrorAction Ignore — that overrides the script-level Stop preference and breaks fail-closed");
        System.Text.RegularExpressions.Regex.IsMatch(probeScript, @"2\s*>\s*\$null")
            .Should().BeFalse("the probe script must not redirect stderr to $null (2>$null) — that hides lookup failures");
        System.Text.RegularExpressions.Regex.IsMatch(probeScript, @"\*\s*>\s*\$null")
            .Should().BeFalse("the probe script must not use *>$null stream redirection — that hides lookup failures");

        System.Text.RegularExpressions.Regex.IsMatch(
                probeScript, @"\$ErrorActionPreference\s*=\s*'Stop'")
            .Should().BeTrue("probe script must set $ErrorActionPreference = 'Stop' so non-terminating errors become terminating");
        System.Text.RegularExpressions.Regex.IsMatch(
                probeScript, @"(?s)try\s*\{.*\}\s*catch\s*\{[^}]*probe-failed:")
            .Should().BeTrue("probe script must contain a try/catch that maps unknown failures to 'probe-failed:<reason>'");
        System.Text.RegularExpressions.Regex.IsMatch(
                probeScript, @"Get-VM[^\r\n|]*-ErrorAction\s+Stop")
            .Should().BeTrue("Get-VM must be invoked with -ErrorAction Stop so lookup failures propagate to the script-level catch");
    }

    /// <summary>
    /// Positive contract: when the probe explicitly emits <c>"absent"</c>, the
    /// host treats it as authoritative-no-residue and does NOT append any
    /// <c>vm:</c> entry (neither bare nor probe-unknown sentinel).
    /// </summary>
    [Fact]
    public async Task ProbeAbsent_DoesNotAppendAnyVmEntry()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue164-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue164-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var exec = new RecordingExecutor
        {
            // Post-registration failure (Start-VM phase) so the residue-probe
            // path is exercised. See companion comment on
            // ProbeFailure_AppendsProbeUnknownSentinel_AndLogsWarning for the
            // VC-DUP-D3 rationale.
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "Start-VM : simulated failure",
                DurationMs = 5,
            }),
            // Rollback PS fails ⇒ triggers probe fallback.
            RollbackHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr = "rollback script failed",
                DurationMs = 5,
            }),
            // Probe authoritatively reports the VM is not registered.
            ProbeHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "absent",
                DurationMs = 2,
            }),
        };

        var manager = BuildManager(exec, options);

        var thrown = await Assert.ThrowsAsync<VmCreateRollbackException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.Rollback.Performed.Should().BeTrue();
        thrown.Rollback.ResidualArtifacts.Should().NotContain(a => a.StartsWith("vm:", StringComparison.Ordinal),
            "an authoritative 'absent' probe result MUST NOT produce any vm: residue entry");

        exec.Calls.Should().HaveCount(4, "LF-D19 probe + primary + rollback + residue-probe");
    }

    // ─── Issue #203 / LF-D18 negative tests — rollback-guard invariant ───

    /// <summary>
    /// Issue #203 / VC-DUP-D3 / LF-D18: when the LF-D19 pre-create probe
    /// detects an existing VM, <c>CreateVmAsync</c> short-circuits with
    /// <see cref="VmAlreadyExistsException"/> and MUST NOT invoke the primary
    /// pipeline NOR the LF-D17 rollback. Concretely:
    /// <list type="bullet">
    /// <item><description>The thrown exception is <c>VmAlreadyExistsException</c>
    /// (not <c>VmCreateRollbackException</c>) — no rollback envelope.</description></item>
    /// <item><description>The executor sees exactly one call (the LF-D19 probe).</description></item>
    /// </list>
    /// This is the invariant that prevents the TC-14 data-loss path: with no
    /// rollback armed, the pre-existing VM and its artifacts cannot be deleted.
    /// </summary>
    [Fact]
    public async Task LfD19ProbeHit_ShortCircuits_AndSkipsRollbackEntirely()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue203-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue203-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        var exec = new RecordingExecutor
        {
            // VM already exists per the LF-D19 probe ⇒ short-circuit.
            LfD19ProbeHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "present",
                DurationMs = 1,
            }),
            // These MUST NOT be invoked.
            PrimaryHandler = (_, _, _) =>
                throw new InvalidOperationException(
                    "Primary pipeline must not be invoked when LF-D19 probe returns 'present'."),
            RollbackHandler = (_, _, _) =>
                throw new InvalidOperationException(
                    "LF-D17 rollback must not be armed when no artifacts were created (LF-D18 invariant)."),
        };

        var manager = BuildManager(exec, options);

        var thrown = await Assert.ThrowsAsync<VmAlreadyExistsException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.VmName.Should().Be(TestVmName);
        thrown.HostId.Should().Be(LocalHostId);
        thrown.Message.Should().Be(
            $"A VM with the name '{TestVmName}' already exists on host '{LocalHostId}'.",
            "VC-DUP-D5 / Constraint #6: the contract message format is pinned");

        // ONLY the LF-D19 probe should have been called. No primary, no rollback.
        exec.Calls.Should().HaveCount(1,
            "VC-DUP-D1 + LF-D18: probe-path failure MUST NOT invoke primary or rollback");
    }

    /// <summary>
    /// Issue #203 / VC-DUP-D4 (residual race): when the LF-D19 probe says
    /// 'absent' but the primary pipeline's own internal probe surfaces an
    /// "already exists" failure (TOCTOU race / parallel client), the host
    /// MUST classify the failure as <c>VM_ALREADY_EXISTS</c> with the
    /// sanitized contract message AND MUST NOT call <c>Remove-VM</c> against
    /// the colliding name (i.e. the rollback script's <c>$ownsVm</c> guard
    /// is <c>$false</c>).
    /// </summary>
    [Fact]
    public async Task ResidualRace_PreservesCollidingVm_AndReturnsSanitizedAlreadyExists()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue203-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue203-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        string? observedRollbackScript = null;

        var exec = new RecordingExecutor
        {
            // LF-D19 probe: 'absent' so the primary pipeline runs.
            LfD19ProbeHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "absent",
                DurationMs = 1,
            }),
            // Primary throws the canonical "already exists" PowerShell text via
            // stderr (mirrors what the script's $existing check throws on a
            // residual race).
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr =
                    "At C:\\Users\\test\\AppData\\Local\\Temp\\hvmcp-create.ps1:14 char:5\n" +
                    $"+     throw \"VM with name '{TestVmName}' already exists\"\n" +
                    "+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n" +
                    "    + CategoryInfo          : OperationStopped: (VM with name '...' already exists:String) [], RuntimeException\n" +
                    "    + FullyQualifiedErrorId : VM with name '" + TestVmName + "' already exists",
                DurationMs = 5,
            }),
            // Rollback handler captures the script content so we can assert the
            // ownership flag is wired correctly. The residual-race path is
            // ownership-empty (created.* all false), so the host should EITHER
            // skip the rollback entirely OR call it with $ownsVm = $false.
            RollbackHandler = (script, _, _) =>
            {
                observedRollbackScript = script;
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = CleanRollbackJson(),
                    DurationMs = 1,
                });
            },
        };

        var manager = BuildManager(exec, options);

        var thrown = await Assert.ThrowsAsync<VmAlreadyExistsException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        thrown.Message.Should().Be(
            $"A VM with the name '{TestVmName}' already exists on host '{LocalHostId}'.",
            "VC-DUP-D5: the wire envelope MUST carry the contract message, never raw PS throw text");
        thrown.Message.Should().NotContain("char:",
            "VC-DUP-D5: PS positional tokens must not leak onto the wire");
        thrown.Message.Should().NotContain("RuntimeException",
            "VC-DUP-D5: PS stack tail must not leak onto the wire");
        thrown.Message.Should().NotContain("FullyQualifiedErrorId",
            "VC-DUP-D5: PS error-id tokens must not leak onto the wire");

        // If the rollback was invoked at all, the script MUST carry $ownsVm =
        // $false so Remove-VM is never called against the colliding name.
        if (observedRollbackScript is not null)
        {
            observedRollbackScript.Should().Contain("$ownsVm = $false",
                "VC-DUP-D3 / LF-D18: residual-race rollback MUST NOT call Remove-VM (data-loss guard)");
        }
    }

    /// <summary>
    /// Issue #203 / VC-DUP-D3 / VC-DUP-D4 / LF-D18 — IA-Gate 6 fix:
    /// Post-<c>New-VHD</c> <c>New-VM</c> collision (residual race where the
    /// LF-D19 probe + the script's internal <c>Get-VM</c> probe both said
    /// 'absent', <c>New-VHD</c> succeeded, then a parallel client registered
    /// the VM before our <c>New-VM</c> ran). The host MUST:
    ///
    /// 1. Surface <see cref="VmAlreadyExistsException"/> with the sanitized
    ///    contract message (NOT raw PowerShell text).
    /// 2. Invoke the rollback helper (the just-created VHDX + per-VM directory
    ///    are leaked artifacts that MUST be cleaned up).
    /// 3. Carry <c>$ownsVm = $false</c> on the rollback script so
    ///    <c>Remove-VM</c> NEVER touches the colliding (foreign-owned) VM —
    ///    data-loss-prevention invariant from VC-DUP-D3 / LF-D18.
    /// 4. Carry <c>$ownsVhdx = $true</c> and <c>$ownsVmDir = $true</c> so the
    ///    owned VHDX and (if-empty) directory ARE cleaned.
    ///
    /// Pre-fix behaviour treated every name-collision branch as
    /// "owns nothing" and skipped rollback entirely, leaking the VHDX + dir.
    /// </summary>
    [Fact]
    public async Task ResidualRace_PostNewVhdCollision_CleansOwnedVhdxAndDir_PreservesCollidingVm()
    {
        var baseVhdx = Path.Combine(Path.GetTempPath(), "issue203-post-base-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var storage = Path.Combine(Path.GetTempPath(), "issue203-post-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        string? observedRollbackScript = null;

        var exec = new RecordingExecutor
        {
            // LF-D19 probe: 'absent' so the primary pipeline runs to New-VHD.
            LfD19ProbeHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 0,
                Stdout = "absent",
                DurationMs = 1,
            }),
            // Primary throws a canonical New-VM "already exists" error record:
            // New-VHD has already succeeded by the time New-VM is reached, so
            // stderr carries the cmdlet name "New-VM" plus "already exists".
            // This is the post-mutator residual-race case.
            PrimaryHandler = (_, _, _) => Task.FromResult(new PowerShellResult
            {
                ExitCode = 1,
                Stderr =
                    "New-VM : Failed to create virtual machine.\n" +
                    $"A virtual machine with the name '{TestVmName}' already exists.\n" +
                    "At C:\\Users\\test\\AppData\\Local\\Temp\\hvmcp-create.ps1:42 char:5\n" +
                    $"+     New-VM -Name '{TestVmName}' -ComputerName localhost ...\n" +
                    "+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n" +
                    "    + CategoryInfo          : InvalidArgument: (:) [New-VM], VirtualizationException\n" +
                    "    + FullyQualifiedErrorId : InvalidParameter,Microsoft.HyperV.PowerShell.Commands.NewVM",
                DurationMs = 5,
            }),
            RollbackHandler = (script, _, _) =>
            {
                observedRollbackScript = script;
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = CleanRollbackJson(),
                    DurationMs = 1,
                });
            },
        };

        var manager = BuildManager(exec, options);

        var thrown = await Assert.ThrowsAsync<VmAlreadyExistsException>(() =>
            manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

        // (1) Sanitized contract message — no raw PS leakage.
        thrown.Message.Should().Be(
            $"A VM with the name '{TestVmName}' already exists on host '{LocalHostId}'.",
            "VC-DUP-D5: the wire envelope MUST carry the contract message, never raw PS error-record text");
        thrown.Message.Should().NotContain("New-VM",
            "VC-DUP-D5: PS cmdlet tokens must not leak onto the wire");
        thrown.Message.Should().NotContain("char:",
            "VC-DUP-D5: PS positional tokens must not leak onto the wire");
        thrown.Message.Should().NotContain("FullyQualifiedErrorId",
            "VC-DUP-D5: PS error-id tokens must not leak onto the wire");

        // (2) Rollback MUST have been invoked — owned VHDX + dir cannot be left
        // dangling. The pre-fix bug skipped rollback entirely on this branch.
        observedRollbackScript.Should().NotBeNull(
            "VC-DUP-D3 / IA-Gate 6 fix: post-New-VHD collision owns artifacts ⇒ rollback is required");

        // (3) Remove-VM MUST be gated off — the colliding VM belongs to a
        // different invocation; touching it would be data loss.
        observedRollbackScript!.Should().Contain("$ownsVm = $false",
            "VC-DUP-D3 / LF-D18: post-New-VHD collision MUST NOT call Remove-VM (data-loss guard)");

        // (4) Owned VHDX + dir MUST be cleaned.
        observedRollbackScript.Should().Contain("$ownsVhdx = $true",
            "VC-DUP-D3 / IA-Gate 6 fix: this call created the differencing VHDX ⇒ rollback owns it");
        observedRollbackScript.Should().Contain("$ownsVmDir = $true",
            "VC-DUP-D3 / IA-Gate 6 fix: this call created the per-VM directory ⇒ rollback owns it");
    }

    // ─── IA-Gate 6 fix: primary-success + post-success host-side failure ───

    /// <summary>
    /// IA-Gate 6 fix on PR #210: the primary PowerShell script ran to
    /// completion successfully (New-VHD + New-VM + Set-VM + optional Start-VM
    /// all returned exit 0 with no stderr) but a subsequent HOST-SIDE
    /// post-success check (here: the BASE_IMAGE_MUTATED post-hash recompute
    /// that runs after the script returns — see <c>HyperVManager.CreateVmAsync</c>
    /// lines ~371-433) threw and set <c>primaryFailure</c>. In this case
    /// THIS invocation OWNS the freshly-registered VM and the rollback MUST
    /// run <c>Remove-VM</c>. Pre-fix behaviour (PR #210 commit 0e95455)
    /// conservatively kept <c>$ownsVm = $false</c> and leaked the VM.
    ///
    /// We reproduce the scenario end-to-end by wiring a real
    /// <see cref="BaseImageHashCache"/> against an actual base file on disk:
    /// pre-hash the file, simulate primary-script success that
    /// preserves-stat-mutates the file, then let the host's post-success
    /// recompute observe the mismatch and escalate as
    /// <c>BASE_IMAGE_MUTATED</c>.
    /// </summary>
    [Fact]
    public async Task PrimarySuccess_PostSuccessHostFailure_RollbackOwnsVm_AndRemovesIt()
    {
        // Create a real on-disk synthetic "base VHDX" so the cache can hash it.
        var baseDir = Path.Combine(Path.GetTempPath(), "iagate6-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var baseVhdx = Path.Combine(baseDir, "synthetic-base.vhdx");
        var originalBytes = new byte[64 * 1024];
        new Random(2103).NextBytes(originalBytes);
        File.WriteAllBytes(baseVhdx, originalBytes);
        var originalMtime = File.GetLastWriteTimeUtc(baseVhdx);

        var storage = Path.Combine(Path.GetTempPath(), "iagate6-storage-" + Guid.NewGuid().ToString("N"));
        var options = BuildOptions(baseVhdx, storage);

        // Real cache so the host actually executes the post-success
        // ForceRecomputeAsync path (which is what fires the BASE_IMAGE_MUTATED
        // guard after primary script success).
        using var cache = new BaseImageHashCache(NullLogger<BaseImageHashCache>.Instance);

        // Pre-populate the cache so HyperVManager's preHash GetOrComputeAsync
        // returns the original content's hash (warm-path).
        _ = await cache.GetOrComputeAsync(baseVhdx, CancellationToken.None);

        string? observedRollbackScript = null;

        var exec = new RecordingExecutor
        {
            // Primary script "succeeds" (exit 0, no stderr) but during its
            // execution we mutate the base file in a stat-preserving way so
            // the host's subsequent ForceRecomputeAsync sees different bytes
            // and escalates as BASE_IMAGE_MUTATED. This emulates the
            // real-world race the post-hash guard is designed to catch:
            // primary completed registration, then a host-side check fired.
            PrimaryHandler = (_, _, _) =>
            {
                var mutated = new byte[originalBytes.Length];
                new Random(9999).NextBytes(mutated);
                // Preserve length (already same), then restore mtime.
                var fi = new FileInfo(baseVhdx);
                var wasReadOnly = fi.IsReadOnly;
                if (wasReadOnly) fi.IsReadOnly = false;
                File.WriteAllBytes(baseVhdx, mutated);
                File.SetLastWriteTimeUtc(baseVhdx, originalMtime);
                if (wasReadOnly) new FileInfo(baseVhdx).IsReadOnly = true;

                // Emulate a clean primary-script success: exit 0, no stderr,
                // and a minimal VmInfo-shaped stdout so ParseSingleVmInfo
                // would succeed if we reached the success branch (we won't,
                // because the post-hash check throws first).
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = "{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"Name\":\""
                             + TestVmName + "\",\"State\":\"Running\",\"ProcessorCount\":2,\"MemoryMB\":4096,\"UptimeSeconds\":0}",
                    Stderr = string.Empty,
                    DurationMs = 5,
                });
            },
            RollbackHandler = (script, _, _) =>
            {
                observedRollbackScript = script;
                return Task.FromResult(new PowerShellResult
                {
                    ExitCode = 0,
                    Stdout = CleanRollbackJson(),
                    DurationMs = 1,
                });
            },
        };

        var manager = BuildManager(exec, options, baseImageHashCache: cache);

        try
        {
            var thrown = await Assert.ThrowsAsync<VmCreateRollbackException>(() =>
                manager.CreateVmAsync(LocalHostId, TestVmName, baseVhdxPath: baseVhdx,
                    cpuCount: 2, memoryMB: 4096, autoStart: false, ct: CancellationToken.None));

            thrown.ErrorCode.Should().Be(ErrorCodes.CommandFailed,
                "BASE_IMAGE_MUTATED escalates as an InvalidOperationException ⇒ COMMAND_FAILED envelope");
            thrown.Rollback.Performed.Should().BeTrue(
                "post-success host-side failure MUST trigger the rollback path");

            observedRollbackScript.Should().NotBeNull(
                "the rollback PowerShell script MUST have been invoked");

            // The critical assertion: primary script success → THIS invocation
            // registered the VM → rollback MUST set $ownsVm = $true so
            // Remove-VM cleans the leaked VM. Pre-fix behaviour kept
            // $ownsVm = $false and leaked the registered VM.
            observedRollbackScript!.Should().Contain("$ownsVm = $true",
                "IA-Gate 6 fix: primary-script success + post-success host failure ⇒ rollback OWNS the VM registration");
            observedRollbackScript.Should().Contain("$ownsVhdx = $true",
                "IA-Gate 6 fix: primary-script success ⇒ the differencing VHDX is owned");
            observedRollbackScript.Should().Contain("$ownsVmDir = $true",
                "IA-Gate 6 fix: primary-script success ⇒ the per-VM directory is owned");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* swallow */ }
            try { if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true); } catch { /* swallow */ }
        }
    }
}
