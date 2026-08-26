namespace JsonRpcKit;

/// <summary>
/// Tuning limits for a <see cref="WsRpcDispatcher"/> connection. Bind from configuration
/// or construct directly; the defaults are conservative and suit a single connection.
/// </summary>
public sealed class WsRpcDispatcherOptions
{
    /// <summary>
    /// Maximum size, in bytes, of a single inbound JSON-RPC message (summed across all
    /// WebSocket frames). When a message exceeds this the dispatcher stops reading and
    /// closes the connection rather than buffer or drain an unbounded stream, so a client
    /// cannot exhaust server memory. Defaults to 10 KiB.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 10 * 1024;

    /// <summary>
    /// Maximum number of handlers running concurrently on one connection. The read loop
    /// stops pulling new messages once this many are in flight, applying backpressure.
    /// Defaults to 10.
    /// </summary>
    public int MaxConcurrentCalls { get; init; } = 10;
}
