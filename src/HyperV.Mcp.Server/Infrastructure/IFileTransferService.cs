using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Manages file transfers between host and guest VMs.
/// See /myplans/execution/file-transfer/file-transfer-design.md — Interfaces: Provided.
/// </summary>
public interface IFileTransferService
{
    /// <summary>
    /// Copy a file or directory from host to guest VM.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D1.
    /// </summary>
    Task<FileTransferResult> CopyToGuestAsync(
        string hostId, string vmId, string sourcePath, string destPath,
        bool isDirectory = false,
        string? username = null, string? password = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve a file from guest VM to host.
    /// See /myplans/execution/file-transfer/file-transfer-design.md — FT-D2, FT-D3.
    /// </summary>
    Task<FileTransferResult> CopyFromGuestAsync(
        string hostId, string vmId, string sourcePath, string destPath,
        string? username = null, string? password = null,
        CancellationToken ct = default);
}
