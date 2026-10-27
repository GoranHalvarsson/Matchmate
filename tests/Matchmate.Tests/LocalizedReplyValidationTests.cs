using System.Text.Json;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;
using Xunit;

namespace Matchmate.Tests;

public sealed class LocalizedReplyValidationTests
{
    private static readonly IReadOnlyDictionary<string, MatchChatEvidence> Evidence = new Dictionary<string, MatchChatEvidence>
    { ["coverage"] = new("coverage", "coverage", "Eligible source evidence", Enumerable.Range(0, 101).Select(n => n.ToString()).Append("79.8").ToArray()) };
    private static string Reply(string answer, string id = "coverage") => JsonSerializer.Serialize(new { answer, evidenceIds = new[] { id } });
    [Theory]
    [InlineData("Han slog tre passningar.")] [InlineData("Laget hade 50 procent bollinnehav.")] [InlineData("Passningsprocenten var 79,8.")]
    [InlineData("Han sprang trettio meter.")] [InlineData("Hastigheten var 30 km/h.")] [InlineData("Formation var fyra-fyra-två.")]
    [InlineData("Inga uppgifter om hastighet finns, men han sprang trettio meter.")]
    [InlineData("Bollinnehav är okänt, men laget hade 50 procent.")]
    [InlineData("Hastighet är okänd; siffran var 30.")]
    [InlineData("Completó tres pases.")] [InlineData("La posesión fue 50 por ciento.")] [InlineData("La precisión de pase fue 79,8.")]
    [InlineData("Corrió treinta metros.")] [InlineData("La velocidad fue 30 km/h.")] [InlineData("La formación es cuatro-cuatro-dos.")]
    [InlineData("No hay datos de velocidad, pero corrió treinta metros.")]
    [InlineData("La posesión es desconocida, pero fue cincuenta por ciento.")]
    [InlineData("La velocidad es desconocida; el valor fue 30.")]
    public void QuantifiedOrPositiveUnsupportedMetricClaimsRejectEvenBesideCaveats(string answer)
    {
        MatchReplyRejection? reason = null;
        Assert.Throws<InvalidDataException>(() => LocalizedMatchReplyValidation.Validate(Reply(answer), Evidence, MatchLanguage.Spanish, r => reason = r));
        Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
    }
    [Theory]
    [InlineData(MatchLanguage.Swedish, "Inget xG. The value was 1.")]
    [InlineData(MatchLanguage.Swedish, "Inget xG. The rate was 1.")]
    [InlineData(MatchLanguage.Spanish, "No hay xG. The value was 1.")]
    [InlineData(MatchLanguage.Spanish, "No hay xG. The rate was 1.")]
    public void LocalizedUnavailableMetricsRetainTheOriginalEnglishValuePointerGuard(MatchLanguage language, string answer)
    {
        MatchReplyRejection? reason = null;
        Assert.Throws<InvalidDataException>(() => LocalizedMatchReplyValidation.Validate(Reply(answer), Evidence, language, r => reason = r));
        Assert.Equal(MatchReplyRejection.UnsupportedMeasurement, reason);
    }
    [Theory]
    [InlineData(MatchLanguage.Swedish, "Inget xG. Matchen slutade 1–0.")]
    [InlineData(MatchLanguage.Swedish, "xG är okänt. Målet kom i minut 1.")]
    [InlineData(MatchLanguage.Spanish, "No hay xG. El marcador fue 1–0.")]
    [InlineData(MatchLanguage.Spanish, "Sin xG. El gol llegó en el minuto 1.")]
    public void LocalizedUnavailableMetricsAllowSupportedScoreAndEventContextUnchanged(MatchLanguage language, string answer)
    {
        var result = LocalizedMatchReplyValidation.Validate(Reply(answer), Evidence, language);
        Assert.Equal(answer, result.Answer); Assert.Single(result.Evidence);
    }
    [Theory]
    [InlineData("Bollen gick mot bortre stolpen. Min tolkning är att returen var viktig.")]
    [InlineData("El balón llegó al segundo palo. Mi lectura es que la segunda jugada fue importante.")]
    [InlineData("Det finns inga uppgifter om hastighet.")] [InlineData("Bollinnehav är okänt.")]
    [InlineData("No hay datos de velocidad.")] [InlineData("La posesión es desconocida.")]
    [InlineData("No hay xG.")] [InlineData("Sin xG")]
    [InlineData("No measured speed or xG is available.")]
    [InlineData("Uppskattade positioner är en illustration, inte uppmätta löpningar.")]
    public void OrdinaryFootballAndExplicitUnknownMetricsRemainValid(string answer)
    {
        var result = LocalizedMatchReplyValidation.Validate(Reply(answer), Evidence, MatchLanguage.Swedish);
        Assert.Equal(answer, result.Answer); Assert.Single(result.Evidence);
    }
    [Theory] [InlineData("unknown")] [InlineData("number")] [InlineData("html")]
    public void CitationNumericAndContentGuardsRetainTheirAuthority(string fault)
    {
        var answer = fault == "number" ? "El marcador es 999." : fault == "html" ? "<b>Gol</b>" : "Det finns ett rapporterat mål.";
        Assert.Throws<InvalidDataException>(() => LocalizedMatchReplyValidation.Validate(Reply(answer, fault == "unknown" ? "private-id" : "coverage"), Evidence, MatchLanguage.Spanish));
    }
    [Fact]
    public void EnglishSelectionUsesTheOriginalValidatorExactly()
    {
        var json = Reply("La velocidad fue 30.");
        var expected = MatchReplyValidation.Validate(json, Evidence);
        var actual = LocalizedMatchReplyValidation.Validate(json, Evidence, MatchLanguage.English);
        Assert.Equal(expected.Answer, actual.Answer); Assert.Equal(expected.Evidence, actual.Evidence);
    }
}
