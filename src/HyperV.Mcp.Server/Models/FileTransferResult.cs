using System.Text.Json.Serialization;

namespace HyperV.Mcp.Server.Models;

/// <summary>
/// Response shape for vm_copy_file and vm_get_file operations.
/// See /myplans/execution/file-transfer/file-transfer-design.md — Transfer Integrity Verification.
/// </summary>
public class FileTransferResult
{
    [JsonPropertyName("bytesTransferred")]
    public long BytesTransferred { get; set; }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("destPath")]
    public string DestPath { get; set; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; } = 1;

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }
}
