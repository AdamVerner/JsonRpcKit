using System.Text.Json;
using System.Text.Json.Nodes;
using JsonRpcKit.OpenRpc;

namespace JsonRpcKit.Tests;

public sealed class OpenRpcMethodScenarios
{
    public sealed class RpcRefusalAttribute(string domainCode)
        : RpcErrorAttribute(4000, $"Refused: {domainCode}")
    {
        public override object? Data => new { DomainCode = domainCode };
    }

    public sealed class DocumentedTarget
    {
        [RpcMethod("documented.v1.full", Summary = "Full.", Description = "Does **this**.")]
        [RpcError(1001, "Not found")]
        [RpcRefusal("too_many")]
        [RpcError(1003, "Gone")]
        public void Full() { }

        [RpcMethod("documented.v1.bare", Summary = "Bare.")]
        public void Bare() { }
    }

    [Fact]
    public void Method_carries_its_description_and_errors_only_when_declared()
    {
        var options = new OpenRpcDocumentOptions
        {
            SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };
        options.Scan(typeof(DocumentedTarget).Assembly);
        var methods = OpenRpcDocumentBuilder.Build(options)["methods"]!.AsArray();
        JsonObject Method(string name) =>
            methods.Single(m => m!["name"]!.GetValue<string>() == name)!.AsObject();

        var full = Method("documented.v1.full");
        Assert.Equal("Does **this**.", full["description"]!.GetValue<string>());
        Assert.Equal(
            """[{"code":1001,"message":"Not found"},{"code":4000,"message":"Refused: too_many","data":{"domainCode":"too_many"}},{"code":1003,"message":"Gone"}]""",
            full["errors"]!.ToJsonString()
        );

        var bare = Method("documented.v1.bare");
        Assert.False(bare.ContainsKey("description"));
        Assert.False(bare.ContainsKey("errors"));
    }
}
