# JsonRpcKit

A thin JSON-RPC 2.0 dispatcher over raw WebSocket for ASP.NET Core, with per-call dependency injection —
and, optionally, a published contract for whatever you serve over it.

| Package | What it gives you |
| --- | --- |
| `JsonRpcKit` | The dispatcher. Everything below is optional. |
| `JsonRpcKit.OpenRpc` | An [OpenRPC](https://spec.open-rpc.org/) 1.3.2 document generated from the same methods the dispatcher serves. |
| `JsonRpcKit.Reference` | A dependency-free reference page for that document, with a console that talks to the live socket. |

Each is mapped separately, so a socket, its document and its reference page can live on different
paths — or different ports — without knowing about each other. Only the dispatcher is required.

## What it does

RPC target classes work like ASP.NET Core controllers: constructor-injected from the DI container, one fresh instance per call. No shared state bleeds between concurrent requests.

```csharp
// Registered as Scoped or Transient in DI.
internal sealed class OrganizationTarget(IOrganizationService orgs)
{
    [RpcMethod("org.v1.list", Summary = "List visible organizations.")]
    public Task<IReadOnlyList<OrgInfo>> ListAsync(ListParams p)
        => orgs.ListAsync(p.NameFilter);

    [RpcMethod("org.v1.get", Summary = "Get one organization by id.")]
    public async Task<OrgInfo> GetAsync(GetParams p)
    {
        var result = await orgs.GetAsync(p.Id);
        if (result.IsFailure)
            throw new RpcException("Not found", errorCode: 1001);
        return result.Value;
    }
}
```

## Wire protocol

Standard JSON-RPC 2.0 over WebSocket — each direction sends complete JSON messages over a single WebSocket frame. Any compliant client works.

```
→  {"jsonrpc":"2.0","id":1,"method":"org.v1.list","params":{"nameFilter":null}}
←  {"jsonrpc":"2.0","id":1,"result":[{"id":42,"name":"Acme"}]}

←  {"jsonrpc":"2.0","method":"push.event","params":{...}}   // server notification
```

## Setup

### 1. Register target classes in DI

```csharp
services.AddScoped<OrganizationTarget>();
```

### 2. Wire up the endpoint

```csharp
app.MapGet("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }

    var closeSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();

    var dispatcher = new WsRpcDispatcher(
        socket, scopeFactory, context, closeSignal,
        myJsonOptions,
        typeof(OrganizationTarget).Assembly,   // scanned for [RpcMethod] methods
        new WsRpcDispatcherOptions { MaxMessageBytes = 10 * 1024, MaxConcurrentCalls = 10 });

    dispatcher.StartListening();
    await dispatcher.Completion;
}).AllowAnonymous();
```

The `WsRpcDispatcherOptions` argument is optional (defaults: 10 KiB max message, 10
concurrent calls per connection). When an inbound message exceeds `MaxMessageBytes` the
dispatcher stops reading, fires the `OnMessageTooLarge` callback (wire it to your logger),
and sets the close signal with reason `WsRpcDispatcher.MessageTooLargeCloseReason` so the
endpoint closes the connection — rather than trying to buffer or drain an unbounded stream.
Once `MaxConcurrentCalls` handlers are in flight the read loop pauses, applying backpressure
to the client.

### 3. Connection-scoped services (optional)

Some services have per-connection identity (e.g. a subscriber whose reference is used as a key in a registry). Register them before `StartListening`:

```csharp
var sub = new MySubscriber(dispatcher);
dispatcher.AddConnectionService<IMySubscriber>(sub);
dispatcher.StartListening();
```

`HttpContext` is always registered automatically and injectable by name in target constructors.

## Authentication

The dispatcher checks `httpContext.User.Identity.IsAuthenticated` before dispatching any method not marked `AllowUnauthenticated = true`. An unauthenticated call gets error code 1002 and the `closeSignal` is set so the caller can close the connection.

```csharp
// The only method callable before auth — sets HttpContext.User for the lifetime of the connection.
internal sealed class AuthTarget(HttpContext http, TokenValidationParameters tvp)
{
    [RpcMethod("auth.authenticate", Summary = "Authenticate.", AllowUnauthenticated = true)]
    public async Task<AuthResult> AuthenticateAsync(AuthParams p)
    {
        var result = await handler.ValidateTokenAsync(p.Token, tvp);
        if (!result.IsValid) throw new RpcException("Invalid token", 1002);
        http.User = new ClaimsPrincipal(result.ClaimsIdentity);
        return new AuthResult(...);
    }
}
```

## Error handling

Throw `RpcException` from a target method to send a structured error response:

```csharp
throw new RpcException("Device not found", errorCode: 1001);
throw new RpcException("Validation failed", errorCode: 1004, errorData: new { field = "ioIds" });
```

Any other unhandled exception produces error code `-32603` (JSON-RPC internal error).
This works whether the method is `async` or throws synchronously — a synchronous throw is
unwrapped from the reflection `TargetInvocationException`, so its `RpcException` code is
preserved rather than collapsing to `-32603`.

The codes the dispatcher emits itself — `-32601` (method not found), `-32602` (invalid
params), `-32603` (internal error), and `1002` (not authenticated) — are the `RpcErrorCode`
enum. Application codes passed to `RpcException` are your own; the dispatcher forwards them
verbatim.

## Request validation

Each method's single param object is validated against its JSON Schema (the same schema the
OpenRPC document publishes, generated once per method at startup) before the target runs. A
request whose `params` violate the schema — wrong type, or a missing required field — get a
`-32602` (invalid params) response with the failures listed in `error.data.validationErrors`;
the target is never invoked. Because the schema comes from the C# type, a parameter is
required unless it is nullable or has a default value — give optional params defaults so
callers may omit them.

Add a `[Description]` attribute to a param/result property (or the type) to document it — the
text is emitted into the schema's `description`. On a record's positional parameter, target the
generated property: `record P([property: Description("…")] int DeviceId)`.

## Documenting the API

Two sibling packages publish the same set of methods this dispatcher serves. They are registered
separately from the socket and from each other, so any of the three can be moved — to another path,
or another port — on its own.

```csharp
app.MapOpenRpcDocument(options =>          // JsonRpcKit.OpenRpc
{
    options.Title = "My API";
    options.DocumentPath = "/openrpc.json";
    options.SerializerOptions = myJsonOptions;   // the same options the dispatcher uses
    options.Scan(typeof(OrganizationTarget).Assembly);
    options.Servers.Add(new OpenRpcServer("Socket", "/ws"));
    options.Tag("org", "Organizations", "Who owns what, and who may see it.");
    options.Notification<PushEvent>("push.event", "Sent when a watched thing changes.");
    options.Error("notFound", 1001, "No such resource, or none visible.");
});

app.MapOpenRpcReference(options =>         // JsonRpcKit.Reference
{
    options.Path = "/docs/rpc";
    options.DocumentPath = "/openrpc.json";
    options.Link("REST reference", "/docs");
});
```

`MapOpenRpcDocument` reflects over the same scanner the dispatcher builds its registry from, so a
documented method and a dispatchable one cannot drift apart. `MapOpenRpcReference` serves a
dependency-free page that reads the document in the browser: one section per category, each opening
with its prose and an index of its calls, then every call in full, plus a console that talks to the
real socket.

A method's category is the first segment of its name — `org.v1.list` is an `org` — unless
`[RpcMethod(Tag = "…")]` says otherwise. `options.Tag(...)` gives a category its heading and prose;
an undescribed category still lists its methods, just without either.

The document conforms to OpenRPC 1.3.2. Three things the specification has no field for travel as
extensions: category order (`x-tag-order`), a category's display name (`x-displayName`), and
server-to-client messages (`x-notifications`). `options.Customize` hands you the finished document
for anything else. The reference page renders a mark object on a method — any of `headline`, `body`,
`note`, `badge`, `marker`, `color` — as a warning callout without knowing what your vocabulary
means; `OpenRpcReferenceOptions.MarkExtension` names the extension it reads that from (default
`x-stability`). A mark repeated identically across most methods is hoisted into one banner at the
top of the page, so only what is distinct to a method shows beside it.

## Sending server-initiated notifications

```csharp
await dispatcher.SendNotificationAsync("push.event", new { deviceId = 42, state = "on" });
```

Writes are serialized internally — safe to call from multiple threads concurrently with the read loop.

## Tests

`dotnet test JsonRpcKit.slnx`. They run against a minimal host in the test project — one WebSocket
route and a couple of probe targets — so what belongs here is the dispatcher's own contract: what it
accepts, what it refuses, when it gives up on a connection. Anything that needs to know what a method
means belongs to the consumer.
