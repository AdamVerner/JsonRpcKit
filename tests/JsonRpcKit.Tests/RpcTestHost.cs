using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace JsonRpcKit.Tests;

/// <summary>
/// The smallest host that can serve a <see cref="WsRpcDispatcher"/>: one WebSocket route, no
/// authentication, and the probe targets below as the method surface. Serves one connection, so a
/// test that wants a second dispatcher starts a second host.
/// </summary>
internal sealed class RpcTestHost : IAsyncDisposable
{
    internal const string Path = "/rpc";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    private readonly WsRpcDispatcherOptions _options;

    private readonly TaskCompletionSource<string> _closeSignal = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource<long> _tooLarge = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private RpcTestHost(WebApplication app, WsRpcDispatcherOptions options)
    {
        _app = app;
        _options = options;
    }

    internal TestServer Server => (TestServer)_app.Services.GetRequiredService<IServer>();

    /// <summary>The reason the dispatcher asked for the connection to be closed.</summary>
    internal Task<string> CloseReason => _closeSignal.Task;

    /// <summary>The size the dispatcher reported to <c>OnMessageTooLarge</c>.</summary>
    internal Task<long> OversizeReport => _tooLarge.Task;

    internal static async Task<RpcTestHost> StartAsync(WsRpcDispatcherOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        var host = new RpcTestHost(app, options ?? new WsRpcDispatcherOptions());

        app.UseWebSockets();
        app.MapGet(Path, host.ServeAsync);

        await app.StartAsync();
        return host;
    }

    // Stands in for the consumer's endpoint: accept, dispatch, and close when the dispatcher asks.
    // Deliberately does nothing else — anything a real host adds on top (a closing notification, a
    // close status of its own) is that host's behaviour to test, not the dispatcher's.
    private async Task ServeAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();

        var dispatcher = new WsRpcDispatcher(
            socket,
            context.RequestServices.GetRequiredService<IServiceScopeFactory>(),
            context,
            _closeSignal,
            JsonOptions,
            typeof(RpcTestHost).Assembly,
            _options
        );
        dispatcher.OnMessageTooLarge = bytes => _tooLarge.TrySetResult(bytes);
        dispatcher.StartListening();

        await Task.WhenAny(dispatcher.Completion, _closeSignal.Task);

        if (_closeSignal.Task.IsCompleted)
            await dispatcher.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                _closeSignal.Task.Result
            );

        await Task.WhenAny(dispatcher.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record ProbeIdsParams(IReadOnlyList<int> Ids);

internal sealed record ProbeFilterParams(string? NameFilter = null);

internal sealed record ProbeFilterResult(string? NameFilter);

// Scanned out of this assembly by the dispatcher. AllowUnauthenticated keeps the host free of an
// auth story it has nothing to say about: the gate runs before params binding either way.
internal sealed class ProbeTarget
{
    [RpcMethod("probe.v1.ids", Summary = "Echo the ids it was given.", AllowUnauthenticated = true)]
    public IReadOnlyList<int> Ids(ProbeIdsParams request) => request.Ids;

    [RpcMethod(
        "probe.v1.filter",
        Summary = "Echo the optional filter it was given.",
        AllowUnauthenticated = true
    )]
    public ProbeFilterResult Filter(ProbeFilterParams request) => new(request.NameFilter);
}
