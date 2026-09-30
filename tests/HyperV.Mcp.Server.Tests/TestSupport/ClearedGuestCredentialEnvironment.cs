namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>Clear guest-credential environment variables during tests and restore them on dispose: developer exports could otherwise make
/// missing-credential guards pass for the wrong reason.</summary>
public sealed class ClearedGuestCredentialEnvironment : IDisposable
{
    private const string UsernameVariable = "HYPERV_MCP_VM_USERNAME";
    private const string PasswordVariable = "HYPERV_MCP_VM_PASSWORD";

    private readonly string? _previousUsername = Environment.GetEnvironmentVariable(UsernameVariable);
    private readonly string? _previousPassword = Environment.GetEnvironmentVariable(PasswordVariable);

    public ClearedGuestCredentialEnvironment()
    {
        Environment.SetEnvironmentVariable(UsernameVariable, null);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UsernameVariable, _previousUsername);
        Environment.SetEnvironmentVariable(PasswordVariable, _previousPassword);
    }
}
