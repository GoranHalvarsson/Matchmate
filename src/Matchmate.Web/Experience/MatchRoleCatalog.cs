using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Web.Experience;

public enum MatchRole { Goalkeeper, Defender, Midfielder, Midfield, Winger, Forward, Striker, RightBack }
public enum MatchRoleScope { StartingPosition, StartingLine, ListedCategory, MatchReportDescription }
public sealed record MatchRoleEvidence(MatchRole Role, MatchRoleScope Scope, string SourceUrl, string SourceTitleKey, string Locator);

// Display enrichment only. This catalog never enters imported records, timed incidents or chat context.
public sealed class MatchRoleCatalog
{
    public static MatchRoleCatalog Embedded { get; } = Load();
    private static readonly FrozenDictionary<string, MatchRoleEvidence> Empty = new Dictionary<string, MatchRoleEvidence>().ToFrozenDictionary();
    private readonly BoundRoles[] _packs;
    private sealed record BoundRoles(RoleBinding Binding, FrozenDictionary<string, MatchRoleEvidence> Roles);

    private MatchRoleCatalog(RolePack[] packs) => _packs = packs.Select(Validate).ToArray();
    private static BoundRoles Validate(RolePack pack)
    {
        var liverpool = pack.Binding.DatasetId == "brentford-liverpool-illustration";
        if (pack.Binding.DatasetId is not ("brentford-existing-illustration" or "brentford-liverpool-illustration")
            || pack.Version != 1 || pack.Roles.Length != (liverpool ? 5 : 24) || pack.Sources.Length != (liverpool ? 1 : 2)
            || liverpool && pack.Binding.DatasetSha256 != GeneratedDistanceProjection.LiverpoolDatasetSha256)
            throw new InvalidDataException("Invalid match role catalog.");
        var sources = pack.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var roles = new Dictionary<string, MatchRoleEvidence>(StringComparer.Ordinal);
        foreach (var row in pack.Roles)
        {
            var role = row.Role switch { "goalkeeper" => MatchRole.Goalkeeper, "defender" => MatchRole.Defender,
                "midfielder" => MatchRole.Midfielder, "midfield" => MatchRole.Midfield, "winger" => MatchRole.Winger,
                "forward" => MatchRole.Forward, "striker" => MatchRole.Striker, "right_back" => MatchRole.RightBack,
                _ => throw new InvalidDataException("Unsupported match role.") };
            var scope = row.Scope switch { "starting_position" => MatchRoleScope.StartingPosition, "starting_line" => MatchRoleScope.StartingLine,
                "listed_category" => MatchRoleScope.ListedCategory, "match_report_description" => MatchRoleScope.MatchReportDescription,
                _ => throw new InvalidDataException("Unsupported role evidence scope.") };
            if (liverpool && scope != MatchRoleScope.MatchReportDescription) throw new InvalidDataException("Unsupported Liverpool role scope.");
            var source = sources[row.SourceId];
            var title = source.Id switch { "pl-lineups" when !liverpool && source.Url == "https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/lineups" => "RoleSourcePremierLeague",
                "brentford-report" when !liverpool && source.Url == "https://www.brentfordfc.com/en/news/match-reports-brentford-3-chelsea-0-premier-league-jaidon-anthony-igor-thiago-fabio-carvalho" => "RoleSourceBrentford",
                "brentford-report" when liverpool && source.Url == "https://www.brentfordfc.com/en/news/match-reports-brentford-1-liverpool-2-premier-league-17-11-2026" => "RoleSourceBrentford",
                _ => throw new InvalidDataException("Unsupported match role source.") };
            if (string.IsNullOrWhiteSpace(row.Locator) || row.Locator.Length > 240) throw new InvalidDataException("Invalid role source locator.");
            roles.Add(row.PersonId, new(role, scope, source.Url, title, row.Locator));
        }
        return new(pack.Binding, roles.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, MatchRoleEvidence> Project(FootballView view)
    {
        foreach (var pack in _packs)
        {
            var roles = Project(view, pack);
            if (roles.Count > 0) return roles;
        }
        return Empty;
    }
    private static IReadOnlyDictionary<string, MatchRoleEvidence> Project(FootballView view, BoundRoles pack)
    {
        var d = view.Dataset; var q = view.Context; var b = pack.Binding;
        if (q.KnowledgeMode != "retrospective_replay" || q.KnowledgeAsOfUtc is not null || q.DatasetId != b.DatasetId
            || q.DatasetVersion != b.DatasetVersion || q.StreamId != b.StreamId || q.FixtureId != b.FixtureId
            || d.Manifest.DatasetId != b.DatasetId || d.Manifest.DatasetVersion != b.DatasetVersion || d.Manifest.FixtureId != b.FixtureId
            || d.Manifest.ManifestRevision != b.ManifestRevision || d.Stream.StreamId != b.StreamId || d.Stream.DatasetId != b.DatasetId
            || d.Stream.DatasetVersion != b.DatasetVersion || d.Stream.FixtureId != b.FixtureId || d.Stream.DeliveryMode != "replay"
            || !d.Closed || d.PendingRecords != 0 || d.People.Length != 40
            || !d.Manifest.Sources.Contains(new SourceReference(b.SourceId, b.SourceLocator, b.SourceSha256))) return Empty;
        if (b.DatasetSha256 is not null && (q.EffectiveDate is not null || q.ApplicationSequence is { } sequence && sequence != d.HeadSequence
            || GeneratedDistanceProjection.Fingerprint(d) != b.DatasetSha256)) return Empty;
        var rosterHash = PlayerEvidenceProjection.RosterFingerprint(d.People);
        return rosterHash == b.RosterSha256 && pack.Roles.Keys.All(id => d.People.Any(p => p.PersonId == id)) ? pack.Roles : Empty;
    }

    public static string RoleKey(MatchRole role) => role switch
    {
        MatchRole.Goalkeeper => "RoleGoalkeeper", MatchRole.Defender => "RoleDefender", MatchRole.Midfielder => "RoleMidfielder",
        MatchRole.Midfield => "RoleMidfield", MatchRole.Winger => "RoleWinger", MatchRole.Forward => "RoleForward",
        MatchRole.Striker => "RoleStriker", MatchRole.RightBack => "RoleRightBack", _ => "RoleUnverified"
    };
    public static string ScopeKey(MatchRoleScope scope) => scope switch
    {
        MatchRoleScope.StartingPosition => "RoleStartingPosition", MatchRoleScope.StartingLine => "RoleStartingLine",
        MatchRoleScope.ListedCategory => "RoleListedCategory", MatchRoleScope.MatchReportDescription => "RoleReportDescription",
        _ => "RoleUnverified"
    };

    private static MatchRoleCatalog Load()
    {
        var packs = new List<RolePack>();
        foreach (var name in new[] { "Matchmate.MatchRoles.v1.json", "Matchmate.MatchRoles.Liverpool.v1.json" })
        {
            using var stream = typeof(MatchRoleCatalog).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Missing embedded match role catalog.");
            if (stream.Length is < 1 or > 20000) throw new InvalidDataException("Invalid match role catalog size.");
            packs.Add(JsonSerializer.Deserialize<RolePack>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 }) ?? throw new InvalidDataException("Missing match roles."));
        }
        return new(packs.ToArray());
    }
    private sealed record RolePack(int Version, RoleBinding Binding, RoleSource[] Sources, RoleRow[] Roles);
    private sealed record RoleBinding(string DatasetId, string DatasetVersion, string StreamId, string FixtureId,
        int ManifestRevision, string RosterSha256, string SourceId, string SourceLocator, string SourceSha256, string? DatasetSha256 = null);
    private sealed record RoleSource(string Id, string Url, string ReviewedOn, string? PublishedOn);
    private sealed record RoleRow(string PersonId, string Role, string Scope, string SourceId, string Locator);
}
