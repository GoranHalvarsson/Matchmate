namespace Matchmate.Web.Localization;

// One circuit. The selector persists only the explicit UI preference in the browser.
// Request culture is captured once by DI; callbacks never read HttpContext or change ambient culture.
public sealed class MatchLanguageState
{
    public MatchLanguageState() : this(MatchLanguage.English) { }
    public MatchLanguageState(MatchLanguage language) => Language = MatchLanguages.Normalize(language);
    public MatchLanguage Language { get; private set; }
    public bool BrowserPreferenceReconciled { get; private set; }
    public event Action? Changed;
    public bool ReconcileBrowserPreference(MatchLanguage language)
    {
        if (BrowserPreferenceReconciled) return false;
        BrowserPreferenceReconciled = true;
        return SetLanguage(language);
    }
    public bool SetLanguage(MatchLanguage language)
    {
        if (!Enum.IsDefined(language)) return false;
        if (Language == language) return false;
        Language = language; Changed?.Invoke(); return true;
    }
}
