using System.Globalization;
using System.IO.Compression;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// The file-transfer intents <see cref="FileTransferService"/> expresses as PowerShell script
/// text, which the Linux channel runs as POSIX equivalents.
/// See internal documentation
/// </summary>
internal enum TransferIntent
{
    ProbeGuestPath,
    EnsureParentDirectory,
    VerifyGuestFile,
    BuildGuestTempPath,
    CompressGuestDirectory,
    ExpandGuestArchive,
    BestEffortRemove,
}

/// <summary>
/// Maps transfer script text to a <see cref="TransferIntent"/> by ordinal equality against the
/// service's constants. A substring search would misclassify unrelated text and would keep
/// matching after a script body changed meaning.
/// </summary>
internal static class TransferIntentTranslator
{
    internal static TransferIntent? Recognize(string? script)
    {
        if (string.IsNullOrEmpty(script)) return null;

        if (string.Equals(script, FileTransferService.ProbeGuestPathScript, StringComparison.Ordinal))
            return TransferIntent.ProbeGuestPath;
        if (string.Equals(script, FileTransferService.EnsureParentDirectoryScript, StringComparison.Ordinal))
            return TransferIntent.EnsureParentDirectory;
        if (string.Equals(script, FileTransferService.VerifyFileScript, StringComparison.Ordinal))
            return TransferIntent.VerifyGuestFile;
        if (string.Equals(script, FileTransferService.BuildGuestTempPathScript, StringComparison.Ordinal))
            return TransferIntent.BuildGuestTempPath;
        if (string.Equals(script, FileTransferService.CompressOnGuestScript, StringComparison.Ordinal))
            return TransferIntent.CompressGuestDirectory;
        if (string.Equals(script, FileTransferService.ExpandAndCleanupOnGuestScript, StringComparison.Ordinal))
            return TransferIntent.ExpandGuestArchive;
        if (string.Equals(script, FileTransferService.BestEffortRemoveGuestPathScript, StringComparison.Ordinal))
            return TransferIntent.BestEffortRemove;

        return null;
    }

    /// <summary>
    /// POSIX single-quote wrapping so a path is one injection-safe shell word.
    /// </summary>
    internal static string SingleQuote(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 2);
        builder.Append('\'');
        foreach (var character in value)
        {
            if (character == '\'') builder.Append("'\\''");
            else builder.Append(character);
        }
        builder.Append('\'');
        return builder.ToString();
    }

    /// <summary>
    /// Reads the argument the service bound positionally for this intent.
    /// </summary>
    internal static string RequiredArg(IDictionary<string, object?>? args, string key)
    {
        if (args is not null && args.TryGetValue(key, out var value) && value is string text &&
            !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }
        throw new ArgumentException(
            $"The file-transfer intent requires a non-empty '{key}' argument.", key);
    }

    /// <summary>
    /// Parses a guest-reported byte count. An unparseable value is raised as
    /// <see cref="IOException"/> because the caller's parser would otherwise throw
    /// <see cref="InvalidOperationException"/> and be classified as COMMAND_FAILED.
    /// See internal documentation
    /// </summary>
    internal static long ParseGuestSize(string? stdout, string guestPath, string whatWasExpected)
    {
        var trimmed = (stdout ?? string.Empty).Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            return size;
        }
        throw new IOException(
            $"Expected {whatWasExpected} for guest path '{guestPath}' but the guest reported " +
            $"'{trimmed}'.");
    }
}
