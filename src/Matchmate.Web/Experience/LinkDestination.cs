namespace Matchmate.Web.Experience;

public enum LinkDestinationKind { Internal, External, Email, Telephone, Unsupported }

public readonly record struct LinkDestination(string? Href, LinkDestinationKind Kind)
{
    public bool LeavesApp => Kind is LinkDestinationKind.External or LinkDestinationKind.Email or LinkDestinationKind.Telephone;

    public static LinkDestination Classify(string? href, Uri appBaseUri)
    {
        if (href is null || href.Any(c => char.IsControl(c) || c == '\\')) return new(null, LinkDestinationKind.Unsupported);
        var value = href.Trim();
        if (!Uri.TryCreate(appBaseUri, value, out var destination)) return new(null, LinkDestinationKind.Unsupported);
        if (destination.Scheme == Uri.UriSchemeMailto) return new(value, LinkDestinationKind.Email);
        if (destination.Scheme == "tel") return new(value, LinkDestinationKind.Telephone);
        if (destination.Scheme is not ("http" or "https")) return new(null, LinkDestinationKind.Unsupported);
        var sameOrigin = destination.Scheme.Equals(appBaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && destination.IdnHost.Equals(appBaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) && destination.Port == appBaseUri.Port;
        return new(value, sameOrigin ? LinkDestinationKind.Internal : LinkDestinationKind.External);
    }
}
