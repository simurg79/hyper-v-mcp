using Xunit;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// xUnit collection marker for tests that mutate process-wide environment
/// variables involved in credential resolution (e.g.
/// <c>HYPERV_MCP_VM_USERNAME</c>, <c>HYPERV_MCP_VM_PASSWORD</c>) or that read
/// those variables transitively (e.g. <see cref="HyperV.Mcp.Server.Infrastructure.StderrSpillHelper"/>
/// reading <c>CredentialResolver.EnvVarPassword</c> for redaction).
///
/// All such test classes MUST belong to this single collection so xUnit
/// serializes them against each other and prevents one test from clearing or
/// overwriting another test's env-var state mid-run. <c>DisableParallelization
/// = true</c> makes that serialization explicit and discoverable rather than
/// an emergent side effect of sharing a collection name.
/// </summary>
[CollectionDefinition("EnvVarMutating", DisableParallelization = true)]
public sealed class EnvVarMutatingCollection
{
}
