using System.Reflection;

namespace JsonRpcKit;

/// <summary>
/// Reflects over an assembly to find <see cref="RpcMethodAttribute"/>-decorated public
/// instance methods. Public because it is the definition of "what counts as an RPC method":
/// <see cref="WsRpcDispatcher"/> builds its dispatch registry from it and the OpenRPC
/// generator documents exactly the same set, so a documented method and a dispatchable one
/// can never diverge.
/// </summary>
public static class RpcMethodScanner
{
    /// <summary>
    /// One decorated method. <c>ParamType</c> is its single params object type, or
    /// <see langword="null"/> where it takes none.
    /// </summary>
    public readonly record struct Entry(
        Type TargetType,
        MethodInfo Method,
        RpcMethodAttribute Attribute,
        Type? ParamType
    );

    /// <summary>Finds every RPC method declared in <paramref name="assembly"/>.</summary>
    public static IEnumerable<Entry> Scan(Assembly assembly)
    {
        var types = assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition);

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<RpcMethodAttribute>();
                if (attr is null)
                    continue;

                yield return new Entry(type, method, attr, GetParamType(type, method));
            }
        }
    }

    private static Type? GetParamType(Type type, MethodInfo method)
    {
        var parameters = method.GetParameters();
        return parameters.Length switch
        {
            0 => null,
            1 => parameters[0].ParameterType,
            _ => throw new InvalidOperationException(
                $"{type.Name}.{method.Name}: RPC methods must take zero or one parameter."
            ),
        };
    }
}
