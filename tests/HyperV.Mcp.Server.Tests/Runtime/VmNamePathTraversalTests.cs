using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for VM name path traversal validation.
///
/// Security context: <see cref="HyperVManager.CreateVmAsync"/> uses the VM name in
/// <c>Join-Path $storageRoot $name</c> to construct host filesystem paths for the
/// differencing VHDX and VM directory. Without validation, a crafted name like
/// "../../evil" can escape the configured storage root and create host-side
/// directories/files in arbitrary locations.
///
/// These tests validate that <see cref="InputValidation.ValidateVmName"/> (a new method
/// expected to be added) rejects names containing path traversal characters and patterns,
/// while accepting legitimate VM names.
///
/// See /myplans/security/security-design.md — SEC-D7: VM name path traversal validation.
/// See /myplans/vm-management/vm-management-design.md — VMM-D3: Differencing VHDX for all VM creation.
///
/// NOTE: These tests are written BEFORE the implementation exists. They will fail to
/// compile until <c>InputValidation.ValidateVmName(string)</c> is implemented.
/// This is intentional — test-first development to define the expected behavior.
/// </summary>
public class VmNamePathTraversalTests
{
    // ═══════════════════════════════════════════════════════════════════
    // Path Separator Rejection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names containing forward slashes must be rejected because they form
    /// path components when used in <c>Join-Path</c>.
    /// Attack vector: "../../evil" → escapes storage root via relative path.
    /// </summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/dir")]
    [InlineData("a/b/c")]
    [InlineData("/absolute")]
    public void ValidateVmName_WithForwardSlash_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    /// <summary>
    /// VM names containing backslashes must be rejected because they are the
    /// Windows path separator and form path components in <c>Join-Path</c>.
    /// Attack vector: "..\..\evil" → escapes storage root on Windows.
    /// </summary>
    [Theory]
    [InlineData(@"..\evil")]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData(@"sub\dir")]
    [InlineData(@"a\b\c")]
    [InlineData(@"\absolute")]
    public void ValidateVmName_WithBackslash_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Parent Directory Traversal (..) Rejection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names containing ".." (parent directory reference) must be rejected
    /// even without path separators. The ".." sequence has special meaning in
    /// filesystem paths and could be exploited by combining with other inputs.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("..evil")]
    [InlineData("evil..")]
    [InlineData("my..vm")]
    [InlineData("...")]
    public void ValidateVmName_WithDoubleDot_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Null / Empty / Whitespace Rejection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Null, empty, and whitespace-only VM names must be rejected.
    /// These are invalid identifiers and would cause filesystem issues.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void ValidateVmName_NullOrWhitespace_ThrowsArgumentException(string? name)
    {
        var act = () => InputValidation.ValidateVmName(name!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Leading/Trailing Whitespace Rejection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names with leading or trailing whitespace must be rejected.
    /// Whitespace can cause inconsistencies between the name used for
    /// lookup (trimmed by Hyper-V) and the directory created on disk,
    /// and can be used to disguise malicious names.
    /// </summary>
    [Theory]
    [InlineData(" leading-space")]
    [InlineData("trailing-space ")]
    [InlineData(" both-sides ")]
    [InlineData("\tleading-tab")]
    [InlineData("trailing-tab\t")]
    public void ValidateVmName_WithLeadingOrTrailingWhitespace_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Null Byte / Control Character Rejection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names containing null bytes must be rejected.
    /// Null bytes can truncate strings in C-based APIs and cause the
    /// actual filesystem path to differ from the validated path.
    /// </summary>
    [Theory]
    [InlineData("evil\0name")]
    [InlineData("\0")]
    [InlineData("before\0after")]
    public void ValidateVmName_WithNullByte_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    /// <summary>
    /// VM names containing control characters (ASCII 0x00–0x1F) must be rejected.
    /// Control characters are invalid in Windows filenames and could cause
    /// unexpected behavior in filesystem operations.
    /// </summary>
    [Theory]
    [InlineData("name\x01")]
    [InlineData("name\x1F")]
    [InlineData("\x07bell")]
    public void ValidateVmName_WithControlCharacters_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Windows Reserved Characters / Names
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names containing characters illegal in Windows filenames must be rejected.
    /// These characters (<c>&lt; &gt; : " | ? *</c>) are invalid in NTFS paths
    /// and would cause VM directory/VHDX creation to fail or behave unexpectedly.
    /// </summary>
    [Theory]
    [InlineData("name<evil")]
    [InlineData("name>evil")]
    [InlineData("name:evil")]
    [InlineData("name\"evil")]
    [InlineData("name|evil")]
    [InlineData("name?evil")]
    [InlineData("name*evil")]
    public void ValidateVmName_WithIllegalWindowsChars_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    /// <summary>
    /// VM names that are Windows reserved device names must be rejected.
    /// Names like CON, PRN, AUX, NUL, COM1–COM9, LPT1–LPT9 are reserved
    /// by Windows and cannot be used as directory or file names.
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT1")]
    [InlineData("con")]     // Case-insensitive on Windows
    [InlineData("nul")]
    public void ValidateVmName_WithReservedWindowsName_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Trailing Dot / Space (Windows NTFS edge cases)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// VM names ending with a dot or space must be rejected.
    /// Windows NTFS silently strips trailing dots and spaces from directory names,
    /// which could cause the actual path to differ from the intended path.
    /// </summary>
    [Theory]
    [InlineData("name.")]
    [InlineData("name..")]
    [InlineData("name. ")]
    public void ValidateVmName_WithTrailingDotOrSpace_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Length Validation
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Excessively long VM names must be rejected.
    /// NTFS has a 255-character limit for individual path components.
    /// The VM name is used as a directory name and VHDX filename, so it
    /// must remain within practical filesystem limits.
    /// </summary>
    [Fact]
    public void ValidateVmName_ExceedsMaxLength_ThrowsArgumentException()
    {
        var name = new string('a', 256);

        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Valid Names — Positive Tests
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard VM names containing alphanumerics, hyphens, underscores,
    /// dots, and spaces (internal) should be accepted.
    /// These are common, safe naming patterns for Hyper-V VMs.
    /// </summary>
    [Theory]
    [InlineData("my-vm")]
    [InlineData("test-vm-01")]
    [InlineData("Ubuntu-22.04-Dev")]
    [InlineData("Windows_Server_2022")]
    [InlineData("dev machine 1")]
    [InlineData("a")]
    [InlineData("VM")]
    [InlineData("my.vm.name")]
    [InlineData("vm-with-123")]
    public void ValidateVmName_ValidNames_ReturnsName(string name)
    {
        var result = InputValidation.ValidateVmName(name);

        result.Should().Be(name);
    }

    /// <summary>
    /// A VM name at the maximum allowed length (255 characters) should be accepted.
    /// </summary>
    [Fact]
    public void ValidateVmName_AtMaxLength_ReturnsName()
    {
        var name = new string('a', 255);

        var result = InputValidation.ValidateVmName(name);

        result.Should().Be(name);
    }

    /// <summary>
    /// Single-character VM names should be accepted.
    /// Validates that the minimum length boundary works correctly.
    /// </summary>
    [Fact]
    public void ValidateVmName_SingleCharacter_ReturnsName()
    {
        var result = InputValidation.ValidateVmName("x");

        result.Should().Be("x");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Combined Attack Patterns
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Realistic attack patterns that combine multiple evasion techniques
    /// must all be rejected. These test defense-in-depth by ensuring that
    /// no combination of malicious patterns can bypass validation.
    /// </summary>
    [Theory]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]   // Windows SAM database access
    [InlineData("../../etc/shadow")]                         // Unix shadow file (cross-platform safety)
    [InlineData("valid-name/../../../escape")]               // Mid-path traversal
    [InlineData("normal\0../../etc/passwd")]                 // Null byte truncation + traversal
    [InlineData(" \t../../evil")]                            // Whitespace prefix + traversal
    [InlineData("CON.vhdx")]                                // Reserved name with extension
    public void ValidateVmName_CombinedAttackPatterns_ThrowsArgumentException(string name)
    {
        var act = () => InputValidation.ValidateVmName(name);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("name");
    }
}
