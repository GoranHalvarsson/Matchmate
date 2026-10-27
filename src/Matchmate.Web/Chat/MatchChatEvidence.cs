using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;

namespace Matchmate.Web.Chat;

public static class MatchChatTools
{
    public static readonly string[] Names = ["match_summary", "current_events", "current_player", "coverage"];
    public const string ArgumentsSchema = """{"type":"object","properties":{},"additionalProperties":false}""";
    public static string Description(string name) => name switch
    {
        "match_summary" => "Current eligible reported goal/event counts and bound replay context; not an independently verified scoreboard.",
        "current_events" => "Only events already eligible at the bound cursor; no future index or schedule.",
        "selected_goal" => "Only the explicitly selected retrospective goal; its release may be after the current replay moment.",
        "selected_event" => "Only the explicitly selected event already eligible at the replay cursor; completed possession is not possession now.",
        "moment_context" => "Bounded current ball/player coordinates and sample quality, plus completed possession intervals; no inferred movement or current possession.",
        "current_player" => "Only the app-selected player's current position/quality, or an honest missing/selection result.",
        "illustration_statistics" => "Code-calculated team totals for this generated illustration at the replay cursor, with pinned synthetic/generated provenance; not actual match statistics.",
        "reported_comparison" => "Code-counted source-backed reported goals, cards and substitutions so far at the bound replay cursor; not complete actual-match statistics.",
        _ => "Origin, coverage, gaps and unsupported metrics for this bound context."
    };
    public static void Validate(MatchModelToolCall call)
    {
        if (!Names.Contains(call.Name) || call.Arguments.Length > MatchChatLimits.ArgumentChars
            || !Regex.IsMatch(call.Id, "^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Unsupported tool call.");
        using var args = JsonDocument.Parse(call.Arguments, new() { MaxDepth = 4 });
        if (args.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.EnumerateObject().Any())
            throw new InvalidDataException("Tool context cannot be supplied or overridden by the model.");
    }
}

public sealed class MatchEvidenceProjection(FootballView view, bool selectedGoalScope = false, bool compactEventSources = false)
{
    // Only workflows with a separate full-provenance envelope use this bounded narration selection.
    public static FootballEvidence[] WorkflowEventEvidence(FootballRecord record)
    {
        var bounded = record.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).ToArray();
        if (record.Payload is not MatchEventPayload { Sequence.Steps: { Length: > 0 } steps } payload
            || steps.Any(s => s is null || s.EvidenceIds is null) || record.FieldEvidence is null || record.FieldEvidence.Values.Any(ids => ids is null)
            || payload.Details?.Any(d => d is null || d.EvidenceIds is null) == true || record.Conflicts?.Any(c => c is null || c.EvidenceIds is null) == true) return bounded;
        var references = steps.SelectMany(step => step.EvidenceIds).Concat(payload.Details?.SelectMany(d => d.EvidenceIds) ?? [])
            .Concat(record.Conflicts?.SelectMany(c => c.EvidenceIds) ?? []).ToHashSet(StringComparer.Ordinal);
        var presentation = new HashSet<string>(StringComparer.Ordinal) { "/payload/sequence/presentationLeadInMs", "/payload/sequence/presentationTailMs" };
        for (var index = 0; index < steps.Length; index++) presentation.Add($"/payload/sequence/steps/{index}/presentationOffsetMs");
        return bounded.Where(e => {
            if (e.Origin != "synthetic" || e.Method != "generated" || e.ClaimStatus != "authored" || references.Contains(e.EvidenceId)) return true;
            var fields = record.FieldEvidence.Where(field => field.Value.Contains(e.EvidenceId)).Select(field => field.Key).ToArray();
            return fields.Length == 0 || fields.Any(field => !presentation.Contains(field));
        }).ToArray();
    }

    public MatchChatEvidence SelectedEvent(FootballRecord record, MatchSelectedEvent selection)
    {
        var sequence = record.Payload is PossessionPayload ? MatchSequenceProjector.Possession(view.Dataset, record, view.Context.CursorMs)
            : MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs);
        if (sequence is null || sequence.ReleaseCursorMs != selection.ReleaseCursorMs) throw new InvalidDataException("No eligible selected sequence.");
        var text = Serialize(new { scope = "selected_eligible_event", record = record.RecordId, revision = record.Revision, kind = selection.Kind,
            reportedClock = selection.ReportedClock, replayRelease = ExperiencePresentation.Clock(sequence.ReleaseCursorMs),
            intervalStart = sequence.IntervalStartMs is { } start ? ExperiencePresentation.IntervalTime(start) : null,
            intervalEnd = sequence.IntervalEndMs is { } end ? ExperiencePresentation.IntervalTime(end) : null,
            details = (record.Payload as MatchEventPayload)?.Details,
            sequence = new { id = sequence.Sequence.Id, version = sequence.Sequence.Version, steps = sequence.Sequence.Steps.Select(step => new {
                step.Id, step.Kind, actor = step.ActorId is null ? null : ExperiencePresentation.Person(view.Dataset, step.ActorId),
                relatedActor = step.RelatedActorId is null ? null : ExperiencePresentation.Person(view.Dataset, step.RelatedActorId), step.Text, step.Uncertainty, step.EvidenceIds }) },
            evidence = (compactEventSources ? WorkflowEventEvidence(record) : record.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent)).Select(e => new { e.EvidenceId, e.SourceId, e.Origin, e.Method, e.ClaimStatus }),
            limitation = selection.Kind == "completed_possession" ? "Completed historical interval only, not possession now; unmatched endpoints remain uncertain."
                : "Reported event only. Same-release substitutions have no known within-group order or running paths. Booking reasons do not establish tackle geometry or location." });
        return new("selected_event", "selected_event", text, MatchReplyValidation.Numbers(text).Distinct(StringComparer.Ordinal).ToArray());
    }
    public MatchChatEvidence SelectedGoal(FootballRecord record, MatchSelectedEvent selection)
    {
        // Reuse the existing bounded event projection, never the selected-release view's score or positions.
        var item = new MatchEvidenceProjection(view with { Events = [record] }, true, compactEventSources).Get("current_events");
        using var data = JsonDocument.Parse(item.Text);
        var replayClock = ExperiencePresentation.Clock(selection.ReleaseCursorMs);
        var currentClock = ExperiencePresentation.Clock(view.Context.CursorMs);
        var goal = (MatchEventPayload)record.Payload;
        var team = new[] { view.Dataset.Fixture!.Home, view.Dataset.Fixture.Away }.FirstOrDefault(t => t.Id == goal.TeamId);
        var text = Serialize(new { scope = "retrospective_selected_goal", replayRelease = replayClock, currentReplay = currentClock,
            alreadyEligibleAtCurrentCursor = view.Events.Any(e => e.RecordId == record.RecordId),
            team = Short(team?.Name), selectedEvent = data.RootElement.GetProperty("shown")[0],
            limitation = "Selected goal only, not current score. Distinguish reported mechanism from labelled interpretation; generated positions do not prove defensive fault." });
        return new("selected_goal", "selected_goal", text, item.NumericTokens.Concat(MatchReplyValidation.Numbers(replayClock))
            .Concat(MatchReplyValidation.Numbers(currentClock)).Distinct(StringComparer.Ordinal).ToArray());
    }
    public string? SelectedName => view.Dataset.People.FirstOrDefault(p => p.PersonId == view.Context.SubjectId)?.Name;
    public string FallbackText(string reason)
    {
        var fixture = view.Dataset.Fixture!;
        var score = ExperiencePresentation.SupportsEventReports(view.Dataset)
            ? $"Eligible reported goals: {fixture.Home.Name} {ExperiencePresentation.ReportedGoals(view, fixture.Home.Id)}, {fixture.Away.Name} {ExperiencePresentation.ReportedGoals(view, fixture.Away.Id)}. Disallowed goals are excluded; this is not an independently verified scoreboard."
            : "This recording supplies no goal feed or match score.";
        return $"Evidence only. {reason} At {Short(ExperiencePresentation.PeriodLabel(view))} {ExperiencePresentation.Clock(ExperiencePresentation.MatchClock(view))}: {score} {ExperiencePresentation.SourceLabel(view.Dataset)} {Short(view.CoverageMessage)}";
    }
    private static string Short(string? value) => (value ?? "unknown")[..Math.Min((value ?? "unknown").Length, 180)];
    private static string Serialize(object value, bool omitNull = false) => JsonSerializer.Serialize(value, new JsonSerializerOptions {
        DefaultIgnoreCondition = omitNull ? System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull : System.Text.Json.Serialization.JsonIgnoreCondition.Never });
    public MatchChatEvidence Get(string tool, int eventLimit = MatchWorkflowLimits.Events, bool recentEvents = false, bool includeReportSummary = false)
    {
        var numbers = new List<string>();
        void Numeric(object value) => numbers.AddRange(MatchReplyValidation.Numbers(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        object data;
        var fixture = view.Dataset.Fixture!;
        switch (tool)
        {
            case "illustration_statistics": return MatchIllustrationStatistics.Build(view);
            case "reported_comparison": return MatchReportedEventSummary.Build(view);
            case "match_summary":
                var clock = ExperiencePresentation.Clock(ExperiencePresentation.MatchClock(view)); Numeric(clock); Numeric(view.Events.Length);
                int? home = null, away = null;
                if (ExperiencePresentation.SupportsEventReports(view.Dataset)) { home = ExperiencePresentation.ReportedGoals(view, fixture.Home.Id); away = ExperiencePresentation.ReportedGoals(view, fixture.Away.Id); Numeric(home.Value); Numeric(away.Value); }
                var summary = new Dictionary<string, object?> { ["fixture"] = Short(fixture.Label), ["home"] = Short(fixture.Home.Name), ["away"] = Short(fixture.Away.Name),
                    ["period"] = Short(ExperiencePresentation.PeriodLabel(view)), ["clock"] = clock, ["eligibleReportedEvents"] = view.Events.Length,
                    ["homeReportedGoals"] = home, ["awayReportedGoals"] = away,
                    ["scoreMeaning"] = home is null ? "No goal feed or match score supplied." : "Eligible reported goals only; explicitly disallowed excluded. Not a separately verified scoreboard." };
                if (includeReportSummary)
                {
                    var reportedSummary = MatchReportedEventSummary.Build(view); numbers.AddRange(reportedSummary.NumericTokens);
                    using var reportDocument = JsonDocument.Parse(reportedSummary.Text);
                    summary["reportedEvents"] = reportDocument.RootElement.Clone();
                }
                data = summary;
                break;
            case "current_events":
                Numeric(view.Events.Length);
                var events = (recentEvents ? view.Events.TakeLast(eventLimit) : view.Events.Take(eventLimit)).Select(record =>
                {
                    var e = (MatchEventPayload)record.Payload;
                    var semantic = MatchSequenceProjector.Event(view.Dataset, record, view.Context.CursorMs, selectedGoalScope);
                    Numeric(record.FootballTime?.ReportedClock ?? "");
                    foreach (var detail in e.Details ?? []) { Numeric(detail.Text); Numeric(detail.Uncertainty ?? ""); }
                    foreach (var step in semantic?.Sequence.Steps ?? []) { Numeric(step.Text); Numeric(step.Uncertainty ?? ""); }
                    var provenance = (compactEventSources ? WorkflowEventEvidence(record) : record.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent))
                        .Select(source => new { id = Short(source.EvidenceId), source = Short(source.SourceId), origin = Short(source.Origin), method = Short(source.Method), status = Short(source.ClaimStatus) });
                    // V4 has more semantic steps. Origin/status already travel under the same IDs in bundle.EventProvenance.
                    object boundedSources = compactEventSources && semantic is not null
                        ? provenance.Select(source => new { source.id, source.source, source.method }) : provenance;
                    var sharedStepUncertainty = semantic?.Sequence.Steps is { Length: > 1 } steps
                        && !string.IsNullOrWhiteSpace(steps[0].Uncertainty)
                        && steps.All(step => string.Equals(step.Uncertainty, steps[0].Uncertainty, StringComparison.Ordinal))
                            ? steps[0].Uncertainty : null;
                    if (semantic is not null) return (object)new { record = Short(record.RecordId), revision = record.Revision, kind = e.EventKind,
                        decision = e.Decision, team = Short(new[] { fixture.Home, fixture.Away }.FirstOrDefault(team => team.Id == e.TeamId)?.Name), reportedClock = record.FootballTime?.ReportedClock,
                        attributedDetails = selectedGoalScope ? e.Details : null,
                        sequence = new { id = semantic.Sequence.Id, sharedStepUncertainty, steps = semantic.Sequence.Steps.Select(step => new { id = step.Id, kind = step.Kind,
                            actor = step.ActorId is null ? null : Short(ExperiencePresentation.Person(view.Dataset, step.ActorId)),
                            relatedActor = step.RelatedActorId is null ? null : Short(ExperiencePresentation.Person(view.Dataset, step.RelatedActorId)),
                            text = step.Kind == "substitution" ? null : step.Text, evidenceIds = step.EvidenceIds,
                            uncertainty = step.Kind is "substitution" or "booking" || sharedStepUncertainty is not null ? null : step.Uncertainty }) }, evidence = boundedSources };
                    return new { record = Short(record.RecordId), revision = record.Revision, kind = Short(e.EventKind), decision = Short(e.Decision),
                        player = Short(ExperiencePresentation.Person(view.Dataset, e.PersonId ?? e.IncomingPersonId)), outgoing = e.OutgoingPersonId is null ? null : Short(ExperiencePresentation.Person(view.Dataset, e.OutgoingPersonId)),
                        assist = e.AssistPersonId is null ? null : Short(ExperiencePresentation.Person(view.Dataset, e.AssistPersonId)),
                        team = Short(new[] { fixture.Home, fixture.Away }.FirstOrDefault(team => team.Id == e.TeamId)?.Name),
                        reportedClock = Short(record.FootballTime?.ReportedClock), precision = Short(record.FootballTime?.Precision),
                        details = e.Details,
                        // Full source locators remain in canonical records; bounded model evidence joins record/evidence/source IDs.
                        evidence = provenance };
                }).ToArray();
                var omitted = view.Events.Length - events.Length; Numeric(omitted);
                data = new { eligibleTotal = view.Events.Length, shown = events, omitted,
                    sequenceMeaning = view.Dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1") ? "Sequence v1 replaces duplicate detail prose. Semantic order is evidence-backed; geometry/timing are illustrative. Reported minutes are not exact contacts. Substitutions at one reported minute share a release, with no within-group order/running paths. Booking reasons do not reconstruct a tackle or referee. Full attributed details remain in canonical records." : null,
                    limit = recentEvents ? "Recent eligible reports only; eligibleTotal retains the full current count. No future index or schedule." : "At most twenty already-eligible events; no future schedule or highlight index." };
                break;
            case "moment_context":
                object Observation(FootballObservation? observation)
                {
                    string? x = null, y = null;
                    if (observation?.Availability == "present") { x = observation.XM?.ToString("F2", CultureInfo.InvariantCulture); y = observation.YM?.ToString("F2", CultureInfo.InvariantCulture); Numeric(x ?? ""); Numeric(y ?? ""); }
                    return new { availability = Short(observation?.Availability), method = Short(observation?.Method), quality = Short(observation?.Quality), xMetres = x, yMetres = y };
                }
                var ball = view.Frame?.Ball;
                var positions = (view.Frame?.Players ?? []).OrderBy(p => p.SubjectId == view.Context.SubjectId ? 0 : 1)
                    .ThenBy(p => ball?.Availability == "present" && p.Availability == "present" ? Math.Pow(p.XM!.Value - ball.XM!.Value, 2) + Math.Pow(p.YM!.Value - ball.YM!.Value, 2) : double.MaxValue)
                    .ThenBy(p => p.SubjectId, StringComparer.Ordinal).Take(MatchWorkflowLimits.TrackingPlayers)
                    .Select(p => new { player = Short(ExperiencePresentation.Person(view.Dataset, p.SubjectId)), sample = Observation(p) }).ToArray();
                var completed = view.Possessions.OrderByDescending(r => ((PossessionPayload)r.Payload).EligibleAfterMs).ThenBy(r => r.RecordId, StringComparer.Ordinal)
                    .Take(MatchWorkflowLimits.Possessions).Select(record => {
                        var possession = (PossessionPayload)record.Payload;
                        var semantic = MatchSequenceProjector.Possession(view.Dataset, record, view.Context.CursorMs);
                        var start = record.FootballTime?.StartMs is { } from ? ExperiencePresentation.Clock(from) : "unknown";
                        var end = record.FootballTime?.EndMs is { } to ? ExperiencePresentation.Clock(to) : "unknown"; Numeric(start); Numeric(end);
                        return new { player = Short(ExperiencePresentation.Person(view.Dataset, possession.PersonId)), start, end, possession.StartMatched, possession.EndMatched,
                            sequence = semantic is null ? null : new { id = semantic.Sequence.Id, version = semantic.Sequence.Version,
                                steps = semantic.Sequence.Steps.Select(step => new { id = step.Id, kind = step.Kind, actor = Short(ExperiencePresentation.Person(view.Dataset, step.ActorId)), evidenceIds = step.EvidenceIds, uncertainty = step.Uncertainty }) },
                            evidence = record.Evidence.Take(MatchWorkflowLimits.EvidencePerEvent).Select(e => new { source = Short(e.SourceId), method = Short(e.Method), origin = Short(e.Origin), status = Short(e.ClaimStatus) }) }; }).ToArray();
                Numeric(view.Frame?.Players.Length ?? 0); Numeric(view.Possessions.Length);
                data = new { scope = "current_cursor_only", trackingGap = view.TrackingGap, ball = Observation(ball), players = positions,
                    availablePlayerSamples = view.Frame?.Players.Length ?? 0, playerSelection = "Selected player first, then nearest available coordinates to the ball; bounded geometric sample, not measured pressure or marking.",
                    completedPossessions = completed, eligibleCompletedPossessions = view.Possessions.Length,
                    limitation = "Current sample quality is retained. Completed possession intervals do not establish possession now. Generated coordinates are illustration, not observed movement, speed or intent." };
                break;
            case "current_player":
                var person = view.Dataset.People.FirstOrDefault(p => p.PersonId == view.Context.SubjectId);
                if (person?.Shirt is { } shirt) Numeric(shirt);
                var position = view.Frame?.Players.FirstOrDefault(p => p.SubjectId == person?.PersonId);
                string? x = null, y = null;
                if (position?.Availability == "present") { x = position.XM?.ToString("F2", CultureInfo.InvariantCulture); y = position.YM?.ToString("F2", CultureInfo.InvariantCulture); Numeric(x ?? ""); Numeric(y ?? ""); }
                data = new { selectedPlayer = person is null ? "No player selected; ask the fan to select one." : Short(person.Name),
                    shirt = person?.Shirt,
                    activeInSample = person is not null && view.Frame?.ActiveSubjectIds.Contains(person.PersonId) == true,
                    availability = Short(position?.Availability), method = Short(position?.Method), quality = Short(position?.Quality), xMetres = x, yMetres = y,
                    limitation = "A current coordinate is not measured movement, speed, distance or tactical intent. No missing position is carried forward." };
                break;
            case "coverage":
                data = new { origin = ExperiencePresentation.SourceLabel(view.Dataset), gap = view.TrackingGap, coverage = Short(view.CoverageMessage),
                    ballAvailability = Short(view.Frame?.Ball.Availability), ballMethod = Short(view.Frame?.Ball.Method),
                    unsupported = "Actual/measured pass counts, possession percentages, speed, distance, xG and formations are unavailable. Separately labelled generated replay team totals are supplied only by the dedicated replay-statistics workflow when available. Label football interpretation; do not invent observations or intent.",
                    narrative = view.Dataset.Manifest.Capabilities.Contains("unaligned-commentary-not-exposed") ? "Source-media commentary passages are not continuously aligned to authored replay; unaligned narrative is unavailable here. Only explicitly associated event details are supplied." : "No additional aligned narrative is supplied." };
                break;
            default: throw new InvalidDataException("Unsupported evidence tool.");
        }
        var text = Serialize(data, view.Dataset.Manifest.Capabilities.Contains("shared-event-sequences-v1"));
        if (text.Length > MatchChatLimits.EvidenceChars) throw new InvalidDataException("Evidence budget exceeded.");
        return new(tool, tool, text, numbers.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public static class MatchReplyValidation
{
    private static readonly Regex Numeric = new(@"(?<![\p{L}\d])[-+]?\d+(?:[.,]\d+)?|\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
    private const RegexOptions PolicyOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Metric = @"(?:speeds?|sprints?|sprinted|sprinting|xg|expected[\s-]+goals|distance|pass[\s-]+(?:counts?|totals?|accuracy|completion|percentage)|possession[\s-]+(?:percentage|percent|share)|km/h|mph|m/s)";
    private const string MetricAtom = @"(?:the\s+)?(?:(?:player(?:'s|s')?|team(?:'s)?|running|measured|exact|reliable|individual|top|total)\s+){0,3}" + Metric + @"(?:\s+(?:data|measurements?|figures?|values?|metrics?|statistics|estimates?|readings?|information))?";
    private const string MetricList = MetricAtom + @"(?:\s*(?:,\s*(?:(?:and|or)\s+)?|\s+(?:and|or)\s+)" + MetricAtom + @"){0,7}";
    private const string Assessment = @"(?:measure|quantify|assess|determine|establish|calculate|infer)";
    private const string DataForAssessment = @"(?:(?:(?:enough|sufficient|tracking|player|reliable|measured)\s+){0,2}(?:data|figures|measurements|values)\s+(?:(?:for|of|on)|(?:(?:needed|required|necessary)\s+)?to\s+" + Assessment + @")\s+)?";
    private static readonly Regex UnavailableMetric = new(
        @"\b(?:no|without)\s+" + DataForAssessment + MetricList
        + @"|\b(?:cannot|can\s+not|can't|do\s+not|don't|does\s+not|doesn't|unable\s+to|not\s+possible\s+to)\s+(?:measure|quantify|assess|infer|calculate|establish|determine|derive|report|provide|supply|know|have|give|offer|contain|include|(?:tell|show)\s+us)\s+(?:a\s+|any\s+|the\s+)?" + DataForAssessment + MetricList
        + @"|\b" + MetricList + @"\s+(?:(?:is|are|was|were|remains?|remain)\s+)?(?:unavailable|unknown|unsupported|missing|not\s+(?:available|supplied|measured|recorded|provided|known|established|quantifiable)|(?:cannot|can't|can\s+not)\s+be\s+(?:measured|inferred|established|determined|quantified)|(?:isn't|aren't|wasn't|weren't)\s+(?:available|supplied|measured|recorded|provided|known))\b", PolicyOptions);
    private static readonly Regex UnsupportedMetric = new(@"\b" + Metric + @"\b", PolicyOptions);
    private const string Quantity = @"(?:\d+(?:[.,]\d+)?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred)";
    private const string DistanceUnit = @"(?:m|metres?|meters?|km|kilometres?|kilometers?|mi|miles?|yd|yards?|ft|feet|foot)";
    private const string DistanceAction = @"(?:pass(?:es)?|shots?|cross(?:es)?|runs?|sprints?|carr(?:y|ies)|headers?|finish(?:es)?|deliver(?:y|ies)|clearances?)";
    // These remain forbidden even beside a caveat or when the digits also occur in a score/clock.
    private static readonly Regex QuantifiedMetric = new(
        @"\b\d+(?:[.,]\d+)?\s*(?:%|percent\b|per\s+cent\b|km\s*/\s*h\b|kph\b|kmh\b|mph\b|m\s*/\s*s\b|kilomet(?:re|er)s?\s+per\s+hour\b|met(?:re|er)s?\s+per\s+second\b)"
        + @"|\b" + Quantity + @"(?:[\s-]+(?:completed|successful|short|forward|accurate)){0,3}[\s-]+pass(?:es)?\b"
        + @"|\b(?:pass\s+count|passes|possession|formation)\s*(?:(?:was|is|were|of|stood\s+at)\s*|[=:]\s*)?" + Quantity + @"\b"
        + @"|\b(?:[1-5][-–]){2,3}[1-5]\b"
        + @"|\b(?:covered|ran|run|travelled|traveled)\s+(?:for\s+)?" + Quantity + @"\s*" + DistanceUnit + @"\b"
        + @"|\b" + Quantity + @"[\s-]*" + DistanceUnit + @"[\s-]+(?:(?:long|short|forward|backward|square|headed)\s+){0,2}" + DistanceAction + @"\b"
        + @"|\b" + DistanceAction + @"\s+(?:was|is|were|are)\s+" + Quantity + @"[\s-]*" + DistanceUnit + @"\b"
        + @"|\b" + Quantity + @"[\s-]*(?:miles?|yards?|feet|foot|mi|yd|ft)\b", PolicyOptions);
    // A spelled field-region name is not a measured action distance. Keep compound/signed/decimal near-matches outside this exception.
    private static readonly Regex GoalAreaLandmark = new(@"(?<![\w+\-−–—.,])six-yard +(?:box|area)(?![\w\-−–—])", PolicyOptions);
    private static readonly Regex LandmarkQuantityPrefix = new(@"\b(?:" + Quantity + @"|plus|minus)\s+$", PolicyOptions);
    private static bool HasQuantifiedMeasurement(string text) => QuantifiedMetric.Matches(text).Any(match =>
    {
        if (!string.Equals(match.Value, "six-yard", StringComparison.OrdinalIgnoreCase)) return true;
        var landmark = GoalAreaLandmark.Match(text, match.Index);
        return !landmark.Success || landmark.Index != match.Index || LandmarkQuantityPrefix.IsMatch(text[..match.Index]);
    });
    private static readonly Regex MetricValuePointer = new(@"\b(?:it|that|this\s+figure|the\s+(?:value|figure|rate|reading|number|total))\s+(?:was|is|reached|equals?|stood\s+at)\s*\d", PolicyOptions);
    public static IEnumerable<string> Numbers(string value) => Numeric.Matches(value).Select(match => decimal.TryParse(match.Value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var number) ? number.ToString("G", CultureInfo.InvariantCulture) : match.Value);
    // Lexical guards catch unsupported measurements, not arbitrary fabricated football prose or attribution.
    public static (string Answer, MatchChatEvidence[] Evidence) Validate(string json, IReadOnlyDictionary<string, MatchChatEvidence> supplied,
        Action<MatchReplyRejection>? rejected = null)
    {
        InvalidDataException Reject(MatchReplyRejection reason, string message) { rejected?.Invoke(reason); return new(message); }
        if (json.Length > MatchChatLimits.ReplyChars) throw Reject(MatchReplyRejection.ReplyBudget, "Reply budget exceeded.");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new() { MaxDepth = 5 }); }
        catch (JsonException) { rejected?.Invoke(MatchReplyRejection.MalformedJson); throw; }
        using var doc = parsed; var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not "answer" and not "evidenceIds")
            || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("answer", out var answerValue) || answerValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("evidenceIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 4)
            throw Reject(MatchReplyRejection.InvalidStructure, "A structured, evidence-supported answer is required.");
        var answer = answerValue.GetString()!;
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > MatchChatLimits.AnswerChars || answer.Contains('<') || answer.Contains('>'))
            throw Reject(MatchReplyRejection.AnswerContent, "Unsupported answer content.");
        var policyText = answer.Replace('’', '\'');
        if (HasQuantifiedMeasurement(policyText) || UnsupportedMetric.IsMatch(UnavailableMetric.Replace(policyText, ""))
            || UnsupportedMetric.IsMatch(policyText) && MetricValuePointer.IsMatch(policyText))
            throw Reject(MatchReplyRejection.UnsupportedMeasurement, "Unsupported measurement claim.");
        var evidence = ids.EnumerateArray().Select(id => id.ValueKind == JsonValueKind.String && supplied.TryGetValue(id.GetString()!, out var item)
            ? item : throw Reject(MatchReplyRejection.UnknownEvidence, "Unknown evidence reference.")).DistinctBy(e => e.Id).ToArray();
        var allowed = evidence.SelectMany(e => e.NumericTokens).ToHashSet(StringComparer.Ordinal);
        if (Numbers(answer).Any(number => !allowed.Contains(number))) throw Reject(MatchReplyRejection.UnsupportedNumber, "Unsupported numeric claim.");
        return (answer, evidence);
    }
}
