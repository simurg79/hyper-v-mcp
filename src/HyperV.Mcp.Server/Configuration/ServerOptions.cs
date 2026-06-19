namespace HyperV.Mcp.Server.Configuration;

/// <summary>
/// Server-wide configuration options.
/// See /myplans/design.md §8 — Configuration Model.
/// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
/// </summary>
public class ServerOptions
{
    /// <summary>
    /// Global concurrent operation limit.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D2.
    /// </summary>
    public int MaxConcurrentOperations { get; set; } = 10;

    /// <summary>
    /// Per-host concurrent operation limit.
    /// See /myplans/operational/concurrency/concurrency-design.md — CC-D3.
    /// </summary>
    public int MaxPerHostOperations { get; set; } = 5;

    /// <summary>
    /// Queue wait timeout in seconds.
    /// See /myplans/operational/concurrency/concurrency-design.md — Configuration Defaults.
    /// </summary>
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// VM lock timeout in seconds.
    /// </summary>
    public int VmLockTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Default hostId when not specified in tool calls.
    /// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D3.
    /// </summary>
    public string DefaultHostId { get; set; } = "local";

    /// <summary>
    /// Host connection profiles, keyed by hostId.
    /// </summary>
    public Dictionary<string, HostProfile> Hosts { get; set; } = new();

    /// <summary>
    /// Canonical directory where <c>vm_create_base_image</c> writes generalized
    /// base VHDX images (ISO-D20). When <c>null</c> or empty the tool returns
    /// <c>IMAGE_COPY_FAILED</c> with guidance to configure this property.
    /// See /myplans/vm-management/iso-installation/iso-installation-design.md — ISO-D20.
    /// </summary>
    public string? ImageDirectory { get; set; }
}
