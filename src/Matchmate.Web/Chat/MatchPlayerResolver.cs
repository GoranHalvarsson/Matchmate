using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Matchmate.Contracts;

namespace Matchmate.Web.Chat;

// Server-owned intent text/reference only; Decision supplies the allowlisted workflow ID, never a person ID.
public sealed record MatchPlayerQuestion(string Question, string? OriginalQuestion = null, string? ReferenceSubjectId = null);
public sealed record MatchPlayerResolution(string? SubjectId, string Message);
public static class MatchPlayerResolver
{
    private static readonly FrozenSet<string> References = new[] {
        "HOW DID HE DO", "HOW DID SHE DO", "WHAT DID HE DO", "TELL ME ABOUT HIM", "TELL ME ABOUT HER",
        "HOW DID HE PLAY", "HOW DID SHE PLAY", "HOW DID THEY PLAY", "WHAT ABOUT HIM", "WHAT ABOUT HER", "WHAT ABOUT THEM",
        "HOW IS HE PLAYING", "HOW IS SHE PLAYING", "HOW IS HE DOING", "HOW IS SHE DOING", "WHERE IS HE", "WHERE IS SHE",
        "TELL ME MORE ABOUT HIM", "TELL ME MORE ABOUT HER", "HUR SPELADE HAN", "HUR SPELADE HON", "VAD GJORDE HAN", "VAD GJORDE HON",
        "BERATTA OM HONOM", "BERATTA OM HENNE", "BERATTA MER OM HONOM", "BERATTA MER OM HENNE", "VAR AR HAN", "VAR AR HON",
        "COMO JUGO EL", "COMO JUGO ELLA", "COMO JUEGA EL", "COMO JUEGA ELLA", "HABLAME DE EL", "HABLAME DE ELLA", "QUE HIZO EL", "QUE HIZO ELLA"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static bool ValidText(string? text) => text is { Length: > 0 and <= MatchChatLimits.InputChars } && Normalize(text) is not null;
    public static bool ReferenceQuestion(string question) => Normalize(question) is { } normalized && References.Contains(normalized);
    // Canonical decomposition, invariant Unicode case, marks removed; apostrophes join, other punctuation separates.
    // No transliteration, fuzzy matching or external nickname/alias table.
    private static string? Normalize(string text)
    {
        try
        {
            var result = new StringBuilder(); var space = false;
            foreach (var rune in Regex.Replace(text, @"(?<=\p{L})['\u2019][sS](?=$|[^\p{L}\p{N}])", "", RegexOptions.CultureInvariant).Normalize(NormalizationForm.FormD).EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
                if (rune.Value is '\'' or 0x2019) continue;
                if (Rune.IsLetterOrDigit(rune)) { if (space && result.Length > 0) result.Append(' '); result.Append(Rune.ToUpperInvariant(rune)); space = false; }
                else space = true;
            }
            return result.ToString();
        }
        catch (ArgumentException) { return null; }
    }
    private static readonly FrozenSet<string> SurnameGrammar = new[] {
        "DID", "DOES", "DO", "ABOUT", "IS", "WAS", "HAS", "HAVE", "PLAYER", "THE", "OF", "ON", "FOR", "BY", "FROM", "AND", "OR", "ME",
        "SPELADE", "SPELAR", "GJORDE", "OM", "AR", "JUGO", "JUEGA", "HIZO", "SOBRE", "DE", "EL", "LA", "DEL", "Y", "O"
    }.ToFrozenSet(StringComparer.Ordinal);
    private sealed record Hit(string Id, int Start, int Length, bool Full);
    public static MatchPlayerResolution Resolve(IReadOnlyList<SquadPerson> people, MatchPlayerQuestion question)
    {
        var text = Normalize(question.Question); if (text is null) return new(null, "Use a valid player name from this recording.");
        var hits = new List<Hit>();
        void Find(string name, string id, bool full)
        {
            var offset = 0;
            while (offset < text.Length && text.IndexOf(name, offset, StringComparison.Ordinal) is var start && start >= 0)
            {
                if ((start == 0 || text[start - 1] == ' ') && (start + name.Length == text.Length || text[start + name.Length] == ' ')) hits.Add(new(id, start, name.Length, full));
                offset = start + Math.Max(1, name.Length);
            }
        }
        foreach (var person in people)
        {
            var name = Normalize(person.Name ?? ""); if (string.IsNullOrEmpty(name)) continue;
            Find(name, person.PersonId, name.Contains(' '));
            if (name.LastIndexOf(' ') is var split && split >= 0) Find(name[(split + 1)..], person.PersonId, false);
        }
        var maximal = hits.Where(h => !hits.Any(other => other.Full && other.Start <= h.Start && other.Start + other.Length >= h.Start + h.Length && other.Length > h.Length)).ToArray();
        // Immediate coordination requests another subject even when only one name matches this roster.
        // Keep this lexical boundary conservative; predicates after the name ("play and contribute") are unaffected.
        if (maximal.Any(h => text[(h.Start + h.Length)..].StartsWith(" AND ", StringComparison.Ordinal)
            || text[(h.Start + h.Length)..].StartsWith(" OR ", StringComparison.Ordinal)
            || text[..h.Start].EndsWith(" AND ", StringComparison.Ordinal)
            || text[..h.Start].EndsWith(" OR ", StringComparison.Ordinal)
            || new[] { " OCH ", " ELLER ", " Y ", " O " }.Any(join => text[(h.Start + h.Length)..].StartsWith(join, StringComparison.Ordinal) || text[..h.Start].EndsWith(join, StringComparison.Ordinal))))
            return new(null, "Please ask about one player at a time, using one full roster name.");
        // A unique surname is not permission to replace an explicitly wrong given name.
        foreach (var hit in maximal.Where(h => !h.Full && h.Start > 0))
        {
            var before = text[..(hit.Start - 1)]; var preceding = before[(before.LastIndexOf(' ') + 1)..];
            if (!SurnameGrammar.Contains(preceding)) return new(null, "I could not match that full name in this recording. Please use the full roster name or a bare unique surname.");
        }
        var ids = maximal.Select(h => h.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 1) return new(ids[0], "Player resolved against this recording's roster.");
        if (ids.Length > 1)
        {
            var ambiguous = maximal.GroupBy(h => (h.Start, h.Length)).Any(g => g.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() > 1);
            return new(null, ambiguous ? "That name matches more than one player in this recording. Please use the player's full roster name."
                : "Please ask about one player at a time, using one full roster name.");
        }
        if (question.OriginalQuestion is null && ReferenceQuestion(question.Question) && question.ReferenceSubjectId is { } reference
            && people.Any(p => p.PersonId == reference)) return new(reference, "Trusted conversation subject resolved against this recording's roster.");
        return new(null, "I could not match that player in this recording. Please use their full roster name or unique surname.");
    }
}
