using System.Globalization;
using System.Resources;

namespace Matchmate.Web.Localization;

public static class ShellText
{
    private static readonly ResourceManager Resources = new("Matchmate.Web.Localization.ShellText", typeof(ShellText).Assembly);
    private static readonly string[] RecoveryKeys = ["ReconnectFirst", "ReconnectRepeatedBefore", "ReconnectRepeatedAfter", "ReconnectFailed", "Retry", "SessionPaused", "ResumeFailed", "Resume"];
    public static string Get(MatchLanguage language, string key, params object?[] args)
    {
        var culture = MatchLanguages.Culture(language);
        var format = Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException($"Missing shell resource: {key}");
        return args.Length == 0 ? format : string.Format(culture, format, args);
    }
    public static Dictionary<string, string> Recovery(MatchLanguage language) => RecoveryKeys.ToDictionary(key => key, key => Get(language, key));

    // Only server-rendered utility routes need a refresh. Never navigate a working experience.
    public static bool RequiresPageRefresh(string absoluteUri)
    {
        if (!Uri.TryCreate(absoluteUri, UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.TrimEnd('/').ToLowerInvariant() is "/account/sign-in" or "/account/denied" or "/account/error" or "/error" or "/not-found";
    }
}
