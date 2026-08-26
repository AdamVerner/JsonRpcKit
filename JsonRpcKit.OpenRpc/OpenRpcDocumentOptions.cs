using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonRpcKit.OpenRpc;

/// <summary>
/// Everything the generator cannot work out by reflecting over the RPC methods: the prose, the
/// categories, the server list, and any vocabulary of the application's own.
/// </summary>
public sealed class OpenRpcDocumentOptions
{
    /// <summary>The document's <c>info.title</c>.</summary>
    public string Title { get; set; } = "JSON-RPC API";

    /// <summary>
    /// The API's own version, not the OpenRPC specification version.
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Markdown shown at the top of a reference page, before any method.</summary>
    public string? Description { get; set; }

    /// <summary>Where the document is served. Also what a reference page fetches.</summary>
    public string DocumentPath { get; set; } = "/openrpc.json";

    /// <summary>
    /// The options the dispatcher (de)serializes wire messages with. Params and result schemas
    /// are derived through them, so the published schema matches the actual wire format;
    /// document field names are fixed by the specification and unaffected by any naming policy
    /// set here.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>Scanned for <see cref="RpcMethodAttribute"/>-decorated methods.</summary>
    public IList<Assembly> Assemblies { get; } = [];

    /// <summary>
    /// The category catalogue, in the order a reader should meet the categories — the one
    /// ordering the specification has no field for, so it also travels as the document's
    /// <c>x-tag-order</c>. A method whose tag has no entry here is still published; it just
    /// gets no prose, and sorts after the described categories.
    /// </summary>
    public IList<OpenRpcTag> Tags { get; } = [];

    /// <summary>Where the socket this document describes is reachable.</summary>
    public IList<OpenRpcServer> Servers { get; } = [];

    /// <summary>Server-to-client messages, published under <c>x-notifications</c>.</summary>
    public IList<OpenRpcNotification> Notifications { get; } = [];

    /// <summary>
    /// The error codes the API answers with, published under <c>components/errors</c>.
    /// </summary>
    public IList<OpenRpcError> Errors { get; } = [];

    /// <summary>
    /// Applied to the finished document, for anything this library has no business knowing —
    /// an application's own stability vocabulary, say. The whole document is passed, so a
    /// customization can reach individual methods by name.
    /// </summary>
    public Action<JsonObject>? Customize { get; set; }

    /// <summary>Declares a server-to-client message whose payload is <typeparamref name="TParams"/>.</summary>
    public OpenRpcDocumentOptions Notification<TParams>(
        string name,
        string summary,
        string? description = null
    )
    {
        Notifications.Add(new OpenRpcNotification(name, summary, typeof(TParams), description));
        return this;
    }

    /// <summary>Describes a category. Call once per category, in reading order.</summary>
    public OpenRpcDocumentOptions Tag(string name, string displayName, string? description = null)
    {
        Tags.Add(new OpenRpcTag(name, displayName, description));
        return this;
    }

    /// <summary>Declares an error code, for the reference page's error table.</summary>
    public OpenRpcDocumentOptions Error(string name, int code, string message)
    {
        Errors.Add(new OpenRpcError(name, code, message));
        return this;
    }

    /// <summary>
    /// Scans <paramref name="assembly"/> for RPC methods. Defaults to the calling assembly.
    /// </summary>
    public OpenRpcDocumentOptions Scan(Assembly? assembly = null)
    {
        Assemblies.Add(assembly ?? Assembly.GetCallingAssembly());
        return this;
    }
}
