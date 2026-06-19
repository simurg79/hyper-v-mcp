using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Issue #203 / VC-DUP-D7: end-to-end and ErrorMapper-focused tests for the
/// <c>vm_create</c> duplicate-name fix. Three classes of behaviour are covered
/// here:
/// <list type="number">
/// <item><description>
/// <see cref="VmAlreadyExistsException"/> wire-message contract (VC-DUP-D5 /
/// Constraint #6) — the message format MUST be pinned to
/// <c>"A VM with the name '{name}' already exists on host '{hostId}'."</c>
/// so smoke tests can assert string equality. Surface tested via
/// <see cref="ErrorMapper.MapException(Exception, string?)"/>.
/// </description></item>
/// <item><description>
/// PS-text sanitization (VC-DUP-D5) — the wire <c>error</c> field MUST NOT
/// contain script paths, line/char tokens, caret-pointer noise,
/// <c>CategoryInfo</c>, <c>FullyQualifiedErrorId</c>, or
/// <c>RuntimeException</c> stack tails. Raw PS text remains available for
/// operator triage only via <c>LogDebug</c> at the throw site.
/// </description></item>
/// <item><description>
/// Residual-race classification (VC-DUP-D4) — when an
/// <see cref="InvalidOperationException"/> carrying PowerShell's canonical
/// "already exists" stderr reaches the mapper directly, it MUST be classified
/// as <c>VM_ALREADY_EXISTS</c> with a sanitized message (NOT as the generic
/// <c>COMMAND_FAILED</c> with leaked PS text — the TC-14 contract violation #1
/// and #2).
/// </description></item>
/// </list>
///
/// The probe-path (LF-D19) + rollback-skip invariant (LF-D18) is covered by
/// <see cref="Issue164VmCreateRollbackTests.LfD19ProbeHit_ShortCircuits_AndSkipsRollbackEntirely"/>
/// and
/// <see cref="Issue164VmCreateRollbackTests.ResidualRace_PreservesCollidingVm_AndReturnsSanitizedAlreadyExists"/>
/// which exercise the full <see cref="HyperVManager.CreateVmAsync"/> path via
/// the same recording-executor harness.
/// </summary>
[Trait("Category", "Runtime")]
public sealed class Issue203VmCreateDuplicateNameTests
{
    private const string Host = "local";
    private const string VmName = "issue203-dup-vm";

    // ─── VmAlreadyExistsException wire-message contract (VC-DUP-D5) ─────

    /// <summary>
    /// VC-DUP-D5 / Constraint #6: the envelope message format is
    /// <c>"A VM with the name '{name}' already exists on host '{hostId}'."</c>
    /// exactly. No trailing-punctuation variation, no parenthetical detail.
    /// Smoke tests assert string equality against this exact text.
    /// </summary>
    [Fact]
    public void ErrorMapper_VmAlreadyExistsException_ReturnsContractEnvelope()
    {
        var mapper = new ErrorMapper();
        var ex = new VmAlreadyExistsException(Host, VmName);

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.VmAlreadyExists,
            "Issue #203 violation #1 fix: the code MUST be VM_ALREADY_EXISTS, not COMMAND_FAILED");
        response.Error.Should().Be(
            $"A VM with the name '{VmName}' already exists on host '{Host}'.",
            "VC-DUP-D5 / Constraint #6: contract message format is pinned");
        response.Error.Should().NotContain("char:",
            "Issue #203 violation #2 fix: no PS positional tokens on the wire");
        response.Error.Should().NotContain("RuntimeException",
            "Issue #203 violation #2 fix: no PS stack tail on the wire");
    }

    // ─── PS-text sanitization (VC-DUP-D5) ───────────────────────────────

    /// <summary>
    /// VC-DUP-D5: <see cref="ErrorMapper.SanitizePowerShellErrorText(string)"/>
    /// strips PS positional tokens, caret-pointer indicator lines,
    /// <c>CategoryInfo</c>, <c>FullyQualifiedErrorId</c>, and
    /// <c>RuntimeException</c> stack-tail noise from a wire-bound error string.
    /// </summary>
    [Fact]
    public void SanitizePowerShellErrorText_StripsAllKnownTokens()
    {
        const string raw =
            "vm_create PowerShell pipeline failed (exit code 1): " +
            "VM with name 'foo' already exists\n" +
            "At C:\\Users\\test\\AppData\\Local\\Temp\\hvmcp-create.ps1:14 char:5\n" +
            "+     throw \"VM with name 'foo' already exists\"\n" +
            "+     ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n" +
            "    + CategoryInfo          : OperationStopped: (VM with name 'foo' already exists:String) [], RuntimeException\n" +
            "    + FullyQualifiedErrorId : VM with name 'foo' already exists\n" +
            "System.Management.Automation.RuntimeException: VM with name 'foo' already exists\n" +
            "   at System.Management.Automation.Internal.PipelineProcessor.SynchronousExecuteEnumerate";

        var sanitized = ErrorMapper.SanitizePowerShellErrorText(raw);

        sanitized.Should().NotContain("char:",
            "PS positional tokens must be stripped");
        sanitized.Should().NotContain("~~~",
            "caret-pointer lines must be stripped");
        sanitized.Should().NotContain("CategoryInfo",
            "CategoryInfo lines must be stripped");
        sanitized.Should().NotContain("FullyQualifiedErrorId",
            "FullyQualifiedErrorId lines must be stripped");
        sanitized.Should().NotContain("RuntimeException",
            "RuntimeException stack-tail noise must be stripped");
        sanitized.Should().NotContain("ps1:",
            "script path positional tokens must be stripped");

        sanitized.Should().Contain("already exists",
            "the human-readable failure summary at the head of the message MUST be preserved");
        sanitized.Should().Contain("vm_create PowerShell pipeline failed",
            "the leading exit-code summary MUST be preserved");
    }

    [Fact]
    public void SanitizePowerShellErrorText_NullOrEmpty_ReturnsUnchanged()
    {
        ErrorMapper.SanitizePowerShellErrorText(string.Empty).Should().BeEmpty();
        // Sanity check: the helper is null-safe by virtue of IsNullOrEmpty early-out.
        // We exercise the empty path explicitly because the regex engine treats
        // null specially.
    }

    // ─── Residual-race classification (VC-DUP-D4) ───────────────────────

    /// <summary>
    /// VC-DUP-D4: if a generic <see cref="InvalidOperationException"/> carrying
    /// PowerShell's "already exists" stderr reaches the mapper (defense-in-depth
    /// — the primary <see cref="HyperVManager.CreateVmAsync"/> path now throws
    /// <see cref="VmAlreadyExistsException"/> directly), it MUST be classified
    /// as <c>VM_ALREADY_EXISTS</c> with a sanitized message instead of falling
    /// through to the generic COMMAND_FAILED branch with raw PS-text leakage
    /// (TC-14 contract violations #1 + #2).
    /// </summary>
    [Fact]
    public void ErrorMapper_InvalidOperationException_AlreadyExistsStderr_MapsToVmAlreadyExists()
    {
        var mapper = new ErrorMapper();
        var ex = new InvalidOperationException(
            "vm_create PowerShell pipeline failed (exit code 1): " +
            "VM with name 'foo' already exists\n" +
            "At C:\\script.ps1:14 char:5\n" +
            "+ throw \"VM with name 'foo' already exists\"\n" +
            "+ ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n" +
            "    + CategoryInfo          : OperationStopped\n" +
            "    + FullyQualifiedErrorId : VM with name 'foo' already exists");

        var response = mapper.MapException(ex);

        response.ErrorCode.Should().Be(ErrorCodes.VmAlreadyExists,
            "VC-DUP-D4 defense-in-depth: name-collision pattern MUST map to VM_ALREADY_EXISTS");
        response.Error.Should().NotContain("char:",
            "VC-DUP-D5: PS positional tokens must not leak onto the wire");
        response.Error.Should().NotContain("RuntimeException",
            "VC-DUP-D5: PS stack tail must not leak onto the wire");
        response.Error.Should().NotContain("CategoryInfo",
            "VC-DUP-D5: CategoryInfo lines must not leak onto the wire");
        response.Error.Should().NotContain("FullyQualifiedErrorId",
            "VC-DUP-D5: FullyQualifiedErrorId lines must not leak onto the wire");
    }

    /// <summary>
    /// VC-DUP-D4 guard: <c>BASE_IMAGE_MUTATED</c> failures (which do NOT contain
    /// the substring "already exists") MUST NOT be misclassified as
    /// VM_ALREADY_EXISTS. They continue to flow through the existing
    /// InvalidOperationException → COMMAND_FAILED branch.
    /// </summary>
    [Fact]
    public void ErrorMapper_BaseImageMutated_DoesNotMisClassify()
    {
        var mapper = new ErrorMapper();
        var ex = new InvalidOperationException(
            "BASE_IMAGE_MUTATED: Base VHDX was mutated during differencing disk creation! " +
            "Pre=abc Post=def Path=C:\\base.vhdx");

        var response = mapper.MapException(ex);

        response.ErrorCode.Should().NotBe(ErrorCodes.VmAlreadyExists,
            "BASE_IMAGE_MUTATED must not be misclassified as a name collision");
        response.ErrorCode.Should().Be(ErrorCodes.CommandFailed);
    }

    /// <summary>
    /// VC-DUP-D4 / VC-DUP-D5: <see cref="ErrorMapper.IsNameCollisionMessage(string?)"/>
    /// matches the canonical Hyper-V substring case-insensitively, requires
    /// co-occurrence with a VM-specific token (so non-VM "already exists"
    /// messages such as <c>ImageCopyFailedException</c>'s
    /// "Destination image file already exists at ..." do NOT misclassify as
    /// VM_ALREADY_EXISTS — IA-Gate 5 BaseImageCreationTests fix), and rejects
    /// the BASE_IMAGE_MUTATED false-positive shape.
    /// </summary>
    [Theory]
    [InlineData("VM with name 'foo' already exists", true)]
    [InlineData("New-VM : Failed to create virtual machine. Already exists.", true)]
    [InlineData("Get-VM reports VM 'foo' already exists", true)]
    // Bare "already exists" without a VM-specific token must NOT match — this
    // is the over-match path that misclassified File.Copy(overwrite:false)
    // failures as VM_ALREADY_EXISTS prior to the IA-Gate 5 tightening.
    [InlineData("ALREADY EXISTS", false)]
    [InlineData("Destination image file already exists at 'C:\\img.vhdx'.", false)]
    [InlineData("BASE_IMAGE_MUTATED: ...", false)]
    [InlineData("Some unrelated PowerShell error", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNameCollisionMessage_BehavesAsDocumented(string? message, bool expected)
    {
        ErrorMapper.IsNameCollisionMessage(message).Should().Be(expected);
    }
}
