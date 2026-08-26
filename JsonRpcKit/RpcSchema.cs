using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using JsonSchemaNet = Json.Schema.JsonSchema;

namespace JsonRpcKit;

/// <summary>
/// Central JSON Schema generation for RPC types, shared by the OpenRPC generator (the published
/// schema) and <see cref="WsRpcDispatcher"/> (request validation) so the schema a client reads and
/// the schema its requests are validated against can never drift.
/// </summary>
public static class RpcSchema
{
    // Reference types are non-nullable by default (NRT enabled), so treat null-oblivious roots
    // as non-nullable; a constructor parameter is "required" in the schema unless it has a
    // default value or is nullable — so give genuinely optional params defaults / make them
    // nullable, and they drop out of the schema's required set.
    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = AddDescription,
    };

    // Surfaces [Description] attributes (on a property/parameter, or on the type itself) into
    // the schema's "description" keyword, so param and result fields carry documentation. On a
    // record's positional parameter the attribute must target the generated property —
    // [property: Description("…")] — for it to appear here via the property's AttributeProvider.
    private static JsonNode AddDescription(JsonSchemaExporterContext context, JsonNode schema)
    {
        var provider = context.PropertyInfo?.AttributeProvider ?? context.TypeInfo.Type;
        var description = provider
            ?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description;

        // A schema can be the boolean `true`/`false` (e.g. for `object`); only an object node
        // can carry a description.
        if (description is not null && schema is JsonObject obj)
            obj["description"] = description;

        return schema;
    }

    /// <summary>
    /// Generates the JSON Schema for <paramref name="type"/> as a mutable node, using the same
    /// serializer options the dispatcher uses on the wire so the schema matches the actual
    /// wire format.
    /// </summary>
    public static JsonNode Generate(JsonSerializerOptions options, Type type) =>
        WithResolver(options).GetJsonSchemaAsNode(type, ExporterOptions);

    // JsonSchemaExporter needs an explicit TypeInfoResolver: a JsonSerializerOptions instance
    // doesn't attach the reflection-based resolver until it's first used for (de)serialization,
    // and schema generation can happen before that (e.g. at dispatcher construction). Supply one
    // on a copy when absent — the copy keeps the caller's converters so the schema still matches
    // the wire format.
    private static JsonSerializerOptions WithResolver(JsonSerializerOptions options) =>
        options.TypeInfoResolver is not null
            ? options
            : new JsonSerializerOptions(options)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            };

    /// <summary>
    /// Generates and compiles the JSON Schema for <paramref name="type"/> into a validator.
    /// </summary>
    public static JsonSchemaNet CompileValidator(JsonSerializerOptions options, Type type) =>
        JsonSchemaNet.FromText(Generate(options, type).ToJsonString(options));
}
