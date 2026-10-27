using System.Text.RegularExpressions;

namespace Matchmate.Web.Chat;

// Closed aliases for existing presentation/selection guards only. Never used to choose a workflow or alter router input.
internal static class MatchLocalizedIntent
{
    private static readonly (string From, string To)[] Aliases = [
        ("andra gula kortet", "second yellow card"), ("andra gula", "second yellow"), ("röda kortet", "red card"), ("rött kort", "red card"),
        ("gula kortet", "yellow card"), ("gult kort", "yellow card"), ("utvisningen", "dismissal"), ("utvisning", "dismissal"), ("kortet", "card"), ("kort", "card"),
        ("segunda amarilla", "second yellow"), ("tarjeta roja", "red card"), ("tarjeta amarilla", "yellow card"), ("expulsión", "dismissal"),
        ("stapeldiagram", "bar chart"), ("gráfico de barras", "bar chart"), ("gráfica de barras", "bar chart"),
        ("gráfico", "chart"), ("gráfica", "chart"), ("graf", "graph"), ("som ett", "as a"), ("como un", "as a"),
        ("som", "as"), ("como", "as"), ("ett", "a"), ("un", "a"), ("över", "of"),
        ("skott på mål", "shots on target"), ("skott utanför mål", "shots off target"), ("tiros a puerta", "shots on target"), ("tiros fuera", "shots off target"),
        ("passningsförsök", "pass attempts"), ("passningsprocent", "pass completion rate"), ("passningssäkerhet", "pass accuracy"), ("passningsprecision", "pass accuracy"),
        ("bollinnehavsandel", "possession share"), ("bollinnehavsprocent", "possession percentage"), ("porcentaje de acierto en pases", "pass completion rate"),
        ("intentos de pase", "pass attempts"), ("porcentaje de pases completados", "pass completion rate"), ("precisión de pase", "pass accuracy"),
        ("båda lagen", "both teams"), ("ambos equipos", "both teams"), ("den här", "this"), ("hur många", "how many"), ("hur mycket", "how much"),
        ("cuántos", "how many"), ("cuántas", "how many"), ("första halvlek", "first half"), ("andra halvlek", "second half"),
        ("primera parte", "first half"), ("segunda parte", "second half"), ("primer tiempo", "first half"), ("segundo tiempo", "second half"),
        ("jämfört med", "compared with"), ("comparado con", "compared with"), ("jämför", "compare"), ("comparar", "compare"), ("compara", "compare"),
        ("visa", "show"), ("visar", "show"), ("mostrar", "show"), ("muestra", "show"), ("muéstrame", "show me"),
        ("skott", "shots"), ("avslut", "shots"), ("tiros", "shots"), ("disparos", "shots"), ("tiro", "shot"), ("passningar", "passes"), ("passning", "pass"), ("pases", "passes"), ("pase", "pass"),
        ("lyckade", "completed"), ("completados", "completed"), ("completado", "completed"), ("exitosos", "successful"),
        ("bollinnehav", "possession"), ("posesión", "possession"), ("bollkontroll", "control"), ("andel", "share"), ("cuota", "share"),
        ("procent", "percentage"), ("porcentaje", "percentage"), ("illustrerad", "illustrated"), ("ilustrado", "illustrated"),
        ("statistik", "statistics"), ("estadísticas", "statistics"), ("reprisen", "replay"), ("repris", "replay"), ("repetición", "replay"),
        ("genererade", "generated"), ("genererad", "generated"), ("generada", "generated"), ("generado", "generated"),
        ("verkliga", "actual"), ("verklig", "actual"), ("verkligt", "actual"), ("faktiska", "actual"), ("faktisk", "actual"), ("faktiskt", "actual"),
        ("riktiga", "actual"), ("riktig", "actual"), ("riktigt", "actual"), ("reales", "actual"), ("real", "actual"),
        ("uppmätt", "measured"), ("uppmätta", "measured"), ("medida", "measured"), ("medido", "measured"),
        ("hastighet", "speed"), ("velocidad", "speed"), ("distans", "distance"), ("distancia", "distance"),
        ("spelare", "player"), ("spelaren", "player"), ("jugador", "player"), ("jugadores", "players"),
        ("senaste", "last"), ("últimos", "last"), ("minuter", "minutes"), ("minutos", "minutes"), ("tid", "time"), ("tiempo", "time"), ("duración", "duration"),
        ("mål", "goal"), ("målet", "goal"), ("gol", "goal"), ("byte", "substitution"), ("bytet", "substitution"), ("sustitución", "substitution"),
        ("varning", "booking"), ("varningen", "booking"), ("tarjeta", "card"), ("amonestación", "booking"),
        ("händelse", "event"), ("evento", "event"), ("sekvens", "sequence"), ("secuencia", "sequence"), ("rita", "draw"), ("dibuja", "draw"), ("diagrama", "diagram"),
        ("resultat", "score"), ("marcador", "score"), ("hur", "how"), ("cómo", "how"), ("vad", "what"), ("qué", "what"), ("cuál", "what"),
        ("lag", "team"), ("laget", "team"), ("lagen", "teams"), ("equipo", "team"), ("equipos", "teams"),
        ("har", "have"), ("är", "is"), ("es", "is"), ("son", "are"), ("ha", "has"), ("han", "have"), ("tiene", "have"), ("gjort", "made"), ("slagit", "made"), ("tagit", "taken"), ("realizado", "made"),
        ("båda", "both"), ("ambos", "both"), ("hittills", "so far"), ("ännu", "yet"), ("vänligen", "please"), ("por favor", "please"),
        ("por", "by"),
        ("och", "and"), ("eller", "or"), ("y", "and"), ("o", "or"), ("inte", "not"), ("no", "not"),
        ("i", "in"), ("en", "in"), ("för", "for"), ("para", "for"), ("av", "of"), ("de", "of"), ("del", "of"), ("till", "to"),
        ("denna", "this"), ("esta", "this"), ("este", "this"), ("el", "the"), ("la", "the"), ("los", "the"), ("las", "the"), ("den", "the"), ("det", "the")
    ];
    internal static string Canonical(string question)
    {
        var value = question;
        foreach (var (from, to) in Aliases) value = Regex.Replace(value, @"(?<![\p{L}\p{N}])" + Regex.Escape(from) + @"(?![\p{L}\p{N}])", to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Closed recipient construction; do not relabel an individual recipient count as team passing totals.
        return Regex.Replace(value, @"\bpasses\s+a\s+", "passes to ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
