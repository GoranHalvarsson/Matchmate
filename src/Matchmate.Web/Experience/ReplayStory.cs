using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Experience;

public sealed record ReplayStoryAction(FootballQuery Query, long HeadSequence, MatchSelectedPassage Passage);
public sealed record ReplayStoryPreparation(Guid Id, ReplayStoryAction Action, MatchChatContext Context);
public sealed record ReplayStoryItem(FootballStoryPassage Passage, string Team, string Interval, string Summary, ReplayStoryAction Action);

// Presentation accepts only completed passages in the accepted API view. The API's calculated
// projection is authoritative; raw question text can never choose its interval or record membership.
public static class ReplayStory
{
    public static ReplayStoryItem[]? Project(FootballView? view, MatchLanguage language = MatchLanguage.English)
    {
        if (!Bound(view) || view!.Story?.Passages is not { } passages || passages.Length > FootballActionPolicy.MaxGeneratedActionRecordIds) return null;
        try
        {
            MatchIllustrationStatistics.Source(view);
            var members = view.GeneratedActionRecordIds;
            if (members is null || members.Length > FootballActionPolicy.MaxGeneratedActionRecordIds
                || members.Any(id => !DatasetIdentity.IsValid(id)) || members.Distinct(StringComparer.Ordinal).Count() != members.Length
                || passages.Any(p => p is null) || passages.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != passages.Length) return null;
            var known = members.ToHashSet(StringComparer.Ordinal);
            long priorEnd = -1;
            var result = new List<ReplayStoryItem>();
            foreach (var passage in passages)
            {
                if (!Valid(passage, view, known) || passage.StartCursorMs < priorEnd) return null;
                priorEnd = passage.EndCursorMs;
                var team = passage.TeamId == view.Dataset.Fixture!.Home.Id ? view.Dataset.Fixture.Home.Name : view.Dataset.Fixture.Away.Name;
                var part = view.Dataset.Schedule!.Segments.Single(s => s.PeriodId == passage.PeriodId && FootballPresentationPolicy.IsPlay(s));
                var period = view.Dataset.Fixture.Periods.Single(p => p.Id == passage.PeriodId);
                string Clock(long cursor) => ExperiencePresentation.Clock(period.DisplayBaseMs + part.LocalStartMs + cursor - part.CursorStartMs);
                var interval = ReplayText.Get(language, "StoryInterval", Clock(passage.StartCursorMs), Clock(passage.EndCursorMs));
                result.Add(new(passage, team, interval, Summary(passage, language), new(view.Context, view.Dataset.HeadSequence, Locator(passage))));
            }
            return result.ToArray();
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException) { return null; }
    }

    public static bool Bound(FootballView? view) => view is { Context: not null, Dataset: { Manifest: { Sources: not null, Capabilities: not null },
        Stream: not null, Fixture: { Home: not null, Away: not null, Periods: not null }, Schedule: { Segments: not null } } }
        && MatchWorkflowExecutor.IsValidSnapshot(new("replay-story", 0, new(view.Context, view.Dataset.HeadSequence), MatchWorkflowTemporalMode.ReplayAtCursor))
        && view.Context.DatasetId == view.Dataset.Manifest.DatasetId && view.Context.DatasetVersion == view.Dataset.Manifest.DatasetVersion
        && view.Context.FixtureId == view.Dataset.Manifest.FixtureId && view.Context.StreamId == view.Dataset.Stream.StreamId
        && view.Context.DatasetId == view.Dataset.Stream.DatasetId && view.Context.DatasetVersion == view.Dataset.Stream.DatasetVersion
        && view.Context.FixtureId == view.Dataset.Stream.FixtureId && view.Dataset.Stream.DeliveryMode == "replay"
        && view.Context.PresentationPolicyId == view.Dataset.Schedule.PolicyId && view.Context.PresentationPolicyRevision == view.Dataset.Schedule.PolicyRevision
        && view.Dataset.Closed && view.Dataset.PendingRecords == 0 && (view.Context.ApplicationSequence is null || view.Context.ApplicationSequence == view.Dataset.HeadSequence)
        && view.Context.KnowledgeAsOfUtc is null && view.Context.EffectiveDate is null
        && view.Dataset.Manifest.Sources.All(source => source is not null)
        && view.Dataset.Fixture.Periods.All(period => period is not null) && view.Dataset.Schedule.Segments.All(segment => segment is not null)
        && view.Dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1)
        && new[] { view.Dataset.Fixture.Home, view.Dataset.Fixture.Away }.All(t => DatasetIdentity.IsValid(t.Id)
            && !string.IsNullOrWhiteSpace(t.Name) && t.Name.Length <= 180 && !t.Name.Any(c => char.IsControl(c) || c is '<' or '>'))
        && view.Dataset.Fixture.Home.Id != view.Dataset.Fixture.Away.Id;

    private static bool Valid(FootballStoryPassage p, FootballView view, HashSet<string> members) => ValidLocator(Locator(p))
        && p.StartCursorMs >= 0 && p.EndCursorMs <= view.Context.CursorMs && p.DurationMs is > 0 and <= FootballStoryProjector.MaxDurationMs
        && view.Dataset.Schedule!.Segments.Count(s => s.PeriodId == p.PeriodId && FootballPresentationPolicy.IsPlay(s)
            && p.StartCursorMs >= s.CursorStartMs && p.EndCursorMs <= s.CursorEndMs) == 1
        && view.Dataset.Fixture!.Periods.Count(period => period.Id == p.PeriodId) == 1
        && (p.TeamId == view.Dataset.Fixture.Home.Id || p.TeamId == view.Dataset.Fixture.Away.Id)
        && p.PassAttempts is >= 0 and <= FootballStoryProjector.MaxActions && p.PassCompleted >= 0 && p.PassCompleted <= p.PassAttempts
        && p.Turnovers is >= 0 and <= 1 && p.Shots is >= 0 and <= 1 && p.PassAttempts + p.Shots <= FootballStoryProjector.MaxActions
        && p.Ending is "turnover" or "shot" or "period_end" or "control_end"
        && (p.Ending == "turnover") == (p.Turnovers == 1) && (p.Ending == "shot") == (p.Shots == 1)
        && p.Evidence is { Length: > 0 and <= FootballStoryProjector.MaxActions + 1 }
        && p.Evidence.All(e => e is not null && DatasetIdentity.IsValid(e.RecordId) && members.Contains(e.RecordId)
            && e.Revision > 0 && Hash(e.Fingerprint) && e.EvidenceIds is { Length: > 0 and <= 32 }
            && e.EvidenceIds.All(DatasetIdentity.IsValid) && e.EvidenceIds.Distinct(StringComparer.Ordinal).Count() == e.EvidenceIds.Length)
        && p.Evidence.Select(e => e.RecordId).Distinct(StringComparer.Ordinal).Count() == p.Evidence.Length;

    public static MatchSelectedPassage Locator(FootballStoryPassage p) => new(p.Id, p.Fingerprint, p.StartCursorMs, p.EndCursorMs);
    public static bool ValidLocator(MatchSelectedPassage p) => !string.IsNullOrWhiteSpace(p.Id) && p.Id.Length <= 180
        && p.Id.StartsWith("passage:", StringComparison.Ordinal) && DatasetIdentity.IsValid(p.Id[8..]) && Hash(p.Fingerprint)
        && p.StartCursorMs >= 0 && p.EndCursorMs > p.StartCursorMs && p.EndCursorMs - p.StartCursorMs <= FootballStoryProjector.MaxDurationMs;
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    public static ReplayStoryItem? Find(FootballView? view, MatchSelectedPassage locator, MatchLanguage language = MatchLanguage.English)
        => Project(view, language)?.SingleOrDefault(item => Locator(item.Passage) == locator);
    public static bool Matches(ReplayStoryAction action, FootballView? view) => view is not null && action.Query == view.Context
        && action.HeadSequence == view.Dataset.HeadSequence && Find(view, action.Passage) is not null;
    public static string Summary(FootballStoryPassage p, MatchLanguage language) => ReplayText.Get(language, p.PassCompleted == 1 ? "StorySummaryOne" : "StorySummary", p.PassCompleted, Ending(p, language));
    public static string Ending(FootballStoryPassage p, MatchLanguage language) => ReplayText.Get(language, p.Ending switch
    { "turnover" => "StoryTurnover", "shot" => "StoryShot", "period_end" => "StoryPeriodEnd", _ => "StoryControlEnd" });
    public static string Question(ReplayStoryItem item, MatchLanguage language) => ReplayText.Get(language, "StoryQuestion", item.Team, item.Interval);
    public static string Answer(ReplayStoryItem item, MatchLanguage language, string? question = null) => UnsupportedKey(question) is { } unsupported
        ? ReplayText.Get(language, unsupported) : ReplayText.Get(language, "StoryAnswer", item.Team, item.Interval,
        item.Passage.PassCompleted, item.Passage.PassAttempts, item.Passage.Shots, item.Passage.DurationMs / 1000m, Ending(item.Passage, language))
        + " " + ReplayText.Get(language, "StoryAnswerLimit");
    private static string? UnsupportedKey(string? question)
    {
        if (question is null) return null;
        bool Match(string pattern) => Regex.IsMatch(question, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (Match(@"\b(why|tactics?|formation|varför|taktik|taktiken|táctica|tácticas)\b|por qué|how did|hur kunde|cómo ocurrió")) return "StoryUnsupportedTactics";
        if (Match(@"\b(speed|distance|sprints?|hastighet|sträcka|sträckan|distans|velocidad|distancia)\b")) return "StoryUnsupportedMovement";
        if (Match(@"\b(rating|ratings|performance|betyg|spelarbetyg|prestation|valoración|rendimiento)\b")) return "StoryUnsupportedRating";
        if (Match(@"\b(actual|xg|verklig|riktig|verkliga)\b|real.match|partido real")) return "StoryUnsupportedActual";
        return null;
    }
}
