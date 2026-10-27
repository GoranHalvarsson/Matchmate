using System.Reflection;
using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;
using Xunit;

namespace Matchmate.Tests;

public sealed class LocalizedStatisticsPresentationTests
{
    private static string Format(string question, MatchLanguage language, MatchAudienceMode mode = MatchAudienceMode.Fan, bool empty = false)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { replayClock = "98:00", totalClassifiedControlMs = empty ? 0 : 5850000,
            teams = new[] { new { name = "Brentford", passAttempts = empty ? 0 : 361, passCompleted = empty ? 0 : 290, passCompletionPercent = empty ? (decimal?)null : 100m * 290 / 361, shots = empty ? 0 : 3,
                controlledMs = empty ? 0 : 2905300, illustratedControlSharePercent = empty ? (decimal?)null : 100m * 2905300 / 5850000 },
                new { name = "Chelsea", passAttempts = empty ? 0 : 367, passCompleted = empty ? 0 : 293, passCompletionPercent = empty ? (decimal?)null : 100m * 293 / 367, shots = 0,
                controlledMs = empty ? 0 : 2944700, illustratedControlSharePercent = empty ? (decimal?)null : 100m * 2944700 / 5850000 } } }));
        return (string)typeof(MatchSourceQuestions).Assembly.GetType("Matchmate.Web.Chat.MatchStatisticsPresentation")!
            .GetMethod("FormatForLanguage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [data.RootElement, question, mode, language])!;
    }
    [Theory]
    [InlineData("Visa skott för båda lagen i den här genererade reprisen", MatchLanguage.Swedish, "3 skott", "passningar")]
    [InlineData("Mostrar tiros para ambos equipos en esta repetición generada", MatchLanguage.Spanish, "3 tiros", "pases")]
    [InlineData("Vad är Chelseas passningsprocent?", MatchLanguage.Swedish, "79,8%", "Brentford")]
    [InlineData("¿Cuál es el porcentaje de pases completados de Chelsea?", MatchLanguage.Spanish, "79,8%", "Brentford")]
    [InlineData("Visa lyckade passningar för Chelsea", MatchLanguage.Swedish, "293", "skott")]
    [InlineData("Mostrar pases completados de Chelsea", MatchLanguage.Spanish, "293", "tiros")]
    [InlineData("Visa bollinnehav för båda lagen i reprisen", MatchLanguage.Swedish, "49,7%", "passningar")]
    [InlineData("Mostrar posesión para ambos equipos en la repetición", MatchLanguage.Spanish, "49,7%", "pases")]
    public void ClosedAliasesFocusOnlyRequestedMetricAndTeamWithLocalDecimalCulture(string question, MatchLanguage language, string expected, string excluded)
    {
        var answer = Format(question, language); Assert.Contains("98:00", answer); Assert.Contains(expected, answer); Assert.DoesNotContain(excluded, answer);
        Assert.InRange(answer.Length, 1, 1800);
    }
    [Theory]
    [InlineData("Visa faktiska skott", MatchLanguage.Swedish, "faktisk")]
    [InlineData("Mostrar tiros reales", MatchLanguage.Spanish, "reales")]
    [InlineData("Visa skott på mål", MatchLanguage.Swedish, "på eller utanför")]
    [InlineData("Mostrar tiros a puerta", MatchLanguage.Spanish, "tiros a puerta")]
    [InlineData("Visa passningar till Thiago", MatchLanguage.Swedish, "passningsmottagare")]
    [InlineData("Mostrar pases a Thiago", MatchLanguage.Spanish, "receptor")]
    [InlineData("Visa skott i första halvlek", MatchLanguage.Swedish, "tidsintervallet")]
    [InlineData("Mostrar tiros en la primera parte", MatchLanguage.Spanish, "intervalo")]
    public void UnsupportedScopeIsNotRelabelledAsAvailableTeamTotals(string question, MatchLanguage language, string expected)
    { var answer = Format(question, language); Assert.Contains(expected, answer); Assert.DoesNotContain("3 skott", answer); Assert.DoesNotContain("3 tiros", answer); }
    [Theory] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public void OverviewFallbackAndEmptyDenominatorsRemainHonest(MatchLanguage language)
    {
        var answer = Format("??? Chelsea + ???", language); Assert.Contains("Brentford", answer); Assert.Contains("Chelsea", answer);
        var empty = Format(language == MatchLanguage.Swedish ? "Visa Chelseas passningsprocent" : "Mostrar precisión de pase de Chelsea", language, MatchAudienceMode.Analyst, true);
        Assert.DoesNotContain("0%", empty); Assert.Contains(ChatText.Get(language, "Stats.RateZero", "Chelsea"), empty); Assert.InRange(empty.Length, 1, 1800);
    }
}
