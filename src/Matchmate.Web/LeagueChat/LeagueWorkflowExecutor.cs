using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;

namespace Matchmate.Web.LeagueChat;

public sealed class LeagueWorkflowExecutor(Func<string, int?, string?, CancellationToken, Task<LeagueSeasonSnapshot>> read)
{
    private static readonly Regex Forbidden = new(@"\b(?:invent|fabricate|make up|secrets?|password|api key|private|other user|ignore (?:the |all )?(?:rules|instructions)|xg|possession percentage|pass counts?|speed|lineups?|scorers?|player ratings?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool ValidContext(LeagueChatContext? c) => c is not null && c.Version is { Length: > 0 and <= 100 }
        && c.SeasonKey is not null && Regex.IsMatch(c.SeasonKey, "^[0-9]{4}-[0-9]{2}$", RegexOptions.CultureInvariant) && c.DateIndex is >= 0 and <= 380
        && (c.DateIndex == 0 ? c.Date is null : DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        && (c.SelectedClubId is null ? c.SelectedClubName is null : LeagueSeasonModel.ValidId(c.SelectedClubId) && c.SelectedClubName is { Length: > 0 and <= 150 });

    public static void Validate(LeagueSeasonSnapshot s, LeagueChatContext context, int index, string? club)
    {
        if (s is null || s.Version != context.Version || s.Season is null || s.Season.Version != context.Version || s.Season.SeasonKey != context.SeasonKey
            || s.DateIndex != index || s.Season.PlayedDates is null || index < 0 || index > s.Season.PlayedDates.Length
            || s.Date != (index == 0 ? null : s.Season.PlayedDates[index - 1]) || index == context.DateIndex && s.Date != context.Date
            || s.Clubs is not { Length: 20 } || s.Rows is not { Length: 20 } || s.Results is null || s.Results.Length > 20 || s.Completed is < 0 or > 380
            || s.Season.ScheduledMatchCount != 380 || s.Season.Status is not ("complete" or "ongoing")
            || s.SelectedClub?.Club.Id != club || s.Ranked != (index > 0)) throw new InvalidDataException("The league response does not match its bound context.");
        var ids = s.Clubs.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != 20 || s.Clubs.Any(c => !LeagueSeasonModel.ValidId(c.Id) || string.IsNullOrWhiteSpace(c.Name))
            || s.Rows.Select(r => r.Id).Distinct().Count() != 20 || s.Rows.Any(r => !ids.Contains(r.Id) || r.Played is < 0 or > 38 || r.Won < 0 || r.Drawn < 0 || r.Lost < 0
                || r.Played != r.Won + r.Drawn + r.Lost || r.Points != r.Won * 3 + r.Drawn || r.GoalsFor < 0 || r.GoalsAgainst < 0 || r.GoalDifference != r.GoalsFor - r.GoalsAgainst)
            || s.Rows.Sum(r => r.Played) != s.Completed * 2 || index == 0 && (s.Completed != 0 || s.Results.Length != 0)
            || s.Results.Any(m => !ValidMatch(m, ids, s.Date) || m.Date != s.Date)) throw new InvalidDataException("Invalid bounded league facts.");
        if (s.SelectedClub is { } selected && (selected.Club != s.Clubs.Single(c => c.Id == club) || selected.Row != s.Rows.Single(r => r.Id == club) || selected.Date != s.Date
            || club == context.SelectedClubId && selected.Club.Name != context.SelectedClubName
            || selected.Recent is null || selected.Recent.Length > 3 || selected.Today is null
            || selected.Recent.Concat(selected.Today).Any(m => !ValidMatch(m, ids, s.Date) || m.Home != club && m.Away != club)
            || selected.Latest is not null && (!ValidMatch(selected.Latest, ids, s.Date) || selected.Latest.Home != club && selected.Latest.Away != club))) throw new InvalidDataException("Invalid selected club facts.");
    }
    private static bool ValidMatch(LeagueMatch m, HashSet<string> ids, string? cutoff) => m is not null && cutoff is not null && ids.Contains(m.Home) && ids.Contains(m.Away)
        && m.Home != m.Away && m.MatchKey == m.Home + ":" + m.Away && m.HomeGoals >= 0 && m.AwayGoals >= 0
        && DateOnly.TryParseExact(m.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && string.CompareOrdinal(m.Date, cutoff) <= 0;
    public Task<LeagueWorkflowResult> ExecuteAsync(string workflow, LeagueChatContext context, string question, string? namedReference, CancellationToken token, string? originalQuestion = null)
        => ExecuteCoreAsync(workflow, context, question, namedReference, token, originalQuestion, null);
    public Task<LeagueWorkflowResult> ExecuteContinuationAsync(LeagueChatContext context, string question, LeagueAcceptedTopic topic, CancellationToken token)
    {
        if (!LeagueWorkflowRegistry.ValidTopic(topic)) throw new InvalidDataException("Invalid accepted league topic.");
        return ExecuteCoreAsync(topic.WorkflowId, context, question, null, token, null, topic);
    }
    private async Task<LeagueWorkflowResult> ExecuteCoreAsync(string workflow, LeagueChatContext context, string question, string? namedReference, CancellationToken token, string? originalQuestion, LeagueAcceptedTopic? accepted)
    {
        LeagueWorkflowResult Feedback(MatchChatOutcome outcome, string message) => new(outcome, message, []);
        if (!ValidContext(context) || !LeagueWorkflowRegistry.Criteria.ContainsKey(workflow)) throw new InvalidDataException("Invalid league workflow/context.");
        if (workflow == "unsupported")
            return Feedback(MatchChatOutcome.Unsupported, "I can explain the available table, results and club ground reference. These records do not supply private data, player/tactical statistics or invented football facts.");
        if (workflow == "clarify")
            return Feedback(MatchChatOutcome.Clarify, "Please ask about the selected table, one club, or what changed on the selected played date.");
        // Classification never grants facts outside the trusted page scope, even if the router errs.
        originalQuestion = originalQuestion is not null && LeagueQuestionProjection.BriefIdentityReply(question) ? originalQuestion : null;
        var scopeQuestion = originalQuestion ?? question;
        var time = LeagueTimeScope.Resolve(context.SeasonKey, context.Date, scopeQuestion);
        if (workflow == "live_unavailable" || workflow != "coverage_limits" && time == LeagueTimeResolution.RealWorldNow)
            return Feedback(MatchChatOutcome.Unavailable, "Live football data is not available here. This page is a pinned season snapshot, not today's live table.");
        if (workflow == "other_season" || workflow != "coverage_limits" && time is LeagueTimeResolution.DifferentTime or LeagueTimeResolution.Unresolved)
            return Feedback(MatchChatOutcome.Clarify, "Please select the season and played date you want, then ask again. I will keep this question within the selected snapshot.");
        if (workflow == "continue_topic") return Feedback(MatchChatOutcome.Clarify, "Choose a table, club or ground question first, then I can continue it.");
        if (Forbidden.IsMatch(question) && workflow != "coverage_limits") return Feedback(MatchChatOutcome.Unsupported, "I can explain the available table, results and club ground reference. These records do not supply private data, player/tactical statistics or invented football facts.");
        if (Regex.IsMatch(question, @"\b(?:compare|versus|vs)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return Feedback(MatchChatOutcome.Clarify, "Please ask about the selected table, one club, or what changed on the selected played date.");
        var questionNeedsClub = LeagueClubResolver.RequiresSubject(scopeQuestion);
        var wholeLeague = !questionNeedsClub && LeagueClubResolver.WholeLeague(scopeQuestion);
        var trustedSubject = accepted is not null ? accepted.SubjectId : context.SelectedClubId ?? namedReference;
        if (workflow != "coverage_limits" && questionNeedsClub && trustedSubject is null && originalQuestion is null && !workflow.StartsWith("named_", StringComparison.Ordinal))
            return Feedback(MatchChatOutcome.Clarify, "Which club do you mean? Select it in the table or use its name so I can explain its points and results.");
        var current = await read(context.SeasonKey, context.DateIndex, context.SelectedClubId, token).WaitAsync(token); Validate(current, context, context.DateIndex, context.SelectedClubId);
        var projection = workflow switch { "named_club_context" => "club_context", "named_club_standing" => "club_standing", "named_club_form" => "club_form", "named_ground_context" => "ground_context", "named_date_changes" => "date_changes", _ => workflow };
        var needsClub = projection is "club_context" or "club_standing" or "club_form" or "ground_context" || workflow == "named_date_changes";
        var binding = workflow is "named_club_context" or "named_club_standing" or "named_club_form" or "named_ground_context" or "named_date_changes" ? LeagueClubBinding.Named : LeagueClubBinding.Context;
        var resolvesSubject = workflow != "coverage_limits";
        var resolution = !resolvesSubject ? new LeagueClubResolution(null, null, false) : accepted is null ? LeagueClubResolver.Resolve(current.Clubs, question, context.SelectedClubId, namedReference, binding, needsClub || questionNeedsClub, wholeLeague)
            : LeagueClubResolver.Resolve(current.Clubs, question, null, null, LeagueClubBinding.Context, false, wholeLeague);
        if (accepted is not null)
        {
            if (resolution.Explicit && (resolution.Clarification is not null || resolution.ClubId != accepted.SubjectId))
                return Feedback(MatchChatOutcome.Clarify, "That names a different club. Ask a new question for that club so I can use its facts.");
            if (accepted.SubjectId is not null && !current.Clubs.Any(c => c.Id == accepted.SubjectId)) throw new InvalidDataException("Accepted subject is absent from this season.");
            resolution = new(accepted.SubjectId, null, false);
        }
        if (resolution.Clarification is not null) return Feedback(MatchChatOutcome.Clarify, resolution.Clarification);
        if (projection is "league_context" or "league_table" && !wholeLeague && (questionNeedsClub || resolution.Explicit))
            return Feedback(MatchChatOutcome.Clarify, "That asks about one club. Please ask for its club picture or choose a whole-league table question.");
        if (wholeLeague && needsClub)
            return Feedback(MatchChatOutcome.Clarify, "That asks about the whole league. Please ask for the league table or all-club changes.");
        if (originalQuestion is not null && needsClub && (binding != LeagueClubBinding.Named || !resolution.Explicit)) return Feedback(MatchChatOutcome.Clarify, "Please name the club you mean so I can answer the earlier question.");
        var subject = needsClub || projection == "date_changes" && !wholeLeague ? resolution.ClubId : null;
        if (subject is not null && subject != context.SelectedClubId)
        {
            current = await read(context.SeasonKey, context.DateIndex, subject, token).WaitAsync(token); Validate(current, context, context.DateIndex, subject);
        }
        var evidence = new List<MatchChatEvidence>();
        void Item(string id, object facts, string answer)
        {
            var text = JsonSerializer.Serialize(new { scope = "selected_season_end_of_played_date_only", season = context.SeasonKey, date = context.Date, facts, sourceAnswer = answer });
            if (text.Length > 12000) throw new InvalidDataException("League evidence budget exceeded.");
            evidence.Add(new(id, id, text, MatchReplyValidation.Numbers(text).Distinct(StringComparer.Ordinal).ToArray()));
        }
        string Name(string id) => current.Clubs.Single(c => c.Id == id).Name;
        object Match(LeagueMatch m) => new { m.Date, home = Name(m.Home), away = Name(m.Away), m.HomeGoals, m.AwayGoals };
        string Count(int amount, string singular, string plural) => $"{amount} {(amount == 1 ? singular : plural)}";
        if (projection is "league_table" or "league_context")
        {
            var first = current.Rows[0];
            Item("table_snapshot", new { current.Completed, rows = current.Rows.Select(r => new { club = Name(r.Id), r.Rank, r.Shared, r.Played, r.Won, r.Drawn, r.Lost, r.Points, r.GoalsFor, r.GoalsAgainst, r.GoalDifference }) },
                current.Ranked ? $"{Name(first.Id)} {(first.Shared ? "share the lead" : "lead")} on {Count(first.Points, "point", "points")} from {Count(first.Played, "match", "matches")}. There {(current.Completed == 1 ? "has" : "have")} been {Count(current.Completed, "completed league match", "completed league matches")} so far." : "No results have entered the table yet. Every club is on zero points, with no ranking.");
        }
        if (projection is "club_context" or "club_standing" or "club_form")
        {
            var facts = current.SelectedClub ?? throw new InvalidDataException("Resolved club was not returned."); var r = facts.Row;
            Item("club_standing", new { club = facts.Club.Name, r.Rank, r.Shared, r.Points, r.Played, r.Won, r.Drawn, r.Lost, r.GoalsFor, r.GoalsAgainst, r.GoalDifference },
                $"{facts.Club.Name} {(r.Rank is null ? "have no table position yet" : r.Shared ? "share position " + r.Rank : "are in position " + r.Rank)}, with {Count(r.Points, "point", "points")} from {Count(r.Played, "match", "matches")}: {Count(r.Won, "win", "wins")}, {Count(r.Drawn, "draw", "draws")} and {Count(r.Lost, "loss", "losses")}.");
            if (projection is "club_context" or "club_form") Item("club_results", new { club = facts.Club.Name, recent = facts.Recent.Select(Match), latest = facts.Latest is null ? null : Match(facts.Latest) },
                facts.Recent.Length == 0 ? $"No completed result is recorded for {facts.Club.Name} by this point." : $"The latest recorded results for {facts.Club.Name}: " + string.Join(" ", facts.Recent.Select(m => $"{m.Date}: {Name(m.Home)} {m.HomeGoals}–{m.AwayGoals} {Name(m.Away)}.")));
        }
        if (projection == "ground_context")
        {
            var c = current.SelectedClub?.Club ?? throw new InvalidDataException("Resolved club was not returned.");
            Item("ground_reference", new { club = c.Name, c.Ground, c.Town, limitation = "Club-season reference context, not a verified fixture venue, surveyed map point or stadium history." },
                $"{c.Name}'s ground is listed as {c.Ground} in {c.Town} for {current.Season.Label}. That lists the ground for this season; it doesn't confirm a particular match venue or tell us the ground's history.");
        }
        if (projection is "date_changes" or "club_context" or "league_context")
        {
            var previous = context.DateIndex == 0 ? null : await read(context.SeasonKey, context.DateIndex - 1, subject, token).WaitAsync(token);
            if (previous is not null) Validate(previous, context, context.DateIndex - 1, subject);
            var rows = current.Rows.Where(r => subject is null || r.Id == subject).Select(r =>
            {
                var before = previous?.Rows.Single(p => p.Id == r.Id);
                return new { club = Name(r.Id), playedOnDate = current.Results.Any(m => m.Home == r.Id || m.Away == r.Id), played = r.Played,
                    points = r.Points, previousPoints = before?.Points ?? 0, pointsChange = r.Points - (before?.Points ?? 0), position = r.Rank, shared = r.Shared,
                    previousPosition = before?.Rank, previousShared = before?.Shared,
                    positionChange = before?.Rank is { } rank && r.Rank is { } now ? (int?)(rank - now) : null };
            }).ToArray();
            string Position(int? rank, bool shared) => rank is null ? "unranked" : (shared ? "shared position " : "position ") + rank;
            var changes = rows.Select(r =>
            {
                var points = r.pointsChange == 0 ? $"Points stayed at {r.points} from {Count(r.played, "match", "matches")}."
                    : $"Added {Count(r.pointsChange, "point", "points")} to reach {r.points} from {Count(r.played, "match", "matches")}.";
                var movement = r.previousPosition is null ? $"Now {Position(r.position, r.shared)}; there was no previous ranking."
                    : r.position == r.previousPosition && r.shared == r.previousShared ? $"Stayed in {Position(r.position, r.shared)}."
                    : $"Moved from {Position(r.previousPosition, r.previousShared == true)} to {Position(r.position, r.shared)}.";
                return $"{r.club} {(r.playedOnDate ? "played" : "did not play")} on this date. {points} {movement}";
            });
            var dayResults = current.Results.Length == 0 ? "No results entered on this date."
                : "Results on this date: " + string.Join(" ", current.Results.Select(m => $"{Name(m.Home)} {m.HomeGoals}–{m.AwayGoals} {Name(m.Away)}."));
            Item("date_change", new { previousDate = previous?.Date, rows, resultsOnSelectedDate = current.Results.Select(Match), rule = "Every result on this date enters together. A club can move even without playing; movement is not attributed to one fixture." },
                context.DateIndex == 0 ? "No results have entered the table yet. All clubs are unranked with zero points."
                    : string.Join(" ", changes) + " " + dayResults + " All results from the day count together, so these figures don't single out one fixture as the cause of a move.");
        }
        Item("league_coverage", new { selectedDate = context.Date, completedResultsAtSelectedDate = current.Completed, scheduledFixtures = current.Season.ScheduledMatchCount,
            current.Season.Status, sourceSnapshotResultsThrough = current.Season.ResultsThrough, current.Season.RetrievedAt,
            sourceCoverageOnly = "The source snapshot cutoff describes coverage, not permission to narrate later results. No automatic updates, live feed, known-then state, player/events/tactical metrics or deductions.",
            sources = current.Season.Sources.Take(16).Select(s => new { s.SourceId, s.License }) },
            $"The community results record has {Count(current.Completed, "result", "results")} from {current.Season.ScheduledMatchCount} scheduled fixtures by this point. All results from a day count together. This isn't a live feed and it doesn't update automatically.");
        return new(MatchChatOutcome.Answered, "Answer prepared from the selected snapshot.", evidence.ToImmutableArray(), subject, subject is null ? null : Name(subject), projection, accepted is not null);
    }
    public static string SourceAnswer(ImmutableArray<MatchChatEvidence> evidence)
        => string.Join(" ", evidence.Where(e => evidence.Length == 1 || e.Id != "league_coverage").Select(e => { using var d = JsonDocument.Parse(e.Text); return d.RootElement.GetProperty("sourceAnswer").GetString(); }));
}
