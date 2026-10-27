using System.Globalization;
using System.Resources;

namespace Matchmate.Web.Localization;

public static class ConversationText
{
    private static readonly ResourceManager Resources = new("Matchmate.Web.Localization.ConversationText", typeof(ConversationText).Assembly);
    public static string Get(MatchLanguage language, string key, params object?[] args)
    {
        var culture = MatchLanguages.Culture(language);
        var text = Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException($"Missing conversation resource: {key}");
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }
}
