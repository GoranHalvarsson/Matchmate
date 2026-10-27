using System.Resources;
namespace Matchmate.Web.Localization;
public static class LeagueChatText
{
    private static readonly ResourceManager Resources = new("Matchmate.Web.Localization.LeagueChatText", typeof(LeagueChatText).Assembly);
    public static string Get(MatchLanguage language, string key, params object?[] args)
    {
        var culture = MatchLanguages.Culture(language);
        var value = Resources.GetString(key, culture) ?? Resources.GetString(key, MatchLanguages.Culture(MatchLanguage.English))
            ?? throw new MissingManifestResourceException($"Missing chat resource: {key}");
        return args.Length == 0 ? value : string.Format(culture, value, args);
    }
}
