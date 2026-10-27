using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public sealed record MatchSnapshotStep(string Kind, string? Actor, string? RelatedActor, string Text, string? Uncertainty);
public sealed record MatchSnapshotData(bool NoFrame, bool Neutral, bool Generated, bool Estimated, bool UnknownMethod,
    string? BallThird, string? BallBand, string[] Nearby, string[] Rings, string? ActiveActor, string? ActiveKind,
    bool Path, bool DismissalAnnotation, bool CardMarker, bool ChangeMarker, bool SelectedOverlay,
    string? SelectedKind, string? SelectedClock, MatchSnapshotStep[] SelectedSteps);

// Local, bounded display evidence only. Raw coordinates, distances and the full retrospective index
// cannot reach a narrator through this workflow.
public static class MatchSnapshotExplanation
{
    internal static bool ValidFrame(FootballView view)
    {
        var pitch = view.Dataset.Fixture!.Pitch;
        if (pitch.LengthM <= 0 || pitch.WidthM <= 0 || !double.IsFinite(pitch.LengthM) || !double.IsFinite(pitch.WidthM)
            || view.Dataset.People.GroupBy(p => p.PersonId, StringComparer.Ordinal).Any(g => g.Count() != 1)) return false;
        if (view.Frame is not { } frame) return true;
        return frame.Players.GroupBy(p => p.SubjectId, StringComparer.Ordinal).All(g => g.Count() == 1)
            && frame.Players.Append(frame.Ball).All(p => p.Availability != "present"
                ? p.XM is null && p.YM is null
                : p.XM is { } x && double.IsFinite(x) && p.YM is { } y && double.IsFinite(y));
    }

    internal static MatchSnapshotData Capture(FootballView view, MatchPitchPresentation pitch,
        MatchSequenceProjection[] scenes, bool mask, MatchEventVisual? selected,
        MatchSelectedEvent? selectedReport = null, MatchSnapshotStep[]? reportedSteps = null)
    {
        string? Name(string? id) => id is null ? null : view.Dataset.People.SingleOrDefault(p => p.PersonId == id) is { } person
            && MatchPlayerResolver.ValidText(person.Name) && person.Name.Length <= 180 ? person.Name : null;
        var neutral = mask || pitch.GeneratedLayout && pitch.Sequence is null;
        var frame = pitch.Frame;
        var ball = !neutral && frame?.Ball is { Availability: "present", XM: not null, YM: not null } b ? b : null;
        var present = frame?.Players.Where(p => p is { Availability: "present", XM: not null, YM: not null }).ToArray() ?? [];
        var actors = scenes.SelectMany(s => s.Sequence.Steps).SelectMany(s => new[] { s.ActorId, s.RelatedActorId }).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var rings = neutral ? [] : present.Where(p => actors.Contains(p.SubjectId)).Select(p => Name(p.SubjectId)).OfType<string>().Distinct().ToArray();
        var nearby = ball is null ? [] : present.Where(p => Name(p.SubjectId) is not null)
            .OrderBy(p => Math.Pow(p.XM!.Value - ball.XM!.Value, 2) + Math.Pow(p.YM!.Value - ball.YM!.Value, 2))
            .ThenBy(p => p.SubjectId, StringComparer.Ordinal).Take(3).Select(p => Name(p.SubjectId)!).ToArray();
        var methods = neutral ? [] : present.Select(p => p.Method).Append(ball?.Method ?? "missing").ToArray();
        var unknownQuality = !neutral && present.Concat(ball is null ? [] : [ball]).Any(p => p.Method != "generated" && p.Quality == "unknown");
        var active = pitch.ActiveStep;
        var steps = selected?.Steps.Select(s => new MatchSnapshotStep(s.Kind, s.ActorName, s.RelatedActorName, s.Text, s.Uncertainty)).ToArray() ?? reportedSteps ?? [];
        var selectedOverlay = selected is not null && scenes.Any(s => s.RecordId == selected.EventId);
        return new(frame is null, neutral, pitch.GeneratedLayout || methods.Contains("generated"), methods.Contains("estimated"), methods.Contains("unknown") || unknownQuality,
            ball is null ? null : ball.XM < view.Dataset.Fixture!.Pitch.LengthM / 3 ? "Left" : ball.XM > view.Dataset.Fixture.Pitch.LengthM * 2 / 3 ? "Right" : "Middle",
            ball is null ? null : ball.YM < view.Dataset.Fixture!.Pitch.WidthM / 3 ? "Top" : ball.YM > view.Dataset.Fixture.Pitch.WidthM * 2 / 3 ? "Bottom" : "Central",
            nearby, rings, neutral ? null : Name(active?.ActorId), neutral ? null : active?.Kind,
            !neutral && pitch.Path.Length > 0, scenes.Any(s => s.Sequence.Steps is [{ Kind: "red_card" }]),
            scenes.Any(s => s.Sequence.Steps.Any(s => s.Kind == "booking" && present.Any(p => p.SubjectId == s.ActorId))),
            scenes.Any(s => s.Sequence.Steps.Any(s => s.Kind == "substitution" && present.Any(p => p.SubjectId == (s.RelatedActorId ?? s.ActorId)))),
            selectedOverlay, selected?.Kind ?? selectedReport?.Kind, selected?.ReportedClock ?? selectedReport?.ReportedClock, steps);
    }

    internal static (string Answer, MatchChatEvidence[] Evidence) Format(MatchWorkflowBundle bundle, MatchLanguage language, MatchAudienceMode mode)
    {
        var item = bundle.Items.Single(i => i.Tool == "pitch_snapshot");
        var data = JsonSerializer.Deserialize<MatchSnapshotData>(item.Text) ?? throw new InvalidDataException("Missing pitch snapshot.");
        string T(string key, params object?[] args) => SnapshotText.Get(language, key, args);
        var paragraphs = new List<string>();
        var pitchFacts = new List<string>();
        var qualifications = new List<string>();
        if (data.SelectedKind is { } kind)
        {
            paragraphs.Add(T("Selected", ReplayText.Kind(language, kind), data.SelectedClock));
            var notes = data.SelectedSteps.Select(step => {
                if (step.Kind == "substitution_report") return T("Substitution", step.Actor ?? T("UnknownActor"), step.RelatedActor ?? T("UnknownActor"));
                if (step.Kind == "substitution_participant") return T("SubstitutionParticipant", step.Actor ?? T("UnknownActor"));
                var translated = SourceNoteText.Get(step.Kind, step.Text, step.Uncertainty, language);
                // Unknown source prose stays in the source record; the localized explanation can still
                // name the validated semantic action and actors without inventing a translation.
                var text = translated.IsFallback ? MatchPitchPresenter.Label(step.Kind, language) : WithoutRepeatedAttribution(translated.Text, language, step.Actor);
                // Exact token expansion only, with the validated actor's own surname. If the note
                // does not name that token, retain an explicit actor label rather than guessing grammar.
                if (step.Actor is { } actor && step.RelatedActor is null)
                {
                    if (text.Contains(actor, StringComparison.Ordinal)) return text;
                    var surname = actor.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    var token = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(surname) + @"(?![\p{L}\p{N}])", RegexOptions.CultureInvariant);
                    if (token.Matches(text).Count == 1) return token.Replace(text, _ => actor, 1);
                }
                return (step.Actor is null ? T("UnknownActor") : step.Actor) + (step.RelatedActor is null ? "" : " → " + step.RelatedActor) + ": " + text;
            });
            if (data.SelectedSteps.Length > 0)
                paragraphs.Add(T("ReportedSequence", "").TrimEnd() + "\n" + string.Join("\n", notes.Select((note, index) => $"{index + 1}. {note}")));
            if (!data.SelectedOverlay) pitchFacts.Add(T("OutsideOverlay"));
            if (kind != "goal") qualifications.Add(T("NonSpatial"));
        }
        if (data.NoFrame) pitchFacts.Add(T("NoFrame"));
        else if (data.Neutral) pitchFacts.Add(T("Neutral"));
        else
        {
            pitchFacts.Add(data.BallThird is null ? T("NoBall") : T("Ball", T(data.BallThird), T(data.BallBand!)));
            if (data.Rings.Length > 0) pitchFacts.Add(T("Rings", string.Join(", ", data.Rings)));
            if (data.Path && data.ActiveKind is null) pitchFacts.Add(T("Path"));
            if (data.ActiveKind is { } active) pitchFacts.Add(T(data.Path ? "ActivePath" : "Active", MatchPitchPresenter.Label(active, language), data.ActiveActor ?? T("UnknownActor")));
            if (data.DismissalAnnotation) pitchFacts.Add(T("Dismissal"));
            if (data.CardMarker) pitchFacts.Add(T("Card"));
            if (data.ChangeMarker) pitchFacts.Add(T("Change"));
            if (data.ActiveKind is null && data.Nearby.Length > 0) pitchFacts.Add(T("Nearby", string.Join(", ", data.Nearby)));
            if (data.Generated) qualifications.Add(T("Generated"));
            else if (data.Estimated) qualifications.Add(T("Estimated"));
            if (data.UnknownMethod) qualifications.Add(T("UnknownMethod"));
        }
        if (pitchFacts.Count > 0) paragraphs.Add((data.NoFrame || data.Neutral ? "" : T("OnPitch") + "\n") + string.Join(" ", pitchFacts));
        if (MatchAudiencePresentation.Normalize(mode) == MatchAudienceMode.Analyst) qualifications.Add(T("Analyst"));
        if (qualifications.Count > 0) paragraphs.Add(string.Join(" ", qualifications));
        var answer = string.Join("\n\n", paragraphs);
        if (answer.Length > MatchChatLimits.AnswerChars) throw new InvalidDataException("Snapshot answer budget exceeded.");
        return (answer, [new(item.Id, item.Tool, item.Text, [])]);
    }

    private static string WithoutRepeatedAttribution(string text, MatchLanguage language, string? actor)
    {
        const string swedishAttribution = "Enligt rapporten ";
        if (language == MatchLanguage.Swedish && text.StartsWith(swedishAttribution, StringComparison.Ordinal))
        {
            var body = text[swedishAttribution.Length..];
            if (actor is not null)
                foreach (var verb in new[] { "slår", "nickar", "avslutar" })
                    foreach (var name in new[] { actor, actor.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] })
                        if (body.StartsWith(verb + " " + name + " ", StringComparison.Ordinal))
                            return actor + " " + verb + " " + body[(verb.Length + name.Length + 2)..];
            return text; // Preserve the reviewed sentence when exact, bounded reordering is unavailable.
        }
        var prefixes = language switch {
            MatchLanguage.Swedish => new[] { "Klubbrapport: ", "Båda klubbarnas rapporter: " },
            MatchLanguage.Spanish => ["Informe del club: ", "Según el informe, ", "Informes de ambos clubes: "],
            _ => ["Club report: ", "Reported ", "Both club reports: "] };
        return prefixes.FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.Ordinal)) is { } matched ? text[matched.Length..] : text;
    }
}
