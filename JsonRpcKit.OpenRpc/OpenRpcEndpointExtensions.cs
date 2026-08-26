using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JsonRpcKit.OpenRpc;

/// <summary>
/// Serves a generated OpenRPC document. Mapping it is separate from mapping the reference page
/// that reads it, and from mapping the socket itself: each can go on a different route builder —
/// a different port included — without the others moving.
/// </summary>
public static class OpenRpcEndpointExtensions
{
    // A document is read by people as often as by tooling, so it is written to be readable:
    // indented, and without escaping the arrows and accented characters that show up in prose.
    // It is served as application/json and never embedded in a page, so relaxed escaping is safe.
    private static readonly JsonSerializerOptions Layout = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <returns>
    /// The mapped endpoint's builder, so a caller can describe it in its own API documentation or
    /// restrict who may read it.
    /// </returns>
    public static RouteHandlerBuilder MapOpenRpcDocument(
        this IEndpointRouteBuilder app,
        Action<OpenRpcDocumentOptions> configure
    )
    {
        var options = new OpenRpcDocumentOptions();
        configure(options);

        if (options.Assemblies.Count == 0)
            throw new InvalidOperationException(
                "No assemblies to scan for RPC methods — call OpenRpcDocumentOptions.Scan()."
            );

        // Built once and held as text: reflection and schema generation are the expensive part,
        // and nothing about the document changes after startup.
        var json = OpenRpcDocumentBuilder.Build(options).ToJsonString(Layout);

        return app.MapGet(options.DocumentPath, () => Results.Text(json, "application/json"));
    }
}
