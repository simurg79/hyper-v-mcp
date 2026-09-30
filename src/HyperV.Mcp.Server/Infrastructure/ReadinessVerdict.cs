using System.Net.Sockets;
using Renci.SshNet.Common;

namespace HyperV.Mcp.Server.Infrastructure;

public enum ReadinessVerdictKind
{
    Ready,
    Obstacle,
    Terminal
}

public sealed record ReadinessVerdict(ReadinessVerdictKind Kind, string Observation, bool CredentialRejected = false)
{
    public const string Sentinel = "HVMCP_READY";

    public static ReadinessVerdict Evaluate(
        bool completed, bool hasErrors, string? stdout, string? diagnostic = null, Exception? fault = null)
    {
        var evidence = diagnostic ?? string.Empty;
        for (var current = fault; current is not null; current = current.InnerException)
        {
            evidence += " " + current.GetType().Name + " " + current.Message;
        }

        if (IsCredentialRejection(evidence))
            return new(ReadinessVerdictKind.Obstacle, "Credential rejection observed.", true);

        if (fault is not null)
        {
            var temporary = fault is IOException or SocketException or TimeoutException or SshException
                || TokenMatcher.ContainsToken(evidence, "PSRemotingTransportException")
                || TokenMatcher.ContainsToken(evidence, "PSRemotingDataStructureException");
            return temporary
                ? new(ReadinessVerdictKind.Obstacle, "Guest access is unavailable; boot progress or transport may be responsible.")
                : new(ReadinessVerdictKind.Terminal, "Observation failed; the cause is unknown. Check guest access configuration.");
        }

        if (!completed || hasErrors)
            return new(ReadinessVerdictKind.Obstacle, "Guest access did not complete successfully; the cause is unknown.");

        return TokenMatcher.ContainsToken(stdout, Sentinel)
            ? new(ReadinessVerdictKind.Ready, "Authenticated read-only guest operation positively confirmed.")
            : new(ReadinessVerdictKind.Obstacle, "Guest operation returned no affirmative readiness confirmation.");
    }

    private static bool IsCredentialRejection(string evidence)
    {
        string[] signals =
        {
            "HVMCP_CREDENTIAL_REJECTED", "PSDirectException", "SshAuthenticationException",
            "credential is invalid", "invalid credential", "bad username or password",
            "logon failure", "user name or password is incorrect", "0x8009030c"
        };
        return signals.Any(signal => TokenMatcher.ContainsPhrase(evidence, signal));
    }
}
