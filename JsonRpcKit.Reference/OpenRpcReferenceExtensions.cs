using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JsonRpcKit.Reference;

/// <summary>
/// A reference page for an OpenRPC document, in the same spirit as an OpenAPI one: categories and
/// their prose first, then every call, plus a console that talks to the live socket.
/// </summary>
/// <remarks>
/// Hand-written rather than borrowed. The OpenRPC renderers on offer are npm/React builds with no
/// CDN distribution, and none of them knows about the server-to-client messages a socket API
/// publishes under <c>x-notifications</c> — half of what a client has to implement.
/// <para>
/// The page reads two extensions beyond the specification, and degrades to a flat method list
/// without either: <c>x-tag-order</c> and <c>components/tags</c> for the categories, and
/// <c>x-notifications</c> for server-to-client messages. It also renders a mark object on a method
/// — <c>headline</c>, <c>body</c>, <c>note</c>, <c>badge</c>, <c>marker</c> and <c>color</c>, from
/// whichever extension <see cref="OpenRpcReferenceOptions.MarkExtension"/> names — as a warning
/// callout, so an application can mark unsettled or unsupported calls in its own vocabulary without
/// this package knowing any of it.
/// </para>
/// </remarks>
public static class OpenRpcReferenceExtensions
{
    /// <returns>
    /// The mapped endpoint's builder, so a caller can exclude the page from its own API
    /// documentation, or restrict who may open it.
    /// </returns>
    public static RouteHandlerBuilder MapOpenRpcReference(
        this IEndpointRouteBuilder app,
        Action<OpenRpcReferenceOptions> configure
    )
    {
        var options = new OpenRpcReferenceOptions();
        configure(options);

        // Substituted once: the page is static after this, and everything else it shows is
        // fetched from the document at load.
        var page = Template()
            .Replace("__DOCUMENT_URL__", JsonEncode(options.DocumentPath))
            .Replace("__MARK_EXTENSION__", JsonEncode(options.MarkExtension))
            .Replace("__LINKS__", JsonEncode(options.Links))
            .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(options.Title));

        return app.MapGet(options.Path, () => Results.Content(page, "text/html; charset=utf-8"));
    }

    // Values reach the page as JSON literals inside its script, so a path or label containing a
    // quote cannot break out of the string it is substituted into.
    private static string JsonEncode<T>(T value) => JsonSerializer.Serialize(value, JsonLayout);

    private static readonly JsonSerializerOptions JsonLayout = new(JsonSerializerDefaults.Web);

    private static string Template()
    {
        using var stream =
            typeof(OpenRpcReferenceExtensions).Assembly.GetManifestResourceStream(
                "JsonRpcKit.Reference.OpenRpcReference.html"
            )
            ?? throw new InvalidOperationException(
                "OpenRpcReference.html is missing from the assembly."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
