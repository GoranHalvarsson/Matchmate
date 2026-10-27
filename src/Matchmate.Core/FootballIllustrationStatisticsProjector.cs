using Matchmate.Contracts;

namespace Matchmate.Core;

// Input is the ledger's latest active, application/knowledge/release/completion-eligible selection.
public static class FootballIllustrationStatisticsProjector
{
    public static FootballIllustrationStatistics? Project(FootballDataset dataset, IEnumerable<FootballRecord> records)
    {
        if (!dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
            || dataset.Fixture is not { } fixture || dataset.Schedule is not { } schedule || dataset.People.Length == 0) return null;
        var teams = new[] { fixture.Home.Id, fixture.Away.Id };
        var people = dataset.People.ToDictionary(p => p.PersonId, p => p.TeamId, StringComparer.Ordinal);
        var actions = records.Where(r => r.Payload is FootballActionPayload).ToArray();
        bool Member(string? person, string team) => person is null || people.TryGetValue(person, out var actual) && actual == team;
        foreach (var record in actions)
        {
            var action = (FootballActionPayload)record.Payload;
            var part = schedule.Segments.SingleOrDefault(s => s.PeriodId == record.FootballTime?.PeriodId && FootballPresentationPolicy.IsPlay(s));
            if (!teams.Contains(action.TeamId) || !Member(action.PersonId, action.TeamId)
                || action.RecipientTeamId is not null && (!teams.Contains(action.RecipientTeamId) || !Member(action.RecipientId, action.RecipientTeamId))
                || part is null || record.FootballTime?.StartMs < part.LocalStartMs
                || record.FootballTime?.EndMs > part.LocalStartMs + part.CursorEndMs - part.CursorStartMs) return null;
        }
        // Concurrent team-control spells would make the denominator ambiguous. Never publish such totals.
        foreach (var period in actions.Where(r => ((FootballActionPayload)r.Payload).ActionKind == "possession").GroupBy(r => r.FootballTime!.PeriodId))
        {
            long previousEnd = -1;
            foreach (var spell in period.OrderBy(r => r.FootballTime!.StartMs))
            {
                if (spell.FootballTime!.StartMs < previousEnd) return null;
                previousEnd = spell.FootballTime.EndMs!.Value;
            }
        }
        long Control(string team) => actions.Where(r => r.Payload is FootballActionPayload { ActionKind: "possession" } p && p.TeamId == team)
            .Sum(r => r.FootballTime!.EndMs!.Value - r.FootballTime.StartMs!.Value);
        var total = teams.Sum(Control);
        return new(teams.Select(team =>
        {
            var own = actions.Select(r => (FootballActionPayload)r.Payload).Where(p => p.TeamId == team).ToArray();
            var control = Control(team);
            return new FootballTeamIllustrationStatistics(team, own.Count(p => p.ActionKind == "pass"), own.Count(p => p.ActionKind == "pass" && p.Outcome == "completed"),
                own.Count(p => p.ActionKind == "shot"), control, total == 0 ? null : 100m * control / total);
        }).ToArray(), total);
    }
}
