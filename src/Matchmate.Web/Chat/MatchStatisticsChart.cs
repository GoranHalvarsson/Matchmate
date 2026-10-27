using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public enum MatchStatisticsChartMetric { PassAttempts, PassAccuracy, Shots, Goals, YellowCards, RedCards, Substitutions }
public enum MatchStatisticsChartEvidenceKind { GeneratedReplay, ReportedEvents }

public sealed record MatchStatisticsChartTeam(string TeamId, string Name, int? PassAttempts, int? PassCompleted, decimal? PassAccuracy, int? Shots,
    int? Goals = null, int? YellowCards = null, int? RedCards = null, int? Substitutions = null);

// Immutable values captured from the validated, fresh comparison/statistics workflow, never narrator output.
public sealed class MatchStatisticsChartSnapshot
{
    public MatchChatContext Context { get; }
    public string ReplayClock { get; }
    public ImmutableArray<MatchStatisticsChartTeam> Teams { get; }
    public MatchStatisticsChartMetric InitialMetric { get; }
    public bool InitiallyExpanded { get; }
    public bool CanChooseMetric { get; }
    public MatchStatisticsChartEvidenceKind EvidenceKind { get; }
    public ImmutableArray<MatchStatisticsChartMetric> AvailableMetrics { get; }

    private MatchStatisticsChartSnapshot(MatchChatContext context, string clock, ImmutableArray<MatchStatisticsChartTeam> teams,
        MatchStatisticsChartMetric initialMetric, bool initiallyExpanded, bool canChooseMetric,
        MatchStatisticsChartEvidenceKind evidenceKind, ImmutableArray<MatchStatisticsChartMetric> availableMetrics)
        => (Context, ReplayClock, Teams, InitialMetric, InitiallyExpanded, CanChooseMetric, EvidenceKind, AvailableMetrics)
            = (context, clock, teams, initialMetric, initiallyExpanded, canChooseMetric, evidenceKind, availableMetrics);

    internal static MatchStatisticsChartSnapshot? Capture(MatchWorkflowBundle bundle, string question, MatchLanguage language, bool expanded = false)
    {
        if (bundle.WorkflowId == MatchSourceQuestions.CompareTeamsWorkflow)
        {
            try { return CaptureReported(bundle, expanded); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { throw new InvalidDataException("The reported chart evidence is invalid.", exception); }
        }
        if (bundle.WorkflowId != "current_replay_statistics") return null;
        var item = bundle.Items.SingleOrDefault(item => item.Id == "illustration_statistics" && item.Tool == "illustration_statistics");
        if (item is null) return null;
        using var document = JsonDocument.Parse(item.Text);
        var data = document.RootElement;
        if (data.GetProperty("scope").GetString() != "generated_replay_statistics"
            || data.GetProperty("replayClock").GetString() != ExperiencePresentation.Clock(bundle.Context.Query.CursorMs))
            throw new InvalidDataException("The chart must use the accepted replay statistics clock.");
        var selection = MatchStatisticsPresentation.Select(data, question, language);
        if (!selection.ChartEligible) return null;
        var teams = data.GetProperty("teams").EnumerateArray().Select(team => new MatchStatisticsChartTeam(
            team.GetProperty("teamId").GetString()!, team.GetProperty("name").GetString()!,
            team.GetProperty("passAttempts").GetInt32(), team.GetProperty("passCompleted").GetInt32(),
            team.GetProperty("passCompletionPercent").ValueKind == JsonValueKind.Null ? null : team.GetProperty("passCompletionPercent").GetDecimal(),
            team.GetProperty("shots").GetInt32())).ToImmutableArray();
        if (teams.Length != 2 || teams.Any(team => team.PassAttempts < 0 || team.PassCompleted < 0 || team.PassCompleted > team.PassAttempts || team.Shots < 0
            || (team.PassAttempts == 0 ? team.PassAccuracy is not null : team.PassAccuracy != 100m * team.PassCompleted / team.PassAttempts)))
            throw new InvalidDataException("The chart statistics must retain their validated counts and denominator.");
        var metric = selection.Metric switch
        {
            MatchStatisticsPresentation.Metric.Shots => MatchStatisticsChartMetric.Shots,
            MatchStatisticsPresentation.Metric.Rate => MatchStatisticsChartMetric.PassAccuracy,
            _ => MatchStatisticsChartMetric.PassAttempts
        };
        return new(bundle.Context, data.GetProperty("replayClock").GetString()!, teams, metric, expanded || selection.ChartRequested,
            selection.Metric == MatchStatisticsPresentation.Metric.Overview, MatchStatisticsChartEvidenceKind.GeneratedReplay,
            [MatchStatisticsChartMetric.PassAttempts, MatchStatisticsChartMetric.PassAccuracy, MatchStatisticsChartMetric.Shots]);
    }

    private static MatchStatisticsChartSnapshot? CaptureReported(MatchWorkflowBundle bundle, bool expanded)
    {
        if (bundle.Version != MatchWorkflowRegistry.Version || bundle.TemporalMode != MatchWorkflowTemporalMode.ReplayAtCursor
            || bundle.DeliveryMode != "replay" || bundle.Context.SelectedEvent is not null
            || !MatchWorkflowExecutor.IsValidSnapshot(new("chart", 0, bundle.Context, bundle.TemporalMode)))
            throw new InvalidDataException("A reported chart requires the accepted current replay context.");
        var item = bundle.Items.SingleOrDefault(item => item.Id == "reported_comparison" && item.Tool == "reported_comparison");
        if (item is null) return null;
        using var document = JsonDocument.Parse(item.Text);
        var data = document.RootElement;
        if (data.GetProperty("scope").GetString() != "reported_events_so_far"
            || data.GetProperty("replayClock").GetString() != ExperiencePresentation.Clock(bundle.Context.Query.CursorMs))
            throw new InvalidDataException("The chart must use the accepted reported comparison clock and scope.");
        if (!data.GetProperty("reportsSupplied").GetBoolean()) return null;
        var reportCount = data.GetProperty("eligibleReportCount").GetInt32();
        var teams = data.GetProperty("teams").EnumerateArray().Select(team => new MatchStatisticsChartTeam(
            team.GetProperty("teamId").GetString()!, team.GetProperty("name").GetString()!, null, null, null, null,
            team.GetProperty("goals").GetInt32(), team.GetProperty("yellowCards").GetInt32(),
            team.GetProperty("redCards").GetInt32(), team.GetProperty("substitutions").GetInt32())).ToImmutableArray();
        if (reportCount < 0 || teams.Length != 2 || teams.Select(team => team.TeamId).Distinct(StringComparer.Ordinal).Count() != 2
            || teams.Any(team => string.IsNullOrWhiteSpace(team.TeamId) || string.IsNullOrWhiteSpace(team.Name)
                || team.Goals < 0 || team.YellowCards < 0 || team.RedCards < 0 || team.Substitutions < 0)
            || teams.Sum(team => (long)team.Goals!.Value + team.YellowCards!.Value + team.RedCards!.Value + team.Substitutions!.Value) > reportCount)
            throw new InvalidDataException("The chart must retain both teams' validated reported counts.");
        return new(bundle.Context, data.GetProperty("replayClock").GetString()!, teams, MatchStatisticsChartMetric.Goals, expanded, true,
            MatchStatisticsChartEvidenceKind.ReportedEvents,
            [MatchStatisticsChartMetric.Goals, MatchStatisticsChartMetric.YellowCards, MatchStatisticsChartMetric.RedCards, MatchStatisticsChartMetric.Substitutions]);
    }

    public decimal? Value(MatchStatisticsChartTeam team, MatchStatisticsChartMetric metric)
    {
        if (!AvailableMetrics.Contains(metric)) throw new ArgumentOutOfRangeException(nameof(metric), "The chart does not supply this metric.");
        return metric switch
        {
            MatchStatisticsChartMetric.PassAttempts => team.PassAttempts,
            MatchStatisticsChartMetric.PassAccuracy => team.PassAccuracy,
            MatchStatisticsChartMetric.Shots => team.Shots,
            MatchStatisticsChartMetric.Goals => team.Goals,
            MatchStatisticsChartMetric.YellowCards => team.YellowCards,
            MatchStatisticsChartMetric.RedCards => team.RedCards,
            MatchStatisticsChartMetric.Substitutions => team.Substitutions,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    public decimal Maximum(MatchStatisticsChartMetric metric) => !AvailableMetrics.Contains(metric) ? throw new ArgumentOutOfRangeException(nameof(metric))
        : metric == MatchStatisticsChartMetric.PassAccuracy ? 100m
        : Math.Max(1m, Teams.Max(team => Value(team, metric) ?? 0m));

    public string BarWidth(MatchStatisticsChartTeam team, MatchStatisticsChartMetric metric)
        => (100m * (Value(team, metric) ?? 0m) / Maximum(metric)).ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
