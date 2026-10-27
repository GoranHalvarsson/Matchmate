using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;

namespace Matchmate.Core;

public interface ILeagueReferenceStore { Task<LeagueModel> ReadAsync(CancellationToken token); }
public sealed class LeagueReferenceValidationException(string message) : Exception(message);
public sealed class LeagueCursorException(string message) : Exception(message);
public static class LeagueReferenceCodec
{
    public const int MaxBytes = 2_000_000;
    public const string Version = "poc-league-v1", BlobPath = "football/reference/poc-league-v1.json";
    public const string CurrentVersion = "league-reference-v2", CurrentBlobPath = "football/reference/league-reference-v2.json";
    public const string Timing = "Retrospective corrected end of played date; not known-then, intraday or live.";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public static LeagueReferenceBundle Decode(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new LeagueReferenceValidationException("League bundle size is invalid.");
        try
        {
            var bundle = JsonSerializer.Deserialize<LeagueReferenceBundle>(bytes, Json) ?? throw new LeagueReferenceValidationException("League bundle is missing.");
            _ = new LeagueModel(bundle); return bundle;
        }
        catch (JsonException) { throw new LeagueReferenceValidationException("League bundle JSON is invalid."); }
    }
}
public sealed class LeagueModel
{
    private readonly Dictionary<string, LeagueSeasonModel> _seasons;
    public string Version { get; }
    public LeagueModel(LeagueReferenceBundle bundle)
    {
        var legacy = bundle is not null && bundle.SchemaVersion == 1 && bundle.Version == LeagueReferenceCodec.Version;
        var current = bundle is not null && bundle.SchemaVersion == 2 && bundle.Version == LeagueReferenceCodec.CurrentVersion;
        if (bundle is null || !(legacy || current) || bundle.Timing != LeagueReferenceCodec.Timing
            || string.IsNullOrWhiteSpace(bundle.Calculation) || bundle.Calculation.Length > 2000 || string.IsNullOrWhiteSpace(bundle.ResultsLicenseText)
            || bundle.ResultsLicenseText.Length > 30_000 || bundle.ResultsSourceMetadata.ValueKind != JsonValueKind.Object || bundle.CrestSourceMetadata.ValueKind != JsonValueKind.Object
            || bundle.Sources is not { Length: > 0 and <= 16 } || bundle.Sources.Any(s => s is null || !LeagueSeasonModel.ValidId(s.SourceId)
                || string.IsNullOrWhiteSpace(s.Path) || s.Path.Length > 500 || s.Sha256 is not { Length: 64 } || !s.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(s.License))
            || bundle.Sources.Select(s => s.SourceId).Distinct().Count() != bundle.Sources.Length || bundle.Seasons is null
            || bundle.Seasons.Any(s => s is null) || !bundle.Seasons.Select(s => s.SeasonKey).Order().SequenceEqual(legacy ? new[] { "2015-16", "2016-17" } : new[] { "2015-16", "2016-17", "2026-27" })
            || bundle.Seasons.Any(s => s.Status != (s.SeasonKey == "2026-27" ? "ongoing" : "complete")))
            throw new LeagueReferenceValidationException("Invalid league reference bundle/version/provenance.");
        Version = bundle.Version;
        _seasons = bundle.Seasons.ToDictionary(s => s.SeasonKey, s => new LeagueSeasonModel(s, sources: bundle.Sources, version: Version));
        if (current && !bundle.Seasons.Single(s => s.SeasonKey == "2026-27").Clubs.Select(c => c.Id).Order(StringComparer.Ordinal).SequenceEqual(
            new[] { "arsenal", "aston-villa", "bournemouth", "brentford", "brighton", "chelsea", "coventry", "crystal-palace", "everton", "fulham", "hull", "ipswich", "leeds", "liverpool", "manchester-city", "manchester-united", "newcastle", "nottingham-forest", "sunderland", "tottenham" }))
            throw new LeagueReferenceValidationException("Invalid current Premier League membership.");
    }
    public LeagueSeasonInfo[] Seasons() => _seasons.Values.OrderByDescending(s => s.Key, StringComparer.Ordinal).Select(s => s.Info()).ToArray();
    public LeagueSeasonSnapshot Snapshot(string key, int? index, string? clubId = null) => Season(key).Snapshot(index, clubId);
    public LeagueHistoricalMatch Match(string key, string matchKey)
    {
        var season = Season(key); var match = season.Matches.SingleOrDefault(m => m.MatchKey == matchKey) ?? throw new KeyNotFoundException("Unknown historical match.");
        var index = Array.IndexOf(season.Dates, match.Date) + 1;
        LeagueMatch[] Before(string club) => season.Matches.Where(m => string.CompareOrdinal(m.Date, match.Date) < 0 && (m.Home == club || m.Away == club)).TakeLast(5).ToArray();
        var prior = _seasons.Values.SelectMany(s => s.Matches.Where(m => string.CompareOrdinal(m.Date, match.Date) < 0 &&
            (m.Home == match.Home && m.Away == match.Away || m.Home == match.Away && m.Away == match.Home))
            .Select(m => new LeaguePriorMeeting(s.Key, m, s.Club(m.Home).Name, s.Club(m.Away).Name))).OrderBy(p => p.Match.Date, StringComparer.Ordinal).ThenBy(p => p.Match.MatchKey, StringComparer.Ordinal).ToArray();
        return new(Version, season.Info(), match, season.Club(match.Home), season.Club(match.Away), season.Facts(index, match.Home), season.Facts(index, match.Away), Before(match.Home), Before(match.Away), prior,
            $"Real full-time result from the pinned community-maintained results source. {Coverage(season.Info())} Form and meetings use only the {_seasons.Count} loaded seasons and dates strictly earlier than this played date. Table impact includes every result on the selected played date. No scorers, lineups, match events, tactics or known-then knowledge are supplied; the ground/crest are club-season context, not a verified match venue.");
    }
    private static string Coverage(LeagueSeasonInfo season) => season.Status == "ongoing"
        ? $"Ongoing snapshot: results through {season.ResultsThrough}; retrieved {season.RetrievedAt}. {season.MatchCount} completed results from {season.ScheduledMatchCount} scheduled fixtures. This snapshot does not update automatically."
        : $"Complete {season.Label} results reference: {season.MatchCount} completed matches.";
    private LeagueSeasonModel Season(string key) => _seasons.GetValueOrDefault(key) ?? throw new KeyNotFoundException("Unknown season.");
}

// Results-only rules match the original season-table-model.js. No point deductions are supplied or invented.
public sealed class LeagueSeasonModel
{
    private static readonly CompareInfo English = CultureInfo.GetCultureInfo("en").CompareInfo;
    private readonly LeagueClub[] _clubs;
    public string Key { get; }
    private readonly string _label;
    private readonly LeagueReferenceSource[] _sources;
    private readonly string _version, _status;
    private readonly int _scheduled;
    private readonly string? _resultsThrough, _retrievedAt;
    public LeagueMatch[] Matches { get; }
    public string[] Dates { get; }
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 80 } && Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    public LeagueSeasonModel(LeagueSeasonData season, bool complete = true, LeagueReferenceSource[]? sources = null, string version = LeagueReferenceCodec.Version)
    {
        if (season is null || !Regex.IsMatch(season.SeasonKey ?? "", "^[0-9]{4}-[0-9]{2}$", RegexOptions.CultureInvariant) || string.IsNullOrWhiteSpace(season.Label)
            || season.Label.Length > 100 || season.Clubs is not { Length: > 0 and <= 20 } || season.Matches is not { Length: <= 380 }
            || season.Status is not ("complete" or "ongoing") || season.ScheduledMatchCount != 380) throw new LeagueReferenceValidationException("Invalid season shape.");
        var ongoing = season.Status == "ongoing";
        DateOnly cutoff = default;
        if (ongoing && (!DateOnly.TryParseExact(season.ResultsThrough, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out cutoff)
            || !DateTimeOffset.TryParseExact(season.RetrievedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var retrieved)
            || cutoff > DateOnly.FromDateTime(retrieved.UtcDateTime) || season.Clubs.Length != 20
            || cutoff.Year < int.Parse(season.SeasonKey![..4], CultureInfo.InvariantCulture) || cutoff.Year > int.Parse(season.SeasonKey[..4], CultureInfo.InvariantCulture) + 1))
            throw new LeagueReferenceValidationException("Ongoing season requires a valid results cutoff and UTC retrieval time.");
        foreach (var club in season.Clubs)
            if (club is null || !ValidId(club.Id) || string.IsNullOrWhiteSpace(club.Name) || club.Name.Length > 150 || string.IsNullOrWhiteSpace(club.Initials) || club.Initials.Length > 8
                || string.IsNullOrWhiteSpace(club.Ground) || club.Ground.Length > 200 || string.IsNullOrWhiteSpace(club.Town) || club.Town.Length > 150
                || !double.IsFinite(club.X) || !double.IsFinite(club.Y) || club.X is < 0 or > 1000 || club.Y is < 0 or > 1000
                || club.CrestPath is not null && !Regex.IsMatch(club.CrestPath, "^assets/crests/[a-z0-9-]+\\.(?:png|svg)$", RegexOptions.CultureInvariant)) throw new LeagueReferenceValidationException("Invalid club/illustrated ground/crest context.");
        var ids = season.Clubs.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != season.Clubs.Length) throw new LeagueReferenceValidationException("Duplicate club identity.");
        var fixtures = new HashSet<string>(); var appearances = ids.ToDictionary(id => id, _ => 0);
        foreach (var match in season.Matches)
        {
            if (match is null || !ids.Contains(match.Home) || !ids.Contains(match.Away) || match.Home == match.Away || match.MatchKey != match.Home + ":" + match.Away
                || !fixtures.Add(match.MatchKey) || !DateOnly.TryParseExact(match.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var played)
                || ongoing && (played > cutoff || played < new DateOnly(int.Parse(season.SeasonKey![..4], CultureInfo.InvariantCulture), 7, 1))
                || match.HomeGoals < 0 || match.AwayGoals < 0 || match.Round?.Length > 100) throw new LeagueReferenceValidationException("Invalid/duplicate season fixture, date or full-time result.");
            appearances[match.Home]++; appearances[match.Away]++;
        }
        if (appearances.Values.Any(n => n > 38)) throw new LeagueReferenceValidationException("Too many club appearances.");
        if (complete && !ongoing && (ids.Count != 20 || season.Matches.Length != 380 || appearances.Values.Any(n => n != 38))) throw new LeagueReferenceValidationException("Incomplete Premier League season.");
        Key = season.SeasonKey!; _label = season.Label; _clubs = season.Clubs.ToArray(); _sources = sources?.ToArray() ?? [];
        _version = version; _status = season.Status; _scheduled = season.ScheduledMatchCount; _resultsThrough = season.ResultsThrough; _retrievedAt = season.RetrievedAt;
        Matches = season.Matches.OrderBy(m => m.Date, StringComparer.Ordinal).ThenBy(m => m.MatchKey, StringComparer.Ordinal).ToArray();
        Dates = Matches.Select(m => m.Date).Distinct().ToArray();
    }
    public LeagueClub Club(string id) => _clubs.SingleOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("Unknown club in this season.");
    public LeagueSeasonInfo Info() => new(Key, _label, _clubs.Length, Matches.Length, Dates.ToArray(), _version, LeagueReferenceCodec.Timing, _sources.ToArray(), _status, _scheduled, _resultsThrough, _retrievedAt);
    private sealed class Totals(LeagueClub club)
    {
        public readonly LeagueClub Club = club;
        public int P, W, D, L, Pts; public long Gf, Ga;
        public long Gd => Gf - Ga;
    }
    public LeagueSeasonSnapshot Snapshot(int? requested, string? clubId = null)
    {
        var index = requested ?? Dates.Length;
        if (index < 0 || index > Dates.Length) throw new LeagueCursorException("dateIndex must be an integer between zero and the number of played dates.");
        if (clubId is not null) _ = Club(clubId);
        var date = index > 0 ? Dates[index - 1] : null; var rows = _clubs.ToDictionary(c => c.Id, c => new Totals(c));
        var eligible = date is null ? [] : Matches.Where(m => string.CompareOrdinal(m.Date, date) <= 0).ToArray();
        foreach (var match in eligible)
        {
            Add(rows[match.Home], match.HomeGoals, match.AwayGoals); Add(rows[match.Away], match.AwayGoals, match.HomeGoals);
        }
        var ordered = rows.Values.OrderByDescending(r => r.Pts).ThenByDescending(r => r.Gd).ThenByDescending(r => r.Gf).ThenBy(r => r.Club.Name, Comparer<string>.Create((a,b) => English.Compare(a,b,CompareOptions.None))).ToArray();
        var result = new LeagueTableRow[ordered.Length]; int rank = 0; (int,long,long)? previous = null;
        for (var position = 0; position < ordered.Length; position++)
        {
            var r = ordered[position]; var key = (r.Pts, r.Gd, r.Gf); if (previous != key) rank = position + 1; previous = key;
            var shared = ordered.Count(other => (other.Pts, other.Gd, other.Gf) == key) > 1;
            result[position] = new(r.Club.Id, r.Club.Name, r.Club.Initials, r.P, r.W, r.D, r.L, r.Gf, r.Ga, r.Gd, r.Pts, index == 0 ? null : rank, shared);
        }
        return new(_version, Info(), index, date, index > 0, eligible.Length, _clubs.ToArray(), result,
            date is null ? [] : Matches.Where(m => m.Date == date).ToArray(), clubId is null ? null : Facts(index, clubId));
    }
    private static void Add(Totals row, int gf, int ga)
    {
        row.P++; row.Gf += gf; row.Ga += ga;
        if (gf > ga) { row.W++; row.Pts += 3; } else if (gf == ga) { row.D++; row.Pts++; } else row.L++;
    }
    public LeagueClubFacts Facts(int index, string clubId)
    {
        var club = Club(clubId); var current = Snapshot(index); var row = current.Rows.Single(r => r.Id == clubId);
        var previous = index > 0 ? Snapshot(index - 1).Rows.Single(r => r.Id == clubId) : null;
        var recent = current.Date is null ? [] : Matches.Where(m => string.CompareOrdinal(m.Date, current.Date) <= 0 && (m.Home == clubId || m.Away == clubId)).TakeLast(3).Reverse().ToArray();
        return new(club, row, previous, current.Date, index > 1 ? Dates[index - 2] : null, row.Points - (previous?.Points ?? 0),
            index > 1 ? previous!.Rank - row.Rank : null, current.Results.Where(m => m.Home == clubId || m.Away == clubId).ToArray(), recent, recent.FirstOrDefault());
    }
}
