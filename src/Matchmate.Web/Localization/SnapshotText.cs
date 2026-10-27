using System.Resources;
using Matchmate.Web.Chat;

namespace Matchmate.Web.Localization;

public static class SnapshotText
{
    private static readonly ResourceManager Resources = new("Matchmate.Web.Localization.SnapshotText", typeof(SnapshotText).Assembly);
    public static string Get(MatchLanguage language, string key, params object?[] args)
    {
        var culture = MatchLanguages.Culture(language);
        var text = Resources.GetString(key, culture) ?? throw new MissingManifestResourceException($"Missing snapshot text: {key}");
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }
}
