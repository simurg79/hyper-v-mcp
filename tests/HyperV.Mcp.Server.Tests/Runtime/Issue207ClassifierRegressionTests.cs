using System.Text.RegularExpressions;
using FluentAssertions;
using HyperV.Mcp.Server.Configuration;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #207 safety fix — the three-way cleanup-orphans classifier
/// (orphan-candidate / needs-attention / ignored) and its boundary-anchored
/// role/created regexes. Acceptance authority:
/// <c>internal documentation</c>
/// AC-1..AC-12 and <c>cleanup-orphans-design.md</c> CO-D2..CO-D6.
///
/// <para><b>Why this file exists — the TC-W14 / TC-L13 donor bug.</b> The pre-fix
/// classifier keyed "orphan" on the legacy <c>hyper-v-mcp:created=</c> marker
/// with an age check ALONE. A persistent donor VM (created before the
/// <c>role=ephemeral</c> marker existed, or a user-tagged VM) whose Notes carry
/// only the legacy created marker was mis-classified as a destroyable orphan.
/// The ratified contract requires a valid <c>role=ephemeral</c> segment AND a
/// parseable, aged <c>created=</c> before a row is ever a destroy candidate;
/// everything else is <c>needs-attention</c> (report-only, fail-closed).</para>
///
/// <para><b>Test layering.</b> The classification predicate runs inside the
/// PowerShell host (composed by <see cref="HyperVManager.CleanupOrphansAsync"/>),
/// which the .NET test host cannot execute against live Hyper-V. We therefore:
/// <list type="number">
///   <item><b>Predicate side</b> — evaluate the EXACT shipping regexes
///     (<see cref="EphemeralRolePattern"/> / <see cref="CreatedCapturePattern"/>)
///     in .NET via <see cref="ClassifyDonorNotes"/>, proving the classification
///     of real donor/orphan/spoof Notes strings. These are genuine behavioral
///     tests, not string matches. To prevent drift, each predicate test is
///     paired with <see cref="ShippingScript_ContainsBoundaryAnchoredPatterns"/>,
///     which binds the replica patterns to the real composed script string.</item>
///   <item><b>Mapping / parity side</b> — mock <see cref="IPowerShellExecutor"/>
///     with canned JSON to assert the C# value-space mapping and dry-run vs
///     destructive parity of the destroy gate.</item>
/// </list></para>
/// </summary>
[Trait("Category", "Runtime")]
public class Issue207ClassifierRegressionTests
{
    private const string LocalHostId = "local";

    // ─── Exact shipping predicate patterns (HyperVManager.cs ~2171 / ~2175) ──
    // These MUST stay byte-identical to the regexes embedded in the composed
    // cleanup script. ShippingScript_ContainsBoundaryAnchoredPatterns asserts
    // that binding so the replica cannot silently drift from the source.
    private const string EphemeralRolePattern = @"(?:^|[\s;])role=ephemeral(?:$|[\s;])";

    private static readonly Regex RoleRegex = new(EphemeralRolePattern, RegexOptions.CultureInvariant);
    private static readonly Regex CreatedRegex =
        new(@"(?:^|[\s;])hyper-v-mcp:created=([^\s;]+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Faithful .NET replica of the PowerShell classifier's per-VM predicate
    /// (HyperVManager.cs lines ~2166-2191). Given a VM's Notes and the age
    /// cutoff, returns the ratified reason: <c>"orphan-candidate"</c>,
    /// <c>"needs-attention"</c>, or <c>null</c> (live/ignored, not returned).
    /// This replica models the per-VM predicate only; the shipping script's
    /// upstream <c>hyper-v-mcp:</c> tag filter runs before this stage, so a note
    /// reaching here that lacks a valid ephemeral marker fail-closes to
    /// <c>needs-attention</c> (never a destroy candidate).
    /// Power state is deliberately NOT an input — the contract is state-neutral
    /// (CO-D3, #93 power-state-neutrality intent preserved).
    /// </summary>
    private static string? ClassifyDonorNotes(string? notes, DateTimeOffset cutoff)
    {
        notes ??= string.Empty;
        var isEphemeral = RoleRegex.IsMatch(notes);

        DateTimeOffset? createdAt = null;
        var createdMatch = CreatedRegex.Match(notes);
        if (createdMatch.Success)
        {
            if (DateTimeOffset.TryParse(createdMatch.Groups[1].Value, out var parsed))
            {
                createdAt = parsed;
            }
        }

        if (isEphemeral && createdAt is not null && createdAt < cutoff)
        {
            return "orphan-candidate";
        }
        if (isEphemeral && createdAt is not null)
        {
            return null; // ephemeral + within cutoff -> live, skipped
        }
        return "needs-attention"; // fail-closed
    }

    private static DateTimeOffset Cutoff => DateTimeOffset.UtcNow.AddHours(-24);
    private static string AgedIso => DateTimeOffset.UtcNow.AddHours(-48).ToString("o");
    private static string FreshIso => DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o");

    // ══════════════════════════════════════════════════════════════════════
    //  PREDICATE-SIDE TESTS (genuine classification behavior)
    // ══════════════════════════════════════════════════════════════════════

    // ─── AC: TC-W14/TC-L13 — Running persistent donor, legacy marker only ───

    /// <summary>
    /// Case 1 (TC-W14/TC-L13): a persistent donor whose Notes carry ONLY the
    /// legacy <c>hyper-v-mcp:created=</c> marker (aged) and NO valid
    /// <c>role=ephemeral</c> must classify as <c>needs-attention</c> — never a
    /// destroy candidate. Power state does not change this (state-neutral):
    /// Running and Off donors yield the identical verdict.
    /// </summary>
    [Fact]
    public void LegacyMarkerOnlyDonor_AgedNoEphemeralRole_IsNeedsAttention_StateNeutral()
    {
        // Notes a real pre-role-marker donor would carry (no role= segment).
        var donorNotes = $"hyper-v-mcp:created={AgedIso}";

        var reason = ClassifyDonorNotes(donorNotes, Cutoff);

        reason.Should().Be("needs-attention",
            "an aged donor with the legacy created marker but NO role=ephemeral must " +
            "be report-only, never an orphan-candidate (TC-W14/TC-L13, CO-D3).");
        reason.Should().NotBe("orphan-candidate", "the donor must never be deletable.");

        // Power-state neutrality: the classifier takes no state input, so the
        // Running-donor and Off-donor cases are the SAME code path and verdict.
        // We assert the invariant explicitly for both conceptual states.
        ClassifyDonorNotes(donorNotes, Cutoff)
            .Should().Be(reason, "classification is independent of VM power state (#93 intent).");
    }

    /// <summary>
    /// Case 2: an Off persistent donor (same legacy-only Notes) is likewise
    /// excluded — confirming the contract does NOT delete based on power state.
    /// Modeled identically to Case 1 because state is not a predicate input;
    /// the parameterization documents the intent across both states.
    /// </summary>
    [Theory]
    [InlineData("Running")]
    [InlineData("Off")]
    public void PersistentDonor_AnyPowerState_LegacyOnly_IsNeedsAttention(string conceptualState)
    {
        var donorNotes = $"hyper-v-mcp:created={AgedIso}";

        var reason = ClassifyDonorNotes(donorNotes, Cutoff);

        reason.Should().Be("needs-attention",
            $"a {conceptualState} donor without role=ephemeral is report-only regardless of state.");
    }

    // ─── AC: valid MCP-owned ephemeral orphan is INCLUDED ───────────────────

    /// <summary>
    /// Case 3: a valid MCP-owned ephemeral VM (<c>role=ephemeral</c> present AND
    /// a parseable <c>created=</c> aged past the 24h cutoff) remains an
    /// <c>orphan-candidate</c> — the true-positive the tool must still act on.
    /// Covers both marker orderings the code emits (vm_create and iso-install).
    /// </summary>
    [Theory]
    [InlineData("created-first")]
    [InlineData("iso-install-multisegment")]
    public void ValidEphemeralAgedVm_IsOrphanCandidate(string shape)
    {
        var notes = shape == "created-first"
            ? $"hyper-v-mcp:created={AgedIso};role=ephemeral"
            // vm_os_install emits a 3-segment note with type=iso-install between.
            : $"hyper-v-mcp:created={AgedIso};type=iso-install;role=ephemeral";

        var reason = ClassifyDonorNotes(notes, Cutoff);

        reason.Should().Be("orphan-candidate",
            "a genuinely MCP-owned ephemeral VM aged past cutoff must remain deletable (CO-D6).");
    }

    /// <summary>
    /// A valid ephemeral VM still WITHIN the cutoff is 'live' — not returned at
    /// all (reason null). Confirms the classifier does not over-report fresh
    /// ephemeral VMs.
    /// </summary>
    [Fact]
    public void ValidEphemeralFreshVm_IsLive_NotReturned()
    {
        var notes = $"hyper-v-mcp:created={FreshIso};role=ephemeral";

        ClassifyDonorNotes(notes, Cutoff)
            .Should().BeNull("ephemeral + within cutoff is live and must not be returned.");
    }

    // ─── AC: fail-closed on missing/malformed metadata ──────────────────────

    /// <summary>
    /// Case 4: every malformed/missing-metadata shape must fail closed to
    /// <c>needs-attention</c> and NEVER become an <c>orphan-candidate</c>:
    /// missing created=, unparseable timestamp, missing role, and role/created
    /// spoof strings. This is the core safety predicate.
    /// </summary>
    [Theory]
    // missing created= entirely (role present)
    [InlineData("hyper-v-mcp:tag-only;role=ephemeral")]
    // unparseable timestamp (role present)
    [InlineData("hyper-v-mcp:created=not-a-date;role=ephemeral")]
    // empty created value (role present)
    [InlineData("hyper-v-mcp:created=;role=ephemeral")]
    // missing role (aged created present) — the donor case
    [InlineData("hyper-v-mcp:created=AGED")]
    // role-spoof: 'notrole=ephemeral' must NOT satisfy the boundary-anchored role
    [InlineData("hyper-v-mcp:created=AGED;notrole=ephemeral")]
    // role-spoof: 'xrole=ephemeral'
    [InlineData("hyper-v-mcp:created=AGED;xrole=ephemeral")]
    // role value spoof: 'role=ephemeralish' (right-boundary must reject)
    [InlineData("hyper-v-mcp:created=AGED;role=ephemeralish")]
    // created-spoof: prefixed key 'xhyper-v-mcp:created=' must not be captured as ours,
    // and the only real role here is absent -> needs-attention
    [InlineData("xhyper-v-mcp:created=AGED;role=ephemeral")]
    public void MalformedOrSpoofedMetadata_FailsClosed_NeedsAttention(string notesTemplate)
    {
        var notes = notesTemplate.Replace("AGED", AgedIso, StringComparison.Ordinal);

        var reason = ClassifyDonorNotes(notes, Cutoff);

        reason.Should().NotBe("orphan-candidate",
            $"malformed/spoofed metadata '{notes}' must never be a destroy candidate (fail-closed).");
        reason.Should().Be("needs-attention",
            "owned-but-unclean rows are report-only.");
    }

    // ─── AC: boundary-anchoring proof (the anti-drift core) ─────────────────

    /// <summary>
    /// Case 6: direct boundary-anchoring proof on the role regex. The spoof
    /// segments must NOT match; only a true delimited <c>role=ephemeral</c>
    /// segment matches. This is the exact predicate whose UNANCHORED pre-fix
    /// form produced the false positive.
    /// </summary>
    [Theory]
    [InlineData("hyper-v-mcp:created=x;role=ephemeral", true)]
    [InlineData("role=ephemeral", true)]
    [InlineData("a;role=ephemeral;b", true)]
    [InlineData("notrole=ephemeral", false)]
    [InlineData("xrole=ephemeral", false)]
    [InlineData("role=ephemeralish", false)]
    [InlineData("myrole=ephemeralx", false)]
    public void RoleRegex_IsBoundaryAnchored(string notes, bool shouldMatch)
    {
        RoleRegex.IsMatch(notes).Should().Be(shouldMatch,
            $"boundary-anchored role predicate must {(shouldMatch ? "accept" : "reject")} '{notes}'.");
    }

    /// <summary>
    /// Boundary-anchoring proof on the created-capture regex: a prefixed key
    /// (<c>xhyper-v-mcp:created=</c>) must NOT be captured as our marker, and a
    /// trailing segment must not glue onto the captured timestamp value.
    /// </summary>
    [Fact]
    public void CreatedRegex_IsBoundaryAnchored_AndValueDelimited()
    {
        // Prefixed spoof key: left boundary rejects the leading 'x...'.
        CreatedRegex.IsMatch("xhyper-v-mcp:created=2020-01-01T00:00:00Z")
            .Should().BeFalse("a prefixed key must not be captured as our created marker.");

        // Real marker with a trailing segment: capture must stop at ';'.
        var m = CreatedRegex.Match("hyper-v-mcp:created=2020-01-01T00:00:00Z;role=ephemeral");
        m.Success.Should().BeTrue();
        m.Groups[1].Value.Should().Be("2020-01-01T00:00:00Z",
            "the captured value must be delimited at ';' so no adjacent segment glues on.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ANTI-DRIFT BINDING — replica patterns == shipping script patterns
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Binds the in-test regex replicas to the real composed script string
    /// emitted by <see cref="HyperVManager.CleanupOrphansAsync"/>. If the
    /// shipping predicate is ever changed (e.g. anchoring removed), this test
    /// fails, invalidating the predicate-side tests' authority to speak for the
    /// source. Also asserts the ratified value-space and the destroy gate.
    /// </summary>
    [Fact]
    public async Task ShippingScript_ContainsBoundaryAnchoredPatterns()
    {
        var (manager, exec) = BuildManager();
        string capturedScript = string.Empty;
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<string, int, CancellationToken, bool>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(SuccessResult("[]"));

        _ = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        capturedScript.Should().Contain(@"role=ephemeral(?:$|[\s;])",
            "the shipping role predicate must remain right-boundary anchored.");
        capturedScript.Should().Contain(@"(?:^|[\s;])role=ephemeral",
            "the shipping role predicate must remain left-boundary anchored.");
        capturedScript.Should().Contain(@"(?:^|[\s;])hyper-v-mcp:created=([^\s;]+)",
            "the shipping created-capture must remain left-anchored and ';'-delimited.");
        capturedScript.Should().Contain("$reason = 'orphan-candidate'",
            "ratified value-space: orphan-candidate.");
        capturedScript.Should().Contain("$reason = 'needs-attention'",
            "ratified value-space: needs-attention.");
        capturedScript.Should().Contain("$reason -eq 'orphan-candidate' -and -not $dryRun",
            "destroy gate must be restricted to orphan-candidate under -not $dryRun (CO-D6).");
        capturedScript.Should().NotContain("State -eq 'Off'",
            "the classifier must remain power-state-neutral (#93 intent).");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  MAPPING / PARITY-SIDE TESTS (mocked executor)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Case 5 (dry-run/destructive parity): the classification set the PS
    /// predicate emits is identical across dryRun:true and dryRun:false — the
    /// only difference is whether orphan-candidate rows are destroyed. We assert
    /// the C# mapping surfaces the SAME candidate set (same ids/reasons) for
    /// both flags, given identical predicate output. This is the parity the
    /// design requires: dry-run reports exactly what destructive would delete.
    /// </summary>
    [Fact]
    public async Task DryRunAndDestructive_YieldSameCandidateSet()
    {
        const string payload = @"[
  { ""Id"": ""aaa"", ""Name"": ""orphan-1"", ""State"": 2, ""ProcessorCount"": 2, ""MemoryMB"": 2048, ""UptimeSeconds"": 0, ""Reason"": ""orphan-candidate"" },
  { ""Id"": ""bbb"", ""Name"": ""donor-1"",  ""State"": 2, ""ProcessorCount"": 2, ""MemoryMB"": 2048, ""UptimeSeconds"": 0, ""Reason"": ""needs-attention"" }
]";

        var (managerDry, execDry) = BuildManager();
        execDry.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(payload));
        var dryRows = await managerDry.CleanupOrphansAsync(LocalHostId, dryRun: true);

        var (managerDestroy, execDestroy) = BuildManager();
        execDestroy.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(payload));
        var destroyRows = await managerDestroy.CleanupOrphansAsync(LocalHostId, dryRun: false);

        // Parity: same candidate set (ids + reasons), independent of dryRun.
        dryRows.Select(r => (r.VmId, r.Reason))
            .Should().BeEquivalentTo(destroyRows.Select(r => (r.VmId, r.Reason)),
                "the reported candidate set must be identical in dry-run and destructive mode.");

        // And specifically: exactly one destroy-eligible orphan-candidate in both.
        dryRows.Count(r => r.Reason == "orphan-candidate").Should().Be(1);
        destroyRows.Count(r => r.Reason == "orphan-candidate").Should().Be(1);
    }

    /// <summary>
    /// Value-space mapping: the C# layer faithfully surfaces the new reason
    /// value-space (<c>orphan-candidate</c> / <c>needs-attention</c>) from the
    /// predicate output, replacing the retired <c>orphan</c>/<c>unknown-age</c>.
    /// </summary>
    [Theory]
    [InlineData("orphan-candidate")]
    [InlineData("needs-attention")]
    public async Task Mapping_SurfacesRatifiedReasonValueSpace(string reason)
    {
        var (manager, exec) = BuildManager();
        var payload = $@"[
  {{ ""Id"": ""id1"", ""Name"": ""vm1"", ""State"": 2, ""ProcessorCount"": 2, ""MemoryMB"": 2048, ""UptimeSeconds"": 0, ""Reason"": ""{reason}"" }}
]";
        exec.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(SuccessResult(payload));

        var result = await manager.CleanupOrphansAsync(LocalHostId, dryRun: true);

        result.Should().HaveCount(1);
        result[0].Reason.Should().Be(reason);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static (HyperVManager manager, Mock<IPowerShellExecutor> exec) BuildManager()
    {
        var exec = new Mock<IPowerShellExecutor>();
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
        var resolver = new HostResolver(options);
        var manager = new HyperVManager(
            exec.Object, resolver, options, NullLogger<HyperVManager>.Instance, new TestIsoInspector());
        return (manager, exec);
    }

    private static PowerShellResult SuccessResult(string stdout) => new()
    {
        ExitCode = 0,
        Stdout = stdout,
        Stderr = string.Empty,
        TimedOut = false,
        Cancelled = false,
        DurationMs = 100,
    };
}
