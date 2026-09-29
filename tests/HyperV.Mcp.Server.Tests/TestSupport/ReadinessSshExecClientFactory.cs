using HyperV.Mcp.Server.Infrastructure;
using Renci.SshNet.Common;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>Records authenticator connections/commands with independently controlled connection and confirmation outcomes.</summary>
public sealed class ReadinessSshExecClientFactory : ISshExecClientFactory
{
    private readonly List<string> _connections = new();

    /// <summary>One of <c>ok</c>, <c>reject</c>, <c>transport</c>.</summary>
    public string ConnectMode { get; set; } = "ok";

    /// <summary>One of <c>ok</c>, <c>empty</c>, <c>nonsentinel</c>, <c>nonzero</c>, <c>fault</c>.</summary>
    public string ConfirmMode { get; set; } = "ok";

    public IReadOnlyList<string> Connections => _connections;

    /// <summary>Runs once a connection is established, to age the clock mid-call.</summary>
    public Action? OnConnected { get; set; }

    public ReadinessSshExecClient? LastClient { get; private set; }

    public Task<ISshExecClient> ConnectAsync(
        string host, int port, string username, string password, CancellationToken ct = default)
    {
        _connections.Add($"{host}:{port} user={username} pass={password}");
        ct.ThrowIfCancellationRequested();
        switch (ConnectMode)
        {
            case "reject": throw new SshAuthenticationException("Permission denied (password).");
            case "transport": throw new System.Net.Sockets.SocketException(10061);
            default:
                LastClient = new ReadinessSshExecClient(ConfirmMode);
                OnConnected?.Invoke();
                return Task.FromResult<ISshExecClient>(LastClient);
        }
    }
}

public sealed class ReadinessSshExecClient : ISshExecClient
{
    private readonly string _confirmMode;
    private readonly List<string> _commands = new();

    internal ReadinessSshExecClient(string confirmMode) => _confirmMode = confirmMode;

    public IReadOnlyList<string> Commands => _commands;

    public int DisposeCount { get; private set; }

    public bool IsConnected => DisposeCount == 0;

    public Task<SshCommandResult> ExecuteAsync(string commandText, CancellationToken ct = default)
    {
        _commands.Add(commandText);
        ct.ThrowIfCancellationRequested();
        return _confirmMode switch
        {
            "empty" => Task.FromResult(new SshCommandResult(string.Empty, string.Empty, 0)),
            "nonsentinel" => Task.FromResult(new SshCommandResult("SOMETHING_ELSE", string.Empty, 0)),
            "nonzero" => Task.FromResult(new SshCommandResult(ReadinessVerdict.Sentinel, "boom", 1)),
            "fault" => throw new System.Net.Sockets.SocketException(10054),
            _ => Task.FromResult(new SshCommandResult(ReadinessVerdict.Sentinel, string.Empty, 0)),
        };
    }

    public Task UploadAsync(string localSourcePath, string guestDestinationPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DownloadAsync(string guestSourcePath, string localDestinationPath, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Dispose() => DisposeCount++;
}
