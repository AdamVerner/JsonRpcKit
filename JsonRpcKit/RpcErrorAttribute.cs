namespace JsonRpcKit;

/// <summary>
/// An error the method can answer with, published in its OpenRPC <c>errors</c> list in
/// declaration order.
/// </summary>
/// <remarks>
/// Unsealed so an application can derive its own vocabulary and attach <see cref="Data"/>,
/// serialized with the document's serializer options.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class RpcErrorAttribute(int code, string message) : Attribute
{
    /// <summary>The JSON-RPC <c>error.code</c>.</summary>
    public int Code { get; } = code;

    /// <summary>The JSON-RPC <c>error.message</c>.</summary>
    public string Message { get; } = message;

    /// <summary>Published as the Error Object's <c>data</c>; omitted when <see langword="null"/>.</summary>
    public virtual object? Data => null;
}
