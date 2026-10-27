using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web;
using Matchmate.Web.Chat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Xunit;
using static Matchmate.Tests.SharedFootballTests;

namespace Matchmate.Tests;

public sealed class MatchNamedPlayerTests
{
    private static readonly SquadPerson[] Names = [new("jose", "team", "Jos\u00e9 Silva", 1, "starter"), new("andre", "team", "Andr\u00e9 Silva", 2, "bench"),
        new("emiliano", "team", "Emiliano Mart\u00ednez", 3, "starter"), new("pele", "team", "Pel\u00e9", 4, "starter"), new("obrien", "team", "Alex O\u2019Brien", 5, "starter")];
    [Theory]
    [InlineData("How did Jose Silva play?", "jose")] [InlineData("What about Jos\u00e9 Silva?", "jose")]
    [InlineData("Where is EMILIANO MARTINEZ?", "emiliano")] [InlineData("Mart\u00ednez's performance?", "emiliano")]
    [InlineData("How did Pele play?", "pele")] [InlineData("Alex OBrien", "obrien")] [InlineData("Andr\u00e9-Silva", "andre")]
    public void FullNamesUniqueSurnamesMononymsAndExplicitNormalizationResolve(string question, string id)
        => Assert.Equal(id, MatchPlayerResolver.Resolve(Names, new(question)).SubjectId);
    [Theory]
    [InlineData("Silva", "more than one")] [InlineData("Jose Silva and Emiliano Martinez", "one player at a time")]
    [InlineData("Unknown Person", "could not match")] [InlineData("Martine", "could not match")] [InlineData("How did Ian Martine\u0301z play?", "could not match")]
    public void AmbiguousMultipleUnknownAndFuzzyNamesClarifyWithoutReferenceFallback(string question, string message)
    {
        var result = MatchPlayerResolver.Resolve(Names, new(question, ReferenceSubjectId: "pele"));
        Assert.Null(result.SubjectId); Assert.Contains(message, result.Message);
    }
    [Fact]
    public void WrongGivenNameCannotBorrowTheUniqueSurnameOfDannyWelbeck()
    {
        SquadPerson[] roster = [new("danny", "team", "Danny Welbeck", 18, "starter")];
        Assert.Null(MatchPlayerResolver.Resolve(roster, new("How did Ian Welbeck play?", ReferenceSubjectId: "danny")).SubjectId);
        Assert.Equal("danny", MatchPlayerResolver.Resolve(roster, new("Welbeck")).SubjectId);
        Assert.Equal("danny", MatchPlayerResolver.Resolve(roster, new("Tell me about Welbeck")).SubjectId);
        Assert.Equal("danny", MatchPlayerResolver.Resolve(roster, new("How did Danny Welbeck play?")).SubjectId);
    }

    [Theory]
    [InlineData("How did Danny Welbeck and Lionel Messi play?")]
    [InlineData("Tell me about Lionel Messi and Cole Palmer.")]
    [InlineData("How did Danny Welbeck or Lionel Messi play?")]
    [InlineData("Tell me about Lionel Messi or Cole Palmer.")]
    [InlineData("How did Danny Welbeck and Cole Palmer play?")]
    public void F001_ImmediateCoordinatedSubjectsClarifyEvenIfAnotherIdentityIsUnknown(string question)
    {
        SquadPerson[] roster = [new("danny", "team", "Danny Welbeck", 18, "starter"), new("cole", "team", "Cole Palmer", 20, "starter")];
        var result = MatchPlayerResolver.Resolve(roster, new(question, ReferenceSubjectId: "cole"));
        Assert.Null(result.SubjectId); Assert.Contains("one player at a time", result.Message);
        Assert.Equal("danny", MatchPlayerResolver.Resolve(roster, new("How did Danny Welbeck play and contribute?")).SubjectId);
        Assert.Equal("cole", MatchPlayerResolver.Resolve(roster, new("Tell me about Cole Palmer's play and contribution.")).SubjectId);
    }

    [Fact]
    public void F002_PlayerInvolvementShirtMetadataIsAnswerableWithoutLicensingOtherNumbersOrMetrics()
    {
        var fixture = Fixture(); var view = Ledger(fixture).Query(Query(fixture, 0));
        var item = MatchPlayerEvidence.Build(view, "person-one").Item;
        var supplied = new Dictionary<string, MatchChatEvidence> { [item.Id] = item };
        string Reply(string answer) => JsonSerializer.Serialize(new { answer, evidenceIds = new[] { item.Id } });
        Assert.Contains("shirt\":1", item.Text); Assert.Contains("1", item.NumericTokens);
        Assert.Equal("One wears shirt 1.", MatchReplyValidation.Validate(Reply("One wears shirt 1."), supplied).Answer);
        foreach (var (answer, expected) in new[] { ("One wears shirt 99.", MatchReplyRejection.UnsupportedNumber),
            ("He covered 1 metre.", MatchReplyRejection.UnsupportedMeasurement), ("He made a one-metre pass.", MatchReplyRejection.UnsupportedMeasurement) })
        {
            MatchReplyRejection? rejection = null;
            Assert.Throws<InvalidDataException>(() => MatchReplyValidation.Validate(Reply(answer), supplied, reason => rejection = reason));
            Assert.Equal(expected, rejection);
        }
    }

    [Fact]
    public void MalformedUnicodeFailsSafelyAndOnlyWholePronounPhrasesCanReuseTrustedReference()
    {
        Assert.False(MatchPlayerResolver.ValidText("\ud800")); Assert.Null(MatchPlayerResolver.Resolve(Names, new("\ud800")).SubjectId);
        Assert.Equal("pele", MatchPlayerResolver.Resolve(Names, new("How did he play?", ReferenceSubjectId: "pele")).SubjectId);
        Assert.Null(MatchPlayerResolver.Resolve(Names, new("How did Unknown Person play? What about him?", ReferenceSubjectId: "pele")).SubjectId);
        Assert.Null(MatchPlayerResolver.Resolve(Names, new("How did he play?", "Unknown Person", "pele")).SubjectId);
    }

    private static readonly Lazy<Task<FootballLedger>> Recordings = new(async () => {
        var root = LocalPaths.FindRoot(); var ledger = new FootballLedger();
        foreach (var batch in await new AustralianFootballSource(LocalPaths.Source, Path.Combine(LocalPaths.Source, "1874553-completed-events.json")).ReadAsync(default)) ledger.Accept(new(batch, DateTimeOffset.UtcNow));
        await foreach (var batch in new FullBrentfordFootballSource(Path.Combine(root, ".local/shared-inputs/brentford-full-v4/manifest.json")).ReadBatchesAsync(default))
        {
            ledger.Accept(new(batch, DateTimeOffset.UtcNow)); var brent = ledger.Catalog().Single(d => d.Manifest.DatasetId == "brentford-existing-illustration");
            if (brent.Schedule is not null && ledger.Query(FullFootballSourceTests.Query(brent, 5880000)).Events.Length == 14) break;
        }
        return ledger;
    });
    [Fact]
    public async Task ExactDannyQuestionOverridesUiAndSelectedFutureGoalWithOneCursorReadAndSeparateSubject()
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId == "brentford-existing-illustration");
        var danny = dataset.People.Single(p => p.Name == "Danny Welbeck"); var ui = dataset.People.First(p => p.PersonId != danny.PersonId);
        var context = new MatchChatContext(FullFootballSourceTests.Query(dataset, 3765000) with { SubjectId = ui.PersonId }, dataset.HeadSequence, new("goal-1", 3780000, "61"));
        var reads = 0; var executor = new MatchWorkflowExecutor((query, _) => { reads++; Assert.Equal(context.Query with { ApplicationSequence = context.HeadSequence }, query); return Task.FromResult(ledger.Query(query)); });
        var request = Request(context, "How did Danny Welbeck play in this match?"); var result = await executor.ExecuteAsync(request, snapshot => snapshot == request.Snapshot);
        Assert.Equal(1, reads); Assert.Equal(danny.PersonId, result.ResolvedSubjectId); Assert.Equal(ui.PersonId, request.Snapshot.Context.Query.SubjectId);
        var bundle = Assert.IsType<MatchWorkflowBundle>(result.Bundle); Assert.Equal(danny.PersonId, bundle.ResolvedSubjectId); Assert.Null(bundle.Context.SelectedEvent);
        Assert.Equal(MatchWorkflowTemporalMode.ReplayAtCursor, bundle.TemporalMode); Assert.Equal(ui.PersonId, bundle.Context.Query.SubjectId);
        Assert.Equal(new[] { "current_player", "player_involvement", "coverage" }, bundle.Items.Select(i => i.Id));
        var json = JsonSerializer.Serialize(bundle); Assert.DoesNotContain("goal-1", json); Assert.DoesNotContain("goal-2", json); Assert.DoesNotContain("goal-3", json);
        Assert.DoesNotContain("match_summary", json); Assert.DoesNotContain("moment_context", json); Assert.DoesNotContain("People", json);
        Assert.Contains("Danny Welbeck", bundle.Items[0].Text); Assert.InRange(json.Length, 1, MatchWorkflowLimits.BundleChars);
    }
    [Fact]
    public async Task AustralianRosterAndCompletedActorIntervalsRemainAvailableWithoutGoalFeed()
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId == "australian-recorded");
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, 240000)); var actor = ((PossessionPayload)view.Possessions.First().Payload).PersonId; var person = dataset.People.Single(p => p.PersonId == actor);
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(Request(new(view.Context, dataset.HeadSequence), "How did " + person.Name + " play?"), _ => true);
        Assert.Equal(person.PersonId, result.ResolvedSubjectId); Assert.Equal(MatchWorkflowStatus.Partial, result.Status);
        Assert.Contains("event_reports_not_supplied", result.Bundle!.Missingness); Assert.Contains(result.Bundle.EventProvenance, p => view.Possessions.Any(r => r.RecordId == p.RecordId));
        Assert.Contains("completedPossessions", result.Bundle.Items[1].Text); Assert.DoesNotContain("homeGoals", result.Bundle.Items[1].Text);
    }
    [Fact]
    public async Task ActualSequenceActorsFormalAssistAndSubstitutionRolesKeepOnlyRelevantEligibleReportsAndUncertainty()
    {
        var ledger = await Recordings.Value; var dataset = ledger.Catalog().Single(d => d.Manifest.DatasetId == "brentford-existing-illustration");
        var view = ledger.Query(FullFootballSourceTests.Query(dataset, 5880000));
        foreach (var name in new[] { "Mikkel Damsgaard", "Jannik Schuster", "Michael Kayode", "Kevin Schade", "Josh Acheampong" })
        {
            var target = dataset.People.Single(p => p.Name == name); var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(ledger.Query(q))).ExecuteAsync(Request(new(view.Context, dataset.HeadSequence), name), _ => true);
            Assert.Equal(target.PersonId, result.ResolvedSubjectId); var item = result.Bundle!.Items.Single(i => i.Id == "player_involvement"); using var doc = JsonDocument.Parse(item.Text);
            foreach (var row in doc.RootElement.GetProperty("roles").EnumerateArray())
            {
                var id = row.GetProperty("record").GetString(); var record = view.Events.Single(r => r.RecordId == id); var e = (MatchEventPayload)record.Payload;
                Assert.True(new[] { e.PersonId, e.AssistPersonId, e.OutgoingPersonId, e.IncomingPersonId }.Contains(target.PersonId) || e.Sequence!.Steps.Any(s => s.ActorId == target.PersonId || s.RelatedActorId == target.PersonId));
                Assert.Contains(result.Bundle.EventProvenance, p => p.RecordId == id);
            }
            if (name == "Jannik Schuster") Assert.Contains("formal_assist", item.Text);
            if (name == "Michael Kayode") Assert.Contains("outgoing", item.Text);
            if (name == "Josh Acheampong") { Assert.Contains("Brentford", item.Text); Assert.Contains("formalAssistSupplied\":false", item.Text); }
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BoundedLargeEvidenceTruncatesHonestlyIncludingSingleOversizedEventWithZeroShown(bool oneHuge)
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var events = Enumerable.Range(0, oneHuge ? 1 : 25).Select(i => Goal(fixture) with { RecordId = "event-" + i,
            Payload = ((MatchEventPayload)Goal(fixture).Payload) with { Details = Enumerable.Range(0, 6).Select(d => new MatchEventDetail("reviewed_observation", new string('"', 319) + d, ["source"], new string('"', 239) + d)).ToArray() } }).ToArray();
        var sequence = 2L; foreach (var chunk in events.Chunk(5)) Add(ledger, fixture, sequence++, chunk); var view = ledger.Query(Query(fixture, 80000));
        var result = await new MatchWorkflowExecutor((q, _) => Task.FromResult(view with { Context = q })).ExecuteAsync(Request(new(view.Context, view.Dataset.HeadSequence), "How did One play?"), _ => true);
        Assert.Equal(MatchWorkflowStatus.Partial, result.Status); Assert.Contains("player_events_truncated", result.Bundle!.Missingness);
        var item = result.Bundle.Items.Single(i => i.Id == "player_involvement"); using var data = JsonDocument.Parse(item.Text);
        Assert.Equal(events.Length, data.RootElement.GetProperty("eligibleTargetEvents").GetInt32()); Assert.Equal(0, data.RootElement.GetProperty("shown").GetArrayLength());
        Assert.Equal(events.Length, data.RootElement.GetProperty("omittedEvents").GetInt32()); Assert.InRange(item.Text.Length, 1, 8000);
        Assert.InRange(JsonSerializer.Serialize(result.Bundle).Length, 1, MatchWorkflowLimits.BundleChars);
    }
    [Fact]
    public async Task UnknownAndInvalidNamedArgumentsClarifyOrRejectWithoutUnrelatedEvidence()
    {
        var fixture = Fixture(); var ledger = Ledger(fixture); var view = ledger.Query(Query(fixture, 0)); var context = new MatchChatContext(view.Context, view.Dataset.HeadSequence); var reads = 0;
        var executor = new MatchWorkflowExecutor((q, _) => { reads++; return Task.FromResult(view with { Context = q }); });
        var unknown = await executor.ExecuteAsync(Request(context, "How did Unknown Person play?"), _ => true); Assert.Equal(MatchWorkflowStatus.Clarify, unknown.Status); Assert.Null(unknown.Bundle); Assert.Contains("could not match", unknown.Message);
        foreach (var args in new MatchPlayerQuestion?[] { null, new(""), new(new string('a', 1001)), new("\ud800"), new("One", ReferenceSubjectId: "../bad") })
            Assert.Equal(MatchWorkflowStatus.InvalidRequest, (await executor.ExecuteAsync(new("named_player_context", MatchWorkflowRegistry.Version, new("owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor), args), _ => true)).Status);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task SessionNamedSubjectHistoryAndPronounsKeepUiSnapshotAndNewUiSelectionWins()
    {
        using var h = new Harness(); var ui = h.Context.Query.SubjectId;
        var first = await h.Ask("How did One play?"); Assert.True(first.Accepted); Assert.Equal("person-one", first.ResolvedSubjectId);
        var turn = Assert.Single(h.Session.Turns); Assert.Equal(ui, turn.Context.Query.SubjectId); Assert.Equal("person-one", turn.ResolvedSubjectId);
        Assert.False(h.Router.Inputs[^1].HasNamedPlayerReference);
        h.Set(h.Context with { Query = h.Context.Query with { } }); h.Set(h.Context); // Equal-context renders must preserve the trusted named pointer.
        Assert.True((await h.Ask("How did he play?")).Accepted); Assert.Equal("person-one", h.Model.Bundles[^1].ResolvedSubjectId);
        Assert.True(h.Router.Inputs[^1].HasNamedPlayerReference); Assert.Equal(ui, h.Router.Inputs[^1].Context.Query.SubjectId);
        Assert.Equal("person-one", h.Router.Inputs[^1].PreviousQuestions.First().SelectedSubjectId);
        Assert.DoesNotContain("unit grounded answer", JsonSerializer.Serialize(h.Router.Inputs[^1]));
        h.Set(h.Context with { Query = h.Context.Query with { SubjectId = "person-one" } });
        h.Set(h.Context with { Query = h.Context.Query with { SubjectId = "person-two" } }); // Changing away and back cannot resurrect the old named pointer.
        h.Router.Route = "selected_player_context";
        Assert.True((await h.Ask("What about him?")).Accepted); Assert.False(h.Router.Inputs[^1].HasNamedPlayerReference);
        Assert.Equal("selected_player_context", h.Model.Bundles[^1].WorkflowId); Assert.Equal("person-two", h.Model.Bundles[^1].Context.Query.SubjectId);
        Assert.Null(h.Model.Bundles[^1].ResolvedSubjectId);
    }
    [Fact]
    public async Task SpecificUnknownClarificationPreservesOriginalOnlyAfterNewValidFullName()
    {
        using var h = new Harness(); await h.Ask("How did One play?"); var calls = h.Model.Bundles.Count;
        var unknown = await h.Ask("How did Unknown Person play?"); Assert.Equal(MatchChatOutcome.Clarify, unknown.Outcome); Assert.Contains("could not match", h.Session.Turns[^1].Answer); Assert.Equal(calls, h.Model.Bundles.Count);
        var unresolved = await h.Ask("How did he play?"); Assert.Equal(MatchChatOutcome.Clarify, unresolved.Outcome); Assert.Equal(calls, h.Model.Bundles.Count);
        Assert.False(h.Router.Inputs[^1].HasNamedPlayerReference); // Pending clarification cannot reuse a previous accepted named subject.
        Assert.True((await h.Ask("One")).Accepted); Assert.Equal("How did Unknown Person play?", h.Session.Turns[^1].OriginalQuestion);
        Assert.Equal("person-one", h.Model.Bundles[^1].ResolvedSubjectId); Assert.Contains("How did Unknown Person play?", h.Model.Messages[^1].Messages.Single(m => m.Content.StartsWith("Untrusted question")).Content);
    }
    [Theory]
    [InlineData("reset")] [InlineData("auth")] [InlineData("ui")] [InlineData("rewind")] [InlineData("head")]
    public async Task TrustedNamedMarkerClearsAcrossAuthoritativeContextAndOwnerBoundaries(string change)
    {
        using var h = new Harness(); Assert.True((await h.Ask("One")).Accepted);
        if (change == "reset") h.Session.NewChat();
        else if (change == "auth") h.Auth.Change("other");
        else if (change == "ui") h.Set(h.Context with { Query = h.Context.Query with { SubjectId = "person-one" } });
        else if (change == "head") h.Set(h.Context with { HeadSequence = h.Context.HeadSequence + 1 });
        else { h.Set(h.Context with { Query = h.Context.Query with { CursorMs = 10 } }); h.Set(h.Context with { Query = h.Context.Query with { CursorMs = 0 } }); }
        h.Router.Route = "selected_player_context"; var routed = h.Router.Inputs.Count;
        await h.Ask("How did he play?"); Assert.Equal(routed + 1, h.Router.Inputs.Count); Assert.False(h.Router.Inputs[^1].HasNamedPlayerReference);
    }

    [Theory]
    [InlineData("selected_player_context")] [InlineData("current_match_summary")]
    public async Task ConflictingSelectedRouteForTrustedNamedPronounClarifiesBeforeAnyOtherSubjectRead(string route)
    {
        using var h = new Harness(); Assert.True((await h.Ask("One")).Accepted);
        var reads = h.Api.Queries.Count; var modelCalls = h.Model.Bundles.Count; h.Router.Route = route;
        var result = await h.Ask("How did he play?");
        Assert.True(h.Router.Inputs[^1].HasNamedPlayerReference); Assert.Equal(MatchChatOutcome.Clarify, result.Outcome);
        Assert.Equal(reads, h.Api.Queries.Count); Assert.Equal(modelCalls, h.Model.Bundles.Count);
        Assert.True(h.Session.AwaitingClarification); Assert.Contains("Please name the player", result.Message);
        Assert.Empty(h.Session.Turns[^1].Evidence); Assert.Equal(MatchChatOutcome.Clarify, h.Session.Turns[^1].Outcome);
        Assert.Equal("person-one", h.Session.Turns[0].ResolvedSubjectId);
    }

    [Theory]
    [InlineData("reset")] [InlineData("auth")] [InlineData("context")] [InlineData("cancel")]
    public async Task LateNamedReadNeverCommitsOrCreatesTrustedSubjectAfterLifecycleChange(string change)
    {
        using var h = new Harness(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Api.Wait = async _ => { started.SetResult(); await release.Task; }; using var cancel = new CancellationTokenSource();
        var ask = h.Session.AskAsync(h.Context, "One", cancel.Token); await started.Task;
        if (change == "reset") h.Session.NewChat(); else if (change == "auth") h.Auth.Change("other");
        else if (change == "context") h.Set(h.Context with { Query = h.Context.Query with { CursorMs = 1 } }); else cancel.Cancel();
        release.SetResult(); Assert.False((await ask).Accepted); Assert.Empty(h.Session.Turns); Assert.Empty(h.Model.Bundles);
    }
    [Fact]
    public async Task NewChatClearsNamedReferenceWhileSourceChipStillMakesZeroModelCalls()
    {
        using var h = new Harness(); await h.Ask("One"); h.Session.NewChat(); h.Router.Route = "selected_player_context";
        Assert.True((await h.Ask("What about him?")).Accepted); Assert.False(h.Router.Inputs[^1].HasNamedPlayerReference);
        Assert.Null(h.Model.Bundles[^1].ResolvedSubjectId); Assert.Equal("person-two", h.Model.Bundles[^1].Context.Query.SubjectId);
        h.Session.NewChat(); var router = h.Router.Inputs.Count; var model = h.Model.Bundles.Count;
        Assert.True((await h.Session.AskSourceAsync(h.Context, MatchSourceQuestionId.Player)).Accepted); Assert.Equal(router, h.Router.Inputs.Count); Assert.Equal(model, h.Model.Bundles.Count);
    }

    private static MatchWorkflowRequest Request(MatchChatContext context, string question) => new("named_player_context", MatchWorkflowRegistry.Version,
        new("owner", 1, context, MatchWorkflowTemporalMode.ReplayAtCursor), new(question));
    private sealed class Router : IMatchWorkflowRouter
    {
        public bool Configured => true; public string Route { get; set; } = "named_player_context"; public List<MatchRoutingInput> Inputs { get; } = [];
        public Task<MatchRoutingResult> RouteAsync(MatchRoutingInput input, CancellationToken token) { Inputs.Add(input); return Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, Route)); }
    }
    private sealed class Model : IMatchChatModel
    {
        public bool Configured => true; public List<MatchWorkflowBundle> Bundles { get; } = []; public List<MatchNarratorRequest> Messages { get; } = [];
        public Task<MatchModelReply> CompleteAsync(MatchNarratorRequest request, CancellationToken token)
        {
            Messages.Add(request); var prepared = request.Messages.Single(m => m.Content.StartsWith("Prepared app-computed"));
            Bundles.Add(JsonSerializer.Deserialize<MatchWorkflowBundle>(prepared.Content[(prepared.Content.IndexOf('\n') + 1)..])!);
            return Task.FromResult(new MatchModelReply("""{"answer":"unit grounded answer","evidenceIds":["current_player"]}""", []));
        }
    }
    private sealed class Auth : AuthenticationStateProvider
    {
        private string _id = "tester";
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new(new ClaimsIdentity([new("sub", _id), new("roles", "Tester")], "unit", "sub", "roles"))));
        public void Change(string id) { _id = id; NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new(new ClaimsIdentity([new("sub", _id), new("roles", "Tester")], "unit", "sub", "roles"))))); }
    }
    private sealed class Api(FootballView view) : HttpMessageHandler
    {
        public Func<FootballQuery, Task>? Wait; public List<FootballQuery> Queries { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!; Queries.Add(query); if (Wait is not null) await Wait(query); return new(HttpStatusCode.OK) { Content = JsonContent.Create(view with { Context = query }) }; }
    }
    private sealed class Harness : IDisposable
    {
        public Auth Auth { get; } = new(); public Router Router { get; } = new(); public Model Model { get; } = new(); public Api Api { get; }
        private readonly HttpClient _http; public MatchChatContext Context { get; private set; } public MatchChatSession Session { get; }
        public Harness()
        {
            var fixture = Fixture(); var ledger = Ledger(fixture); Add(ledger, fixture, 2, Frame(fixture, 0)); var view = ledger.Query(Query(fixture, 0));
            Context = new(view.Context with { SubjectId = "person-two" }, view.Dataset.HeadSequence); Api = new(view); _http = new(Api) { BaseAddress = new("https://unit.invalid/") };
            Session = new(Auth, new ReplayApiClient(_http), Router, Model, Options.Create(new MatchChatOptions())); Session.SetContext(Context);
        }
        public void Set(MatchChatContext context) { Context = context; Session.SetContext(context); }
        public Task<MatchChatResult> Ask(string question) => Session.AskAsync(Context, question);
        public void Dispose() { Session.Dispose(); _http.Dispose(); }
    }
}
