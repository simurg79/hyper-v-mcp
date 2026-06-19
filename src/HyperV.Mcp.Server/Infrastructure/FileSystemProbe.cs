namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Default <see cref="IFileSystemProbe"/> implementation delegating to
/// <see cref="System.IO.Directory"/>. Behavior is byte-for-byte equivalent to
/// the inline probe previously embedded in
/// <see cref="HyperVManager.ListImagesAsync"/>: a single cursor-advance over
/// <c>EnumerateFileSystemEntries</c>. Native exceptions propagate unchanged.
/// </summary>
public sealed class FileSystemProbe : IFileSystemProbe
{
    /// <inheritdoc />
    public void ProbeDirectory(string path)
    {
        using var probe = System.IO.Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        probe.MoveNext();
    }
}
