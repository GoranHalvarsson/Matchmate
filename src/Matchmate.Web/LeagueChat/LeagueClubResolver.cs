using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Matchmate.Contracts;

namespace Matchmate.Web.LeagueChat;

public enum LeagueClubBinding { Context, Named }
public sealed record LeagueClubResolution(string? ClubId, string? Clarification, bool Explicit);
public static class LeagueClubResolver
{
    private const string ClubReferenceNoun = @"(?:club|team|form|performance|results|record|standing|position|points|ground|stadium|town|matches|games|wins|draws|losses|goals)";
    private const string ScopeReferenceNoun = @"(?:day|date|season|snapshot|view|cutoff|reference|table|standings|league)";
    private const string ReferenceQualifier = @"(?:recent|latest|new|home|current|selected|league|particular|older|historical|played|committed|chosen|shown|listed|recorded|same|highlighted)";
    private const string ReferenceDeterminer = @"(?:our|their|the|this|that|selected|my|your)";
    private const string InterrogativeDeterminer = @"(?:what|which)";
    // Complete bounded noun phrases, not aliases or a global exemption for their words.
    // The bare legacy heads preserve established owner references; day/selected alone do not.
    private const string ClubReferenceFacets = ClubReferenceNoun + @"(?: (?:and|or) (?:" + ReferenceQualifier + @" ){0,4}" + ClubReferenceNoun + @"){0,3}";
    private const string QualifiedClubReferenceFacts = @"(?:" + ReferenceQualifier + @" ){0,4}" + ClubReferenceFacets;
    private const string NamedReference = @"supportedclub(?: " + ClubReferenceFacets + @")?";
    private const string ScopeReferencePhrase = ReferenceDeterminer + @" (?:" + ReferenceQualifier + @" ){0,4}" + ScopeReferenceNoun;
    private const string ContextReferencePhrase = @"(?:" + ReferenceDeterminer + @" (?:" + ReferenceQualifier + @" ){0,4}(?:" + ClubReferenceFacets + "|" + ScopeReferenceNoun
        + "|" + NamedReference + ")|" + NamedReference + @"|club|team|ground|stadium|town|season)";
    private static string ContextReferenceView(string value)
    {
        // Only a complete selector-relative clause belongs to its existing
        // closed club/team reference. Never discard residual identity words.
        var relative = Regex.Match(value.Trim(), "^(?<reference>" + ContextReferencePhrase
            + @") (?:i (?:(?:have|ve) )?(?:selected|picked|chosen)|i have open|under discussion)$", RegexOptions.CultureInvariant);
        return relative.Success && Regex.IsMatch(relative.Groups["reference"].Value, @"\b(?:supportedclub|club|team)$", RegexOptions.CultureInvariant)
            ? relative.Groups["reference"].Value : value.Trim();
    }
    private static bool IsContextReference(string value) => Regex.IsMatch(ContextReferenceView(value), "^(?:" + ContextReferencePhrase + ")$", RegexOptions.CultureInvariant);
    private static bool IsClubContextReference(string value) => IsContextReference(value)
        && Regex.IsMatch(ContextReferenceView(value), @"(?:\b" + ClubReferenceNoun + @"|\bsupportedclub(?: (?:club|team))?)$", RegexOptions.CultureInvariant);
    private static bool IsClubIdentityReference(string value) => IsContextReference(value)
        && Regex.IsMatch(ContextReferenceView(value), @"\b(?:supportedclub|club|team)$", RegexOptions.CultureInvariant);
    private static bool IsIdentityOrPlaceReference(string value) => IsContextReference(value)
        && Regex.IsMatch(ContextReferenceView(value), @"\b(?:supportedclub|club|team|ground|stadium|town)$", RegexOptions.CultureInvariant);
    private static bool RequiresReferenceSubject(string value) => IsClubContextReference(value)
        && Regex.IsMatch(ContextReferenceView(value), "^" + ReferenceDeterminer + @"\b", RegexOptions.CultureInvariant)
        && (Regex.IsMatch(ContextReferenceView(value), @"^(?:our|their|my|your|selected)\b", RegexOptions.CultureInvariant)
            || Regex.IsMatch(ContextReferenceView(value), @"\b(?:club|team|ground|stadium|town)$", RegexOptions.CultureInvariant));

    public static string Normalize(string value)
    {
        try
        {
            var chars = value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
            return Regex.Replace(new string(chars).ToLowerInvariant().Replace("’s", "").Replace("'s", ""), "[^a-z0-9]+", " ").Trim();
        }
        catch (ArgumentException) { return ""; }
    }
    public static bool Reference(string question) => Regex.IsMatch(Normalize(question), @"\b(they|their|them)\b", RegexOptions.CultureInvariant);
    public static bool WholeLeague(string question) => Regex.IsMatch(Normalize(question), @"\b(?:whole|entire|full) (?:league|table)\b|\ball (?:the )?(?:clubs|teams)\b|\b(?:show|see|view|display|give|list)(?: (?:me|us|the|this|our|selected|current|a))* league table\b", RegexOptions.CultureInvariant);
    public static bool RequiresSubject(string question)
    {
        var text = ValidationText(Normalize(question));
        return Subjects(text, FactSyntax(question)).Any(s => s == "it" || IsClubContextReference(s) || Regex.IsMatch(s, @"^(?:we|us|they|them|our|their|my|your|selected)\b", RegexOptions.CultureInvariant) && !IsContextReference(s))
            || Regex.Matches(text, @"\b" + ContextReferencePhrase + @"\b", RegexOptions.CultureInvariant).Any(m => RequiresReferenceSubject(m.Value))
            || PlaceSubjects(text).Any(subject => subject == "it")
            || Regex.IsMatch(text, @"\bwhere (?:is|are|do|does) (?:we|they)\b", RegexOptions.CultureInvariant);
    }
    public static LeagueClubResolution Resolve(LeagueClub[] clubs, string question, string? selected, string? reference, LeagueClubBinding binding, bool requireClub, bool wholeLeague = false)
    {
        if (binding == LeagueClubBinding.Named && LeagueQuestionProjection.BriefIdentityReply(question))
            return ResolveBrief(clubs, question);
        // Choose factual productions from raw private syntax before any trusted
        // alias substitution. Membership validates the returned whole identities.
        var syntax = FactSyntax(question);
        var referenceSyntax = Regex.Split(question, @"[.!?;:,\r\n]+").Select(FactSyntax).ToArray();
        var text = " " + Normalize(question) + " "; var hits = new List<(string Club, int Start, int Length)>(); var trustedAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var club in clubs)
        {
            var aliases = Aliases(club);
            trustedAliases.UnionWith(aliases.Where(a => a.Length > 1));
            foreach (var alias in aliases.Where(a => a.Length > 1))
            {
                var offset = 0;
                while (text.IndexOf(" " + alias + " ", offset, StringComparison.Ordinal) is var start && start >= 0)
                { hits.Add((club.Id, start, alias.Length + 2)); offset = start + 1; }
            }
        }
        var longest = hits.Where(hit => !hits.Any(other => other.Start <= hit.Start && other.Start + other.Length >= hit.Start + hit.Length && other.Length > hit.Length)).ToArray();
        var matched = longest.Select(h => h.Club).Distinct(StringComparer.Ordinal).ToArray();
        // Mask only trusted aliases, then check bounded grammatical subject slots. This is
        // an eligibility guard, not a workflow router or general-language entity recognizer.
        var masked = ValidationText(text);
        foreach (var hit in longest.OrderByDescending(h => h.Start))
        {
            // Overlapping aliases of the same identity were removed above.
            if (hit.Start + hit.Length <= masked.Length)
                masked = masked[..hit.Start] + " supportedclub " + masked[(hit.Start + hit.Length)..];
        }
        string MaskAliases(string value)
        {
            foreach (var alias in trustedAliases.OrderByDescending(a => a.Length))
                value = Regex.Replace(value, @"(?<![a-z0-9])" + Regex.Escape(alias) + @"(?![a-z0-9])", "supportedclub", RegexOptions.CultureInvariant);
            return value;
        }
        // Preserve structural coordination while masking a full trusted long alias first.
        // Brighton & Hove Albion is one identity; Arsenal & an unknown name is not.
        var coordination = ValidationText(Normalize(question.Replace("&", " and ")));
        foreach (var alias in trustedAliases.OrderByDescending(a => a.Length))
            coordination = Regex.Replace(coordination, @"(?<![a-z0-9])" + Regex.Escape(alias) + @"(?![a-z0-9])", "supportedclub", RegexOptions.CultureInvariant);
        if (wholeLeague) coordination = Regex.Replace(coordination, @"\bsupportedclub(?: and supportedclub)+\b", "supportedclub", RegexOptions.CultureInvariant);
        // Club-owned fact facets require a subject but are not another identity.
        // Mark only complete club/team/name references; preserve anaphoric fact phrases,
        // scope references and every residual unknown token for the existing guards.
        coordination = Regex.Replace(coordination, @"\b" + ContextReferencePhrase + @"\b", m => IsClubIdentityReference(m.Value) ? "contextclub" : m.Value, RegexOptions.CultureInvariant);
        var unresolvedCoordination = QuestionClauses(coordination).Any(clause =>
            Regex.IsMatch(clause, @"\b(?:supportedclub|contextclub) (?:and (?!their\b|its\b|they\b|them\b|it\b)|(?:versus|vs|against) )\S", RegexOptions.CultureInvariant)
            || Regex.IsMatch(clause, @"\b(?:about|for|to) (?!our\b|their\b|the\b|selected\b)[a-z0-9]+(?: [a-z0-9]+){0,2} and (?:their |our )?(?:supportedclub|contextclub)\b", RegexOptions.CultureInvariant));
        // Applicable fact questions already expose every complete owner. Do not
        // reparse their selector-relative tail (I HAVE SELECTED'S) as a shorter
        // possessive identity. Other productions retain the legacy guard.
        var unresolvedPossessive = QuestionClauses(syntax)
            .Where(clause => FactQuestion(clause).State == LeagueClubFactQuestionSyntax.Status.NotApplicable)
            .SelectMany(clause => Regex.Matches(MaskAliases(clause), @"\b(?<subject>" + ContextReferencePhrase + @"|[a-z0-9]+) possessive " + QualifiedClubReferenceFacts + @"\b", RegexOptions.CultureInvariant))
            .Any(m => !IsContextReference(m.Groups["subject"].Value) && !(wholeLeague && m.Groups["subject"].Value == "league"));
        if (UnresolvedIdentity(masked, syntax, referenceSyntax, MaskAliases) || unresolvedPossessive || unresolvedCoordination)
            return new(null, "I could not match one supported club identity in that question. Please use one full club name from the selected season.", true);
        if (matched.Length > 1) return wholeLeague ? new(null, null, true) : new(null, "Please name one club from this selected season; that reference is ambiguous or asks about multiple clubs.", true);
        if (matched.Length == 1)
        {
            return new(matched[0], null, true);
        }
        if (binding == LeagueClubBinding.Named)
            return new(null, "I could not match one supported club name in the selected season. Please use its full name.", true);
        // The fixed workflow supplies selected/reference intent. Ordinary nouns need no guessing.
        var subject = Reference(question) && reference is not null ? reference : selected ?? reference;
        if (subject is not null && clubs.Any(c => c.Id == subject)) return new(subject, null, false);
        return requireClub ? new(null, "Which club in the selected season do you mean? Use its name or select it in the table.", false) : new(null, null, false);
    }
    private static HashSet<string> Aliases(LeagueClub club)
    {
        // Full trusted identities and the same explicit football aliases used for normal questions.
        var aliases = new HashSet<string> { Normalize(club.Name), Normalize(club.Id) };
        foreach (var alias in club.Id switch
        {
            "aston-villa" => new[] { "villa" }, "tottenham" => ["spurs"],
            "manchester-city" => ["man city"], "manchester-united" => ["man united", "man utd"],
            "crystal-palace" => ["palace"], "nottingham-forest" => ["forest"],
            "brighton" => ["brighton and hove albion"], _ => Array.Empty<string>()
        }) aliases.Add(alias);
        return aliases;
    }
    public static LeagueClubResolution ResolveBrief(LeagueClub[] clubs, string question)
    {
        var identity = LeagueQuestionProjection.BriefIdentity(question);
        string[] matches = identity is null ? [] : clubs.Where(c => Aliases(c).Contains(identity)).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray();
        return matches.Length == 1 ? new(matches[0], null, true)
            : new(null, "Please give exactly one full club name or supported alias from the selected season.", true);
    }
    private static bool UnresolvedIdentity(string text, string syntax, string[] referenceSyntax, Func<string, string> maskAliases)
    {
        // These are grammatical identity slots, not arbitrary topic complements.
        // We deliberately do not interpret free-English about/to/location phrases.
        bool Supported(string value) => IsContextReference(value)
            || Regex.IsMatch(value.Trim(), @"^(?:we|us|they|them|our|their|it|each (?:club|team)|all (?:the )?(?:clubs|teams)|(?:the )?(?:whole|entire|full) league)$", RegexOptions.CultureInvariant);
        bool SupportedPlaceReference(string value)
        {
            if (IsContextReference(value)) return true;
            var nominal = LeagueClubFactQuestionSyntax.AnalyzePlaceReference(value, ReferenceQualifier);
            return nominal.State == LeagueClubFactQuestionSyntax.Status.Parsed && nominal.Identities.All(SupportedPlaceOwner);
        }
        bool SupportedPlaceOwner(string value) => Supported(value) || Regex.IsMatch(value, "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant);
        bool SupportedFactIdentity(string value)
        {
            var maskedIdentity = maskAliases(value);
            // A partial known alias plus TOWN/RECORD is not a whole identity.
            // Club/team wrappers remain the existing closed identity references.
            return Regex.IsMatch(maskedIdentity, @"\bsupportedclub\b", RegexOptions.CultureInvariant)
                ? IsClubIdentityReference(maskedIdentity) : Supported(maskedIdentity);
        }
        // Verb modifiers belong to the predicate, not the club identity. The -ly
        // class and closed temporal particles cover grammar without topic-word guessing.
        if (FactQuestions(syntax).Any(fact => fact.State == LeagueClubFactQuestionSyntax.Status.InvalidOrAmbiguous)
            || FactQuestions(syntax).SelectMany(fact => fact.Identities).Any(identity => !SupportedFactIdentity(identity))
            || Subjects(text, maskAliases(syntax), syntax).Any(subject => !Supported(maskAliases(subject)))) return true;
        // Validate the entire bounded embedded reference, including any inserted name.
        // A valid neighbouring context phrase never licenses an unknown modifier.
        // The existing possessive anaphor is a fresh boundary in this negative
        // view only. It never changes the original question or trusted subject.
        // A separately headed question is a new clause, while ordinary punctuation
        // still cannot hide an unknown name inside the same conservative span.
        // An interrogative factual heading has a negative noun-phrase role only.
        // It is never a personal owner or positive selected-club authority.
        bool InterrogativeReference(string value)
        {
            if (Supported(value) || Regex.IsMatch(value, "^(?:" + QualifiedClubReferenceFacts + "|(?:" + ReferenceQualifier + @" ){0,4}(?:" + ScopeReferenceNoun + "|clubs|teams))$", RegexOptions.CultureInvariant)) return true;
            var owned = Regex.Match(value, @"^(?<owner>.+?)(?: possessive)? " + QualifiedClubReferenceFacts + "$", RegexOptions.CultureInvariant);
            return owned.Success && Supported(owned.Groups["owner"].Value);
        }
        foreach (var segment in referenceSyntax.SelectMany(QuestionClauses))
        {
            // Fold a complete temporal owner, retaining its determiner and every
            // following token: this season's Unknown club remains this Unknown club.
            var rawReferences = ScopeOwnerView(QuestionBody(segment));
            var factQuestion = FactQuestion(rawReferences);
            var references = maskAliases(rawReferences);
            if (factQuestion.State == LeagueClubFactQuestionSyntax.Status.InvalidOrAmbiguous) return true;
            var tokens = references.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var start = 0; start < tokens.Length; start++)
            {
                var heading = start == 0 && tokens.Length > 1
                    && Regex.IsMatch(tokens[start], "^" + InterrogativeDeterminer + "$", RegexOptions.CultureInvariant);
                // BE and contracted WHAT'S precede a nominal; they do not own it
                // or exempt its identity tokens. Verbal auxiliaries keep their role.
                var copular = heading && tokens.Length > 2
                    && (Regex.IsMatch(tokens[1], @"^(?:is|are|was|were|be)$", RegexOptions.CultureInvariant)
                        || tokens[0] == "what" && tokens[1] == "possessive");
                var interrogative = factQuestion.State == LeagueClubFactQuestionSyntax.Status.NotApplicable && heading && (copular || tokens[1] != "possessive"
                    && !Regex.IsMatch(tokens[1], @"^(?:has|have|had|did|does|do|is|are|was|were|be|been|being|can|could|will|would|should|may|might|must)$", RegexOptions.CultureInvariant));
                if (!interrogative && !Regex.IsMatch(tokens[start], "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant)) continue;
                var phraseStart = copular ? 2 : start + 1;
                for (var end = phraseStart; end < tokens.Length; end++)
                {
                    var token = tokens[end];
                    if (token != "selected" && Regex.IsMatch(token, "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant)
                        || Regex.IsMatch(token, @"^(?:and|or|but|also|for|to|from|of|in|on|at|by|with|without|within|about|after|before|through|across|over|under|into|around|as|than|since|until)$", RegexOptions.CultureInvariant)) break;
                    if (!Regex.IsMatch(token, "^(?:supportedclub|" + ClubReferenceNoun + "|" + ScopeReferenceNoun + ")$", RegexOptions.CultureInvariant)
                        && !(interrogative && token is "clubs" or "teams")) continue;
                    // A trusted name can own a following factual constituent, but
                    // never exempts the unknown tokens between that name and head.
                    if (interrogative && token == "supportedclub") continue;
                    // A dual-role word such as LEAGUE is a qualifier only when
                    // the closed factual phrase actually follows it. Keep the
                    // complete owner/modifier span until that factual head.
                    if (interrogative && Regex.IsMatch(string.Join(" ", tokens[end..]), "^(?:" + ReferenceQualifier + @" ){1,4}" + ClubReferenceNoun + @"\b", RegexOptions.CultureInvariant)) continue;
                    if (interrogative)
                    {
                        if (!InterrogativeReference(string.Join(" ", tokens[phraseStart..(end + 1)]))) return true;
                        break;
                    }
                    // The first fact/scope head completes this constituent. Never
                    // hunt for a later club/town through an already complete phrase.
                    if (Regex.IsMatch(token, @"^(?:supportedclub|club|team|ground|stadium|town)$", RegexOptions.CultureInvariant)
                        && !Supported(string.Join(" ", tokens[start..(end + 1)]))) return true;
                    break;
                }
            }
        }
        // Retain the conservative personal span guard as well as the first-head
        // scanner. Include the unsegmented view: punctuation cannot hide a later
        // unknown club behind a valid personal fact phrase.
        foreach (var view in referenceSyntax.Prepend(syntax).SelectMany(QuestionClauses))
        {
            var ownerPattern = @"\b(?<owner>(?=" + ReferenceDeterminer + @"\b|supportedclub\b)" + ContextReferencePhrase + @")(?: possessive\b|(?= (?:" + ReferenceQualifier + @" ){0,4}" + ClubReferenceNoun + @"\b))";
            var references = Regex.Replace(ScopeOwnerView(maskAliases(view)), ownerPattern,
                m => IsIdentityOrPlaceReference(m.Groups["owner"].Value) ? "our" : m.Value, RegexOptions.CultureInvariant);
            var tokens = references.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var start = 0; start < tokens.Length; start++)
            {
                var active = Regex.IsMatch(tokens[start], @"^(?:our|their|my|your)$", RegexOptions.CultureInvariant);
                if (!active && Regex.IsMatch(tokens[start], "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant))
                    for (var end = start + 1; end < tokens.Length && end <= start + 5; end++)
                    {
                        if (tokens[end] != "selected" && Regex.IsMatch(tokens[end], "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant)) break;
                        if (!Regex.IsMatch(tokens[end], "^(?:supportedclub|" + ClubReferenceNoun + "|" + ScopeReferenceNoun + ")$", RegexOptions.CultureInvariant)) continue;
                        active = IsIdentityOrPlaceReference(string.Join(" ", tokens[start..(end + 1)]));
                        break;
                    }
                if (!active) continue;
                // The same noun reader retains complete coordinated place fields,
                // including owners beyond the legacy five-word endpoint bound.
                // This is additional negative validation, never club authority.
                var nominal = LeagueClubFactQuestionSyntax.AnalyzePlaceReference(string.Join(" ", tokens[start..]), ReferenceQualifier, complete: false);
                if (nominal.State == LeagueClubFactQuestionSyntax.Status.InvalidOrAmbiguous
                    || nominal.State == LeagueClubFactQuestionSyntax.Status.Parsed && nominal.Identities.Any(identity => !SupportedPlaceOwner(identity))) return true;
                // Original bound and endpoints, but inspect every endpoint rather
                // than accepting a short valid place phrase and missing a later club.
                for (var end = start + 1; end < tokens.Length && end <= start + 5; end++)
                {
                    if (tokens[end] != "selected" && Regex.IsMatch(tokens[end], "^" + ReferenceDeterminer + "$", RegexOptions.CultureInvariant)) break;
                    if (Regex.IsMatch(tokens[end], @"^(?:club|team|ground|stadium|town)$", RegexOptions.CultureInvariant)
                        && !SupportedPlaceReference(string.Join(" ", tokens[start..(end + 1)]))) return true;
                }
            }
        }
        // 'for NAME on this date' is an identity slot; 'changes to our position' is not.
        foreach (Match match in Regex.Matches(maskAliases(syntax), @"\bfor (?<subject>[a-z0-9]+(?: [a-z0-9]+)??)(?= (?:on this date|at this date|here)\b|\s*$)", RegexOptions.CultureInvariant))
        {
            var candidate = match.Groups["subject"].Value;
            // Only the shared raw literal projection can create this exact
            // marker. Raw lookalikes were escaped before private expansion.
            if (candidate == LeagueTemporalSyntax.Marker) continue;
            // Recipient pronouns in this terminal for-slot are beneficiaries, not club subjects.
            if (candidate is not ("me" or "us" or "you") && !Regex.IsMatch(candidate, @"^(?:our|their|the|this|selected|my|your)\b", RegexOptions.CultureInvariant) && !Supported(candidate)) return true;
        }
        return false;
    }
    private static IEnumerable<string> Subjects(string text, string? placeSyntax = null, string? factSyntax = null)
    {
        const string modifiers = VerbModifiers;
        string[] slots = [
            @"\bhow (?:is|are|was|were|be) (?<subject>[a-z0-9]+(?: [a-z0-9]+){0,4}?) " + modifiers + @"(?:standing|playing|doing|performing|played|ranked|placed)\b",
            @"\bwhere (?:is|are|does|do) (?<subject>.+?) " + modifiers + @"(?:stand|sit|rank|placed|ranked)\b",
            AuxiliaryQuestion(@"(?<subject>[a-z0-9]+(?: [a-z0-9]+){0,4}?)"),
            @"\b(?:how (?!many\b|much\b)|whether )(?<subject>(?!(?:has|have|had|did|does|do|is|are|was|were)\b)[a-z0-9]+(?: [a-z0-9]+){0,4}?) " + modifiers + @"(?:(?:has|have|had|did|does|do|is|are|was|were) )?(?:been )?" + modifiers + @"(?:won|win|lost|lose|drawn|draw|earned|scored|played|play|playing|do|did|doing|done|ranked|placed|perform|performing|performed|fare|fared)\b",
            // Immediate what/where clauses retain identity protection without restarting
            // the generic slot inside a noun phrase. Agreement keeps DOES + PLAYED record
            // distinct from a finite predicate; no ordinary football-topic whitelist.
            @"\b(?:what|where|how) (?:has|have|had) (?<subject>[a-z0-9]+(?: [a-z0-9]+){0,4}?) " + modifiers
                + @"(?:(?:been )?" + modifiers + @"(?:won|lost|drawn|earned|scored|played|done|ranked|placed|performed|fared)|been " + modifiers + @"(?:standing|playing|doing|performing))\b",
            @"\b(?:what|where|how) (?:do|does|did|can|could|will|would|should) (?<subject>[a-z0-9]+(?: [a-z0-9]+){0,4}?) " + modifiers + @"(?:win|lose|draw|earn|score|play|do|perform|fare|stand|sit|rank)\b",
            @"\b(?:what|where) (?:is|are|was|were|be) (?<subject>[a-z0-9]+(?: [a-z0-9]+){0,4}?) " + modifiers + @"(?:playing|doing|performing|ranked|placed)\b"
        ];
        var factClauses = QuestionClauses(factSyntax ?? placeSyntax ?? text);
        var placeClauses = QuestionClauses(placeSyntax ?? text);
        for (var index = 0; index < placeClauses.Length; index++)
            if (index >= factClauses.Length || !FactQuestion(factClauses[index]).OwnsPlaceSubject)
                foreach (var subject in PlaceSubjects(placeClauses[index])) yield return subject;
        foreach (var fact in factClauses.Select(FactQuestion))
            foreach (var identity in fact.Identities) yield return identity;
        var legacyClauses = QuestionClauses(text);
        if (legacyClauses.Length != factClauses.Length) { yield return "untrustedclausealignment"; yield break; }
        for (var clauseIndex = 0; clauseIndex < legacyClauses.Length; clauseIndex++)
        {
            // A bounded request speaker is not a club. Keep the inner question intact,
            // including a leading calendar qualifier; do not split coordinated names.
            var inner = QuestionBody(legacyClauses[clauseIndex]);
            // The shared syntax analysis owns applicable fact questions, including
            // invalid explicit constructions. Never retry their shorter legacy slot.
            if (FactQuestion(factClauses[clauseIndex]).State != LeagueClubFactQuestionSyntax.Status.NotApplicable) continue;
            for (var i = 0; i < slots.Length; i++)
                foreach (Match match in Regex.Matches(inner, slots[i], RegexOptions.CultureInvariant))
                {
                    // The generic auxiliary must not restart inside a wh/request clause.
                    // Specific wh slots above still inspect its actual grammatical subject.
                    if (i == 2 && Regex.IsMatch(inner[..match.Index], @"\b(?:how|where|what|whether)\b", RegexOptions.CultureInvariant)) continue;
                    yield return match.Groups["subject"].Value;
                }
        }
    }
    private static IEnumerable<string> PlaceSubjects(string text)
    {
        // Interrogative place heads have an actual club subject after their
        // auxiliary. Inspect it without treating generic WHAT/WHICH CLUB as a
        // selected-club request, or supplying authority for the anaphor IT.
        var head = @"\b" + InterrogativeDeterminer + " (?:" + ReferenceQualifier + @" ){0,4}(?:ground|stadium|town) ";
        // The closed owner/fact grammar allows up to 25 owner words, one
        // possessive marker and 23 fact words. Capture that whole constituent
        // so an invalid residual token is rejected rather than silently lost.
        const string subjectWords = @"[a-z0-9]+(?: [a-z0-9]+){0,48}?";
        const string subject = @"(?<subject>" + subjectWords + ")";
        var passiveReference = @"(?<passive>" + VerbModifiers + @"(?:listed|recorded|shown) (?<relation>as|for) (?<reference>" + subjectWords + @"))(?= (?:here|in|on|at|for)\b|$)";
        string[] slots = [
            head + @"(?:do|does|did|can|could|will|would|should) " + subject + " " + VerbModifiers + @"(?:use|play)\b",
            head + @"(?:is|are|was|were) (?:" + passiveReference + "|" + subject + " " + VerbModifiers + @"(?:using|playing|associated with|based in|located in|in|at)\b)",
            head + @"(?:has|have|had) " + subject + " " + VerbModifiers + @"(?:(?:played|used)\b|been " + VerbModifiers + @"(?:using|playing|associated with|based in|located in|in|at)\b)"
        ];
        return slots.SelectMany(slot => Regex.Matches(text, slot, RegexOptions.CultureInvariant)).Select(match =>
        {
            if (match.Groups["reference"].Success)
            {
                // FOR supplies the complete owner; AS owns a HOME description.
                // Both retain unknown/mixed tokens for normal identity checks.
                var reference = match.Groups["reference"].Value;
                if (match.Groups["relation"].Value == "for") return reference == "its" ? "it" : ScopeOwnerView(reference);
                var home = Regex.Match(reference, @"^(?<owner>.+?)(?: possessive)? home(?: (?:ground|stadium))?$", RegexOptions.CultureInvariant);
                if (!home.Success) return match.Groups["passive"].Value;
                var owner = home.Groups["owner"].Value;
                return owner == "its" ? "it" : owner;
            }
            var value = match.Groups["subject"].Value;
            // Only a complete possessive factual subject yields its owner.
            // Unknown owners and malformed fact text retain their identity tokens.
            var owned = Regex.Match(value, @"^(?<owner>.+?) possessive " + QualifiedClubReferenceFacts + "$", RegexOptions.CultureInvariant);
            return owned.Success ? owned.Groups["owner"].Value : value;
        });
    }
    private const string VerbModifiers = @"(?:(?:[a-z]+ly|already|still|yet|now|ever|just)\s+)*";
    private static LeagueClubFactQuestionSyntax.Analysis FactQuestion(string clause)
        => LeagueClubFactQuestionSyntax.Analyze(ScopeOwnerView(QuestionBody(clause)), ReferenceQualifier, "(?:" + LeagueTimeScope.SelectedDay + "|" + ScopeReferencePhrase + ")",
            LeagueTimeScope.TaskOrderRequest);
    private static IEnumerable<LeagueClubFactQuestionSyntax.Analysis> FactQuestions(string text)
        => QuestionClauses(text).Select(FactQuestion);
    // Same-length escapes retain positional alias-hit offsets. Trusted identity
    // markers are created only after this view, by actual alias substitutions.
    private static string ValidationText(string value) => Regex.Replace(value, @"\b(?:supportedclub|contextclub|possessive|" + LeagueTemporalSyntax.Marker + @")\b", match => match.Value switch
    { "supportedclub" => "untrustedclub", "contextclub" => "unknownclub", "possessive" => "unknownraw", _ => "untrustedrawscope" }, RegexOptions.CultureInvariant);
    private static string FactSyntax(string question)
    {
        var normalized = Normalize(question); var ownership = "internalownershipstamp"; var temporal = "internaltemporalstamp";
        while (normalized.Contains(ownership, StringComparison.Ordinal)) ownership += "x";
        while (normalized.Contains(temporal, StringComparison.Ordinal)) temporal += "x";
        var raw = LeagueTemporalSyntax.Project(question, LeagueTemporalSyntax.Scan(question), temporal);
        var syntax = Normalize(Regex.Replace(raw, "['\u2019]s", " " + ownership + " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        return ValidationText(syntax).Replace(ownership, "possessive", StringComparison.Ordinal).Replace(temporal, LeagueTemporalSyntax.Marker, StringComparison.Ordinal);
    }
    private static string ScopeOwnerView(string value) => Regex.Replace(Regex.Replace(value, @"\bits\b", "their", RegexOptions.CultureInvariant),
        @"\b" + ScopeReferencePhrase + @" possessive\b", m => m.Value[..m.Value.IndexOf(' ')], RegexOptions.CultureInvariant);
    private static string QuestionBody(string clause) => LeagueTimeScope.RequestBody(clause);
    private static string AuxiliaryQuestion(string subject) => @"\b(?:has|have|did|does|do|is|are|can|could|will|would|should) " + subject + " "
        + VerbModifiers + @"(?:been )?" + VerbModifiers + @"(?:won|win|lost|lose|drawn|draw|earned|scored|played|play|playing|do|doing|done|ranked|placed|perform|performing|performed|fare|fared)\b";
    private static string[] QuestionClauses(string text) => Regex.Split(text.Trim(),
        @"\b(?:and|but|also|or)\s+(?=(?:how|where|what|which|whether)\b|(?:please )?(?:can|could|would|will|may) (?:you|we|i) (?:please )?(?:show|tell|explain|describe|get)\b|"
        + AuxiliaryQuestion(@"[a-z0-9]+(?: [a-z0-9]+){0,4}?") + ")", RegexOptions.CultureInvariant);
}
