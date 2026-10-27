using System.Globalization;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

// This dedicated tool consumes only the freshly bound shared projection, never coordinates/events or a selected-goal view.
internal static class MatchIllustrationStatistics
{
    internal const string SourceId = "generated-model-actions";
    internal static SourceReference Source(FootballView view)
    {
        var sources = view.Dataset.Manifest.Sources.Where(source => source.SourceId == SourceId).ToArray();
        if (sources.Length != 1 || sources[0].ArtifactHash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Generated action source must have one pinned artifact identity.");
        return sources[0];
    }
    internal static MatchChatEvidence Build(FootballView view)
    {
        var source = Source(view); var stats = view.IllustrationStatistics; var fixture = view.Dataset.Fixture!;
        var ids = new[] { fixture.Home.Id, fixture.Away.Id };
        if (!view.Dataset.Manifest.Capabilities.Contains(FootballActionPolicy.GeneratedModelActionsV1) || stats?.Teams is not { Length: 2 }
            || !ids.All(DatasetIdentity.IsValid) || ids.Distinct(StringComparer.Ordinal).Count() != 2
            || stats.Teams.Any(team => team is null || !ids.Contains(team.TeamId, StringComparer.Ordinal)
                || team.PassAttempts is < 0 or > 2000 || team.PassCompleted < 0 || team.PassCompleted > team.PassAttempts
                || team.Shots is < 0 or > 2000 || team.ControlledMs < 0)
            || stats.Teams.Select(team => team.TeamId).Distinct(StringComparer.Ordinal).Count() != 2
            || new[] { fixture.Home.Name, fixture.Away.Name }.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.Any(c => char.IsControl(c) || c is '<' or '>')))
            throw new InvalidDataException("Malformed generated illustration statistics.");
        long total;
        try { total = checked(stats.Teams[0].ControlledMs + stats.Teams[1].ControlledMs); }
        catch (OverflowException) { throw new InvalidDataException("Generated control total overflow."); }
        if (total != stats.TotalClassifiedControlMs || total > view.Context.CursorMs
            || stats.Teams.Any(team => total == 0 ? team.ControlSharePercent is not null
                : team.ControlSharePercent != 100m * team.ControlledMs / total))
            throw new InvalidDataException("Malformed generated control denominator/share.");
        var text = JsonSerializer.Serialize(new
        {
            scope = "generated_replay_statistics", replayClock = ExperiencePresentation.Clock(view.Context.CursorMs),
            teams = new[] { fixture.Home, fixture.Away }.Select(team =>
            {
                var own = stats.Teams.Single(row => row.TeamId == team.Id);
                return new { teamId = team.Id, name = team.Name, own.PassAttempts, own.PassCompleted, own.PassCompletionPercent, own.Shots,
                    own.ControlledMs, controlDuration = Duration(own.ControlledMs), illustratedControlSharePercent = own.ControlSharePercent };
            }), totalClassifiedControlMs = total, classifiedControlDuration = Duration(total),
            provenance = new { source.SourceId, source.ArtifactHash, origin = "synthetic", method = "generated", claimStatus = "authored" },
            limitation = "Generated illustration only, not actual match metrics. Completed actions/spells only; unfinished spells, holds, restarts and unknown time are excluded from classified control. No player totals or measured movement."
        }, FootballCodec.Json);
        if (text.Length > MatchWorkflowLimits.EvidenceChars) throw new InvalidDataException("Statistics evidence budget exceeded.");
        return new("illustration_statistics", "illustration_statistics", text, MatchReplyValidation.Numbers(text).Distinct(StringComparer.Ordinal).ToArray());
    }
    internal static string Format(JsonElement data, string? question = null) => MatchStatisticsPresentation.Format(data, question);
    internal static string FormatForAudience(JsonElement data, string? question, MatchAudienceMode mode) => MatchStatisticsPresentation.FormatForAudience(data, question, mode);
    internal static string FormatForLanguage(JsonElement data, string? question, MatchAudienceMode mode, MatchLanguage language) => MatchStatisticsPresentation.FormatForLanguage(data, question, mode, language);
    private static string Duration(long milliseconds) => $"{milliseconds / 60000}:{milliseconds / 1000 % 60:00}";
}
