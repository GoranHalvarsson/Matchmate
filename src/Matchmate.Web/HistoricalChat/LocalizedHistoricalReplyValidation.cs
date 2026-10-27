using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.HistoricalChat;

// Additive, conservative lexical gates. These do not validate arbitrary multilingual football prose.
public static class LocalizedHistoricalReplyValidation
{
    private const RegexOptions Policy = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Detail = @"(?:malskytt(?:ar|en|arna)?|malgorare|malmekanismer?|malminuter?|malhandelser?|matchhandelser?|spelare|assist(?:er|ans)?|straff(?:ar|en)?|roda\s+kort|gula\s+kort|laguppstallningar?|formation(?:er|en)?|taktik(?:en)?|presspel|kontring(?:ar)?|nick(?:ar)?|horn(?:a|or)|inlagg|retur(?:er)?|volley|frispark(?:ar)?|skott(?:en)?|raddning(?:ar)?|byten|avspark|halvtidsresultat|arenahistorik|kapacitet|goleador(?:es)?|jugador(?:es)?|mecanismos?\s+de\s+gol|minutos?\s+de\s+gol|eventos?\s+de\s+gol|asistencias?|penaltis?|tarjetas?\s+(?:rojas?|amarillas?)|alineaciones?|formaciones?|tacticas?|presion|contraataques?|cabezazos?|corners?|centros?|rebotes?|voleas?|tiros?\s+libres?|disparos?|paradas?|sustituciones?|saque\s+inicial|resultado\s+al\s+descanso|historia\s+del\s+estadio|capacidad)";
    private const string DetailList = Detail + @"(?:\s*(?:,|och|eller|y|o|ni)\s*(?:de\s+)?" + Detail + @"){0,8}";
    private static readonly Regex Unavailable = new(@"\b(?:inga|ingen|inget)\s+" + DetailList + @"\s+(?:finns\s+i\s+underlaget|anges|ar\s+tillganglig[at]?)\b"
        + @"|\bno\s+hay\s+(?:datos\s+de\s+)?" + DetailList + @"\b"
        + @"|\b" + DetailList + @"\s+(?:saknas|ar\s+okand[at]?|no\s+(?:estan|esta)\s+disponibles?)\b", Policy);
    private static readonly Regex Unsupported = new(@"\b(?:" + Detail + @"|mal|gol(?:es)?|gjorde|gjort|natade|marco|marcaron|anoto|anotaron|anotado|expulsado|utvisad|pressade|byggdes|invigdes|renoverades|inaugur\w*|constru\w*|renov\w*)\b", Policy);
    private static string Fold(string text) => string.Concat(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).Normalize(NormalizationForm.FormC);

    public static (string Answer, MatchChatEvidence[] Evidence) Validate(string json, HistoricalPrepared prepared, MatchLanguage language)
    {
        // The complete original English guard always runs unchanged, including identity and citation checks.
        var validated = HistoricalReplyValidation.Validate(json, prepared);
        if (MatchLanguages.Normalize(language) == MatchLanguage.English) return validated;
        LocalizedMatchReplyValidation.Validate(json, prepared.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal), language);
        var cited = validated.Evidence.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var sentence in Regex.Split(validated.Answer, @"(?<=[.!?])\s+|\r?\n"))
        {
            var text = Fold(sentence);
            var allowedGoal = prepared.GoalFacts.Any(fact => cited.Contains(fact.EvidenceId) && Regex.IsMatch(text,
                @"\A\s*" + Regex.Escape(Fold(fact.ClubName))
                + (language == MatchLanguage.Swedish ? @"\s+gjorde\s+" + fact.Goals.ToString(CultureInfo.InvariantCulture) + @"\s+mal\s+"
                    + (fact.Scope == "fixture" ? @"i\s+den\s+har\s+matchen" : @"i\s+det\s+tidigare\s+resultaturvalet")
                    : @"\s+marco\s+" + fact.Goals.ToString(CultureInfo.InvariantCulture) + @"\s+gol(?:es)?\s+"
                    + (fact.Scope == "fixture" ? @"en\s+este\s+partido" : @"en\s+la\s+muestra\s+de\s+resultados\s+anteriores")) + @"[.!?]?\s*\z", Policy));
            if (allowedGoal) continue;
            var remaining = Unavailable.Replace(text, "");
            if (Unsupported.IsMatch(remaining)) throw new InvalidDataException("Unsupported localized historical goal or detail claim.");
            // An availability caveat cannot shelter a detached detail count in the rest of the sentence.
            if (remaining != text && MatchReplyValidation.Numbers(remaining).Any()) throw new InvalidDataException("Localized historical detail value beside a caveat.");
            if (prepared.Clubs.Any(club => text.Contains(Fold(club.Ground), StringComparison.OrdinalIgnoreCase))
                && Regex.IsMatch(text, @"\b(?:spelades|spelade|vann|forlorade|holls|jugo|jugaron|disputo|disputaron|celebro|gano|perdio|sede)\b", Policy))
                throw new InvalidDataException("Season ground cannot establish the localized fixture venue.");
        }
        return validated;
    }
}
