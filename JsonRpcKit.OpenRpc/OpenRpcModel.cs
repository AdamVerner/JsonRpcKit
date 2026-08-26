namespace JsonRpcKit.OpenRpc;

/// <summary>
/// One entry in the document's <c>servers</c> array — where the socket this document describes is
/// reachable. <c>Url</c> may be a path (<c>/api/app/ws</c>) or absolute; a path is resolved against
/// the host serving the document, which is what a reference page needs to offer a live console.
/// </summary>
public sealed record OpenRpcServer(
    string Name,
    string Url,
    string? Summary = null,
    string? Description = null
);

/// <summary>
/// A category of methods. Published in the document's <c>components/tags</c> catalogue and
/// referenced by every method in it, so a reader meets the category — and the prose saying
/// what it is for — before the individual calls.
/// </summary>
/// <param name="Name">
/// Matches <see cref="RpcMethodAttribute.Tag"/>, or a method name's first segment where that
/// is left unset. It is also the component key, which the spec restricts to letters, digits,
/// <c>.</c>, <c>-</c> and <c>_</c>.
/// </param>
/// <param name="DisplayName">
/// The heading a reader sees, e.g. <c>Devices</c> for the tag <c>device</c>. Travels as
/// <c>x-displayName</c>: OpenRPC's Tag Object is sealed to name/description/externalDocs, so
/// there is no standard field for it.
/// </param>
/// <param name="Description">A paragraph or two of markdown, shown under the heading.</param>
public sealed record OpenRpcTag(string Name, string DisplayName, string? Description = null);

/// <summary>
/// A JSON-RPC error the API can answer with, published in the document's <c>components/errors</c>
/// catalogue under <c>Name</c>. <c>Message</c> is one concise sentence and is the whole
/// explanation: OpenRPC's Error Object is sealed to code/message/data and, alone among the spec's
/// objects, rejects <c>x-</c> extensions, so there is nowhere else for prose to go.
/// </summary>
public sealed record OpenRpcError(string Name, int Code, string Message);

/// <summary>
/// A server-to-client message. OpenRPC describes only what a client may call — it has no
/// concept of a server-initiated one — so these are published under the document's
/// <c>x-notifications</c> extension, each shaped like a Method Object with no result.
/// </summary>
public sealed record OpenRpcNotification(
    string Name,
    string Summary,
    Type ParamType,
    string? Description = null
);
