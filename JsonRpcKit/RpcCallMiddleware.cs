using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace JsonRpcKit;

/// <summary>One call as a middleware sees it: resolved, authenticated, params bound, not yet invoked.</summary>
public sealed class RpcCallContext
{
    internal RpcCallContext(
        string methodName,
        MethodInfo method,
        object target,
        object?[] arguments,
        IServiceProvider services,
        HttpContext httpContext
    )
    {
        MethodName = methodName;
        Method = method;
        Target = target;
        Arguments = arguments;
        Services = services;
        HttpContext = httpContext;
    }

    /// <summary>The wire name, as registered by <see cref="RpcMethodAttribute"/>.</summary>
    public string MethodName { get; }

    /// <summary>The handler about to run; its attributes are where a middleware reads per-method marks.</summary>
    public MethodInfo Method { get; }

    /// <summary>The target instance the handler runs on, built in this call's scope.</summary>
    public object Target { get; }

    /// <summary>The bound params object, or empty for a parameterless method.</summary>
    public object?[] Arguments { get; }

    /// <summary>This call's service scope, with the connection-scoped services shadowing DI.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The WebSocket's own request, which carries the connection's <c>User</c>.</summary>
    public HttpContext HttpContext { get; }
}

/// <summary>Runs the rest of the pipeline and returns the handler's result.</summary>
public delegate Task<object?> RpcCallDelegate(RpcCallContext context);

/// <summary>
/// Wraps a call. Call <paramref name="next"/> to continue, throw <see cref="RpcException"/> to refuse,
/// or return a result of your own.
/// </summary>
public delegate Task<object?> RpcCallMiddleware(RpcCallContext context, RpcCallDelegate next);
