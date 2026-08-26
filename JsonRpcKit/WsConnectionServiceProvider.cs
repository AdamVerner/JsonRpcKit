using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JsonRpcKit;

// Wraps a fresh per-call IServiceScope but lets connection-level service instances
// (HttpContext, ISubscriptionSubscriber, …) shadow container registrations so target
// constructors receive them via ordinary DI without any base class or manual plumbing.
// The dispatcher creates one of these per RPC invocation and disposes it when done.
internal sealed class WsConnectionServiceProvider(
    IServiceScope scope,
    IReadOnlyDictionary<Type, object> overrides
) : IServiceProvider, IDisposable
{
    public object? GetService(Type serviceType)
    {
        if (overrides.TryGetValue(serviceType, out var v))
            return v;
        return scope.ServiceProvider.GetService(serviceType);
    }

    public void Dispose() => scope.Dispose();
}
