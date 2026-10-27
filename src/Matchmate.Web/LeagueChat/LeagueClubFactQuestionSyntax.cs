using System.Text.RegularExpressions;

namespace Matchmate.Web.LeagueChat;

// Syntax only: neither membership, aliases, UI selection nor factual eligibility
// participates in choosing a production or consuming a constituent.
internal static class LeagueClubFactQuestionSyntax
{
    internal enum Status { NotApplicable, Parsed, InvalidOrAmbiguous }
    internal readonly record struct Span(int Start, int Length, string Text);
    internal sealed record Analysis(Status State, Span Facts, Span Auxiliary, Span Subject, Span Predicate, IReadOnlyList<Span> Owners)
    {
        internal IEnumerable<string> Identities => Owners.Select(owner => owner.Text)
            .Concat(Subject.Length == 0 ? [] : new[] { Subject.Text }).Distinct(StringComparer.Ordinal);
        internal bool OwnsPlaceSubject => Predicate.Text is "attached to" or "associated with";
    }
    internal static Analysis Analyze(string clause, string qualifierPattern, string scopePattern, string requestPattern)
        => new Reader(clause, qualifierPattern, scopePattern, requestPattern: requestPattern).Read();
    internal static Analysis AnalyzePlaceReference(string phrase, string qualifierPattern, bool complete = true)
        => new Reader(phrase, qualifierPattern, "(?!)", placeNominal: true).ReadNominal(complete);

    private sealed class Reader
    {
        private readonly string _text;
        private readonly Match[] _tokens;
        private readonly Regex _qualifier, _scope;
        private readonly string _qualifierPattern;
        private readonly string _requestPattern;
        private readonly bool _placeNominal;
        private readonly HashSet<int> _relative = [];
        private readonly List<Span> _prefixOwners = [];
        private static readonly HashSet<string> Counts = ["points", "matches", "games", "wins", "draws", "losses", "defeats", "goals"];
        private static readonly HashSet<string> SingularCounts = ["point", "match", "game", "win", "draw", "loss", "defeat", "goal"];
        private static readonly HashSet<string> SummaryHeads = ["record", "summary", "overview", "picture"];
        private static readonly HashSet<string> Facts = ["record", "form", "result", "results", "outcome", "performance", "standing", "standings", "position", "placing", "place", "rank", "ranking", "picture", "summary", "overview"];
        private static readonly HashSet<string> Quantities = ["total", "tally", "count", "number", "amount", "return"];
        private static readonly HashSet<string> Bases = ["have", "add", "gain", "earn", "win", "draw", "lose", "score", "play", "do", "perform", "fare", "stand", "sit", "rank", "look", "show", "mean", "tell", "say", "indicate", "reflect", "affect", "contain", "complete", "collect", "contribute", "enter", "put", "start", "belong", "bring", "accumulate"];
        private static readonly HashSet<string> Past = ["had", "added", "gained", "earned", "won", "drawn", "lost", "scored", "played", "done", "performed", "fared", "ranked", "placed", "looked", "shown", "meant", "told", "said", "indicated", "reflected", "affected", "contained", "completed", "collected", "contributed", "entered", "put", "started", "belonged", "brought", "accumulated"];
        private static readonly HashSet<string> Progressives = ["standing", "playing", "doing", "performing", "accumulating"];
        private static readonly HashSet<string> Auxiliaries = ["do", "does", "did", "has", "have", "had", "is", "are", "was", "were", "be", "can", "could", "will", "would", "should"];
        private static readonly HashSet<string> Copulas = ["is", "are", "was", "were", "be"];
        private sealed record FactRead(int End, List<Span> Owners);
        private readonly record struct VerbRead(int End, int Auxiliary, int Predicate);

        internal Reader(string text, string qualifierPattern, string scopePattern, bool placeNominal = false, string requestPattern = "(?!)")
        {
            _text = text.Trim(); _tokens = Regex.Matches(_text, @"\S+", RegexOptions.CultureInvariant).ToArray();
            _placeNominal = placeNominal; _qualifierPattern = qualifierPattern; _requestPattern = requestPattern;
            _qualifier = new("^(?:" + qualifierPattern + "|table|overall|away|completed|last|few|most)$", RegexOptions.CultureInvariant);
            const string selector = @"(?: i (?:(?:have|ve) )?selected)?";
            _scope = new("^(?:(?:" + scopePattern + @"|(?:the|this|that|our|their|selected) (?:" + qualifierPattern
                + @" |starting ){0,4}(?:page|stage|point|row|screen|timeline|position)|screen)(?: possessive)?(?: (?:shown|selected|displayed|counted))?" + selector
                + @"|(?:(?:the|this|that|our|their|selected) )?(?:" + qualifierPattern + @" ){0,4}" + LeagueTemporalSyntax.Marker
                + @"(?: season)?(?: cutoff)?(?: possessive)?(?: (?:shown|selected|chosen|displayed|counted))?(?: here)?" + selector + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (var i = 0; i + 1 < _tokens.Length; i++)
            {
                if (Word(i) != "i") continue;
                var end = i + 1;
                if (Word(end) is "have" or "ve") end++;
                if (Word(end) is "selected" or "picked" or "chosen" || Word(i + 1) == "have" && Word(end) == "open")
                    for (var at = i; at <= end; at++) _relative.Add(at);
            }
        }
        private string Word(int index) => index < 0 || index >= _tokens.Length ? "" : _tokens[index].Value;
        private Span Slice(int start, int end)
        {
            if (start < 0 || start >= end || end > _tokens.Length) return new(0, 0, "");
            var offset = _tokens[start].Index; var length = _tokens[end - 1].Index + _tokens[end - 1].Length - offset;
            return new(offset, length, _text.Substring(offset, length));
        }
        private Analysis Result(Status state, int factStart = 0, int factEnd = 0, int auxiliary = -1,
            Span subject = default, int predicate = -1, int predicateEnd = -1, IEnumerable<Span>? owners = null)
            => new(state, Slice(factStart, factEnd), Slice(auxiliary, auxiliary + 1), subject,
                Slice(predicate, predicateEnd), (owners ?? []).Concat(_prefixOwners).DistinctBy(owner => owner.Text).ToArray());

        internal Analysis Read()
        {
            var head = Array.FindIndex(_tokens, token => token.Value is "what" or "which" or "how");
            var ownerBoundary = Array.FindIndex(_tokens, token => token.Value is "what" or "which" or "how" or "where" or "whether" or "who");
            var introducer = Array.FindIndex(_tokens, token => token.Value == "for");
            if (introducer >= 0 && (ownerBoundary < 0 || introducer < ownerBoundary))
            {
                var request = new Regex(@"\b(?:" + _requestPattern + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .Match(_text, introducer + 1 < _tokens.Length ? _tokens[introducer + 1].Index : _text.Length);
                if (request.Success && (ownerBoundary < 0 || request.Index < _tokens[ownerBoundary].Index))
                    ownerBoundary = Array.FindIndex(_tokens, token => token.Index == request.Index);
                // A pre-head ownership introducer must be fully accounted for.
                // Unknown wrappers never license an ownerless shorter WH parse.
                if (introducer == 0 || ownerBoundary >= 0)
                {
                    var end = ownerBoundary < 0 ? _tokens.Length : ownerBoundary;
                    var requestBoundary = request.Success && ownerBoundary >= 0 && request.Index == _tokens[ownerBoundary].Index;
                    var nonOwner = NonOwnerPrefix(introducer + 1, end, requestBoundary);
                    // A terminal discourse connector is removed only after the
                    // complete remaining FOR constituent proves non-ownership.
                    if (!nonOwner && requestBoundary && Word(end - 1) == "and") nonOwner = NonOwnerPrefix(introducer + 1, end - 1, true);
                    if (!nonOwner)
                    {
                        if (introducer > 0 && !ScopePrefix(0, introducer)) return Result(Status.InvalidOrAmbiguous);
                        if (!LeadingOwner(end, introducer)) return Result(Status.InvalidOrAmbiguous);
                    }
                }
            }
            if (head < 0 || Word(head) == "how" && Word(head + 1) != "many") return Result(Status.NotApplicable);
            var start = head + (Word(head) == "how" ? 2 : 1);
            if (Word(start) == "else") return Result(Status.NotApplicable);
            var first = start;
            while (_qualifier.IsMatch(Word(first))) first++;
            if (Word(first) is "ground" or "stadium" or "town" && Copulas.Contains(Word(first + 1)))
            {
                var predicate = first + 2;
                while (Modifier(Word(predicate))) predicate++;
                if (Word(predicate) == "attached" && Word(predicate + 1) == "to"
                    || Word(predicate) == "associated" && Word(predicate + 1) == "with")
                    return PlaceAssociation(start, first + 1, predicate);
            }
            if (Word(first) is "ground" or "stadium" or "town" or "club" or "team" or "clubs" or "teams") return Result(Status.NotApplicable);
            // An owner can precede a requested place head. Leave that complete
            // production with the existing place grammar, regardless of owner.
            var placeHead = false;
            for (var at = start; at < _tokens.Length && (!Auxiliaries.Contains(Word(at)) || _relative.Contains(at)); at++)
            {
                if (Word(at) is "ground" or "stadium" or "town") placeHead = true;
                if (CoreLength(at, _tokens.Length) > 0) { placeHead = false; break; }
            }
            if (placeHead) return Result(Status.NotApplicable);
            if (_tokens.Length > 256) return Result(Status.InvalidOrAmbiguous);

            // Immediate auxiliaries can introduce a complete owned factual
            // nominal. An ordinary verbal subject remains with the other grammar.
            if (Auxiliaries.Contains(Word(start)) || Word(head) == "what" && Word(start) == "possessive")
            {
                var owned = ReadFacts(start + 1, _tokens.Length);
                if (owned is null) return Result(Status.NotApplicable);
                if (!Copulas.Contains(Word(start)) && Word(start) != "possessive" && !HasOwnership(start + 1, owned))
                    return Result(Status.InvalidOrAmbiguous);
                var owners = new List<Span>(owned.Owners);
                if (TryVerb(owned.End, Word(start), out var verb) && Tail(verb.End, owners))
                    return Result(Status.Parsed, start + 1, owned.End, start, OwnerSubject(owners), verb.Predicate, verb.End, owners);
                if ((Copulas.Contains(Word(start)) || Word(start) == "possessive") && Tail(owned.End, owners))
                    return Result(Status.Parsed, start + 1, owned.End, start, OwnerSubject(owners), owners: owners);
                return Result(Status.InvalidOrAmbiguous);
            }

            var inverted = new List<(int Aux, int Predicate, VerbRead Verb, bool Nominal)>();
            for (var at = start; at < _tokens.Length; at++)
            {
                if (!Auxiliaries.Contains(Word(at)) || _relative.Contains(at)) continue;
                // A complete requested nominal can itself be described by BE +
                // a passive predicate. Its owner stays in that nominal, rather
                // than fabricating a post-auxiliary club subject.
                if (Copulas.Contains(Word(at)) && Word(at + 1) is "listed" or "recorded" or "shown")
                {
                    inverted.Add((at, at + 1, new(at + 2, -1, at + 1), true));
                    continue;
                }
                for (var predicate = at + 1; predicate < _tokens.Length; predicate++)
                {
                    if (_relative.Contains(predicate)) continue;
                    if (!TryVerb(predicate, Word(at), out var verb)) continue;
                    if (predicate > at + 1) inverted.Add((at, predicate, verb, false));
                    break; // HAS EARNED is a legacy predicate, not inversion.
                }
            }
            if (inverted.Count > 0)
            {
                // Select the explicit construction before checking its entire
                // requested fact phrase. Invalid syntax never retries a shorter one.
                if (inverted.Count != 1) return Result(Status.InvalidOrAmbiguous);
                var candidate = inverted[0];
                var facts = ReadFacts(start, candidate.Aux);
                if (facts is null) return Result(Status.InvalidOrAmbiguous);
                if (facts.End != candidate.Aux) return Result(Status.InvalidOrAmbiguous);
                var owners = new List<Span>(facts.Owners);
                var subject = candidate.Nominal ? default : Slice(candidate.Aux + 1, candidate.Predicate);
                var ownedSubject = candidate.Nominal ? null : ReadFacts(candidate.Aux + 1, candidate.Predicate);
                if (ownedSubject is not null && ownedSubject.End == candidate.Predicate && HasOwnership(candidate.Aux + 1, ownedSubject))
                {
                    owners.AddRange(ownedSubject.Owners);
                    subject = OwnerSubject(ownedSubject.Owners);
                }
                if (!Tail(candidate.Verb.End, owners)) return Result(Status.InvalidOrAmbiguous);
                return Result(Status.Parsed, start, candidate.Aux, candidate.Aux, subject, candidate.Verb.Predicate, candidate.Verb.End, owners);
            }

            var legacy = ReadFacts(start, _tokens.Length);
            if (legacy is null) return Result(Status.NotApplicable);
            // The requested facts can be the grammatical subject themselves:
            // HOW MANY GAMES HAVE ENTERED... has no inverted club subject.
            var direct = TryVerb(legacy.End, "", out var directVerb);
            if (!direct && Copulas.Contains(Word(legacy.End)))
                direct = TryVerb(legacy.End + 1, Word(legacy.End), out directVerb);
            if (direct)
            {
                var owners = new List<Span>(legacy.Owners);
                var end = directVerb.End;
                if (Word(directVerb.Predicate) is "bring" or "brings" or "brought")
                {
                    var relation = end;
                    while (relation < _tokens.Length && Word(relation) != "to") relation++;
                    if (relation == end || relation == _tokens.Length) return Result(Status.InvalidOrAmbiguous);
                    owners.Add(Slice(end, relation)); end = relation;
                }
                if (!Tail(end, owners)) return Result(Status.InvalidOrAmbiguous);
                return Result(Status.Parsed, start, legacy.End, directVerb.Auxiliary, predicate: directVerb.Predicate, predicateEnd: directVerb.End, owners: owners);
            }
            for (var at = legacy.End + 1; at < _tokens.Length; at++)
            {
                if (_relative.Contains(at) || !TryVerb(at, "", out var verb)) continue;
                var owners = new List<Span>(legacy.Owners);
                if (!Tail(verb.End, owners)) return Result(Status.InvalidOrAmbiguous);
                return Result(Status.Parsed, start, legacy.End, verb.Auxiliary, Slice(legacy.End, at), verb.Predicate, verb.End, owners);
            }
            var nominalOwners = new List<Span>(legacy.Owners);
            return Tail(legacy.End, nominalOwners)
                ? Result(Status.Parsed, start, legacy.End, subject: OwnerSubject(nominalOwners), owners: nominalOwners)
                : Result(Status.InvalidOrAmbiguous);
        }
        private static Span OwnerSubject(IReadOnlyList<Span> owners)
            => owners.Select(owner => owner.Text).Distinct(StringComparer.Ordinal).Count() == 1 ? owners[0] : default;
        private bool HasOwnership(int start, FactRead facts) => facts.Owners.Count > 0
            && (_tokens.Skip(start).Take(facts.End - start).Any(token => token.Value == "possessive")
                || facts.Owners.All(owner => Regex.IsMatch(owner.Text, @"^(?:our|their|my|your|selected)\b", RegexOptions.CultureInvariant)));
        private bool LeadingOwner(int head, int introducer)
        {
            var start = introducer + 1;
            if (head <= start) return false;
            if (ScopePrefix(start, head)) return true;
            var generic = ReadFacts(start, head);
            if (generic is not null && generic.End == head && generic.Owners.Count == 0) return true;
            var end = start;
            while (end < head && Word(end) is not ("in" or "on" or "at" or "as" or "by")) end++;
            if (end == start || end < head && !ScopePrefix(end, head)) return false;
            if (end == start + 1 && Word(start) is "me" or "us" or "you") return true;
            if (OwnedNominal(start, end, false, out var owners)) _prefixOwners.AddRange(owners);
            else _prefixOwners.Add(Slice(start, end));
            return true;
        }
        private bool NonOwnerPrefix(int start, int end, bool requestBoundary)
        {
            if (ScopePrefix(start, end)) return true;
            if (end == start + 1 && Word(start) is "me" or "us" or "you") return true;
            if (requestBoundary && Slice(start, end).Text == "a moment") return true;
            var generic = ReadFacts(start, end);
            return generic is not null && generic.End == end && generic.Owners.Count == 0;
        }
        private bool ScopePrefix(int start, int limit)
        {
            var at = start;
            for (var count = 0; count < 4 && at < limit; count++)
            {
                if (Word(at) is "in" or "on" or "at" or "by") at++;
                else if (Word(at) == "as" && Word(at + 1) == "of") at += 2;
                var remaining = Slice(at, limit).Text;
                var scope = _scope.Match(remaining);
                if (!scope.Success) return false;
                at += scope.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return at == limit;
        }
        private bool OwnedNominal(int start, int end, bool place, out List<Span> owners)
        {
            owners = [];
            var whole = Slice(start, end);
            var reader = new Reader(whole.Text, _qualifierPattern, "(?!)", place);
            // A real genitive marker delimits the whole owner, including a
            // proper name ending in TOWN, before its factual place head.
            var possession = Array.FindIndex(reader._tokens, token => token.Value == "possessive");
            if (place && possession > 0)
            {
                var fields = reader.ReadFacts(possession + 1, reader._tokens.Length);
                if (fields is null || fields.End != reader._tokens.Length) return false;
                var owner = reader.Slice(0, possession);
                owners.Add(new(whole.Start + owner.Start, owner.Length, owner.Text));
                owners.AddRange(fields.Owners.Select(field => new Span(whole.Start + field.Start, field.Length, field.Text)));
                return true;
            }
            var facts = reader.ReadFacts(0, reader._tokens.Length);
            if (facts is null || facts.End != reader._tokens.Length || !reader.HasOwnership(0, facts)) return false;
            // A personal wrapper cannot turn an unlisted proper name ending in
            // TOWN into a shorter trusted alias. Retain that entire identity.
            if (place && !facts.Owners.All(owner => Regex.IsMatch(owner.Text, @"^(?:our|their|my|your|selected)$", RegexOptions.CultureInvariant))) return false;
            owners.AddRange(facts.Owners.Select(owner => new Span(whole.Start + owner.Start, owner.Length, owner.Text)));
            return true;
        }
        private Analysis PlaceAssociation(int start, int auxiliary, int predicate)
        {
            var ownerStart = predicate + 2;
            for (var end = ownerStart + 1; end <= _tokens.Length; end++)
            {
                if (end < _tokens.Length && Word(end) is not ("here" or "in" or "on" or "at" or "for" or "by" or "as")) continue;
                var owners = new List<Span>();
                if (!Tail(end, owners)) continue;
                var subject = Slice(ownerStart, end);
                if (OwnedNominal(ownerStart, end, true, out var owned)) { owners.AddRange(owned); subject = OwnerSubject(owned); }
                return Result(Status.Parsed, start, auxiliary, auxiliary, subject, predicate, ownerStart, owners);
            }
            return Result(Status.InvalidOrAmbiguous, start, auxiliary, auxiliary, Slice(ownerStart, _tokens.Length), predicate, ownerStart);
        }
        internal Analysis ReadNominal(bool complete)
        {
            var facts = ReadFacts(0, _tokens.Length);
            if (facts is null) return Result(Status.NotApplicable);
            if (!complete && !_tokens.Take(facts.End).Any(token => token.Value is "ground" or "stadium" or "town" or "home")) return Result(Status.NotApplicable);
            if (!complete)
            {
                var place = Array.FindIndex(_tokens, token => token.Value is "ground" or "stadium" or "town" or "home");
                if (_tokens.Take(place).Any(token => token.Value is "club" or "team")
                    && !_tokens.Take(place).Any(token => token.Value == "possessive")) return Result(Status.NotApplicable);
            }
            if (complete && facts.End != _tokens.Length) return Result(Status.InvalidOrAmbiguous);
            if (!complete)
                for (var at = facts.End; at < Math.Min(_tokens.Length, facts.End + 48); at++)
                {
                    if (NominalBoundary(at)) break;
                    if (Word(at) is not ("and" or "or")) continue;
                    var next = ReadFacts(at + 1, _tokens.Length);
                    if (next is not null && _tokens.Skip(at + 1).Take(next.End - at - 1).Any(token => token.Value is "ground" or "stadium" or "town"))
                        return Result(Status.InvalidOrAmbiguous);
                    break;
                }
            return Result(Status.Parsed, 0, facts.End, owners: facts.Owners);
        }
        private bool NominalBoundary(int at) => Auxiliaries.Contains(Word(at)) || TryVerb(at, "", out _)
            || Word(at) is "here" or "in" or "on" or "at" or "for" or "to" or "from" or "by" or "with" or "about" or "as"
                or "called" or "named" or "recorded" or "listed" or "shown" or "located" or "based";

        private FactRead? ReadFacts(int start, int limit)
        {
            var first = ReadFacet(start, limit);
            if (first is null) return null;
            var owners = new List<Span>(first.Owners); var end = first.End; var facets = 1;
            while (facets < 8 && end < limit)
            {
                // Normalization removes list commas. Adjacent count heads
                // still form a bounded enumeration; identities never do.
                var coordinated = Word(end) is "and" or "or";
                if (!coordinated && !(Counts.Contains(Word(end - 1)) && Counts.Contains(Word(end)))) break;
                var next = ReadFacet(end + (coordinated ? 1 : 0), limit);
                if (next is null) break;
                owners.AddRange(next.Owners); end = next.End; facets++;
            }
            return new(end, owners);
        }
        private FactRead? ReadFacet(int start, int limit)
        {
            for (var head = start; head < limit; head++)
            {
                var length = CoreLength(head, limit);
                if (length > 0)
                {
                    var qualifiers = head; var count = 0;
                    while (qualifiers > start && count < 4 && _qualifier.IsMatch(Word(qualifiers - 1)) && !_relative.Contains(qualifiers - 1))
                    { qualifiers--; count++; }
                    var ownerEnd = qualifiers;
                    if (Word(ownerEnd - 1) == "possessive") ownerEnd--;
                    var owner = ownerEnd == start + 1 && Word(start) is "the" or "a" or "an" ? default : Slice(start, ownerEnd);
                    return new(head + length, owner.Length == 0 ? [] : [owner]);
                }
                if (!_relative.Contains(head) && (Auxiliaries.Contains(Word(head)) || Bases.Contains(Word(head)) || Past.Contains(Word(head)))) return null;
                if (Word(head) is "here" or "in" or "on" or "at" or "for" or "by" or "with" or "about" or "to" or "from" or "of") return null;
            }
            return null;
        }
        private int CoreLength(int start, int limit)
        {
            bool At(int offset, string word) => start + offset < limit && Word(start + offset) == word;
            var word = Word(start); var length = 0;
            if (_placeNominal && word is "ground" or "stadium" or "town") length = At(1, "name") ? 2 : 1;
            if (_placeNominal && word == "home" && Word(start + 1) is not ("ground" or "stadium" or "town")) length = At(1, "name") ? 2 : 1;
            if (Quantities.Contains(word))
            {
                var count = At(1, "of") ? 2 : 1;
                if (start + count < limit && Counts.Contains(Word(start + count))) length = count + 1;
            }
            if (At(0, "goal") && At(1, "difference")) length = 2;
            if (word is "win" or "draw" or "loss" or "wins" or "draws" or "losses" or "played")
            {
                var at = start;
                while (at < limit && at < start + 12 && Word(at) is "win" or "draw" or "loss" or "wins" or "draws" or "losses" or "played" or "and" or "or") at++;
                if (at < limit && Word(at) == "record") length = at - start + 1;
            }
            if (length == 0 && (Counts.Contains(word) || Facts.Contains(word))) length = 1;
            if (length == 0 && SingularCounts.Contains(word) && (Quantities.Contains(Word(start + 1)) || SummaryHeads.Contains(Word(start + 1)))) length = 1;
            if (length == 0) return 0;
            if (word == "goals" && start + length < limit && Word(start + length) is "for" or "against") length++;
            if (word is "matches" or "games" && start + length < limit && Word(start + length) is "played" or "counted" or "completed") length++;
            if (start + length < limit && Quantities.Contains(Word(start + length))) length++;
            // Counts and context facts can modify a closed summary head. This
            // consumes RESULTS RECORD as one fact without freeing owner tokens.
            if (start + length < limit && SummaryHeads.Contains(Word(start + length))) length++;
            return length;
        }
        private bool TryVerb(int start, string auxiliary, out VerbRead result)
        {
            result = default; var at = start;
            while (Modifier(Word(at))) at++;
            if (at >= _tokens.Length || _relative.Contains(at)) return false;
            var word = Word(at); var ownAuxiliary = -1;
            if (auxiliary.Length == 0 && word is "has" or "have" or "had" && Past.Contains(Word(at + 1)))
            { ownAuxiliary = at; auxiliary = word; at++; word = Word(at); }
            var valid = auxiliary is "has" or "have" or "had" ? Past.Contains(word) || word == "been"
                : Copulas.Contains(auxiliary) ? Progressives.Contains(word) || word is "in" or "at" or "on" or "ranked" or "placed" or "attached" or "based" or "located"
                : auxiliary.Length > 0 ? Bases.Contains(word)
                : Bases.Contains(word) || Past.Contains(word) || word == "has" || word.EndsWith('s') && Bases.Contains(word[..^1]);
            if (!valid) return false;
            var predicate = at; at++;
            if (word == "been")
            {
                while (Modifier(Word(at))) at++;
                if (!Progressives.Contains(Word(at)) && Word(at) is not ("ranked" or "placed")) return false;
                at++;
            }
            if (word is "look" or "looks" or "looked" && Word(at) == "like") at++;
            if (word == "put" && Word(at) == "together" || word is "start" or "started" && Word(at) == "with"
                || word is "based" or "located" && Word(at) == "in") at++;
            result = new(at, ownAuxiliary, predicate); return true;
        }
        private static bool Modifier(string word) => word.Length > 2 && word.EndsWith("ly", StringComparison.Ordinal)
            || word is "already" or "still" or "yet" or "now" or "ever" or "just";

        private bool Tail(int start, List<Span> owners)
        {
            var at = start;
            while (at < _tokens.Length)
            {
                if (Word(at) is "here" or "already" or "still" or "yet" or "now") { at++; continue; }
                if (Word(at) == "so" && Word(at + 1) == "far" || Word(at) == "to" && Word(at + 1) == "date") { at += 2; continue; }
                if (Word(at) == "including" || Word(at) == "rather" && Word(at + 1) == "than")
                { at += Word(at) == "rather" ? 2 : 1; if (Word(at) == "just") at++; continue; }
                var directScope = _scope.Match(Slice(at, _tokens.Length).Text);
                if (directScope.Success) { ScopeOwner(at, directScope.Value, owners); at += directScope.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; continue; }
                var complement = at;
                if (Word(at) is "for" or "to" or "from" or "in" or "on" or "at" or "by" or "through" or "before" or "after" or "about" or "of") at++;
                else if (Word(at) == "as" && Word(at + 1) == "of") at += 2;
                if (at != complement)
                {
                    if (Word(complement) == "for" && Word(at) is "me" or "us" or "you") { at++; continue; }
                    var remaining = Slice(at, _tokens.Length).Text;
                    var scope = _scope.Match(remaining);
                    if (scope.Success) { ScopeOwner(at, scope.Value, owners); at += scope.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length; continue; }
                }
                var facts = ReadFacts(at, _tokens.Length);
                if (facts is not null && facts.End > at)
                { owners.AddRange(facts.Owners); at = facts.End; continue; }
                return false;
            }
            return true;
        }
        private void ScopeOwner(int start, string scope, List<Span> owners)
        {
            if (scope.EndsWith(" row", StringComparison.Ordinal) && Word(start) is "our" or "their") owners.Add(Slice(start, start + 1));
        }
    }
}
