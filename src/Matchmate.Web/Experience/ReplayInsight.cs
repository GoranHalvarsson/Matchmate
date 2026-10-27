using System.Globalization;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public enum ReplayInsightKind { ShotControlContrast, PassCompletionGap, BalancedControl, ReportedMatchState }
public sealed record ReplayInsightAction(ReplayInsightKind Kind, FootballQuery Query, long HeadSequence, string? ArtifactHash,
    FootballTeamIllustrationStatistics? Home, FootballTeamIllustrationStatistics? Away, long? ClassifiedControlMs, string? ReportedAuthorityHash = null);
public sealed record ReplayInsightPreparation(Guid Id, ReplayInsightAction Action, MatchChatContext Context);
public sealed record ReplayInsight(ReplayInsightKind Kind, string Headline, string Body, string? ControlNote, ReplayInsightAction Action)
{
    public string CategoryKey => Kind.ToString();
    public string Clock => ExperiencePresentation.Clock(Action.Query.CursorMs);
    public bool IsReported => Action.ReportedAuthorityHash is not null;
    public static string Question(ReplayInsightKind kind, MatchLanguage language = MatchLanguage.English) => ReplayText.Get(language, kind switch
    {
        ReplayInsightKind.ShotControlContrast => "ShotQuestion",
        ReplayInsightKind.PassCompletionGap => "PassQuestion",
        ReplayInsightKind.BalancedControl => "ControlQuestion",
        ReplayInsightKind.ReportedMatchState => "ReportedInsightQuestion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });
    public static bool Matches(ReplayInsightAction? action, FootballView? current) => action is not null && Project(current)?.Action == action;

    // No history, coordinates, event index or IO: only the accepted view's validated generated-statistics authority.
    public static ReplayInsight? Project(FootballView? view, MatchAudienceMode audience = MatchAudienceMode.Fan, MatchLanguage language = MatchLanguage.English)
    {
        if (ReportedReplayProjection.Project(view) is { ReportCount: > 0 } reports)
        {
            var fixture = view!.Dataset.Fixture!;
            string T(string key, params object?[] args) => ReplayText.Get(language, key, args);
            var headline = reports.Home.Goals == reports.Away.Goals ? T("ReportedInsightLevel")
                : T("ReportedInsightLead", reports.Home.Goals > reports.Away.Goals ? fixture.Home.Name : fixture.Away.Name);
            var body = T("RecapReported", T("RecapScore", fixture.Home.Name, reports.Home.Goals, reports.Away.Goals, fixture.Away.Name), "").Trim();
            foreach (var team in new[] { reports.Home, reports.Away }.Where(team => team.RedCards > 0))
                body += " " + T("ReportedDismissals", team.TeamId == fixture.Home.Id ? fixture.Home.Name : fixture.Away.Name, team.RedCards);
            if (MatchAudiencePresentation.Normalize(audience) == MatchAudienceMode.Analyst)
                body += " " + T("ReportedInsightCounts", reports.ReportCount, reports.Home.YellowCards + reports.Away.YellowCards,
                    reports.Home.Substitutions + reports.Away.Substitutions);
            return new(ReplayInsightKind.ReportedMatchState, headline, body, ReportedReplayProjection.Limitation(language),
                new(ReplayInsightKind.ReportedMatchState, view.Context, view.Dataset.HeadSequence, null, null, null, null, reports.AuthorityHash));
        }
        if (view is not { Dataset: { Manifest: { Sources: not null, Capabilities: not null }, Fixture: { Home: not null, Away: not null }, Schedule: not null, Stream: not null } }) return null;
        var dataset = view.Dataset; var query = view.Context;
        if (query is null || !MatchWorkflowExecutor.IsValidSnapshot(new("replay-insight", 0, new(query, dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor))
            || query.DatasetId != dataset.Manifest.DatasetId || query.DatasetVersion != dataset.Manifest.DatasetVersion || query.FixtureId != dataset.Manifest.FixtureId
            || query.DatasetId != dataset.Stream.DatasetId || query.DatasetVersion != dataset.Stream.DatasetVersion || query.FixtureId != dataset.Stream.FixtureId
            || query.StreamId != dataset.Stream.StreamId || dataset.Stream.DeliveryMode != "replay"
            || query.PresentationPolicyId != dataset.Schedule.PolicyId || query.PresentationPolicyRevision != dataset.Schedule.PolicyRevision
            || (query.ApplicationSequence ?? dataset.HeadSequence) != dataset.HeadSequence
            || dataset.Schedule.Segments is not { Length: > 0 } || dataset.Schedule.Segments.Any(s => s is null)
            || query.CursorMs < dataset.Schedule.Segments[0].CursorStartMs || query.CursorMs > dataset.Schedule.Segments[^1].CursorEndMs
            || !dataset.Closed && (view.Frame is null || view.TrackingGap)) return null;
        try
        {
            // Reuse the existing capability/team/count/denominator/source validation without changing its authority.
            var evidence = MatchIllustrationStatistics.Build(view);
            using var data = JsonDocument.Parse(evidence.Text);
            var stats = view.IllustrationStatistics!;
            if (stats.TotalClassifiedControlMs < 300000) return null;
            var home = stats.Teams.Single(t => t.TeamId == dataset.Fixture.Home.Id);
            var away = stats.Teams.Single(t => t.TeamId == dataset.Fixture.Away.Id);
            var shared = home.ControlSharePercent is >= 45m and <= 55m && away.ControlSharePercent is >= 45m and <= 55m;
            string T(string key, params object?[] args) => ReplayText.Get(language, key, args);
            var duration = stats.TotalClassifiedControlMs / 1000 % 60 == 0
                ? T("DurationMinutes", stats.TotalClassifiedControlMs / 60000)
                : T("DurationMinutesSeconds", stats.TotalClassifiedControlMs / 60000, stats.TotalClassifiedControlMs / 1000 % 60);
            var note = T("InsightControlNote", duration);
            ReplayInsight Card(ReplayInsightKind kind, string headline, string body, bool usesControl) => new(kind, headline, body, usesControl ? note : null,
                new(kind, query, dataset.HeadSequence, data.RootElement.GetProperty("provenance").GetProperty("artifactHash").GetString()!, home, away, stats.TotalClassifiedControlMs));
            string Percent(decimal? value) => value!.Value.ToString("0.#", MatchLanguages.Culture(language)) + "%";
            var homeName = dataset.Fixture.Home.Name; var awayName = dataset.Fixture.Away.Name;
            var analyst = MatchAudiencePresentation.Normalize(audience) == MatchAudienceMode.Analyst;
            if (shared && (home.Shots >= 1 && away.Shots == 0 || away.Shots >= 1 && home.Shots == 0))
            {
                var shooting = home.Shots > 0 ? home : away; var name = home.Shots > 0 ? homeName : awayName; var other = home.Shots > 0 ? awayName : homeName;
                var shots = analyst ? shooting.Shots == 1 ? T("AShot") : T("ShotsCount", shooting.Shots)
                    : shooting.Shots == 1 ? T("OnlyShot") : T("AllShots", shooting.Shots);
                return Card(ReplayInsightKind.ShotControlContrast, T("InsightShotHeadline"),
                    analyst ? T("InsightShotAnalyst", name, shots, other, Percent(home.ControlSharePercent), Percent(away.ControlSharePercent))
                        : T("InsightShotFan", name, shots, other), true);
            }
            if (home.PassAttempts >= 20 && away.PassAttempts >= 20 && Math.Abs(home.PassCompletionPercent!.Value - away.PassCompletionPercent!.Value) >= 10m)
                return Card(ReplayInsightKind.PassCompletionGap, T("InsightPassHeadline"),
                    analyst ? T("InsightPassAnalyst", homeName, home.PassCompleted, home.PassAttempts, Percent(home.PassCompletionPercent), awayName, away.PassCompleted, away.PassAttempts, Percent(away.PassCompletionPercent))
                        : T("InsightPassFan", home.PassCompletionPercent > away.PassCompletionPercent ? homeName : awayName), false);
            if (shared) return Card(ReplayInsightKind.BalancedControl, T("InsightBalancedHeadline"),
                analyst ? T("InsightBalancedAnalyst", homeName, Percent(home.ControlSharePercent), awayName, Percent(away.ControlSharePercent))
                    : T("InsightBalancedFan", homeName, awayName), true);
            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        { return null; }
    }
}
