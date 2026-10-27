using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

// Presentation facts computed from the fresh cursor-filtered view, never from the twenty-row evidence excerpt.
internal static class MatchReportedEventSummary
{
    internal const int RecentLimit = 3;

    internal static ReportedTeamCounts Counts(FootballView view, string teamId) => new(teamId,
        ExperiencePresentation.ReportedGoals(view, teamId),
        view.Events.Count(record => record.Payload is MatchEventPayload { EventKind: "yellow_card" } card && card.TeamId == teamId),
        view.Events.Count(record => record.Payload is MatchEventPayload { EventKind: "red_card" } card && card.TeamId == teamId),
        view.Events.Count(record => record.Payload is MatchEventPayload { EventKind: "substitution" } change && change.TeamId == teamId));

    internal static MatchChatEvidence Build(FootballView view)
    {
        var supplied = ExperiencePresentation.SupportsEventReports(view.Dataset);
        var fixture = view.Dataset.Fixture!;
        var teams = supplied ? new[] { fixture.Home, fixture.Away }.Select(team => (Team: team, Counts: Counts(view, team.Id))).Select(row => new
        {
            teamId = row.Team.Id, name = Short(row.Team.Name), goals = row.Counts.Goals,
            yellowCards = row.Counts.YellowCards, redCards = row.Counts.RedCards, substitutions = row.Counts.Substitutions
        }).ToArray() : [];
        var recent = supplied ? view.Events.Where(record => record.Payload is MatchEventPayload
            { EventKind: "goal" or "yellow_card" or "red_card" or "substitution" or "var_decision" }).TakeLast(RecentLimit).Select(record =>
        {
            var report = (MatchEventPayload)record.Payload;
            return new
            {
                recordId = record.RecordId, kind = report.EventKind, decision = report.Decision,
                team = new[] { fixture.Home, fixture.Away }.FirstOrDefault(team => team.Id == report.TeamId) is { } team ? Short(team.Name) : null,
                player = Name(report.PersonId), outgoing = Name(report.OutgoingPersonId), incoming = Name(report.IncomingPersonId),
                reportedClock = record.FootballTime?.ReportedClock
            };
        }).ToArray() : [];
        string? Name(string? id) => id is null ? null : view.Dataset.People.SingleOrDefault(person => person.PersonId == id) is { } person ? Short(person.Name) : null;
        var text = JsonSerializer.Serialize(new
        {
            scope = "reported_events_so_far", replayClock = ExperiencePresentation.Clock(view.Context.CursorMs), reportsSupplied = supplied,
            eligibleReportCount = supplied ? (int?)view.Events.Length : null, teams, recent,
            limitation = "Source-backed reported totals at this cursor, not a complete account of actual incidents or measured match statistics. Disallowed goals are excluded. A second-yellow dismissal counts as one red-card report without inferring another yellow-card report; VAR review records are not additional cards. Recent incidents are bounded to three; event provenance may be truncated separately."
        });
        if (text.Length > MatchChatLimits.EvidenceChars) throw new InvalidDataException("Reported summary evidence budget exceeded.");
        var numbers = teams.SelectMany(team => new[] { team.goals, team.yellowCards, team.redCards, team.substitutions })
            .Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Concat(supplied ? [view.Events.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [])
            .Concat(MatchReplyValidation.Numbers(ExperiencePresentation.Clock(view.Context.CursorMs)))
            .Concat(recent.SelectMany(incident => MatchReplyValidation.Numbers(incident.reportedClock ?? "")))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new("reported_comparison", "reported_comparison", text, numbers);
    }

    internal static string Format(JsonElement reports, MatchLanguage language, bool comparison)
    {
        string T(string key, params object?[] args) => ChatText.Get(language, "Source.Reported." + key, args);
        if (!reports.GetProperty("reportsSupplied").GetBoolean()) return T("Unavailable");
        var teams = reports.GetProperty("teams").EnumerateArray().ToArray();
        if (teams.Length != 2) throw new InvalidDataException("A report comparison requires both fixture teams.");
        var parts = new List<string>();
        if (comparison)
        {
            parts.Add(T("Intro", reports.GetProperty("replayClock").GetString()));
            parts.AddRange(teams.Select(team => T("Team", team.GetProperty("name").GetString(), team.GetProperty("goals").GetInt32(),
                team.GetProperty("yellowCards").GetInt32(), team.GetProperty("redCards").GetInt32(), team.GetProperty("substitutions").GetInt32())));
        }
        else parts.Add(T("ReportCount", reports.GetProperty("eligibleReportCount").GetInt32()));
        var recent = reports.GetProperty("recent").EnumerateArray().Select(incident =>
        {
            string? Text(string property) => incident.GetProperty(property).ValueKind == JsonValueKind.String ? incident.GetProperty(property).GetString() : null;
            var clock = Text("reportedClock") is { } minute ? minute + "′" : T("TimeUnknown");
            var prefix = T("IncidentPrefix", clock, Text("team") ?? T("TeamUnknown"));
            var player = Text("player") ?? T("PlayerUnknown");
            return prefix + (Text("kind") switch
            {
                "goal" when Text("decision") == "disallowed" => T("GoalDisallowed"),
                "goal" => T("Goal", player),
                "yellow_card" => T("Yellow", player),
                "red_card" => T("Red", player, ReplayText.Value(language, Text("decision"))),
                "substitution" => T("Substitution", Text("incoming") ?? T("PlayerUnknown"), Text("outgoing") ?? T("PlayerUnknown")),
                "var_decision" => T("Var"),
                _ => throw new InvalidDataException("Unsupported recent report kind.")
            });
        }).ToArray();
        if (recent.Length == 0) parts.Add(T("None"));
        else if (!comparison) parts.Add(T("Recent", string.Join(" ", recent)));
        parts.Add(T("Limits"));
        return string.Join(" ", parts);
    }

    private static string Short(string text) => text[..Math.Min(text.Length, 180)];
}
