using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public sealed record MatchPitchPresentation(TrackingPayload? Frame, MatchSequenceProjection? Sequence, MatchEventStep? ActiveStep,
    (double X, double Y)[] Path, (double X, double Y)? Contact, string Caption, bool GeneratedLayout);

// UI geometry only. These copies never enter the ledger, factual tools or narration evidence.
public static class MatchPitchPresenter
{
    public static MatchPitchPresentation Project(FootballView view, MatchSequenceProjection[] sequences, bool indexAvailable, bool maskGoalWindow, MatchLanguage language = MatchLanguage.English)
    {
        string T(string key, params object?[] args) => ReplayText.Get(language, key, args);
        var frame = view.Frame;
        var enabled = view.Dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1");
        var available = enabled && indexAvailable ? sequences : [];
        var scene = available.FirstOrDefault(s => s.Scope != "completed_possession");
        var possession = sequences.FirstOrDefault(s => s.Scope == "completed_possession");
        var goal = scene?.Sequence.Steps.Any(s => s.Kind is not "substitution" and not "booking" and not "red_card") == true;
        var neutral = enabled && (!indexAvailable || maskGoalWindow || goal);
        var step = scene?.Sequence.Steps[scene.ActiveStep];
        if (frame is null) return new(null, scene, step, [], null, T("NoCurrentPosition"), false);
        var players = neutral ? Neutral(view, frame.Players) : frame.Players;
        var path = Array.Empty<(double X, double Y)>(); (double X, double Y)? contact = null;
        var team = scene is null ? null : view.Events.FirstOrDefault(r => r.RecordId == scene.RecordId)?.Payload is MatchEventPayload report ? report.TeamId
            : view.Dataset.People.FirstOrDefault(p => scene.Sequence.Steps.Any(s => s.ActorId == p.PersonId))?.TeamId;
        var ball = neutral ? frame.Ball with { XM = frame.Ball.Availability == "present" ? view.Dataset.Fixture!.Pitch.LengthM / 2 : null,
            YM = frame.Ball.Availability == "present" ? view.Dataset.Fixture!.Pitch.WidthM / 2 : null, Method = "generated", Quality = "unknown" } : frame.Ball;
        if (goal && scene is not null)
        {
            var eventPeriod = view.Events.FirstOrDefault(r => r.RecordId == scene.RecordId)?.FootballTime?.PeriodId
                ?? view.Dataset.Schedule?.Segments.FirstOrDefault(s => scene.ReleaseCursorMs >= s.CursorStartMs && scene.ReleaseCursorMs <= s.CursorEndMs)?.PeriodId;
            var points = scene.Sequence.Steps.Select(s => Point(view, team, s.Kind, eventPeriod)).ToArray(); path = points;
            // A known report actor is staged only under its explicit semantic role. Unknown actors never borrow a roster identity.
            players = players.Select(player => {
                var index = Array.FindLastIndex(scene.Sequence.Steps, scene.ActiveStep, scene.ActiveStep + 1, s => s.ActorId == player.SubjectId);
                if (index < 0) index = Array.FindIndex(scene.Sequence.Steps, s => s.ActorId == player.SubjectId);
                return index < 0 || player.Availability != "present" ? player : player with { XM = points[index].X, YM = points[index].Y, Method = "generated", Quality = "unknown" };
            }).ToArray();
            var from = points[scene.ActiveStep];
            var to = scene.ActiveStep + 1 < points.Length ? points[scene.ActiveStep + 1] : Point(view, team, "net", eventPeriod);
            contact = from;
            if (ball.Availability == "present") ball = ball with { XM = from.X + (to.X - from.X) * scene.StepProgress,
                YM = from.Y + (to.Y - from.Y) * scene.StepProgress, Method = "generated", Quality = "unknown" };
        }
        var caption = !enabled && possession is null ? "" : enabled && !indexAvailable ? T("EventEvidenceLoading")
            : maskGoalWindow ? T("NeutralPlay")
            : available.Length > 1 && available.All(s => s.Sequence.Steps.All(step => step.Kind == "substitution")) ? T("SubstitutionsCaption", available.Length)
            : step is not null ? Label(step.Kind, language) + (step.ActorId is null ? step.Kind == "keeper_out" ? " · " + T("Goalkeeper", team == view.Dataset.Fixture!.Away.Id ? view.Dataset.Fixture.Home.Name : view.Dataset.Fixture.Away.Name) : "" : " · " + ExperiencePresentation.Person(view.Dataset, step.ActorId, language))
                + (step.RelatedActorId is null ? "" : " → " + ExperiencePresentation.Person(view.Dataset, step.RelatedActorId, language))
            : possession is not null ? T("CompletedPossessionCaption", ExperiencePresentation.Person(view.Dataset, possession.Sequence.Steps[0].ActorId, language),
                ExperiencePresentation.IntervalTime(possession.IntervalStartMs!.Value), ExperiencePresentation.IntervalTime(possession.IntervalEndMs!.Value))
            : "";
        return new(frame with { Players = players, Ball = ball }, scene, step, path, contact, caption, neutral);
    }
    private static FootballObservation[] Neutral(FootballView view, FootballObservation[] observations)
    {
        var fixture = view.Dataset.Fixture!; var result = new List<FootballObservation>(observations.Length);
        foreach (var group in observations.GroupBy(p => view.Dataset.People.FirstOrDefault(person => person.PersonId == p.SubjectId)?.TeamId))
        {
            var ordered = group.OrderBy(p => p.SlotId, StringComparer.Ordinal).ThenBy(p => p.SubjectId, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var p = ordered[i]; var x = i == 0 ? 6 : i < 5 ? 25 : i < 9 ? 43 : 62;
                var y = i == 0 ? .5 : i < 5 ? i / 5d : i < 9 ? (i - 5 + 1d) / 5 : (i - 9 + 1d) / 3;
                var right = AttacksRight(view, group.Key);
                result.Add(p.Availability != "present" ? p : p with { XM = fixture.Pitch.LengthM * (right ? x : 100 - x) / 100,
                    YM = fixture.Pitch.WidthM * Math.Clamp(y, .08, .92), Method = "generated", Quality = "unknown" });
            }
        }
        return result.ToArray();
    }
    public static bool AttacksRight(FootballView view, string? team) =>
        MatchSequenceLayout.AttacksRight(view.Dataset.Fixture!.Periods.FirstOrDefault(p => p.Id == view.PeriodId)?.Order, team, view.Dataset.Fixture.Home.Id);
    private static (double X, double Y) Point(FootballView view, string? team, string kind, string? periodId)
        => MatchSequenceLayout.Point(kind, MatchSequenceLayout.AttacksRight(
            view.Dataset.Fixture!.Periods.FirstOrDefault(p => p.Id == (periodId ?? view.PeriodId))?.Order, team, view.Dataset.Fixture.Home.Id),
            view.Dataset.Fixture.Pitch.LengthM, view.Dataset.Fixture.Pitch.WidthM);
    public static string Label(string kind, MatchLanguage language = MatchLanguage.English) => ReplayText.Kind(language,
        kind is "corner" or "headed_assist" or "header_finish" or "advance" or "keeper_out" or "uncertain_goalmouth"
        or "reported_finish" or "throw_in" or "into_area" or "release_pass" or "shot" or "save" or "rebound_finish"
        or "cross" or "deflection" or "booking" or "red_card" or "substitution" or "reported_assist" ? kind : "completed_possession");
}
