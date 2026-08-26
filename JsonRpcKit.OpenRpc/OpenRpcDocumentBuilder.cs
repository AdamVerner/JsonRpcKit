using System.Text.Json;
using System.Text.Json.Nodes;

namespace JsonRpcKit.OpenRpc;

/// <summary>
/// Builds a document conforming to the OpenRPC specification (https://spec.open-rpc.org/) by
/// reflecting over assemblies for <see cref="RpcMethodAttribute"/>-decorated methods — the same
/// set <see cref="WsRpcDispatcher"/> dispatches to — and deriving each method's params and
/// result schemas from its C# signature.
/// </summary>
/// <remarks>
/// The document is assembled as a <see cref="JsonObject"/> rather than serialized from records:
/// its field names are fixed by the specification, and building the tree directly keeps them
/// from being reshaped by the caller's naming policy. Two things the specification cannot
/// express travel as extensions — category order as <c>x-tag-order</c>, and server-to-client
/// messages as <c>x-notifications</c>.
/// </remarks>
public static class OpenRpcDocumentBuilder
{
    /// <summary>The specification version the emitted document conforms to.</summary>
    public const string SpecVersion = "1.3.2";

    /// <summary>
    /// Where the tag catalogue lives in the document. Methods reference their category as a
    /// Reference Object pointing here.
    /// </summary>
    private const string TagRefPrefix = "#/components/tags/";

    /// <summary>Generates the whole document.</summary>
    public static JsonObject Build(OpenRpcDocumentOptions options)
    {
        var order = options.Tags.Select(tag => tag.Name).ToList();

        var methods = options
            .Assemblies.SelectMany(RpcMethodScanner.Scan)
            .Select(entry => (Entry: entry, Tag: TagOf(entry)))
            // Grouped by category so the document reads in the same order the reference page
            // lays it out; a method whose tag has no catalogue entry sorts last.
            .OrderBy(m => m.Tag is null ? int.MaxValue : IndexOf(order, m.Tag))
            .ThenBy(m => m.Entry.Attribute.Name, StringComparer.Ordinal)
            .Select(m => BuildMethod(options.SerializerOptions, m.Entry, m.Tag))
            .ToJsonArray();

        var document = new JsonObject
        {
            ["openrpc"] = SpecVersion,
            ["info"] = BuildInfo(options),
            ["methods"] = methods,
        };

        if (options.Servers.Count > 0)
            document["servers"] = options.Servers.Select(BuildServer).ToJsonArray();

        if (BuildComponents(options) is { } components)
            document["components"] = components;

        if (order.Count > 0)
            document["x-tag-order"] = order.Select(name => (JsonNode)name!).ToJsonArray();

        if (options.Notifications.Count > 0)
            document["x-notifications"] = options
                .Notifications.Select(n => BuildNotification(options.SerializerOptions, n))
                .ToJsonArray();

        options.Customize?.Invoke(document);
        return document;
    }

    /// <summary>
    /// The category a method is documented under: its declared tag, or the name's first
    /// dot-separated segment. A name in JSON-RPC's reserved <c>$/…</c> namespace has no segment
    /// to take, so it stays uncategorized unless a tag says otherwise.
    /// </summary>
    public static string? DefaultTag(string methodName)
    {
        if (methodName.StartsWith('$'))
            return null;

        var cut = methodName.IndexOf('.');
        return cut > 0 ? methodName[..cut] : null;
    }

    private static string? TagOf(RpcMethodScanner.Entry entry) =>
        entry.Attribute.Tag ?? DefaultTag(entry.Attribute.Name);

    // Ordering key: an unlisted tag sorts after every listed one rather than before it, which
    // List.IndexOf's -1 would do.
    private static int IndexOf(List<string> order, string tag)
    {
        var index = order.IndexOf(tag);
        return index < 0 ? int.MaxValue - 1 : index;
    }

    private static JsonObject BuildInfo(OpenRpcDocumentOptions options)
    {
        var info = new JsonObject { ["title"] = options.Title, ["version"] = options.Version };
        if (!string.IsNullOrWhiteSpace(options.Description))
            info["description"] = options.Description;
        return info;
    }

    private static JsonObject BuildServer(OpenRpcServer server)
    {
        var node = new JsonObject { ["name"] = server.Name, ["url"] = server.Url };
        if (server.Summary is { Length: > 0 })
            node["summary"] = server.Summary;
        if (server.Description is { Length: > 0 })
            node["description"] = server.Description;
        return node;
    }

    // Only categories and errors are reusable here; params and result schemas are inlined, so a
    // reader never has to chase a $ref to find out what a call takes.
    private static JsonObject? BuildComponents(OpenRpcDocumentOptions options)
    {
        var components = new JsonObject();

        if (options.Tags.Count > 0)
        {
            var tags = new JsonObject();
            foreach (var tag in options.Tags)
            {
                var node = new JsonObject
                {
                    ["name"] = tag.Name,
                    // The Tag Object is sealed to name/description/externalDocs, but does accept
                    // extensions — so the reader-facing heading rides on one.
                    ["x-displayName"] = tag.DisplayName,
                };
                if (tag.Description is { Length: > 0 })
                    node["description"] = tag.Description;
                tags[tag.Name] = node;
            }
            components["tags"] = tags;
        }

        if (options.Errors.Count > 0)
        {
            var errors = new JsonObject();
            foreach (var error in options.Errors)
                errors[error.Name] = new JsonObject
                {
                    ["code"] = error.Code,
                    ["message"] = error.Message,
                };
            components["errors"] = errors;
        }

        return components.Count > 0 ? components : null;
    }

    private static JsonObject BuildMethod(
        JsonSerializerOptions options,
        RpcMethodScanner.Entry entry,
        string? tag
    )
    {
        var method = BuildMethodObject(
            options,
            entry.Attribute.Name,
            entry.Attribute.Summary,
            entry.ParamType,
            UnwrapAsyncResultType(entry.Method.ReturnType)
        );

        if (tag is not null)
            // A Reference Object may carry nothing but $ref, so the catalogue entry is the only
            // place a category's prose lives.
            method["tags"] = new JsonArray(new JsonObject { ["$ref"] = TagRefPrefix + tag });

        return method;
    }

    private static JsonObject BuildNotification(
        JsonSerializerOptions options,
        OpenRpcNotification notification
    )
    {
        var method = BuildMethodObject(
            options,
            notification.Name,
            notification.Summary,
            notification.ParamType,
            resultType: null
        );
        if (notification.Description is { Length: > 0 })
            method["description"] = notification.Description;
        return method;
    }

    /// <summary>
    /// Builds a single Method Object from a name, summary, and the C# param and result types.
    /// Public so callers can describe message shapes the scanner does not emit as methods; pass
    /// <paramref name="resultType"/> <see langword="null"/> for a message with no response.
    /// </summary>
    public static JsonObject BuildMethodObject(
        JsonSerializerOptions options,
        string name,
        string summary,
        Type? paramType,
        Type? resultType
    )
    {
        var method = new JsonObject
        {
            ["name"] = name,
            ["summary"] = summary,
            // The wire protocol always sends params as a by-name object, so a client builds it by
            // descriptor name rather than by position.
            ["paramStructure"] = "by-name",
            ["params"] = BuildParams(options, paramType),
        };

        if (resultType is not null)
            method["result"] = ContentDescriptor(
                "result",
                RpcSchema.Generate(options, resultType),
                required: true
            );

        return method;
    }

    // An RPC method takes a single C# object that travels on the wire as the JSON-RPC by-name
    // `params` object. OpenRPC models params as a list of Content Descriptors, one per named
    // parameter — so the object schema's properties are split into descriptors. That is exactly
    // how an OpenRPC client reconstructs the params object we expect on the wire.
    private static JsonArray BuildParams(JsonSerializerOptions options, Type? paramType)
    {
        if (paramType is null)
            return [];

        var schema = RpcSchema.Generate(options, paramType);

        // Defensive: every param type is expected to be a flat object DTO. A non-object param —
        // or one the exporter renders without a properties map — is surfaced as a single
        // descriptor rather than dropped.
        if (schema is not JsonObject obj || obj["properties"] is not JsonObject properties)
            return [ContentDescriptor("params", schema, required: true)];

        var required =
            (obj["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet() ?? [];

        return properties
            // The specification requires every optional param to come after every required one.
            .OrderByDescending(p => required.Contains(p.Key))
            // DeepClone so each property schema is detached from the parent object it was read
            // from — a JsonNode can only have one parent.
            .Select(p => ContentDescriptor(p.Key, p.Value!.DeepClone(), required.Contains(p.Key)))
            .ToJsonArray();
    }

    private static JsonObject ContentDescriptor(string name, JsonNode schema, bool required) =>
        new()
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = schema,
        };

    private static Type? UnwrapAsyncResultType(Type returnType)
    {
        if (
            returnType == typeof(void)
            || returnType == typeof(Task)
            || returnType == typeof(ValueTask)
        )
            return null;

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
                return returnType.GetGenericArguments()[0];
        }

        return returnType;
    }

    private static JsonArray ToJsonArray<T>(this IEnumerable<T> nodes)
        where T : JsonNode => new(nodes.Cast<JsonNode>().ToArray());
}
