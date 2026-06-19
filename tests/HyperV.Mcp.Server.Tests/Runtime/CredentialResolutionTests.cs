using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using HyperV.Mcp.Server.Models;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Tests for credential resolution (Phase 1, GitHub Issue #20).
/// Covers CredentialResolver, ErrorMapper credential mapping, RedactCredentials,
/// and credential block injection into CommandExecutor/FileTransferService scripts.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public class CredentialResolutionTests : IDisposable
{
    public CredentialResolutionTests()
    {
        // Clean env vars before each test.
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarUsername, null);
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, null);
    }

    public void Dispose()
    {
        // Clean up env vars after each test.
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarUsername, null);
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, null);
    }

    // ─── CredentialResolver.ResolveCredentials ──────────────────────────

    /// <summary>
    /// When both username and password are provided as tool parameters, they are returned directly.
    /// </summary>
    [Fact]
    public void ResolveCredentials_WithToolParams_UsesToolParams()
    {
        var (username, password) = CredentialResolver.ResolveCredentials("admin", "s3cret");

        username.Should().Be("admin");
        password.Should().Be("s3cret");
    }

    /// <summary>
    /// When no tool params are provided but env vars are set, env vars are used.
    /// </summary>
    [Fact]
    public void ResolveCredentials_WithEnvVarFallback_UsesEnvVars()
    {
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarUsername, "envUser");
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, "envPass");

        var (username, password) = CredentialResolver.ResolveCredentials(null, null);

        username.Should().Be("envUser");
        password.Should().Be("envPass");
    }

    /// <summary>
    /// Tool parameters take precedence over environment variables.
    /// </summary>
    [Fact]
    public void ResolveCredentials_ToolParamOverridesEnvVar()
    {
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarUsername, "envUser");
        Environment.SetEnvironmentVariable(CredentialResolver.EnvVarPassword, "envPass");

        var (username, password) = CredentialResolver.ResolveCredentials("toolUser", "toolPass");

        username.Should().Be("toolUser");
        password.Should().Be("toolPass");
    }

    /// <summary>
    /// When neither tool params nor env vars are available, throws MissingCredentialsException.
    /// </summary>
    [Fact]
    public void ResolveCredentials_MissingBoth_ThrowsMissingCredentialsException()
    {
        Action act = () => CredentialResolver.ResolveCredentials(null, null);

        act.Should().Throw<MissingCredentialsException>();
    }

    /// <summary>
    /// When username is provided but password is missing, throws MissingCredentialsException.
    /// </summary>
    [Fact]
    public void ResolveCredentials_MissingPassword_ThrowsMissingCredentialsException()
    {
        Action act = () => CredentialResolver.ResolveCredentials("admin", null);

        act.Should().Throw<MissingCredentialsException>();
    }

    // ─── CredentialResolver.BuildCredentialBlock ─────────────────────────

    /// <summary>
    /// Password containing single quotes is properly escaped in the credential block.
    /// </summary>
    [Fact]
    public void BuildCredentialBlock_EscapesSingleQuotes()
    {
        var block = CredentialResolver.BuildCredentialBlock("admin", "p@ss'word");

        // Single quote should be doubled for PowerShell escaping.
        block.Should().Contain("p@ss''word");
    }

    /// <summary>
    /// The credential block contains ConvertTo-SecureString and PSCredential construction.
    /// </summary>
    [Fact]
    public void BuildCredentialBlock_ContainsCredentialConstruction()
    {
        var block = CredentialResolver.BuildCredentialBlock("admin", "secret");

        block.Should().Contain("ConvertTo-SecureString");
        block.Should().Contain("PSCredential");
        block.Should().Contain("$cred");
        block.Should().Contain("$secPass");
    }

    /// <summary>
    /// Regression test for GitHub Issue #37: BuildCredentialBlock must use
    /// 'New-Object -TypeName ... -ArgumentList' syntax instead of constructor-call
    /// syntax 'New-Object Type(args)' which fails in constrained PowerShell environments.
    /// </summary>
    [Fact]
    public void BuildCredentialBlock_UsesArgumentListSyntax_Issue37()
    {
        var block = CredentialResolver.BuildCredentialBlock("testuser", "testpass");

        // Must use named-parameter syntax, NOT constructor-call syntax.
        block.Should().Contain(
            "New-Object -TypeName System.Management.Automation.PSCredential -ArgumentList 'testuser', $secPass",
            "Issue #37: credential block must use -ArgumentList syntax");

        // Ensure the old broken constructor-call syntax is NOT present,
        // including whitespace variants before the opening parenthesis.
        block.Should().NotMatchRegex(
            @"New-Object\s+System\.Management\.Automation\.PSCredential\s*\(");
    }

    // ─── ErrorMapper: MissingCredentialsException mapping ────────────────

    /// <summary>
    /// MissingCredentialsException maps to MISSING_CREDENTIALS error code.
    /// </summary>
    [Fact]
    public void MissingCredentialsException_MapsTo_MISSING_CREDENTIALS()
    {
        var mapper = new ErrorMapper();
        var ex = new MissingCredentialsException();

        var response = mapper.MapException(ex);

        response.Success.Should().BeFalse();
        response.ErrorCode.Should().Be(ErrorCodes.MissingCredentials);
        response.Error.Should().Contain("credentials",
            "error message should mention credentials");
    }

    // ─── ErrorMapper.RedactCredentials ───────────────────────────────────

    /// <summary>
    /// RedactCredentials removes the literal password from text.
    /// </summary>
    [Fact]
    public void RedactCredentials_RemovesPasswordFromText()
    {
        var text = "Error: ConvertTo-SecureString 'myP@ss' failed. -Credential $cred was invalid.";

        var result = ErrorMapper.RedactCredentials(text, "myP@ss");

        result.Should().NotContain("myP@ss");
        result.Should().Contain("***REDACTED***");
    }

    /// <summary>
    /// RedactCredentials handles null/empty text safely.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactCredentials_NullOrEmpty_ReturnsSame(string? text)
    {
        var result = ErrorMapper.RedactCredentials(text!, null);

        result.Should().Be(text);
    }

    // NOTE: Tests for BuildCommandScript / BuildScriptExecutionScript /
    // BuildCopyToGuestScript / BuildCopyFromGuestScript were removed in
    // Phase 2 (issue #52) — those static script-builder methods no longer
    // exist on CommandExecutor / FileTransferService. The new
    // PowerShellDirectChannel architecture executes scripts inside a
    // singleton PowerShell host using persistent PSSessions, so per-call
    // credential blocks are no longer baked into the script body. The
    // remaining tests in this file still cover CredentialResolver behavior.
}
