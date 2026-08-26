namespace JsonRpcKit;

/// <summary>
/// Marks a public instance method as a JSON-RPC endpoint and supplies the metadata
/// the dispatcher and OpenRPC document builder need.
/// </summary>
/// <remarks>
/// Place this on methods that take zero or one parameter object and return
/// <c>void</c>, <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c>, or
/// <c>ValueTask&lt;T&gt;</c>. The single parameter is deserialized from the
/// JSON-RPC <c>params</c> object; the return value (if any) becomes the
/// JSON-RPC <c>result</c>.
/// </remarks>
/// <example>
/// <code>
/// [RpcMethod("organization.v1.list", Summary = "List visible organizations.")]
/// public Task&lt;IReadOnlyList&lt;OrganizationInfo&gt;&gt; ListAsync(ListParams p)
///     => _service.ListAsync(p.NameFilter);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcMethodAttribute(string name) : Attribute
{
    /// <summary>Wire method name, e.g. <c>"organization.v1.list"</c>.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// One-line description of what the method does. Included in the OpenRPC document
    /// if one is generated from this assembly.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the method may be called before the connection has
    /// been authenticated. Defaults to <see langword="false"/>; unauthenticated calls
    /// to restricted methods receive error code 1002 and the connection is closed.
    /// </summary>
    public bool AllowUnauthenticated { get; init; }

    /// <summary>
    /// Which category the method is documented under. Defaults to the name's first
    /// dot-separated segment (<c>organization.v1.list</c> → <c>organization</c>), which is
    /// wrong only where the name doesn't follow that shape — a JSON-RPC reserved
    /// <c>$/…</c> method, say. Categories are described in the OpenRPC document's tag
    /// catalogue; a name that has no entry there is still listed, just without prose.
    /// </summary>
    public string? Tag { get; init; }
}
