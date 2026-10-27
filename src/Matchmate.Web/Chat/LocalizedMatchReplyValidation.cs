using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

// Additive lexical safeguards, not a guarantee against arbitrary invented prose. Original English validation remains authoritative.
public static class LocalizedMatchReplyValidation
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Quantity = @"(?:\d+(?:[.,]\d+)?|en|ett|tva|tre|fyra|fem|sex|sju|atta|nio|tio|elva|tolv|tjugo|trettio|fyrtio|femtio|sextio|sjuttio|attio|nittio|hundra|un|uno|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|once|doce|veinte|treinta|cuarenta|cincuenta|sesenta|setenta|ochenta|noventa|cien|ciento)";
    private const string Metric = @"(?:passnings(?:procent|statistik|antal|sakerhet)(?:en)?|bollinnehav(?:sandel|sprocent)?|hastighet(?:en)?|fart(?:en)?|distans(?:en)?|lopt(?:id|stracka)|lopstracka|xg|forvantade\s+mal|velocidad(?:es)?|distancia(?:s)?|precision\s+de\s+pas(?:e|es)|porcentaje\s+de\s+pases|posesion(?:\s+del\s+balon)?|goles\s+esperados)";
    private const string List = Metric + @"(?:\s*(?:,|och|eller|y|o)\s*" + Metric + @"){0,6}";
    private static readonly Regex Unavailable = new(@"\b(?:inga|ingen|inget|saknar|utan|okand|okant|no\s+hay|sin|faltan)\s+(?:(?:uppgifter|data|information|siffror|datos|informacion)\s+(?:om|for|sobre|de)\s+)?" + List
        + @"|\b" + List + @"\s+(?:(?:ar|es|son|esta|estan)\s+)?(?:okand|okant|okanda|saknas|inte\s+(?:tillganglig[at]?|uppmatt|kand[at]?)|desconocid[ao]s?|no\s+(?:esta\s+|estan\s+)?disponibles?|no\s+se\s+(?:conoce|mide|puede\s+medir))\b"
        + @"|\b(?:kan\s+inte|gar\s+inte\s+att|no\s+se\s+puede|no\s+podemos)\s+(?:mata|berakna|faststalla|avgora|medir|calcular|determinar)\s+" + List, Options);
    private static readonly Regex Metrics = new(@"\b" + Metric + @"\b", Options);
    private static readonly Regex Quantified = new(@"\b" + Quantity + @"\s*(?:%|procent\b|por\s+ciento\b|km\s*/\s*h\b|m\s*/\s*s\b|kilometer\s+i\s+timmen\b|kilometros\s+por\s+hora\b)"
        + @"|\b(?:siffran|vardet|talet|el\s+valor|la\s+cifra|el\s+numero)\s+(?:ar|var|blev|es|fue|era|alcanzo)\s*" + Quantity + @"\b"
        + @"|\b" + Quantity + @"\s+(?:(?:lyckade|korta|avslutade|exitosos|completados)\s+){0,3}(?:passningar|pases)\b"
        + @"|\b(?:passningar|pases|bollinnehav|posesion|formation|formacion)\s*(?:(?:ar|var|es|fue|de|pa|med|con)\s*|[=:]\s*)?" + Quantity + @"\b"
        + @"|\b" + Quantity + @"[\s-]*(?:meter|metrar|kilometer|metros?|kilometros?|yardas?)\b"
        + @"|\b(?:fyra|tre|fem)[-–](?:fyra|tre|fem)[-–](?:tva|tre|ett)|\b(?:cuatro|tres|cinco)[-–](?:cuatro|tres|cinco)[-–](?:dos|tres|uno)\b", Options);
    private static string Fold(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).Normalize(NormalizationForm.FormC);

    public static (string Answer, MatchChatEvidence[] Evidence) Validate(string json, IReadOnlyDictionary<string, MatchChatEvidence> supplied,
        MatchLanguage language, Action<MatchReplyRejection>? rejected = null)
    {
        if (MatchLanguages.Normalize(language) == MatchLanguage.English) return MatchReplyValidation.Validate(json, supplied, rejected);
        MatchReplyRejection? originalReason = null;
        (string Answer, MatchChatEvidence[] Evidence) validated;
        string? originalAnswer = null;
        try { validated = MatchReplyValidation.Validate(json, supplied, reason => originalReason = reason); }
        catch (InvalidDataException) when (originalReason == MatchReplyRejection.UnsupportedMeasurement)
        {
            // Normalize only a closed nonnumeric unavailable-metric clause. Keep a metric cue for the original value-pointer guard.
            using var document = JsonDocument.Parse(json); var answer = document.RootElement.GetProperty("answer").GetString()!;
            var folded = Fold(answer); var normalized = Unavailable.Replace(folded, "no xG");
            if (normalized == folded) { rejected?.Invoke(originalReason.Value); throw; }
            originalAnswer = answer;
            validated = MatchReplyValidation.Validate(JsonSerializer.Serialize(new { answer = normalized, evidenceIds = document.RootElement.GetProperty("evidenceIds") }), supplied, rejected);
        }
        catch { if (originalReason is { } reason) rejected?.Invoke(reason); throw; }
        var finalAnswer = originalAnswer ?? validated.Answer; var policy = Fold(finalAnswer);
        // xG itself is already policed by the unchanged English guard, including its legitimate English caveats.
        if (Quantified.IsMatch(policy) || Metrics.Matches(Unavailable.Replace(policy, "")).Any(match => !match.Value.Equals("xg", StringComparison.OrdinalIgnoreCase)))
        { rejected?.Invoke(MatchReplyRejection.UnsupportedMeasurement); throw new InvalidDataException("Unsupported localized measurement claim."); }
        return (finalAnswer, validated.Evidence);
    }
}
