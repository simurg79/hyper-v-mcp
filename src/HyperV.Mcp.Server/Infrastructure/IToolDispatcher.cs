namespace HyperV.Mcp.Server.Infrastructure;

/// <summary>
/// Dispatches MCP tool calls to the appropriate handler.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D1: Attribute-based tool registration.
/// 
/// The dispatcher is responsible for:
/// - Maintaining a registry of known tool names to handlers
/// - Routing incoming tool calls to the correct handler
/// - Returning TOOL_NOT_FOUND for unregistered tool names
/// 
/// Design note: This abstraction exists to enable deterministic testing of
/// dispatch behavior without requiring the full MCP SDK pipeline.
/// See /myplans/mcp-interface/mcp-interface-design.md — MCP-D6: Exceptions caught and wrapped.
/// </summary>
public interface IToolDispatcher
{
    /// <summary>
    /// Dispatch a tool call by name with the given arguments.
    /// Returns a serialized MCP response envelope.
    /// </summary>
    Task<string> DispatchAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct = default);

    /// <summary>
    /// Tuple-based convenience overload that materializes a <see cref="Dictionary{TKey, TValue}"/>
    /// from the supplied (key, value) pairs and delegates to the dictionary overload.
    ///
    /// This is a Default Interface Method (DIM) so the concrete <c>ToolDispatcher</c> class
    /// inherits the implementation for free without any changes (see PR-C: VmTools dedup design).
    ///
    /// Edge cases:
    /// - <paramref name="args"/> is <c>null</c> → treated as an empty argument list.
    /// - Empty array → empty dictionary, produces a byte-identical envelope to the dictionary
    ///   overload invoked with an empty dictionary.
    /// - Duplicate key in <paramref name="args"/> → throws <see cref="ArgumentException"/>
    ///   whose message names the offending key. The exception is allowed to propagate
    ///   unmapped from this DIM (callers/tests assert on it directly).
    /// </summary>
    Task<string> DispatchAsync(string toolName, CancellationToken ct, params (string Key, object? Value)[] args)
    {
        var tuples = args ?? Array.Empty<(string Key, object? Value)>();
        var dict = new Dictionary<string, object?>(tuples.Length);
        foreach (var (key, value) in tuples)
        {
            if (dict.ContainsKey(key))
            {
                throw new ArgumentException(
                    $"Duplicate key '{key}' in tuple argument list.",
                    nameof(args));
            }
            dict.Add(key, value);
        }
        return DispatchAsync(toolName, dict, ct);
    }
}
