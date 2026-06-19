using FluentAssertions;
using HyperV.Mcp.Server.Infrastructure;
using Xunit;

namespace HyperV.Mcp.Server.Tests.Runtime;

/// <summary>
/// Contract tests for <see cref="CredentialResolver"/> env-var names and
/// priority order. Pinned by GitHub Issue #46.
///
/// Purpose: every Phase-1 smoke run discovers the canonical env-var names
/// from a single source — the lab-environment SSoT, relocated to the
/// operator-local roo-vault at <c>myplans/operational/lab-environment.md</c>
/// (not tracked in this repo) — which mirrors the
/// constants in <see cref="CredentialResolver"/>. If a future PR renames
/// either env-var constant in <see cref="CredentialResolver"/>, these tests
/// MUST fail loudly with a message instructing the author to also update
/// <c>myplans/operational/lab-environment.md</c> and
/// <c>myscripts/smoke-test/_phase1-preflight.ps1</c> (both relocated to the
/// operator-local roo-vault, not tracked in this repo), before the manual smoke
/// silently degrades to MISSING_CREDENTIALS envelopes.
///
/// See also: <see cref="CredentialResolutionTests"/> for behavior coverage.
/// </summary>
[Trait("Category", "Runtime")]
[Collection("EnvVarMutating")]
public class CredentialResolverContractTests : IDisposable
{
    // The canonical names this contract pins. Hard-coded literals here on
    // purpose — comparing the resolver's constants against themselves would
    // be tautological. If you are tempted to "fix" this by switching to
    // CredentialResolver.EnvVarUsername / .EnvVarPassword, STOP and read the
    // class-level XML doc above first: the whole point is to detect a drift
    // of those constants away from these literals.
    private const string CanonicalUsernameVar = "HYPERV_MCP_VM_USERNAME";
    private const string CanonicalPasswordVar = "HYPERV_MCP_VM_PASSWORD";

    private const string DriftRemediation =
        "Env-var name changed in CredentialResolver.cs. Update " +
        "myplans/operational/lab-environment.md and " +
        "myscripts/smoke-test/_phase1-preflight.ps1 (both relocated to the " +
        "operator-local roo-vault, not tracked in this repo) " +
        "to match the new name(s), then update the literals in " +
        "CredentialResolverContractTests.cs.";

    // Snapshot whatever might already be set so we restore cleanly. Use a
    // collection fixture to serialize with other env-touching tests.
    private readonly string? _origCanonicalUser;
    private readonly string? _origCanonicalPass;

    public CredentialResolverContractTests()
    {
        _origCanonicalUser = Environment.GetEnvironmentVariable(CanonicalUsernameVar);
        _origCanonicalPass = Environment.GetEnvironmentVariable(CanonicalPasswordVar);
        Environment.SetEnvironmentVariable(CanonicalUsernameVar, null);
        Environment.SetEnvironmentVariable(CanonicalPasswordVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CanonicalUsernameVar, _origCanonicalUser);
        Environment.SetEnvironmentVariable(CanonicalPasswordVar, _origCanonicalPass);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Pins the env-var names the resolver actually reads.
    /// Sets the canonical names; if the resolver looks at different names,
    /// resolution falls through to MissingCredentialsException and this test
    /// fails with the drift-remediation message.
    /// </summary>
    [Fact]
    public void EnvVarNames_AreStable()
    {
        Environment.SetEnvironmentVariable(CanonicalUsernameVar, "lab-user");
        Environment.SetEnvironmentVariable(CanonicalPasswordVar, "lab-pass");

        // Also assert the public constants on the resolver match the
        // canonical names — a renamed constant would still trip this even if
        // the env-driven behavioral assertion below somehow stayed green.
        CredentialResolver.EnvVarUsername.Should().Be(
            CanonicalUsernameVar,
            DriftRemediation);
        CredentialResolver.EnvVarPassword.Should().Be(
            CanonicalPasswordVar,
            DriftRemediation);

        var act = () => CredentialResolver.ResolveCredentials(null, null);

        act.Should().NotThrow<MissingCredentialsException>(
            DriftRemediation);

        var (username, password) = CredentialResolver.ResolveCredentials(null, null);
        username.Should().Be("lab-user", DriftRemediation);
        password.Should().Be("lab-pass", DriftRemediation);
    }

    /// <summary>
    /// Pins the documented priority order: per-call tool args win over env
    /// vars. The lab-environment SSoT (relocated to the operator-local
    /// roo-vault at myplans/operational/lab-environment.md, not tracked in
    /// this repo) §2 advertises this contract; if it
    /// flips, every smoke-run consumer of explicit username/password params
    /// silently breaks.
    /// </summary>
    [Fact]
    public void PerCallArgs_TakePriorityOverEnvVars()
    {
        Environment.SetEnvironmentVariable(CanonicalUsernameVar, "env-user");
        Environment.SetEnvironmentVariable(CanonicalPasswordVar, "env-pass");

        var (username, password) = CredentialResolver.ResolveCredentials(
            username: "call-user",
            password: "call-pass");

        username.Should().Be(
            "call-user",
            "per-call args must override env vars; " + DriftRemediation);
        password.Should().Be(
            "call-pass",
            "per-call args must override env vars; " + DriftRemediation);
    }
}
