using System.Globalization;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public sealed record SourcePrompt(string Question, string Answer);
public sealed record ReplayHighlight(string RecordId, long CursorMs, string Kind, string Label, string ReportedClock,
    string? Actor = null, string? Assist = null, string? TeamName = null, string? ScoreAfter = null, int? GoalNumber = null, string? Decision = null);

public static class ExperiencePresentation
{
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Clock(long ms) => $"{ms / 60000:00}:{ms / 1000 % 60:00}";
    public static string IntervalTime(long ms) => ms % 1000 == 0 ? Clock(ms) : $"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}".TrimEnd('0');
    public static string Date(string? value, MatchLanguage language = MatchLanguage.English) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("d MMM yyyy", MatchLanguages.Culture(language)) : LeagueText.Get(language, "BeforeFirstMatch");
    public static string Rank(LeagueTableRow row) => row.Rank is null ? "—" : (row.Shared ? "=" : "") + row.Rank;
    public static string LeagueUrl(string season, int? index, string? club) => "/?season=" + Uri.EscapeDataString(season)
        + (index is null ? "" : "&date=" + index.Value.ToString(CultureInfo.InvariantCulture))
        + (string.IsNullOrEmpty(club) ? "" : "&club=" + Uri.EscapeDataString(club));
    public static string MatchUrl(string season, string key, int index, string? club) => "/league/" + Uri.EscapeDataString(season) + "/matches/" + Uri.EscapeDataString(key)
        + "?date=" + index.ToString(CultureInfo.InvariantCulture) + (club is null ? "" : "&club=" + Uri.EscapeDataString(club));
    public static bool Generated(FootballDataset dataset) => dataset.Manifest.Capabilities.Any(c => c is "sparse-generated-windows-only" or "complete-existing-generated-100ms-model" or FootballPresentationPolicy.GeneratedMovement);
    public static string SourceLabel(FootballDataset dataset, MatchLanguage language = MatchLanguage.English) => ReplayText.Get(language,
        dataset.Manifest.Capabilities.Contains(FootballPresentationPolicy.GeneratedMovement) ? "GeneratedMovementSource" : Generated(dataset) ? "GeneratedSource" : "RecordedSource");
    public static string Person(FootballDataset dataset, string? id, MatchLanguage language = MatchLanguage.English) => dataset.People.FirstOrDefault(p => p.PersonId == id)?.Name ?? ReplayText.Get(language, "UnspecifiedPlayer");
    public static string Pressed(bool value) => value ? "true" : "false";
    public static bool SupportsEventReports(FootballDataset dataset) => dataset.Manifest.Capabilities.Contains("match_event");
    public static string PeriodLabel(FootballView view, MatchLanguage language = MatchLanguage.English) => view.Phase == "halftime_transition" ? ReplayText.Get(language, "HalftimeTransition")
        : Period(view.Dataset.Fixture?.Periods.FirstOrDefault(p => p.Id == view.PeriodId), language, "PeriodUnavailable");
    public static string SegmentLabel(FootballDataset dataset, PresentationSegment segment, MatchLanguage language = MatchLanguage.English) => segment.Phase == "halftime_transition" ? ReplayText.Get(language, "HalftimeTransition")
        : Period(dataset.Fixture?.Periods.FirstOrDefault(p => p.Id == segment.PeriodId), language, "MatchPeriod");
    private static string Period(PeriodDefinition? period, MatchLanguage language, string fallback) => period is null ? ReplayText.Get(language, fallback)
        : language == MatchLanguage.English ? period.Label : period.Order switch { 1 => ReplayText.Get(language, "FirstHalf"), 2 => ReplayText.Get(language, "SecondHalf"), _ => period.Label };
    public static string HighlightLabel(ReplayHighlight highlight, MatchLanguage language = MatchLanguage.English) => language == MatchLanguage.English ? highlight.Label
        : ReplayText.Kind(language, highlight.Kind) + (highlight.Decision is null ? "" : " (" + ReplayText.Value(language, highlight.Decision) + ")") + " · " + (highlight.Actor ?? ReplayText.Get(language, "UnspecifiedPlayer"));
    public static string ReleasePercent(long cursor, SchedulePayload schedule)
    {
        var start = schedule.Segments[0].CursorStartMs; var end = schedule.Segments[^1].CursorEndMs;
        return Number(end <= start ? 0 : Math.Clamp((cursor - start) * 100d / (end - start), 0, 100)) + "%";
    }
    public static int ReportedGoals(FootballView view, string team) => view.Events.Count(r => r.Payload is MatchEventPayload { EventKind: "goal" } e && e.TeamId == team && e.Decision != "disallowed");
    public static long MatchClock(FootballView view) => view.PausedDisplayMs ?? (view.Dataset.Fixture?.Periods.FirstOrDefault(p => p.Id == view.PeriodId)?.DisplayBaseMs ?? 0) + (view.LocalMs ?? 0);
    public static ReplayHighlight[] Highlights(FootballView end)
    {
        var released = end.Events.Where(r => r.Payload is MatchEventPayload)
        .Select(r => (Record: r, Release: end.Dataset.Schedule?.Releases.FirstOrDefault(t => t.RecordId == r.RecordId)))
        .Where(p => p.Release is not null).OrderBy(p => p.Release!.CursorMs).ThenBy(p => p.Record.RecordId, StringComparer.Ordinal).ToArray();
        var goalNumber = 0;
        var reports = released.Select(p =>
        {
            var e = (MatchEventPayload)p.Record.Payload;
            var actor = Person(end.Dataset, e.EventKind == "substitution" ? e.IncomingPersonId : e.PersonId);
            var labelActor = e.EventKind == "substitution" ? Person(end.Dataset, e.OutgoingPersonId) + " → " + actor : actor;
            var team = end.Dataset.Fixture is { } fixture ? new[] { fixture.Home, fixture.Away }.FirstOrDefault(t => t.Id == e.TeamId)?.Name : null;
            var eligibleAtRelease = end with { Events = end.Events.Where(r => FootballCutoffReached(r, p.Release!.CursorMs, end.Dataset.Schedule!)
                && (end.Dataset.Schedule!.Releases.FirstOrDefault(t => t.RecordId == r.RecordId) is not { } release || release.CursorMs <= p.Release.CursorMs)).ToArray() };
            var score = e.EventKind == "goal" && end.Dataset.Fixture is { } f
                ? $"{ReportedGoals(eligibleAtRelease, f.Home.Id)}–{ReportedGoals(eligibleAtRelease, f.Away.Id)}" : null;
            var eventLabel = e.EventKind is "red_card" or "var_decision" ? ReplayText.Kind(MatchLanguage.English, e.EventKind) : e.EventKind;
            var decisionLabel = e.EventKind is "red_card" or "var_decision" ? ReplayText.Value(MatchLanguage.English, e.Decision) : e.Decision;
            return new ReplayHighlight(p.Record.RecordId, p.Release!.CursorMs, e.EventKind, eventLabel + (e.Decision is null ? "" : " (" + decisionLabel + ")") + " · " + labelActor,
                p.Record.FootballTime?.ReportedClock ?? "reported clock unavailable", actor,
                e.EventKind == "goal" && e.AssistPersonId is not null ? Person(end.Dataset, e.AssistPersonId) : e.EventKind == "substitution" ? "For " + Person(end.Dataset, e.OutgoingPersonId) : null,
                team, score, e.EventKind == "goal" ? ++goalNumber : null, e.Decision);
        }).ToArray();
        var possessions = end.Possessions.Select(r => (Record: r, Projection: MatchSequenceProjector.Possession(end.Dataset, r, end.Context.CursorMs)))
            .Where(p => p.Projection is not null).Select(p => new ReplayHighlight(p.Record.RecordId, p.Projection!.ReleaseCursorMs,
                "completed_possession", "Completed possession · " + Person(end.Dataset, ((PossessionPayload)p.Record.Payload).PersonId),
                MatchEventVisual.IntervalLabel(p.Projection), Person(end.Dataset, ((PossessionPayload)p.Record.Payload).PersonId), "Completed interval; not possession now"));
        return reports.Concat(possessions).OrderBy(h => h.CursorMs).ThenBy(h => h.RecordId, StringComparer.Ordinal).ToArray();
    }
    private static bool FootballCutoffReached(FootballRecord record, long cursor, SchedulePayload schedule)
    {
        if (record.FootballTime is not { Precision: not "unknown" } time) return true;
        var cutoff = time.Precision == "reported_minute" ? time.EndMs : time.StartMs;
        var segment = schedule.Segments.FirstOrDefault(s => s.PeriodId == time.PeriodId && FootballPresentationPolicy.IsPlay(s));
        return cutoff is { } value && segment is not null && segment.CursorStartMs + value - segment.LocalStartMs <= cursor;
    }
}
