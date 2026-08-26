namespace JsonRpcKit.Reference;

/// <param name="Label">Link text.</param>
/// <param name="Href">Where it goes. Relative to the host serving the page.</param>
public sealed record OpenRpcReferenceLink(string Label, string Href);

/// <summary>
/// What the reference page needs that it cannot read out of the OpenRPC document: where the
/// document is, and where the rest of the API's documentation lives.
/// </summary>
public sealed class OpenRpcReferenceOptions
{
    /// <summary>Where the page is served.</summary>
    public string Path { get; set; } = "/docs/rpc";

    /// <summary>Where the page fetches its OpenRPC document from.</summary>
    public string DocumentPath { get; set; } = "/openrpc.json";

    /// <summary>
    /// Browser tab title, and the heading shown until the document loads. Everything else on the
    /// page — including the real title — comes from the document.
    /// </summary>
    public string Title { get; set; } = "Realtime API";

    /// <summary>
    /// The method extension the page reads a warning mark from. The object it points at may carry
    /// <c>headline</c>, <c>body</c>, <c>note</c>, <c>badge</c>, <c>marker</c> and <c>color</c>, all
    /// optional — the page lays them out without knowing what any of the words mean, so an
    /// application can mark unsettled or unsupported calls in a vocabulary of its own.
    /// </summary>
    public string MarkExtension { get; set; } = "x-stability";

    /// <summary>
    /// Shown in the sidebar footer. The document itself is always linked; use this for the
    /// neighbouring documentation, such as an OpenAPI reference for the same product.
    /// </summary>
    public IList<OpenRpcReferenceLink> Links { get; } = [];

    /// <summary>Adds a sidebar link.</summary>
    public OpenRpcReferenceOptions Link(string label, string href)
    {
        Links.Add(new OpenRpcReferenceLink(label, href));
        return this;
    }
}
