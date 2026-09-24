using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace JsonRpcKit.Tests;

/// <summary>
/// What -32602 tells the caller. A union param is checked against every branch, and a client
/// needs the complaints of the branch it meant, not one per branch it did not.
/// </summary>
public sealed class ValidationErrorScenarios
{
    [Fact]
    public async Task A_bad_member_of_the_matching_branch_is_the_only_complaint()
    {
        var errors = await ErrorsAsync("""[{"type":"circle","size":{"value":1,"color":"red"}}]""");

        var error = Assert.Single(errors.EnumerateArray());
        Assert.Equal("/shapes/0/size/color", error.GetProperty("path").GetString());
        Assert.Equal("additionalProperties", error.GetProperty("keyword").GetString());
    }

    [Fact]
    public async Task A_missing_member_of_the_matching_branch_is_the_only_complaint()
    {
        var errors = await ErrorsAsync("""[{"type":"square","size":{"value":1}}]""");

        var error = Assert.Single(errors.EnumerateArray());
        Assert.Equal("/shapes/0", error.GetProperty("path").GetString());
        Assert.Contains("label", error.GetProperty("message").GetString());
    }

    // Each element rejects a different branch, so a rejection is only good for its own element.
    [Fact]
    public async Task Mixed_elements_keep_the_real_complaint()
    {
        var errors = await ErrorsAsync(
            """[{"type":"circle","size":{"value":1}},{"type":"square","size":{"value":1},"label":"x","extra":1}]"""
        );

        var error = Assert.Single(errors.EnumerateArray());
        Assert.Equal("/shapes/1/extra", error.GetProperty("path").GetString());
    }

    [Fact]
    public async Task An_unknown_discriminator_lists_the_values_it_could_have_been()
    {
        var errors = await ErrorsAsync("""[{"type":"hexagon"}]""");

        Assert.Equal(
            ["Expected \"circle\"", "Expected \"square\""],
            errors.EnumerateArray().Select(e => e.GetProperty("message").GetString()).Order()
        );
    }

    // Raw socket: StreamJsonRpc surfaces -32602 as RemoteMethodNotFoundException and drops data.
    private static async Task<JsonElement> ErrorsAsync(string shapes)
    {
        await using var host = await RpcTestHost.StartAsync();
        using var socket = await host
            .Server.CreateWebSocketClient()
            .ConnectAsync(
                new Uri(host.Server.BaseAddress, RpcTestHost.Path.TrimStart('/')),
                default
            );
        var request =
            """{"jsonrpc":"2.0","id":1,"method":"probe.v1.shapes","params":{"shapes":"""
            + shapes
            + "}}";
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(request),
            WebSocketMessageType.Text,
            true,
            default
        );
        var buffer = new byte[64 * 1024];
        var received = await socket.ReceiveAsync(buffer, default);
        using var reply = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
        return reply
            .RootElement.GetProperty("error")
            .GetProperty("data")
            .GetProperty("validationErrors")
            .Clone();
    }
}
