namespace JsonRpcKit;

/// <summary>How one call ended, for the host's metrics and records.</summary>
/// <param name="Method">The resolved method name, or <c>"unknown"</c> when none matched.</param>
/// <param name="Outcome">
/// <c>"success"</c>, <c>"error"</c> (an <see cref="RpcException"/>), <c>"exception"</c> (anything
/// else a handler threw), <c>"unauthorized"</c> or <c>"not_found"</c>.
/// </param>
/// <param name="ErrorCode">The JSON-RPC error code sent, on <c>"error"</c> and <c>"exception"</c>.</param>
/// <param name="ErrorData">The error's <c>data</c> as sent, when there was any.</param>
/// <param name="Exception">The escaped exception, on <c>"exception"</c> only.</param>
public sealed record RpcCallOutcome(
    string Method,
    TimeSpan Elapsed,
    string Outcome,
    int? ErrorCode = null,
    object? ErrorData = null,
    Exception? Exception = null
);
