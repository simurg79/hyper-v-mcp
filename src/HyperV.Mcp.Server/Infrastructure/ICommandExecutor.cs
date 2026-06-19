using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Executes commands and scripts on guest VMs.
/// See /myplans/execution/commands/commands-design.md — Interfaces: Provided.
/// </summary>
public interface ICommandExecutor
{
    /// <summary>
    /// Execute a single command on a guest VM.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(
        string hostId, string vmId, string command,
        string shell = "cmd", int timeoutSeconds = 30,
        string? username = null, string? password = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a multi-line script on a guest VM.
    /// See /myplans/execution/commands/commands-design.md — CMD-D1.
    /// </summary>
    Task<CommandResult> ExecuteScriptAsync(
        string hostId, string vmId, string script,
        string shell = "powershell", int timeoutSeconds = 60,
        string? username = null, string? password = null,
        CancellationToken ct = default);
}
