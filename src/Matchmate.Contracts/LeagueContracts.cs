using System.Text.Json;
using System.Text.Json.Serialization;

namespace Matchmate.Contracts;

public sealed record LeagueReferenceSource(string SourceId, string Path, string Sha256, string License);
public sealed record LeagueClub(string Id, string Name, string Initials, string Town, string Ground,
    double X, double Y, bool Inset, string? CrestPath);
public sealed record LeagueMatch(string MatchKey, string Date, string Home, string Away,
    [property: JsonRequired] int HomeGoals, [property: JsonRequired] int AwayGoals, string? Round);
public sealed record LeagueSeasonData(string SeasonKey, string Label, LeagueClub[] Clubs, LeagueMatch[] Matches,
    string Status = "complete", int ScheduledMatchCount = 380, string? ResultsThrough = null, string? RetrievedAt = null);
public sealed record LeagueReferenceBundle(int SchemaVersion, string Version, string Timing, string Calculation,
    LeagueReferenceSource[] Sources, string ResultsLicenseText, JsonElement ResultsSourceMetadata,
    JsonElement CrestSourceMetadata, LeagueSeasonData[] Seasons);
public sealed record LeagueSeasonInfo(string SeasonKey, string Label, int ClubCount, int MatchCount,
    string[] PlayedDates, string Version, string Timing, LeagueReferenceSource[] Sources,
    string Status = "complete", int ScheduledMatchCount = 380, string? ResultsThrough = null, string? RetrievedAt = null);
public sealed record LeagueTableRow(string Id, string Name, string Initials, int Played, int Won, int Drawn, int Lost,
    long GoalsFor, long GoalsAgainst, long GoalDifference, int Points, int? Rank, bool Shared);
public sealed record LeagueClubFacts(LeagueClub Club, LeagueTableRow Row, LeagueTableRow? PreviousRow, string? Date,
    string? PreviousDate, int PointsChange, int? PositionChange, LeagueMatch[] Today, LeagueMatch[] Recent, LeagueMatch? Latest);
public sealed record LeagueSeasonSnapshot(string Version, LeagueSeasonInfo Season, int DateIndex, string? Date, bool Ranked,
    int Completed, LeagueClub[] Clubs, LeagueTableRow[] Rows, LeagueMatch[] Results, LeagueClubFacts? SelectedClub);
public sealed record LeaguePriorMeeting(string SeasonKey, LeagueMatch Match, string HomeName, string AwayName);
public sealed record LeagueHistoricalMatch(string Version, LeagueSeasonInfo Season, LeagueMatch Match, LeagueClub Home,
    LeagueClub Away, LeagueClubFacts HomeTable, LeagueClubFacts AwayTable, LeagueMatch[] HomePreMatchForm,
    LeagueMatch[] AwayPreMatchForm, LeaguePriorMeeting[] PriorMeetings, string SourceContext);
