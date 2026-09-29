using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;
using Xunit.Abstractions;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #294 — acceptance-criteria guards for the session-open error envelope contract.
///
/// <para>These bind to the spec's provenance split. FR-5a (captured path) is ABSOLUTE: an
/// authoritative cause fitting the 1,000-character statement allowance MUST be delivered verbatim
/// and in full, position-independently, subordinate only to the 4,000-character envelope bound.
/// FR-5b (recovered path) is BEST EFFORT ONLY, and L-1 documents that the decisive statement may be
/// omitted entirely — so the AC-5c guard below asserts the spec's comparative outcome and
/// deliberately NOTHING stronger.</para>
///
/// Spec:   internal documentation — AC-3b, AC-5b, AC-5c, AC-9b.
/// Design: internal documentation — SOE-D3, SOE-D11, SOE-D12, SOE-D20.
/// </summary>
[Trait("Category", "Runtime")]
// StderrSpillHelper.TempPathProvider is a process-global static seam: these tests redirect it, so
// they MUST NOT run beside another class that also redirects it, or the spill records interleave.
[Collection("EnvVarMutating")]
public class Issue294EnvelopeContractTests
{
    private readonly ITestOutputHelper _output;

    public Issue294EnvelopeContractTests(ITestOutputHelper output) => _output = output;

    private const int EnvelopeBound = 4000;
    private const string VmId = "dddddddd-1111-2222-3333-444444444444";

    private static string Noise(int length, string token = "irrelevant background diagnostic chatter. ")
    {
        var builder = new StringBuilder(length + token.Length);
        while (builder.Length < length)
        {
            builder.Append(token);
        }
        return builder.ToString(0, length);
    }

    private static McpToolResponse Map(SessionOpenFailedException ex) => new ErrorMapper().MapException(ex);

    // ── Guard 3: AC-5b — captured path, verbatim and in full, at three placements ──

    public static TheoryData<string> Placements => new() { "start", "interior", "end" };

    /// <summary>
    /// AC-5b / FR-5a. The three cases differ ONLY in where the decisive phrase sits inside an
    /// otherwise identical surrounding text. Every placement lies outside any retained leading or
    /// trailing region, so head-only, tail-only and head+tail truncation all fail by construction.
    /// </summary>
    [Theory]
    [MemberData(nameof(Placements))]
    public void AC5b_CapturedDecisiveCause_IsDeliveredVerbatimAndInFull_AtAnyPlacement(string placement)
    {
        const string decisive =
            "The guest refused the Hyper-V socket negotiation because the guest service " +
            "'Hyper-V Guest Service Interface' is not running inside the virtual machine.";

        var reported = placement switch
        {
            "start" => decisive + " " + Noise(8_400),
            "interior" => Noise(4_200) + " " + decisive + " " + Noise(4_200),
            _ => Noise(8_400) + " " + decisive,
        };

        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: $"Failed to create PSSession 'HvMcp-session': {reported}",
            decisiveCause: decisive,
            spillSummary: "Spilled=C:\\Temp\\hypervmcp-stderr-x.log (9000 bytes)"));

        _output.WriteLine($"[{placement}] envelope ({response.Error!.Length} chars): {response.Error}");

        response.Error.Should().Contain(decisive,
            $"FR-5a is ABSOLUTE: a captured authoritative cause fitting the 1,000-character " +
            $"allowance MUST reach the caller verbatim and in full, wherever it sits in the " +
            $"aggregate text (placement: {placement}). Position-dependent selection is non-conforming.");
        response.Error.Length.Should().BeLessThanOrEqualTo(EnvelopeBound,
            "FR-7: the envelope bound is unconditional and is the only thing FR-5a is subordinate to.");
    }

    /// <summary>
    /// FR-5a covers the whole statement, including leading and trailing whitespace: trimming a
    /// captured value would forfeit verbatim delivery.
    /// </summary>
    [Fact]
    public void AC5b_CapturedCause_AtTheStatementAllowanceLimit_SurvivesWhole()
    {
        var decisive = "GUEST-REFUSED: " + Noise(1000 - 15, "socket negotiation refused. ");
        decisive.Length.Should().Be(1000, "this pins the exact statement allowance boundary");

        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: "Failed: " + Noise(6_000) + decisive + Noise(6_000),
            decisiveCause: decisive));

        response.Error.Should().Contain(decisive,
            "FR-5a: a cause of exactly the allowance length still fits and MUST be delivered in full.");
        response.Error!.Length.Should().BeLessThanOrEqualTo(EnvelopeBound);
    }

    // ── Guard 4: AC-5c — recovered path, comparative outcome only (L-1 bounded) ──

    /// <summary>
    /// AC-5c corpus. Five reported texts with NO authoritative cause, each interleaving explicit
    /// failure-condition content with framing and unrelated noise.
    ///
    /// <para>The assertion is deliberately the spec's comparative outcome and no more: bounded,
    /// carries an omission indication, and is NOT composed solely of a leading region, solely of a
    /// trailing region, or of a leading-plus-trailing region. Asserting that the decisive statement
    /// always surfaces would contradict L-1, which states it may be omitted entirely.</para>
    /// </summary>
    [Theory]
    [InlineData("credential")]
    [InlineData("socket")]
    [InlineData("notrunning")]
    [InlineData("timeout")]
    [InlineData("transport")]
    public void AC5c_RecoveredPath_IsBounded_MarksOmission_AndIsNotAPositionalCut(string entry)
    {
        var condition = entry switch
        {
            "credential" => "The user name or password is incorrect for the guest.",
            "socket" => "The Hyper-V socket target process is not listening.",
            "notrunning" => "The virtual machine is not in a running state.",
            "timeout" => "The operation timed out while waiting for the guest to respond.",
            _ => "The PSSession transport connection was closed by the remote endpoint.",
        };

        // Condition content sits in the interior, surrounded by framing and unrelated noise, so a
        // positional strategy would drop it and a salience-ranked one may retain it.
        var reported =
            "PSDirect bootstrap diagnostic follows. " + Noise(5_000) +
            condition + " " +
            Noise(5_000, "unrelated module load chatter and version banners. ");

        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: $"Failed to create PSSession 'HvMcp-session': {reported}",
            decisiveCause: null,
            spillSummary: "Spilled=C:\\Temp\\hypervmcp-stderr-y.log (10240 bytes)"));

        var error = response.Error!;
        _output.WriteLine($"[{entry}] recovered envelope ({error.Length} chars): {error}");

        error.Length.Should().BeLessThanOrEqualTo(EnvelopeBound, "FR-7 applies unconditionally.");
        error.Should().MatchRegex("omitted|truncated|spill|Spilled",
            "AC-5c/FR-6: a shortened envelope MUST carry an omission indication.");

        // Not a positional cut: the envelope must not be reproducible as a leading region, a
        // trailing region, or a leading-plus-trailing concatenation of the reported text.
        reported.Should().NotStartWith(error, "the envelope must not be solely a leading region");
        reported.Should().NotEndWith(error, "the envelope must not be solely a trailing region");
    }

    /// <summary>
    /// AC-5c's structural half, stated once rather than per corpus entry: the envelope is composed,
    /// not cut. It names the operation, the VM and the classified reason — content that appears
    /// nowhere in the reported text, so no positional slice of that text could produce it.
    /// </summary>
    [Fact]
    public void AC5c_RecoveredPath_EnvelopeIsComposed_NotASliceOfReportedText()
    {
        var reported = Noise(12_000, "opaque diagnostic filler with no statement structure ");

        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: $"Failed to create PSSession 'HvMcp-session': {reported}",
            decisiveCause: null,
            spillSummary: "Spilled=C:\\Temp\\hypervmcp-stderr-z.log (12000 bytes)"));

        var error = response.Error!;
        error.Length.Should().BeLessThanOrEqualTo(EnvelopeBound);
        error.Should().Contain(VmId, "FR-4: the envelope names the target VM");
        error.Should().Contain(ErrorCodes.SessionFailed, "FR-4: the envelope names the classified reason");
        reported.Should().NotContain(error,
            "AC-5c: a composed envelope cannot occur as a contiguous region of the reported text");
    }

    // ── Guard 4b: FR-4 framing is unconditional, not length-dependent ─────────

    /// <summary>
    /// PR #304 regression guard. The framing core used to be applied only when the reported cause
    /// exceeded the 4,000-character bound, so short transport texts — the common case — reached
    /// callers unattributed and the envelope's shape depended on length rather than contract.
    /// The transport texts below deliberately never name the VM: attribution must come from the
    /// framing. Covers the PSDirect (<see cref="SessionOpenFailedException"/>) arm.
    /// </summary>
    [Theory]
    [InlineData("The user name or password is incorrect")]
    [InlineData("connection refused")]
    public void FR4_ShortCause_PsDirectArm_StillCarriesOperationVmAndCode(string transportText)
    {
        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: transportText));

        var error = response.Error!;
        _output.WriteLine($"short PSDirect envelope ({error.Length} chars): {error}");

        transportText.Should().NotContain(VmId, "the fixture must not pre-attribute the failure");
        error.Length.Should().BeLessThan(EnvelopeBound, "this fixture must exercise the fitting path");

        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed);
        error.Should().Contain("Open PowerShell Direct guest session",
            "FR-4: the envelope names the operation regardless of cause length.");
        error.Should().Contain(VmId,
            "FR-4/#294: a short cause that never names the VM must still be attributed, or the " +
            "cross-VM ambiguity #294 exists to eliminate reopens.");
        error.Should().Contain(ErrorCodes.SessionFailed,
            "FR-4: the envelope names the classified reason regardless of cause length.");
        error.Should().Contain(transportText, "the underlying cause is still delivered.");
    }

    /// <summary>
    /// PR #304 regression guard, SSH arm (<see cref="SshSessionOpenException"/>) — the second
    /// <c>BoundSessionOpenError</c> call site. Same contract: framing is unconditional.
    /// </summary>
    [Theory]
    [InlineData("The user name or password is incorrect")]
    [InlineData("connection refused")]
    public void FR4_ShortCause_SshArm_StillCarriesOperationVmAndCode(string transportText)
    {
        var response = new ErrorMapper().MapException(
            new SshSessionOpenException(vmId: VmId, message: transportText));

        var error = response.Error!;
        _output.WriteLine($"short SSH envelope ({error.Length} chars): {error}");

        error.Length.Should().BeLessThan(EnvelopeBound, "this fixture must exercise the fitting path");

        response.ErrorCode.Should().Be(ErrorCodes.SessionFailed);
        error.Should().Contain("Open SSH guest session",
            "FR-4: the envelope names the operation regardless of cause length.");
        error.Should().Contain(VmId,
            "FR-4/#294: short SSH transport text names no VM, so attribution must come from framing.");
        error.Should().Contain(ErrorCodes.SessionFailed,
            "FR-4: the envelope names the classified reason regardless of cause length.");
    }

    // ── Guard 5: AC-3b — no server-internal execution renderings on the wire ──

    /// <summary>
    /// AC-3b. A diagnostic carrying a sentinel in a source line, a source-location citation, a
    /// script/call stack trace and a formatted multi-part rendering must yield an envelope free of
    /// all four renderings.
    ///
    /// <para>Guest and platform prose is explicitly allowed through, so this asserts on the
    /// RENDERING SHAPES rather than on the sentinel wholesale — over-asserting would forbid the
    /// actionable guest text the envelope exists to deliver.</para>
    /// </summary>
    [Fact]
    public void AC3b_Envelope_CarriesNoSourceLines_Citations_StackTraces_OrMultiPartRenderings()
    {
        const string guestProse = "The Hyper-V socket target process is not listening.";

        // Canonical PowerShell / .NET failure rendering — the shapes a real ErrorRecord produces,
        // each carrying the sentinel in a different one of AC-3b's four categories.
        var reported = string.Join("\n", new[]
        {
            guestProse,
            "At C:\\server\\Infrastructure\\bootstrap.ps1:210 char:5",
            "+     $session = New-PSSession -VMName $vm.Name -Credential $cred",
            "+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~",
            "   at HyperV.Mcp.Server.Infrastructure.SessionStore.GetOrCreateAsync()",
            "   at System.Management.Automation.Runspaces.Pipeline.Invoke()",
            "CategoryInfo          : OpenError: (vm:String) [New-PSSession], PSRemotingTransportException",
        });

        var response = Map(new SessionOpenFailedException(
            sessionName: "HvMcp-session",
            vmId: VmId,
            message: $"Failed to create PSSession 'HvMcp-session': {reported}",
            decisiveCause: guestProse));

        var error = response.Error!;
        _output.WriteLine($"AC-3b envelope: {error}");

        error.Should().NotContain("SessionStore.GetOrCreateAsync",
            "AC-3b: call-stack frames are server-internal execution renderings.");
        error.Should().NotContain("Pipeline.Invoke",
            "AC-3b: platform call-stack frames MUST NOT reach the caller.");
        error.Should().NotContain("bootstrap.ps1",
            "AC-3b: source-location citations MUST NOT reach the caller.");
        error.Should().NotContain("char:5",
            "AC-3b: positional source-location citations MUST NOT reach the caller.");
        error.Should().NotContain("~~~~",
            "AC-3b: the caret/tilde source-position rendering MUST NOT reach the caller.");
        error.Should().NotContain("$session = New-PSSession",
            "AC-3b/FR-3: server-internal source lines MUST NOT reach the caller.");

        error.Should().Contain(guestProse,
            "guest and platform prose is explicitly allowed through — the envelope must stay actionable.");
    }

    // ── Guard 6 + 8: AC-9b at rest, and the plain-fallback spill correlation ──

    /// <summary>
    /// Guard 8 / SOE-D12. The untyped fallback returns fixed prose plus a spill referent. The
    /// referent must actually correlate to spilled content on disk — a caller left with a bare code
    /// and no actionable referent is the #266 / PR #282 anti-pattern this guards against.
    ///
    /// <para>AC-9b is asserted against the retained content itself: the spilled record must contain
    /// only structural facets, never the sentinel secret.</para>
    /// </summary>
    [Fact]
    public void PlainFallback_ReferentCorrelatesToSpilledContent_AndSpillHoldsNoSecret()
    {
        const string sentinelPassword = "Sup3rS3cretSentinel!";
        var spillDir = Path.Combine(Path.GetTempPath(), "gate10-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spillDir);
        var previous = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDir;
        try
        {
            // Untyped arm: a plain InvalidOperationException whose message carries the secret and
            // was never proven redacted. Both message and secret must be withheld everywhere.
            var ex = new InvalidOperationException(
                $"New-PSSession failed: PSSessionOpenFailed for password {sentinelPassword}",
                new UnauthorizedAccessException($"inner leak {sentinelPassword}"));

            var response = new ErrorMapper().MapException(ex);
            var error = response.Error!;
            _output.WriteLine($"fallback envelope: {error}");

            response.ErrorCode.Should().Be(ErrorCodes.SessionFailed);
            error.Should().NotContain(sentinelPassword,
                "FR-12/AC-9: no envelope field may carry the sentinel.");

            // The referent must be present AND resolvable — not a bare code.
            error.Should().Contain("Spilled=",
                "SOE-D12: bounding without an actionable referent is an information-loss regression " +
                "and reproduces the #266 / PR #282 anti-pattern.");

            var spillFiles = Directory.GetFiles(spillDir, "hypervmcp-stderr-*.log");
            spillFiles.Should().NotBeEmpty("the referent must correspond to a real spilled record");

            // Correlate on the referent as the caller receives it. The referent is capped at 120
            // characters, so a long temp root can truncate the tail; the operator-actionable part
            // is the prefix, which must still identify exactly one real spilled record.
            var referentStart = error.IndexOf("Spilled=", StringComparison.Ordinal) + "Spilled=".Length;
            var referent = error.Substring(referentStart).TrimEnd('.', ' ');
            var referenced = spillFiles.Single(path =>
                path.StartsWith(referent, StringComparison.OrdinalIgnoreCase)
                || referent.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            var spilled = File.ReadAllText(referenced);
            _output.WriteLine($"spilled content:\n{spilled}");

            spilled.Should().NotBeNullOrWhiteSpace(
                "the referent must resolve to content, or the caller has a pointer to nothing");
            spilled.Should().Contain(nameof(InvalidOperationException),
                "the spill must carry the structural type chain the envelope promises");
            spilled.Should().Contain(nameof(UnauthorizedAccessException),
                "the inner chain is part of the promised structural record");

            // AC-9b — at rest.
            spilled.Should().NotContain(sentinelPassword,
                "AC-9b/FR-12 at rest: spilling the message would move a credential disclosure from " +
                "the wire to disk rather than remove it.");
            spilled.Should().NotContain("inner leak",
                "AC-9b: no Exception.Message text may reach the spill on this boundary.");
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = previous;
            try { Directory.Delete(spillDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// AC-9b / SOE-D19. The structural record emits ONLY type identity, HResult hex, Data.Count and
    /// a <c>StackTrace is not null</c> boolean — nothing derived from caller-supplied text.
    /// </summary>
    [Fact]
    public void StructuralDescription_EmitsOnlyTypeIdentity_HResult_DataCount_AndStackPresence()
    {
        const string sentinel = "SECRET-IN-DATA-AND-MESSAGE";
        var spillDir = Path.Combine(Path.GetTempPath(), "gate10-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spillDir);
        var previous = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDir;
        try
        {
            var ex = new InvalidOperationException($"New-PSSession PSSessionOpenFailed {sentinel}");
            ex.Data["k"] = sentinel;

            new ErrorMapper().MapException(ex);

            var spilled = ReadOnlySpillRecord(spillDir);
            _output.WriteLine(spilled);

            spilled.Should().NotContain(sentinel,
                "neither Message nor Data VALUES may be rendered — only counts.");
            spilled.Should().Contain("data entries 1",
                "SOE-D19: Data is reported as a COUNT, never by content.");
            spilled.Should().MatchRegex(@"hresult 0x[0-9A-F]{8}",
                "SOE-D19: the HResult is emitted as hex.");
            spilled.Should().MatchRegex("has stack (True|False)",
                "SOE-D19: stack presence is a boolean, never the stack itself.");
            spilled.Should().NotContain("   at ",
                "SOE-D19: no stack frames may be rendered.");
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = previous;
            try { Directory.Delete(spillDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Guard 7: SOE-D20 — DescribeTypeSafely bounds ─────────────────────────

    /// <summary>
    /// SOE-D20. A runtime type name is not inherently safe metadata: a dynamically emitted type
    /// carries its creator's chosen name, and a constructed generic carries its type arguments, so
    /// the name can reproduce caller-influenced text and grow without limit.
    /// </summary>
    [Fact]
    public void SOE_D20_DynamicallyEmittedExceptionType_IsWithheld_NotNamed()
    {
        const string attackerChosenTypeName = "LeakedSecretInTypeName_ATTACKER_CHOSE_THIS";
        var spillDir = Path.Combine(Path.GetTempPath(), "gate10-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spillDir);
        var previous = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDir;
        try
        {
            var dynamicType = EmitDynamicExceptionType(attackerChosenTypeName);
            var dynamicEx = (Exception)Activator.CreateInstance(dynamicType)!;
            var ex = new InvalidOperationException("New-PSSession PSSessionOpenFailed", dynamicEx);

            var response = new ErrorMapper().MapException(ex);
            var spilled = ReadOnlySpillRecord(spillDir);
            _output.WriteLine(spilled);

            response.Error.Should().NotContain(attackerChosenTypeName,
                "SOE-D20: a dynamic assembly's type name is caller-influenced and MUST be withheld.");
            spilled.Should().NotContain(attackerChosenTypeName,
                "SOE-D20: the withholding applies at rest as well as on the wire.");
            spilled.Should().Contain("<runtime-emitted type withheld>",
                "SOE-D20: a dynamic type yields a placeholder instead of its name.");
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = previous;
            try { Directory.Delete(spillDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// SOE-D20. A deeply nested constructed generic reduces to its open definition, so type
    /// arguments never appear and the name cannot grow without limit. A five-figure type name
    /// previously blew the envelope bound.
    /// </summary>
    [Fact]
    public void SOE_D20_DeeplyNestedConstructedGeneric_ReducesToDefinition_AndStaysBounded()
    {
        var spillDir = Path.Combine(Path.GetTempPath(), "gate10-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spillDir);
        var previous = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDir;
        try
        {
            // A constructed generic exception whose type arguments nest deeply enough that the
            // full name is far longer than the 128-character cap.
            var deep = new DeepGenericException<
                Dictionary<string, List<Dictionary<string, List<Dictionary<string, List<string>>>>>>>();
            deep.GetType().FullName!.Length.Should().BeGreaterThan(200,
                "the fixture must actually exercise the unbounded-name hazard");

            var ex = new InvalidOperationException("New-PSSession PSSessionOpenFailed", deep);
            var response = new ErrorMapper().MapException(ex);
            var spilled = ReadOnlySpillRecord(spillDir);
            _output.WriteLine(spilled);

            spilled.Should().NotContain("System.Collections.Generic.Dictionary",
                "SOE-D20: a constructed generic is reduced to its open definition, so type " +
                "arguments never appear.");
            spilled.Should().Contain("DeepGenericException",
                "the open definition itself is safe, statically-loaded manifest metadata");
            response.Error!.Length.Should().BeLessThanOrEqualTo(EnvelopeBound,
                "SOE-D20: the type-name cap is what keeps the wire envelope bounded.");
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = previous;
            try { Directory.Delete(spillDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// SOE-D20. The 128-character hard cap applies to the survivor — a statically loaded type whose
    /// own manifest name is long — and the emitted value must show it was truncated.
    /// </summary>
    [Fact]
    public void SOE_D20_LongStaticTypeName_IsHardCappedAt128Characters()
    {
        var spillDir = Path.Combine(Path.GetTempPath(), "gate10-spill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spillDir);
        var previous = StderrSpillHelper.TempPathProvider;
        StderrSpillHelper.TempPathProvider = () => spillDir;
        try
        {
            var longNamed = new
                AVeryLongExceptionTypeNameDeliberatelyExceedingTheOneHundredAndTwentyEightCharacterCapUsedByDescribeTypeSafelyForSpillRecords();
            longNamed.GetType().FullName!.Length.Should().BeGreaterThan(128,
                "the fixture must actually exceed the cap");

            new ErrorMapper().MapException(
                new InvalidOperationException("New-PSSession PSSessionOpenFailed", longNamed));

            var spilled = ReadOnlySpillRecord(spillDir);
            _output.WriteLine(spilled);

            spilled.Should().Contain("…(truncated)",
                "SOE-D20: a name over the 128-character cap is truncated with an explicit marker.");
            spilled.Should().NotContain(longNamed.GetType().FullName!,
                "SOE-D20: the full over-cap name MUST NOT be emitted.");
        }
        finally
        {
            StderrSpillHelper.TempPathProvider = previous;
            try { Directory.Delete(spillDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads the single spill record written into this test's own directory. The directory is
    /// per-test, so exactly one record must exist; more would mean the redirect leaked.
    /// </summary>
    private static string ReadOnlySpillRecord(string spillDir)
    {
        var files = Directory.GetFiles(spillDir, "hypervmcp-stderr-*.log");
        files.Should().ContainSingle(
            "the spill redirect is per-test; additional records would mean it leaked across tests");
        return File.ReadAllText(files[0]);
    }

    private static Type EmitDynamicExceptionType(string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Gate10Dynamic"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Gate10DynamicModule");
        var type = module.DefineType(
            typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(Exception));
        return type.CreateType()!;
    }
}

/// <summary>Fixture for the SOE-D20 constructed-generic reduction guard.</summary>
public sealed class DeepGenericException<T> : Exception
{
}

/// <summary>Fixture for the SOE-D20 128-character type-name cap.</summary>
public sealed class
    AVeryLongExceptionTypeNameDeliberatelyExceedingTheOneHundredAndTwentyEightCharacterCapUsedByDescribeTypeSafelyForSpillRecords
    : Exception
{
}
