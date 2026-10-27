using System.Collections.Immutable;
using System.Globalization;

namespace Matchmate.Web.Localization;

public enum MatchLanguage { English, Swedish, Spanish }
public static class MatchLanguages
{
    public static ImmutableArray<MatchLanguage> Supported { get; } = [MatchLanguage.English, MatchLanguage.Swedish, MatchLanguage.Spanish];
    public static MatchLanguage Normalize(MatchLanguage language) => Enum.IsDefined(language) ? language : MatchLanguage.English;
    public static string Code(MatchLanguage language) => Normalize(language) switch { MatchLanguage.Swedish => "sv", MatchLanguage.Spanish => "es", _ => "en" };
    public static CultureInfo Culture(MatchLanguage language) => CultureInfo.GetCultureInfo(Normalize(language) switch { MatchLanguage.Swedish => "sv-SE", MatchLanguage.Spanish => "es-ES", _ => "en-GB" });
    public static string Name(MatchLanguage language) => Normalize(language) switch { MatchLanguage.Swedish => "Svenska", MatchLanguage.Spanish => "Español", _ => "English" };
    public static MatchLanguage FromCulture(CultureInfo? culture) => culture?.Name switch
    {
        "sv" or "sv-SE" => MatchLanguage.Swedish,
        "es" or "es-ES" => MatchLanguage.Spanish,
        _ => MatchLanguage.English
    };
}
