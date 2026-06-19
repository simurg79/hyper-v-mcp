using Xunit;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// xUnit collection marker for the script-dump diagnostic tests.
///
/// All test classes that exercise the <c>HYPERV_MCP_DUMP_PS_SCRIPTS</c> env-var
/// gate, the masked-dump writer, or the temp-staging path of
/// <see cref="HyperV.Mcp.Server.Infrastructure.PowerShellExecutor"/> belong to
/// this collection so they are serialized across the entire test assembly.
///
/// See /myplans/operational/script-dump-test-isolation/script-dump-test-isolation-design.md
/// — Decisions TI-D5 and TI-D8. <c>DisableParallelization = true</c> makes the
/// serialization explicit and discoverable rather than an emergent side effect.
/// </summary>
[CollectionDefinition("ScriptDumpDiagnostic", DisableParallelization = true)]
public sealed class ScriptDumpDiagnosticCollection
{
}
