using System.Net.WebSockets;
using System.Text.Json;
using StreamJsonRpc;

namespace JsonRpcKit.Tests;

/// <summary>
/// A JSON-RPC 2.0 client over the test host's WebSocket. StreamJsonRpc rather than a hand-rolled
/// client on purpose: the dispatcher's whole claim is that a compliant client needs no special
/// handling, and this is one.
/// </summary>
internal sealed class RpcTestClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly JsonRpc _jsonRpc;

    private RpcTestClient(WebSocket socket, JsonRpc jsonRpc)
    {
        _socket = socket;
        _jsonRpc = jsonRpc;
    }

    internal static async Task<RpcTestClient> ConnectAsync(
        RpcTestHost host,
        CancellationToken ct = default
    )
    {
        var socket = await host
            .Server.CreateWebSocketClient()
            .ConnectAsync(new Uri(host.Server.BaseAddress, RpcTestHost.Path.TrimStart('/')), ct);

        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };
        var client = new RpcTestClient(
            socket,
            new JsonRpc(new WebSocketMessageHandler(socket, formatter))
        );
        client._jsonRpc.StartListening();

        return client;
    }

    internal Task<T> InvokeAsync<T>(string method, object args, CancellationToken ct = default) =>
        _jsonRpc.InvokeWithParameterObjectAsync<T>(method, args, ct);

    internal Task Completion => _jsonRpc.Completion;

    public async ValueTask DisposeAsync()
    {
        _jsonRpc.Dispose();
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "test done",
                    cts.Token
                );
            }
            catch
            {
                // Already closing from the server side, or the reply timed out.
            }
        }
        _socket.Dispose();
    }
}
