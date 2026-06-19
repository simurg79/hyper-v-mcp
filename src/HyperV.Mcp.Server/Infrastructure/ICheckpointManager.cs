using HyperV.Mcp.Server.Models;

namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Manages checkpoint (snapshot) operations for VMs.
/// See /myplans/vm-management/checkpoints/checkpoints-design.md — Interfaces: Provided.
/// </summary>
public interface ICheckpointManager
{
    /// <summary>
    /// Create a checkpoint for a VM.
    /// </summary>
    Task<CheckpointResult> CreateCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default);

    /// <summary>
    /// Restore a VM to a named checkpoint.
    /// See /myplans/vm-management/checkpoints/checkpoints-design.md — CP-D3: Invalidate cached session after restore.
    /// </summary>
    Task<CheckpointResult> RestoreCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default);

    /// <summary>
    /// List all checkpoints for a VM.
    /// </summary>
    Task<CheckpointResult> ListCheckpointsAsync(string hostId, string vmId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete a checkpoint from a VM.
    /// </summary>
    Task<CheckpointResult> DeleteCheckpointAsync(string hostId, string vmId, string checkpointName,
        CancellationToken ct = default);

    /// <summary>
    /// Issue #51 / CP-D6: Merge all checkpoints into the parent VHDX, oldest-first.
    /// <para>
    /// Linear-chain only — if the checkpoint tree is branched (any node has more than
    /// one child) the implementation throws <see cref="MergeNotSupportedException"/>
    /// (→ <c>MERGE_NOT_SUPPORTED</c>). Underlying Hyper-V merge-job failures throw
    /// <see cref="CheckpointMergeFailedException"/> (→ <c>CHECKPOINT_MERGE_FAILED</c>).
    /// </para>
    /// <para>
    /// Does NOT manage VM state — caller must ensure the VM is in the appropriate
    /// state before invoking (Hyper-V supports online and offline merge but the
    /// orchestration of state transitions is the caller's responsibility, per CP-D6).
    /// The method awaits the underlying Hyper-V merge-job(s) to completion before
    /// returning.
    /// </para>
    /// </summary>
    /// <param name="hostId">Hyper-V host identifier (local only in Phase 1).</param>
    /// <param name="vmId">VM identifier (GUID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MergeResult"/> describing the merge outcome. <see cref="MergeResult.Success"/>
    /// is <c>true</c> when all merges (including the no-checkpoints case) completed; in that
    /// case <see cref="MergeResult.MergedCount"/> reports how many checkpoint nodes were merged
    /// and <see cref="MergeResult.FailureReason"/> is <c>null</c>.
    /// </returns>
    Task<MergeResult> MergeAllAsync(string hostId, string vmId, CancellationToken ct = default);
}

/// <summary>
/// Issue #51 / CP-D6: Outcome of <see cref="ICheckpointManager.MergeAllAsync"/>.
/// </summary>
/// <param name="Success">True when all merges completed (or there were no checkpoints to merge).</param>
/// <param name="MergedCount">Number of checkpoint nodes that were merged into the parent VHDX.</param>
/// <param name="FailureReason">
/// Human-readable failure reason when <see cref="Success"/> is <c>false</c>;
/// <c>null</c> on success. Per CP-D6 the implementation generally throws typed
/// exceptions instead of returning failure shapes, but this field is preserved
/// so callers can introspect partial-success cases without re-throwing.
/// </param>
public sealed record MergeResult(bool Success, int MergedCount, string? FailureReason);
