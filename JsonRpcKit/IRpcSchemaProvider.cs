using System.Text.Json.Nodes;

namespace JsonRpcKit;

/// <summary>
/// A converter that can describe the JSON it writes. <c>JsonSchemaExporter</c> emits an
/// accept-anything schema for a type with a custom converter, so a converter implementing this
/// supplies the schema the document publishes and requests are validated against.
/// </summary>
public interface IRpcSchemaProvider
{
    JsonNode GetSchema();
}
