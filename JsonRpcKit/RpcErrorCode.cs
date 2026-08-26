namespace JsonRpcKit;

/// <summary>
/// The JSON-RPC error codes emitted by <see cref="WsRpcDispatcher"/> itself — as opposed
/// to application-defined codes, which a target chooses freely when throwing
/// <see cref="RpcException"/>.
/// </summary>
/// <remarks>
/// <see cref="MethodNotFound"/> and <see cref="InternalError"/> are the JSON-RPC 2.0
/// reserved codes. <see cref="NotAuthenticated"/> sits in the application range because
/// the JSON-RPC spec reserves no code for "auth required"; it is the one dispatcher-level
/// signal an application must be aware of.
/// </remarks>
public enum RpcErrorCode
{
    /// <summary>JSON-RPC 2.0 reserved: the requested method name has no registered handler.</summary>
    MethodNotFound = -32601,

    /// <summary>
    /// JSON-RPC 2.0 reserved: the request's <c>params</c> failed schema validation (wrong
    /// type, missing a required field, …). The offending details are in the error's
    /// <c>data.validationErrors</c>.
    /// </summary>
    InvalidParams = -32602,

    /// <summary>JSON-RPC 2.0 reserved: a handler threw an exception other than <see cref="RpcException"/>.</summary>
    InternalError = -32603,

    /// <summary>
    /// A method that is not <see cref="RpcMethodAttribute.AllowUnauthenticated"/> was
    /// called before the connection authenticated. The connection is then closed.
    /// </summary>
    NotAuthenticated = 1002,
}
