using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Azure.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

// Offline contract tests capture the actual adapter wire. The probability map remains a provisional
// fixture, NOT a recorded Azure response or a measurement of Decision-1 quality.
public sealed class DecisionRoutingTests
{
    [Theory]
    [InlineData("current_replay_statistics", false)] [InlineData("current_replay_statistics", true)]
    [InlineData("current_match_events", false)] [InlineData("current_match_events", true)]
    [InlineData("current_match_moment", false)] [InlineData("current_match_moment", true)]
    public async Task F4_OnlySelectedStatisticsBypassVisualSelectionAfterOneDecision(string chosen, bool selectedFutureGoal)
    {
        const string question = "Show possession statistics for both teams in this replay";
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice(chosen)) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input() with { Question = question }; input = input with { Context = input.Context with { SelectedEvent = selectedFutureGoal ? new("future-goal", 60000, "1") : null } };
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default);
        Assert.Equal(chosen == "current_replay_statistics" ? MatchRoutingStatus.Selected : MatchRoutingStatus.Clarify, result.Status);
        Assert.Equal(chosen == "current_replay_statistics" ? chosen : null, result.WorkflowId);
        using var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body); Assert.Equal(question, sent.RootElement.GetProperty("state").GetProperty("Question").GetString());
        Assert.Equal(input.Context, JsonSerializer.Deserialize<MatchChatContext>(sent.RootElement.GetProperty("state").GetProperty("Context").GetRawText()));
    }

    [Fact]
    public async Task ReplayTeamStatisticsUseDecisionChoiceWithNoModelSuppliedArguments()
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("current_replay_statistics")) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input() with { Question = "How many passes have both teams completed and what is their control share in this replay?" };
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default);
        Assert.Equal("current_replay_statistics", result.WorkflowId); using var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var workflow = sent.RootElement.GetProperty("questions").GetProperty("workflow"); Assert.Equal("choice", workflow.GetProperty("type").GetString());
        Assert.Contains("Team-level passes", workflow.GetProperty("criteria").GetProperty("current_replay_statistics").GetString());
        var instructions = workflow.GetProperty("instructions").GetString(); Assert.Contains("Never supply numeric values, team IDs or query arguments", instructions);
        Assert.Contains("Individual player counts are not team replay statistics", instructions); Assert.Contains("unsupported measured speed, distance or xG remain unsupported", instructions);
        Assert.Equal(3, workflow.EnumerateObject().Count()); // type, instructions and criteria only
    }

    [Fact]
    public async Task NamedPlayerIntentWireKeepsOnlyUserTextOpaqueUiPointersAndWorkflowCriteria()
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("named_player_context")) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input() with { Question = "How did Danny Welbeck play in this match?" };
        input = input with { Context = input.Context with { SelectedEvent = new("goal-1", 3780000, "61") } };
        var route = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default);
        Assert.Equal("named_player_context", route.WorkflowId); var body = Assert.Single(handler.Requests).Body;
        using var json = JsonDocument.Parse(body); Assert.Equal("choice", json.RootElement.GetProperty("questions").GetProperty("workflow").GetProperty("type").GetString());
        Assert.Contains("named_player_context", body); Assert.DoesNotContain("People", body); Assert.DoesNotContain("IdentityMappings", body);
        Assert.DoesNotContain("OwnerKey", body); Assert.DoesNotContain("person-pl-reference-chelsea-welbeck", body);
        var workflow = json.RootElement.GetProperty("questions").GetProperty("workflow");
        var criteria = workflow.GetProperty("criteria"); var instructions = workflow.GetProperty("instructions").GetString()!;
        Assert.Contains("incomplete first names and unknown names", criteria.GetProperty("named_player_context").GetString());
        Assert.Contains("what evidence exists for that person", criteria.GetProperty("named_player_context").GetString());
        Assert.Contains("trusted non-null UI-selected player", criteria.GetProperty("selected_player_context").GetString());
        Assert.Contains("Do not choose this for any explicit player name", criteria.GetProperty("clarify").GetString());
        Assert.Contains("A name takes precedence over him/her wording, UI selection and selected goal", instructions);
        Assert.Contains("HasNamedPlayerReference=true requires named_player_context", instructions);
        Assert.Contains("HasNamedPlayerReference=false means there is no trusted named-player reference", instructions);
        Assert.Contains("Context.Query.SubjectId chooses selected_player_context", instructions);
        Assert.False(json.RootElement.GetProperty("state").GetProperty("HasNamedPlayerReference").GetBoolean());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TrustedNamedReferenceMarkerIsExplicitOnWireAndOldHistoryHasNoImplicitAuthority(bool namedReference)
    {
        var chosen = namedReference ? "named_player_context" : "selected_player_context";
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice(chosen)) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input(); input = input with { Question = "How did he play?", HasNamedPlayerReference = namedReference,
            Context = input.Context with { Query = input.Context.Query with { SubjectId = "person-two" } },
            PreviousQuestions = [new("How did One play?", "person-one")] };
        Assert.Equal(chosen, (await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default)).WorkflowId);
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body); var state = json.RootElement.GetProperty("state");
        Assert.Equal(namedReference, state.GetProperty("HasNamedPlayerReference").GetBoolean());
        Assert.Equal("person-two", state.GetProperty("Context").GetProperty("Query").GetProperty("SubjectId").GetString());
        Assert.Equal("person-one", state.GetProperty("PreviousQuestions")[0].GetProperty("SelectedSubjectId").GetString());
        var workflow = json.RootElement.GetProperty("questions").GetProperty("workflow");
        Assert.Contains("Do not infer named-reference authority from PreviousQuestions alone", workflow.GetProperty("instructions").GetString());
        Assert.Contains("HasNamedPlayerReference is true", workflow.GetProperty("criteria").GetProperty("named_player_context").GetString());
        Assert.Contains("HasNamedPlayerReference is false", workflow.GetProperty("criteria").GetProperty("selected_player_context").GetString());
    }

    [Fact]
    public async Task SelectedGoalLeadInQuestionSendsOnlyServerLocatorAndSeparateRetrospectiveCriteria()
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("selected_goal_event")) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input() with { Question = "Why did Chelsea let in a goal here, what did they do wrong?" };
        input = input with { Context = input.Context with { Query = input.Context.Query with { CursorMs = 3765000, SubjectId = null }, SelectedEvent = new("goal-1", 3780000, "61") } };
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default);
        Assert.Equal("selected_goal_event", result.WorkflowId); using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var context = body.RootElement.GetProperty("state").GetProperty("Context"); Assert.Equal(3765000, context.GetProperty("Query").GetProperty("CursorMs").GetInt64());
        Assert.Equal("goal-1", context.GetProperty("SelectedEvent").GetProperty("RecordId").GetString());
        var workflow = body.RootElement.GetProperty("questions").GetProperty("workflow");
        Assert.Contains("lead-in", workflow.GetProperty("criteria").GetProperty("selected_goal_event").GetString());
        Assert.Contains("so far", workflow.GetProperty("instructions").GetString());
        Assert.Equal(MatchWorkflowRegistry.Workflows.Keys.Where(id => id != MatchSourceQuestions.CompareTeamsWorkflow && id != MatchSourceQuestions.ExplainMomentWorkflow).Order(), workflow.GetProperty("criteria").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(workflow.GetProperty("criteria").TryGetProperty(MatchSourceQuestions.CompareTeamsWorkflow, out _));
    }

    [Theory]
    [InlineData(3779999)] [InlineData(3780000)]
    public async Task GoalWhyQuestionSendsMomentExplanationCriteriaAndBoundCursorWithoutRequiringPlayer(long cursor)
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("current_match_moment")) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var input = Input(); input = input with { Question = "Why did Chelsea let in a goal here, what did they do wrong?", Context = input.Context with { Query = input.Context.Query with { CursorMs = cursor, SubjectId = null } } };
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(input, default);
        Assert.Equal("current_match_moment", result.WorkflowId); using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var root = document.RootElement; Assert.Equal(input.Question, root.GetProperty("state").GetProperty("Question").GetString());
        var context = root.GetProperty("state").GetProperty("Context").GetProperty("Query");
        Assert.Equal(cursor, context.GetProperty("CursorMs").GetInt64()); Assert.Equal(JsonValueKind.Null, context.GetProperty("SubjectId").ValueKind);
        var workflow = root.GetProperty("questions").GetProperty("workflow");
        Assert.Contains("why/how", workflow.GetProperty("criteria").GetProperty("current_match_moment").GetString());
        Assert.Contains("a current-match team reference is not an unknown player", workflow.GetProperty("instructions").GetString());
        Assert.Equal(11, workflow.GetProperty("criteria").EnumerateObject().Count());
    }

    [Fact]
    public async Task RepeatedNarratorCallsReuseSdkAuthenticationButKeepMessagesAndTracesSeparate()
    {
        var log = new TimingLog(); var diagnostics = MatchChatDiagnosticsTests.Create(log); var credential = new Credential();
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""{"id":"unit","object":"chat.completion","created":0,"model":"unit","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"prepared answer"}}]}""") });
        using var timing = new MatchChatTimingHandler(diagnostics, MatchChatTimingStage.NarratorHttp) { InnerHandler = handler };
        using var http = new HttpClient(timing);
        var model = new AzureMatchChatModel(http, Options.Create(new MatchChatOptions { Enabled = true,
            Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit" }), new Environment(), credential, diagnostics);
        using (diagnostics.BeginRequest()) await model.CompleteAsync(new([new("system", "FIRST_EVIDENCE"), new("user", "FIRST_QUESTION")], ["match_summary"]), default);
        using (diagnostics.BeginRequest()) await model.CompleteAsync(new([new("system", "SECOND_EVIDENCE"), new("user", "SECOND_QUESTION")], ["coverage"]), default);
        Assert.Equal(1, credential.Calls); Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("FIRST_QUESTION", handler.Requests[0].Body); Assert.DoesNotContain("SECOND_QUESTION", handler.Requests[0].Body);
        Assert.Contains("SECOND_EVIDENCE", handler.Requests[1].Body); Assert.DoesNotContain("FIRST_", handler.Requests[1].Body);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer unit-bearer", request.Authorization));
        Assert.Equal(2, log.Records.Where(r => r.Stage == "NarratorHttp").Select(r => r.TraceId).Distinct().Count());
        Assert.Single(log.Records, r => r.Stage == "NarratorCredential");
    }

    [Theory]
    [InlineData("disabled")] [InlineData("endpoint")] [InlineData("production-key")]
    public async Task RepeatedUnconfiguredNarratorCallsNeverAcquireCredentialsOrSend(string field)
    {
        var options = new MatchChatOptions { Enabled = true, Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit" };
        var environment = new Environment();
        if (field == "disabled") options.Enabled = false;
        else if (field == "endpoint") options.Endpoint = "https://outside.example/";
        else { options.ApiKey = "not-for-production"; environment.EnvironmentName = "Production"; }
        using var handler = new CaptureHandler(_ => throw new InvalidOperationException("No request allowed")); using var http = new HttpClient(handler);
        var credential = new Credential(); var model = new AzureMatchChatModel(http, Options.Create(options), environment, credential);
        for (var call = 0; call < 2; call++) await Assert.ThrowsAsync<InvalidOperationException>(() => model.CompleteAsync(new([new("user", "Question")], ["coverage"]), default));
        Assert.Equal(0, credential.Calls); Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ActualChoiceAdapterUsesDocumentedRequestOneInferenceAndNoPrivateContext()
    {
        var timingLog = new TimingLog(); var diagnostics = MatchChatDiagnosticsTests.Create(timingLog); using var trace = diagnostics.BeginRequest();
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("current_match_summary")) });
        using var http = new HttpClient(handler); var credential = new Credential(); var router = new DecisionWorkflowRouter(http, Options.Create(Enabled()), new Environment(), credential, diagnostics);
        var result = await router.RouteAsync(Input(), default); Assert.Equal(MatchRoutingStatus.Selected, result.Status); Assert.Equal("current_match_summary", result.WorkflowId);
        Assert.Equal(1, credential.Calls); Assert.Equal("https://cognitiveservices.azure.com/.default", Assert.Single(credential.Scopes));
        Assert.Equal("RouterCredential", Assert.Single(timingLog.Records).Stage); Assert.Equal("Success", Assert.Single(timingLog.Records).Outcome);
        var request = Assert.Single(handler.Requests); Assert.Equal("https://unit-resource.services.ai.azure.com/providers/microsoft/v1/systemone", request.Url);
        Assert.Equal("Bearer unit-bearer", request.Authorization);
        using var body = JsonDocument.Parse(request.Body); var root = body.RootElement;
        Assert.Equal("decision-unit", root.GetProperty("model").GetString()); Assert.Equal(3, root.EnumerateObject().Count());
        var workflow = root.GetProperty("questions").GetProperty("workflow"); Assert.Equal("choice", workflow.GetProperty("type").GetString());
        Assert.Equal(MatchWorkflowRegistry.Workflows.Keys.Where(id => id != MatchSourceQuestions.CompareTeamsWorkflow && id != MatchSourceQuestions.ExplainMomentWorkflow).Order(), workflow.GetProperty("criteria").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(workflow.GetProperty("criteria").TryGetProperty(MatchSourceQuestions.CompareTeamsWorkflow, out _));
        Assert.DoesNotContain("Owner", request.Body); Assert.DoesNotContain("People", request.Body); Assert.DoesNotContain("Schedule", request.Body);
        Assert.DoesNotContain("assistant", request.Body); Assert.DoesNotContain("Evidence", root.GetProperty("state").GetRawText());
    }

    [Theory]
    [InlineData("disabled")] [InlineData("endpoint")] [InlineData("deployment")] [InlineData("threshold")]
    [InlineData("margin")] [InlineData("infinity")] [InlineData("timeout")] [InlineData("production-key")]
    public async Task UnconfiguredAdapterDoesNoCredentialLookupOrRequest(string field)
    {
        var options = Enabled(); var environment = new Environment();
        switch (field)
        {
            case "disabled": options.Enabled = false; break; case "endpoint": options.Endpoint = "https://outside.example/"; break;
            case "deployment": options.Deployment = ""; break; case "threshold": options.MinimumProbability = 0.5; break;
            case "margin": options.MinimumMargin = 0; break; case "infinity": options.MinimumProbability = double.PositiveInfinity; break;
            case "timeout": options.TimeoutSeconds = 0; break; default: options.ApiKey = "local-only-key"; environment.EnvironmentName = "Production"; break;
        }
        using var handler = new CaptureHandler(_ => throw new InvalidOperationException("No request allowed")); using var http = new HttpClient(handler); var credential = new Credential();
        var router = new DecisionWorkflowRouter(http, Options.Create(options), environment, credential);
        Assert.False(router.Configured); Assert.Equal(MatchRoutingStatus.Unavailable, (await router.RouteAsync(Input(), default)).Status);
        Assert.Equal(0, credential.Calls); Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://unit-resource.services.ai.azure.com/")] [InlineData("https://outside.example/")]
    [InlineData("https://unit-resource.services.ai.azure.com.evil.example/")] [InlineData("https://unit-resource.services.ai.azure.com/path")]
    [InlineData("https://user@unit-resource.services.ai.azure.com/")] [InlineData("https://unit-resource.services.ai.azure.com/?token=x")]
    [InlineData("https://unit-resource.services.ai.azure.com:444/")] [InlineData("https://a.b.services.ai.azure.com/")]
    public void CredentialsOnlyGoToApprovedAzureResourceRoots(string endpoint) => Assert.False(AzureModelEndpoint.Valid(endpoint));

    [Theory]
    [InlineData("unknown")] [InlineData("missing-probabilities")] [InlineData("missing-option")] [InlineData("extra-option")]
    [InlineData("nan")] [InlineData("infinity")] [InlineData("negative")] [InlineData("too-large")] [InlineData("sum")]
    [InlineData("wrong-winner")] [InlineData("duplicate")] [InlineData("type")] [InlineData("tool-arguments")]
    [InlineData("malformed")] [InlineData("confidence")]
    public async Task StrictProvisionalProbabilityContractFailsClosed(string malformed)
    {
        var body = Choice("current_match_summary"); var probabilities = Probabilities("current_match_summary");
        switch (malformed)
        {
            case "unknown": body = body.Replace("\"choice\":\"current_match_summary\"", "\"choice\":\"invented\""); break;
            case "missing-probabilities": body = "{\"answers\":{\"workflow\":{\"type\":\"choice\",\"choice\":\"current_match_summary\"}}}"; break;
            case "missing-option": probabilities.Remove("clarify"); break;
            case "extra-option": probabilities.Add("other-owner", 0); break;
            case "nan": body = body.Replace("0.94", "\"NaN\""); break;
            case "infinity": body = body.Replace("0.94", "1e999"); break;
            case "negative": probabilities["current_match_summary"] = -1; break; case "too-large": probabilities["current_match_summary"] = 1.1; break;
            case "sum": probabilities["current_match_summary"] = 0.5; break;
            case "wrong-winner": probabilities["clarify"] = 0.95; probabilities["current_match_summary"] = 0; break;
            case "duplicate": body = body.Replace("\"type\":\"choice\"", "\"type\":\"choice\",\"type\":\"choice\""); break;
            case "type": body = body.Replace("\"type\":\"choice\"", "\"type\":\"score\""); break;
            case "tool-arguments": body = body.Replace("\"type\":\"choice\"", "\"ownerId\":\"forged\",\"type\":\"choice\""); break;
            case "confidence": body = body.Replace("\"type\":\"choice\"", "\"confidence\":2,\"type\":\"choice\""); break;
            default: body = "not json"; break;
        }
        if (malformed is "missing-option" or "extra-option" or "negative" or "too-large" or "sum" or "wrong-winner") body = Choice("current_match_summary", probabilities);
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }); using var http = new HttpClient(handler);
        var options = Enabled(); options.ApiKey = "unit-key";
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(Input(), default);
        Assert.Equal(MatchRoutingStatus.InvalidResponse, result.Status); Assert.Null(result.WorkflowId); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LowTopProbabilityOrNarrowMarginAbstainsWithoutGuessingRoute()
    {
        var probabilities = DecisionWorkflowRouter.Criteria.Keys.ToDictionary(key => key, _ => 0d); probabilities["current_match_summary"] = 0.51; probabilities["clarify"] = 0.49;
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("current_match_summary", probabilities)) }); using var http = new HttpClient(handler);
        var options = Enabled(); options.ApiKey = "unit-key";
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(Input(), default);
        Assert.Equal(MatchRoutingStatus.Clarify, result.Status); Assert.Null(result.WorkflowId);
    }

    [Theory]
    [InlineData(0.80, MatchRoutingStatus.Selected)] [InlineData(0.799999, MatchRoutingStatus.Clarify)]
    public async Task DefaultProbabilityGateAcceptsItsBoundaryAndAbstainsBelowIt(double top, MatchRoutingStatus expected)
    {
        var probabilities = DecisionWorkflowRouter.Criteria.Keys.ToDictionary(key => key, _ => 0d);
        probabilities["current_match_summary"] = top; probabilities["clarify"] = 1 - top;
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Choice("current_match_summary", probabilities)) });
        using var http = new HttpClient(handler); var options = Enabled(); options.ApiKey = "unit-key";
        var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(Input(), default);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == MatchRoutingStatus.Selected ? "current_match_summary" : null, result.WorkflowId); Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(302)] [InlineData(429)] [InlineData(503)]
    public async Task RedirectsAndServiceFailuresCauseOneRequestAndNoRetry(int status)
    {
        using var handler = new CaptureHandler(_ => new((HttpStatusCode)status) { Headers = { Location = new("https://outside.example/") } }); using var http = new HttpClient(handler);
        var options = Enabled(); options.ApiKey = "unit-key"; var result = await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(Input(), default);
        Assert.Equal(MatchRoutingStatus.Unavailable, result.Status); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedResponseAndAdapterTimeoutAreBounded()
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', DecisionWorkflowRouter.ResponseBytes + 1)) }); using var http = new HttpClient(handler);
        var options = Enabled(); options.ApiKey = "unit-key";
        Assert.Equal(MatchRoutingStatus.InvalidResponse, (await new DecisionWorkflowRouter(http, Options.Create(options), new Environment()).RouteAsync(Input(), default)).Status);
        using var waiting = new WaitingHandler(); using var waitingHttp = new HttpClient(waiting); options.TimeoutSeconds = 1;
        var pending = new DecisionWorkflowRouter(waitingHttp, Options.Create(options), new Environment()).RouteAsync(Input(), default);
        Assert.Equal(MatchRoutingStatus.Unavailable, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Status); Assert.Equal(1, waiting.Requests);
        waiting.Completion.SetResult(new(HttpStatusCode.ServiceUnavailable));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NarratorAdapterActuallyAdvertisesNoToolsAndRejectsUnexpectedToolOutput(bool toolCall)
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(toolCall
            ? "{\"id\":\"unit\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"unit\",\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"x\",\"type\":\"function\",\"function\":{\"name\":\"coverage\",\"arguments\":\"{}\"}}]}}]}"
            : "{\"id\":\"unit\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"unit\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"prepared answer\"}}]}" ) });
        using var http = new HttpClient(handler); var model = new AzureMatchChatModel(http, Options.Create(new MatchChatOptions { Enabled = true,
            Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "narrator-unit", ApiKey = "unit-key" }), new Environment());
        if (toolCall) await Assert.ThrowsAsync<InvalidDataException>(() => model.CompleteAsync(new([new("system", "Prepared evidence"), new("user", "Question")], ["coverage"]), default));
        else Assert.Empty((await model.CompleteAsync(new([new("system", "Prepared evidence"), new("user", "Question")], ["coverage"]), default)).Calls);
        var request = Assert.Single(handler.Requests); using var json = JsonDocument.Parse(request.Body); Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.False(json.RootElement.TryGetProperty("tool_choice", out _)); Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(700, json.RootElement.GetProperty("max_completion_tokens").GetInt32()); Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal("none", json.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task NarratorSdkDoesNotRetryAServiceFailure()
    {
        using var handler = new CaptureHandler(_ => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"message\":\"unit unavailable\"}}") }); using var http = new HttpClient(handler);
        var model = new AzureMatchChatModel(http, Options.Create(new MatchChatOptions { Enabled = true, Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit", ApiKey = "unit-key" }), new Environment());
        await Assert.ThrowsAnyAsync<Exception>(() => model.CompleteAsync(new([new("user", "Question")], ["coverage"]), default)); Assert.Single(handler.Requests);
    }

    [Fact]
    public void FrozenHeldoutExamplesAreLabelledSeparateFromTuningAndNotPretendedModelMeasurements()
    {
        var directory = Path.Combine(LocalPaths.FindRoot(), "tests", "Matchmate.Tests", "Fixtures");
        using var heldout = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "decision-routing-heldout-v1.json")));
        using var tuning = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "decision-routing-tuning-v1.json")));
        Assert.Contains("pending", heldout.RootElement.GetProperty("status").GetString());
        var cases = heldout.RootElement.GetProperty("cases").EnumerateArray().ToArray(); var tune = tuning.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.True(cases.Length >= 50); Assert.Equal(cases.Length, cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
        Assert.All(cases.Concat(tune), item => Assert.Contains(item.GetProperty("expected").GetString(), DecisionWorkflowRouter.Criteria.Keys));
        Assert.Empty(cases.Select(c => c.GetProperty("id").GetString()).Intersect(tune.Select(c => c.GetProperty("id").GetString())));
        Assert.Empty(cases.Select(c => c.GetProperty("question").GetString()).Intersect(tune.Select(c => c.GetProperty("question").GetString())));
        // Frozen v1 predates the explicit selected-goal capability; preserve its seven-route coverage.
        Assert.Equal(DecisionWorkflowRouter.Criteria.Keys.Where(key => key is not "selected_goal_event" and not "current_match_moment" and not "named_player_context" and not "current_replay_statistics").Order(), cases.Select(c => c.GetProperty("expected").GetString()).Distinct().Order());
    }

    private static MatchRoutingInput Input() => new("What happened by this replay moment?", new(SharedFootballTests.Query(SharedFootballTests.Fixture(), 0), 2), []);
    private static DecisionRoutingOptions Enabled() => new() { Enabled = true, Endpoint = "https://unit-resource.services.ai.azure.com/", Deployment = "decision-unit" };
    private static Dictionary<string, double> Probabilities(string choice) => DecisionWorkflowRouter.Criteria.Keys.ToDictionary(key => key, key => key == choice ? 0.94 : 0.06 / (DecisionWorkflowRouter.Criteria.Count - 1));
    private static string Choice(string choice, Dictionary<string, double>? probabilities = null) => JsonSerializer.Serialize(new { model = "microsoft-decision-1", answers = new { workflow = new { type = "choice", choice, probabilities = probabilities ?? Probabilities(choice) } } });
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing"; public string ApplicationName { get; set; } = "Unit";
        public string ContentRootPath { get; set; } = "."; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Credential : TokenCredential
    {
        public int Calls { get; private set; } public string[] Scopes { get; private set; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new InvalidOperationException("Async only");
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        { Calls++; Scopes = requestContext.Scopes; return ValueTask.FromResult(new AccessToken("unit-bearer", DateTimeOffset.UtcNow.AddHours(1))); }
    }
    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Authorization)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests.Add((request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(token), request.Headers.Authorization?.ToString())); return respond(request); }
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; } public TaskCompletionSource<HttpResponseMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests++; return Completion.Task; }
    }
}
