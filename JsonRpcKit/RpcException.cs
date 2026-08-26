namespace JsonRpcKit;

/// <summary>
/// Thrown by an RPC target method to produce a JSON-RPC error response with a
/// specific application-defined error code.
/// </summary>
/// <remarks>
/// The dispatcher catches this and sends:
/// <code>{"jsonrpc":"2.0","id":…,"error":{"code":…,"message":…,"data":…}}</code>
/// Any other exception produces error code <c>-32603</c> (internal error).
/// </remarks>
/// <example>
/// <code>
/// if (result.IsFailure)
///     throw new RpcException("Organization not found", errorCode: 1001);
/// </code>
/// </example>
public sealed class RpcException(string message, int errorCode, object? errorData = null)
    : Exception(message)
{
    /// <summary>Application-defined JSON-RPC error code sent to the caller.</summary>
    public int ErrorCode { get; } = errorCode;

    /// <summary>
    /// Optional structured data included in the error response's <c>data</c> field.
    /// Must be JSON-serializable with the dispatcher's <see cref="System.Text.Json.JsonSerializerOptions"/>.
    /// </summary>
    public object? ErrorData { get; } = errorData;
}
