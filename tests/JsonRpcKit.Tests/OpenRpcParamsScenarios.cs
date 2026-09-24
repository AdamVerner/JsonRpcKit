using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JsonRpcKit.OpenRpc;

namespace JsonRpcKit.Tests;

/// <summary>
/// A params object is published as one schema per parameter, and every pointer inside one must
/// resolve from that parameter's own schema, which is what a client validating it has.
/// </summary>
public sealed class OpenRpcParamsScenarios
{
    // The dashboard configuration's shape: every branch of a union reuses one body type, and the
    // exporter writes the later copies as a $ref to the first.
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
    [JsonDerivedType(typeof(DeviceList), "deviceList")]
    [JsonDerivedType(typeof(AlertList), "alertList")]
    public abstract record Widget;

    public sealed record DeviceList(Body Body) : Widget;

    public sealed record AlertList(Body Body) : Widget;

    public sealed record Body(IReadOnlyList<string> Items);

    public sealed record SameParam(List<Widget> Config);

    public sealed record Pair(Body Left, Body Right);

    public sealed record CrossParam(Pair First, Pair Second);

    public sealed record Tree(string Name, IReadOnlyList<Tree> Children);

    public sealed record Trees(Tree First, Tree Second);

    [Theory]
    [InlineData(typeof(SameParam), "config")]
    [InlineData(typeof(CrossParam), "second")]
    [InlineData(typeof(Trees), "second")]
    public void Every_pointer_resolves_inside_its_own_parameter(Type type, string name)
    {
        var schema = Param(type, name);

        var pointers = Pointers(schema).ToList();
        Assert.NotEmpty(pointers);
        Assert.All(pointers, p => Assert.NotNull(Resolve(schema, p)));
    }

    private static JsonObject Param(Type type, string name)
    {
        var method = OpenRpcDocumentBuilder.BuildMethodObject(
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            "probe.v1.params",
            "",
            type,
            null
        );
        return method["params"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == name)![
            "schema"
        ]!.AsObject();
    }

    private static IEnumerable<string> Pointers(JsonNode node) =>
        node switch
        {
            JsonObject o => (
                o["$ref"] is { } r ? [r.GetValue<string>()] : Array.Empty<string>()
            ).Concat(o.SelectMany(p => p.Value is null ? [] : Pointers(p.Value))),
            JsonArray a => a.SelectMany(n => n is null ? [] : Pointers(n)),
            _ => [],
        };

    private static JsonNode? Resolve(JsonNode root, string pointer) =>
        pointer
            .TrimStart('#')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Aggregate<string, JsonNode?>(
                root,
                (n, t) =>
                    n switch
                    {
                        JsonObject o => o[t.Replace("~1", "/").Replace("~0", "~")],
                        JsonArray a => a[int.Parse(t)],
                        _ => null,
                    }
            );
}
