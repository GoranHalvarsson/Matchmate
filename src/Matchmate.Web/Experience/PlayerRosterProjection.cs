using Matchmate.Contracts;

namespace Matchmate.Web.Experience;

public sealed record PlayerRosterRow(SquadPerson Person, int RosterOrder, bool OnPitch,
    FootballObservation? Observation, long? CumulativeCentimetres, MatchRoleEvidence? Role);

// Pure accepted-view projection: eligibility never depends on future substitutions or the final participant list.
public sealed class PlayerRosterProjection
{
    public IReadOnlyList<PlayerRosterRow> Rows { get; }
    public bool HasGeneratedDistance { get; }
    public int OnPitchCount { get; }
    public PlayerRosterProjection(FootballView view)
    {
        var roles = MatchRoleCatalog.Embedded.Project(view);
        HasGeneratedDistance = view.GeneratedDistance is { } distance && distance.CursorMs == view.Context.CursorMs;
        var active = view.Frame?.ActiveSubjectIds.ToHashSet(StringComparer.Ordinal) ?? [];
        var observations = view.Frame?.Players.ToDictionary(p => p.SubjectId, StringComparer.Ordinal) ?? [];
        var metres = HasGeneratedDistance ? view.GeneratedDistance!.Players.ToDictionary(p => p.PersonId, p => p.CumulativeCentimetres, StringComparer.Ordinal) : [];
        Rows = Array.AsReadOnly(view.Dataset.People.Select((person, index) => new PlayerRosterRow(person, index,
            active.Contains(person.PersonId), observations.GetValueOrDefault(person.PersonId), metres.GetValueOrDefault(person.PersonId),
            roles.GetValueOrDefault(person.PersonId))).ToArray());
        OnPitchCount = Rows.Count(r => r.OnPitch);
    }
    public IReadOnlyList<PlayerRosterRow> Team(string teamId, bool allRoster, bool ascending) => Rows
        .Where(r => r.Person.TeamId == teamId && (allRoster || r.OnPitch || HasGeneratedDistance && r.CumulativeCentimetres is not null))
        .OrderBy(r => HasGeneratedDistance && r.CumulativeCentimetres is null ? 1 : 0)
        .ThenBy(r => HasGeneratedDistance ? (ascending ? r.CumulativeCentimetres : -r.CumulativeCentimetres) : 0)
        .ThenBy(r => r.RosterOrder).ThenBy(r => r.Person.PersonId, StringComparer.Ordinal).ToArray();
}
