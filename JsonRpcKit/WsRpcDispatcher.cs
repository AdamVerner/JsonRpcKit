using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Json.Schema;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JsonRpcKit;

/// <summary>
/// A thin JSON-RPC 2.0 dispatcher over a raw WebSocket connection.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher scans a caller-supplied <see cref="Assembly"/> for public instance
/// methods decorated with <see cref="RpcMethodAttribute"/> and builds a method registry
/// from them. On each incoming JSON-RPC request it:
/// </para>
/// <list type="number">
///   <item>Checks authentication (unless <see cref="RpcMethodAttribute.AllowUnauthenticated"/> is set).</item>
///   <item>Creates a fresh DI scope so every call is isolated — no shared <c>DbContext</c> or unit-of-work between calls.</item>
///   <item>Resolves the target class from that scope via <see cref="ActivatorUtilities"/>, injecting constructor dependencies from DI plus any connection-level services registered with <see cref="AddConnectionService{T}"/>.</item>
///   <item>Deserializes the <c>params</c> JSON object, invokes the method, and sends the result.</item>
/// </list>
/// <para>
/// The wire format is standard JSON-RPC 2.0 over WebSocket, so any compliant client
/// (browser, StreamJsonRpc, etc.) works without modification.
/// </para>
/// <para><b>Typical setup (in an ASP.NET Core endpoint):</b></para>
/// <code>
/// var closeSignal = new TaskCompletionSource&lt;string&gt;(TaskCreationOptions.RunContinuationsAsynchronously);
/// using var socket = await context.WebSockets.AcceptWebSocketAsync();
///
/// var dispatcher = new WsRpcDispatcher(
///     socket, scopeFactory, context, closeSignal,
///     myJsonOptions, typeof(MyRpcTarget).Assembly);
///
/// // Register connection-scoped services (e.g. per-connection subscriber identity).
/// var subscriber = new MySubscriber(dispatcher);
/// dispatcher.AddConnectionService&lt;IMySubscriber&gt;(subscriber);
///
/// dispatcher.StartListening();
/// await dispatcher.Completion;
/// </code>
/// <para><b>Target class example:</b></para>
/// <code>
/// // Scoped or transient in DI — constructor-injected from the per-call scope.
/// internal sealed class OrganizationTarget(IOrganizationService orgs)
/// {
///     [RpcMethod("org.v1.list", Summary = "List organizations.")]
///     public Task&lt;IReadOnlyList&lt;OrgInfo&gt;&gt; ListAsync(ListParams p)
///         => orgs.ListAsync(p.NameFilter);
/// }
/// </code>
/// </remarks>
public sealed class WsRpcDispatcher
{
    private sealed record MethodEntry(
        ObjectFactory Factory,
        MethodInfo Method,
        Type? ParamType,
        JsonSchema? ParamsSchema,
        bool AllowUnauthenticated
    );

    // List output collects every validation failure (not just the first) so the client's
    // error.data reports all offending fields at once.
    private static readonly EvaluationOptions ParamsEvaluationOptions = new()
    {
        OutputFormat = OutputFormat.List,
    };

    // Stand-in instance used to validate a call that omitted `params` entirely: an empty
    // object still satisfies a schema whose members are all optional, but fails one with
    // required members. Held in a static field so its JsonElement stays rooted.
    private static readonly JsonDocument EmptyParamsDocument = JsonDocument.Parse("{}");

    // The method registry (assembly scan + one compiled JSON-Schema validator per method) is
    // immutable for a given (assembly, options) and every entry is thread-safe to share, yet
    // building it means reflecting over the assembly and generating+compiling a schema per
    // method. Cache it so that cost is paid once per process rather than on every connection —
    // it was previously rebuilt for each WebSocket the dispatcher served. Keyed by the options
    // instance (reference identity is exactly right: a different options object can produce a
    // different schema).
    private static readonly ConcurrentDictionary<
        (Assembly, JsonSerializerOptions),
        IReadOnlyDictionary<string, MethodEntry>
    > RegistryCache = new();

    private readonly WebSocket _socket;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HttpContext _httpContext;
    private readonly TaskCompletionSource<string> _closeSignal;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly WsRpcDispatcherOptions _options;
    private readonly IReadOnlyDictionary<string, MethodEntry> _methods;

    // Copy-on-write: reads take the current reference lock-free, writes swap in a new
    // dictionary under _connectionServicesLock. The published dictionary is never mutated,
    // so a per-call read can never observe a torn state even if AddConnectionService runs
    // concurrently with the read loop.
    private volatile IReadOnlyDictionary<Type, object> _connectionServices;
    private readonly object _connectionServicesLock = new();

    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    /// <param name="socket">The accepted WebSocket for this connection.</param>
    /// <param name="scopeFactory">Used to create a fresh DI scope per RPC call.</param>
    /// <param name="httpContext">
    /// The connection's <see cref="HttpContext"/>. Checked for
    /// <see cref="System.Security.Principal.IIdentity.IsAuthenticated"/> on every
    /// non-<see cref="RpcMethodAttribute.AllowUnauthenticated"/> call, and automatically
    /// available for injection into target constructors.
    /// </param>
    /// <param name="closeSignal">
    /// Completed (with a reason string) when the dispatcher decides to close the
    /// connection — e.g. on an unauthenticated call to a restricted method. The caller
    /// should await this alongside <see cref="Completion"/> to send
    /// <c>connection.closing</c> and call <see cref="WebSocket.CloseAsync"/>.
    /// </param>
    /// <param name="jsonOptions">
    /// Used for both request parameter deserialization and response serialization.
    /// </param>
    /// <param name="targetAssembly">
    /// Assembly scanned at construction time for types with
    /// <see cref="RpcMethodAttribute"/>-decorated public instance methods.
    /// </param>
    /// <param name="options">
    /// Per-connection limits (max message size, max concurrent calls). Pass
    /// <see langword="null"/> for the defaults.
    /// </param>
    public WsRpcDispatcher(
        WebSocket socket,
        IServiceScopeFactory scopeFactory,
        HttpContext httpContext,
        TaskCompletionSource<string> closeSignal,
        JsonSerializerOptions jsonOptions,
        Assembly targetAssembly,
        WsRpcDispatcherOptions? options = null
    )
    {
        _socket = socket;
        _scopeFactory = scopeFactory;
        _httpContext = httpContext;
        _closeSignal = closeSignal;
        _jsonOptions = jsonOptions;
        _options = options ?? new WsRpcDispatcherOptions();
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentCalls, _options.MaxConcurrentCalls);
        _methods = RegistryCache.GetOrAdd(
            (targetAssembly, _jsonOptions),
            static key => BuildRegistry(key.Item1, key.Item2)
        );
        _connectionServices = new Dictionary<Type, object> { [typeof(HttpContext)] = httpContext };
    }

    /// <summary>
    /// Completes when the WebSocket connection closes, either normally or after a
    /// forced close initiated via the close signal.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Registers a connection-scoped service instance that will be injected into target
    /// constructors in preference to any DI container registration of the same type.
    /// </summary>
    /// <remarks>
    /// Call this for every per-connection singleton (e.g. a subscription subscriber whose
    /// identity is used as a registry key) before calling <see cref="StartListening"/>.
    /// <see cref="HttpContext"/> is always registered automatically.
    /// </remarks>
    /// <typeparam name="T">The service type targets will declare in their constructors.</typeparam>
    /// <param name="instance">The connection-scoped instance to provide.</param>
    public void AddConnectionService<T>(T instance)
        where T : notnull
    {
        // Copy-on-write under the lock so the reference the read loop may be reading stays
        // immutable. Safe to call after StartListening.
        lock (_connectionServicesLock)
        {
            var next = new Dictionary<Type, object>(_connectionServices) { [typeof(T)] = instance };
            _connectionServices = next;
        }
    }

    /// <summary>
    /// Invoked after every handled message with the resolved method name (or
    /// <c>"unknown"</c> if no registered method matched), elapsed handling time, and a
    /// terminal outcome (<c>"success"</c>, <c>"error"</c>, <c>"exception"</c>,
    /// <c>"unauthorized"</c>, or <c>"not_found"</c>). Set before <see cref="StartListening"/>.
    /// </summary>
    public Action<string, TimeSpan, string>? OnCallHandled { get; set; }

    /// <summary>
    /// Invoked when an inbound message exceeds <see cref="WsRpcDispatcherOptions.MaxMessageBytes"/>.
    /// The dispatcher stops reading and signals a connection close via the close signal
    /// (reason <see cref="MessageTooLargeCloseReason"/>); the argument is the number of bytes
    /// received before the limit was hit. Set before <see cref="StartListening"/> — use it to
    /// log the event.
    /// </summary>
    public Action<long>? OnMessageTooLarge { get; set; }

    /// <summary>Starts the background read loop. Call once after all setup is complete.</summary>
    public void StartListening() => _ = Task.Run(ReadLoopAsync);

    /// <summary>
    /// Sends a server-to-client JSON-RPC notification (no id, no response expected).
    /// Safe to call concurrently with the read loop — writes are serialized internally.
    /// </summary>
    public Task SendNotificationAsync(string method, object paramsObj) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = paramsObj,
            }
        );

    /// <summary>
    /// Initiates a server-side close, sending the close frame serialized through the same lock
    /// as every other send so it can't collide with an in-flight handler's response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether it performs the full close handshake or only its sending half depends on whether
    /// the read loop is still running — because the read loop owns the socket's <em>receive</em>
    /// side:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Read loop still running</b> (the common kick — auth deadline, unauthenticated
    ///     call): send the close frame only (<see cref="WebSocket.CloseOutputAsync"/>). The full
    ///     <see cref="WebSocket.CloseAsync"/> also <em>receives</em>, issuing a second
    ///     <c>ReceiveAsync</c> that races the read loop's outstanding one on the same socket;
    ///     under load that race wedges the connection half-open and a client awaiting a response
    ///     hangs forever. Left to itself, the read loop observes the peer's close reply and
    ///     completes <see cref="Completion"/>.
    ///   </item>
    ///   <item>
    ///     <b>Read loop already stopped</b> (e.g. after an oversized message broke the loop):
    ///     no one is receiving, so do the full handshake here — otherwise the peer's close reply
    ///     is never drained and, once the socket is disposed, the peer's own close faults.
    ///   </item>
    /// </list>
    /// <para>
    /// Await <see cref="Completion"/> (with a timeout) after calling this to know the read loop
    /// has drained the peer's reply before disposing the socket.
    /// </para>
    /// </remarks>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;

            if (_completion.Task.IsCompleted)
                await _socket.CloseAsync(status, reason, CancellationToken.None);
            else
                await _socket.CloseOutputAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Peer already vanished — the read loop will observe the drop and complete.
        }
        catch (OperationCanceledException) { }
        finally
        {
            _sendLock.Release();
        }
    }

    private static Dictionary<string, MethodEntry> BuildRegistry(
        Assembly assembly,
        JsonSerializerOptions jsonOptions
    )
    {
        var registry = new Dictionary<string, MethodEntry>(StringComparer.Ordinal);
        foreach (var entry in RpcMethodScanner.Scan(assembly))
        {
            // Compile the params validator once per method — the same schema the OpenRPC
            // document publishes — so every call is validated without regenerating it.
            var paramsSchema = entry.ParamType is null
                ? null
                : RpcSchema.CompileValidator(jsonOptions, entry.ParamType);

            // Resolve the target's constructor once. CreateFactory caches the ctor selection
            // that CreateInstance would otherwise redo per call; the per-call provider (which
            // shadows DI with connection-scoped instances) is still supplied at invocation time.
            var factory = ActivatorUtilities.CreateFactory(entry.TargetType, Type.EmptyTypes);

            var methodEntry = new MethodEntry(
                factory,
                entry.Method,
                entry.ParamType,
                paramsSchema,
                entry.Attribute.AllowUnauthenticated
            );
            if (!registry.TryAdd(entry.Attribute.Name, methodEntry))
                throw new InvalidOperationException(
                    $"Duplicate RPC method name '{entry.Attribute.Name}'."
                );
        }
        return registry;
    }

    /// <summary>
    /// Close reason the dispatcher sets on the close signal when an inbound message exceeds
    /// <see cref="WsRpcDispatcherOptions.MaxMessageBytes"/>. The hosting endpoint reads this
    /// to send <c>connection.closing</c> and pick a WebSocket close code.
    /// </summary>
    public const string MessageTooLargeCloseReason = "message_too_large";

    private enum ReceiveResultKind
    {
        Message,
        ConnectionClosed,
        TooLarge,
    }

    private readonly record struct ReceiveOutcome(
        ReceiveResultKind Kind,
        byte[]? Message,
        long ReceivedBytes
    );

    private async Task ReadLoopAsync()
    {
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var outcome = await ReceiveMessageAsync();
                if (outcome.Kind == ReceiveResultKind.ConnectionClosed)
                {
                    // The client initiated the close. Complete the closing handshake by sending
                    // our own close frame back; a client that awaits the reply (the full
                    // WebSocket.CloseAsync — which is what StreamJsonRpc and our own test client
                    // issue) otherwise blocks forever waiting for it, and the connection (and any
                    // Dispose awaiting it) hangs. No-op if we already sent a close frame.
                    await CompleteClosingHandshakeAsync();
                    break;
                }

                // A message over the size cap is not dropped silently: rather than keep
                // reading a potentially unbounded stream, stop and close the connection.
                // The callback lets the host log it; setting the close signal hands off to
                // the endpoint's close path, which tells the client (connection.closing).
                if (outcome.Kind == ReceiveResultKind.TooLarge)
                {
                    OnMessageTooLarge?.Invoke(outcome.ReceivedBytes);
                    _closeSignal.TrySetResult(MessageTooLargeCloseReason);
                    break;
                }

                // Cap concurrent handlers; this awaits when the connection is saturated,
                // which stops us reading more messages and applies backpressure.
                await _concurrency.WaitAsync();

                // Fire-and-forget per message so a slow handler doesn't block reads;
                // JSON-RPC allows out-of-order responses (responses are correlated by id).
                _ = RunHandlerAsync(outcome.Message!);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        // The socket can be disposed out from under a pending receive when the connection is
        // torn down (e.g. the hosting endpoint returning after a server-initiated close).
        catch (ObjectDisposedException) { }
        finally
        {
            _completion.TrySetResult();
        }
    }

    // Sends the server's half of the closing handshake in response to a client-initiated close.
    // Serialized through the send lock so it can't collide with an in-flight response; a no-op
    // unless the socket is in CloseReceived (e.g. the server already sent its close frame first).
    private async Task CompleteClosingHandshakeAsync()
    {
        if (_socket.State != WebSocketState.CloseReceived)
            return;
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(
                    _socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                    _socket.CloseStatusDescription,
                    CancellationToken.None
                );
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunHandlerAsync(byte[] message)
    {
        try
        {
            await HandleMessageAsync(message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<ReceiveOutcome> ReceiveMessageAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    return new ReceiveOutcome(ReceiveResultKind.ConnectionClosed, null, 0);

                // Bail the moment this frame would push the message past the cap — without
                // buffering it — so a client can't exhaust memory (or make us read forever)
                // by streaming an unbounded message. Peak memory stays under the limit.
                if (ms.Length + result.Count > _options.MaxMessageBytes)
                    return new ReceiveOutcome(
                        ReceiveResultKind.TooLarge,
                        null,
                        ms.Length + result.Count
                    );

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return new ReceiveOutcome(ReceiveResultKind.Message, ms.ToArray(), ms.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessageAsync(byte[] message)
    {
        JsonElement idElement = default;
        var haveId = false;
        string? method = null;
        var sw = Stopwatch.StartNew();
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var id))
            {
                idElement = id.Clone();
                haveId = true;
            }

            method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            if (method is null)
                return;

            if (!_methods.TryGetValue(method, out var entry))
            {
                if (haveId)
                    await SendErrorAsync(
                        idElement,
                        (int)RpcErrorCode.MethodNotFound,
                        $"Method not found: {method}"
                    );
                // Client-supplied method name is unbounded — don't let it become a label value.
                OnCallHandled?.Invoke("unknown", sw.Elapsed, "not_found");
                return;
            }

            if (!entry.AllowUnauthenticated && _httpContext.User.Identity?.IsAuthenticated != true)
            {
                _closeSignal.TrySetResult("unauthenticated_access");
                if (haveId)
                    await SendErrorAsync(
                        idElement,
                        (int)RpcErrorCode.NotAuthenticated,
                        "Not authenticated"
                    );
                OnCallHandled?.Invoke(method, sw.Elapsed, "unauthorized");
                return;
            }

            object? paramsArg = null;
            if (entry.ParamType is not null)
            {
                var hasParams =
                    root.TryGetProperty("params", out var p) && p.ValueKind != JsonValueKind.Null;

                // Validate against the published schema before binding. Missing params are
                // treated as an empty object, so a call that omits an all-optional params
                // object still validates, while a method with required params gets a clean
                // -32602 instead of silently binding defaults. ParamsSchema is always set when
                // ParamType is (both are populated together in BuildRegistry).
                var instance = hasParams ? p : EmptyParamsDocument.RootElement;
                var evaluation = entry.ParamsSchema!.Evaluate(instance, ParamsEvaluationOptions);
                if (!evaluation.IsValid)
                    throw new RpcException(
                        "Invalid params",
                        (int)RpcErrorCode.InvalidParams,
                        new { validationErrors = CollectValidationErrors(evaluation) }
                    );

                if (hasParams)
                    paramsArg = p.Deserialize(entry.ParamType, _jsonOptions);
                paramsArg ??= Activator.CreateInstance(entry.ParamType);
            }

            object? resultValue;
            // _connectionServices is an immutable published snapshot (AddConnectionService
            // swaps in a new dictionary rather than mutating), so it's safe to hand
            // straight to the provider without copying.
            using (
                var provider = new WsConnectionServiceProvider(
                    _scopeFactory.CreateScope(),
                    _connectionServices
                )
            )
            {
                var target = entry.Factory(provider, null);
                var args = entry.ParamType is null ? [] : new[] { paramsArg };
                resultValue = await InvokeAsync(entry.Method, target, args);
            }

            if (haveId)
                await SendResultAsync(idElement, resultValue);
            OnCallHandled?.Invoke(method, sw.Elapsed, "success");
        }
        catch (RpcException ex)
        {
            if (haveId)
                await SendErrorAsync(idElement, ex.ErrorCode, ex.Message, ex.ErrorData);
            OnCallHandled?.Invoke(method ?? "unknown", sw.Elapsed, "error");
        }
        catch (Exception ex)
        {
            if (haveId)
                await SendErrorAsync(idElement, (int)RpcErrorCode.InternalError, ex.Message);
            OnCallHandled?.Invoke(method ?? "unknown", sw.Elapsed, "exception");
        }
    }

    private static List<object> CollectValidationErrors(EvaluationResults results) =>
        (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d =>
                d.Errors!.Select(e =>
                    (object)
                        new
                        {
                            path = d.InstanceLocation.ToString(),
                            keyword = e.Key,
                            message = e.Value,
                        }
                )
            )
            .ToList();

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, object?[] args)
    {
        object? returnValue;
        try
        {
            returnValue = method.Invoke(target, args);
        }
        // A synchronous target (e.g. one that validates and throws RpcException before
        // returning its Task) throws through reflection wrapped in TargetInvocationException.
        // Unwrap it so RpcException reaches the dispatcher's RpcException handler and keeps
        // its error code instead of collapsing to a generic internal error.
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw; // unreachable — Throw always rethrows.
        }

        return returnValue switch
        {
            Task task => await AwaitTaskAsync(task),
            ValueTask valueTask => await AwaitValueTaskAsync(valueTask),
            _ when IsGenericValueTask(returnValue) => await AwaitGenericValueTaskAsync(
                returnValue!
            ),
            _ => returnValue,
        };
    }

    private static async Task<object?> AwaitTaskAsync(Task task)
    {
        await task;
        // Can't check task.GetType() == typeof(Task<>) — a Task<T> whose async state machine
        // actually suspends (a real await, not one that completes synchronously) is returned
        // by the runtime as an AsyncTaskMethodBuilder<T>.AsyncStateMachineBox<TStateMachine>, a
        // Task<T> SUBCLASS, not Task<T> itself. That's the common case for any RPC method that
        // awaits real I/O, so the exact-type check silently discarded most results. Only
        // Task<T> (and its subclasses) expose "Result" — a plain non-generic Task doesn't — so
        // probing for the property is both correct and simpler than the type-identity check.
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static async Task<object?> AwaitValueTaskAsync(ValueTask valueTask)
    {
        await valueTask;
        return null;
    }

    private static bool IsGenericValueTask(object? value) =>
        value is not null
        && value.GetType().IsGenericType
        && value.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>);

    private static async Task<object?> AwaitGenericValueTaskAsync(object valueTask)
    {
        var asTask = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        return await AwaitTaskAsync(asTask);
    }

    private Task SendResultAsync(JsonElement id, object? result) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                result,
            }
        );

    private Task SendErrorAsync(JsonElement id, int code, string message, object? data = null) =>
        SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                error = new
                {
                    code,
                    message,
                    data,
                },
            }
        );

    private async Task SendAsync(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State != WebSocketState.Open)
                return;
            await _socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None
            );
        }
        catch (WebSocketException) { }
        finally
        {
            _sendLock.Release();
        }
    }
}
