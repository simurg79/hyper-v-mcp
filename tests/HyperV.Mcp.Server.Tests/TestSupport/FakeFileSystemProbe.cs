using HyperV.Mcp.Server.Infrastructure;

namespace HyperV.Mcp.Server.Tests.TestSupport;

/// <summary>
/// Issue #73 — deterministic, cross-platform fake for <see cref="IFileSystemProbe"/>.
/// Configure <see cref="ExceptionToThrow"/> to drive the probe-time exception
/// classification branches inside <see cref="HyperVManager.ListImagesAsync"/>
/// without relying on platform-specific ACL plumbing.
///
/// Mirrors the seam contract: throws the configured exception unchanged so
/// <c>ListImagesAsync</c>'s catch arms and the downstream <c>ErrorMapper</c>
/// observe the exact same exception fidelity as production.
/// </summary>
public sealed class FakeFileSystemProbe : IFileSystemProbe
{
    /// <summary>
    /// When non-null, <see cref="ProbeDirectory"/> throws this exception.
    /// When null, the call returns silently (happy path).
    /// </summary>
    public System.Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// Captures the most recent path passed to <see cref="ProbeDirectory"/>.
    /// </summary>
    public string? LastProbedPath { get; private set; }

    /// <summary>
    /// Count of <see cref="ProbeDirectory"/> invocations across the test.
    /// </summary>
    public int InvocationCount { get; private set; }

    /// <inheritdoc />
    public void ProbeDirectory(string path)
    {
        LastProbedPath = path;
        InvocationCount++;
        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }
    }
}
