using StreamJsonRpc;

namespace JsonRpcKit.Tests;

/// <summary>
/// The dispatcher's own contract: what it accepts, what it refuses, and when it gives up on a
/// connection. Nothing here knows what the methods mean.
/// </summary>
public sealed class DispatcherScenarios
{
    [Fact]
    public async Task Params_MissingARequiredMember_AreRejectedBeforeTheHandlerRuns()
    {
        await using var host = await RpcTestHost.StartAsync();
        await using var client = await RpcTestClient.ConnectAsync(host);

        // ids has no default, so the generated schema marks it required. StreamJsonRpc surfaces the
        // reserved -32602 as RemoteMethodNotFoundException; the message is what distinguishes it.
        var ex = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() =>
            client.InvokeAsync<object>("probe.v1.ids", new { })
        );
        Assert.Contains("Invalid params", ex.Message);
    }

    [Fact]
    public async Task Params_OfTheWrongType_AreRejectedBeforeTheHandlerRuns()
    {
        await using var host = await RpcTestHost.StartAsync();
        await using var client = await RpcTestClient.ConnectAsync(host);

        var ex = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() =>
            client.InvokeAsync<object>("probe.v1.ids", new { ids = "not-an-array" })
        );
        Assert.Contains("Invalid params", ex.Message);
    }

    [Fact]
    public async Task Params_OmittedEntirely_BindTheirDefaults()
    {
        await using var host = await RpcTestHost.StartAsync();
        await using var client = await RpcTestClient.ConnectAsync(host);

        // An all-optional params object must validate when `params` is absent, rather than be
        // rejected for a member nobody was required to send.
        var result = await client.InvokeAsync<ProbeFilterResult>("probe.v1.filter", new { });

        Assert.Null(result.NameFilter);
    }

    [Fact]
    public async Task AMessageOverTheSizeLimit_EndsTheConnectionWithoutBufferingIt()
    {
        await using var host = await RpcTestHost.StartAsync(
            new WsRpcDispatcherOptions { MaxMessageBytes = 1024 }
        );
        await using var client = await RpcTestClient.ConnectAsync(host);

        var invoke = client.InvokeAsync<object>(
            "probe.v1.filter",
            new { nameFilter = new string('x', 4096) }
        );

        // Reported at the transport layer, before dispatch: the size is the bytes read so far, so
        // it clears the limit without the whole message ever being held.
        var reported = await host.OversizeReport.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reported > 1024, $"reported {reported} bytes, expected more than the limit");

        Assert.Equal(
            WsRpcDispatcher.MessageTooLargeCloseReason,
            await host.CloseReason.WaitAsync(TimeSpan.FromSeconds(5))
        );

        // The call the client was waiting on faults when the connection goes.
        await Assert.ThrowsAnyAsync<Exception>(() => invoke);
    }
}
