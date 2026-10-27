using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Matchmate.Web.Chat;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Matchmate.Tests;

public sealed class DevelopmentModelCredentialTests
{
    private static TokenRequestContext Context(string? parent = null) => new([DevelopmentModelCredential.CognitiveScope], parent);
    private static AccessToken Token(DateTimeOffset now, string value = "unit-token", int expiresMinutes = 60, int? refreshMinutes = null)
        => new(value, now.AddMinutes(expiresMinutes), refreshMinutes is int minutes ? now.AddMinutes(minutes) : null);

    [Fact]
    public async Task ConstructorIsLazyAndSequentialCallersWithDifferentCorrelationIdsReuseOneToken()
    {
        var clock = new Clock(); var inner = new Credential((_, _) => ValueTask.FromResult(Token(clock.Now)));
        var credential = new DevelopmentModelCredential(inner, clock); Assert.Equal(0, inner.Calls);
        var first = await credential.GetTokenAsync(Context("first"), default); var second = credential.GetToken(Context("second"), default);
        Assert.Equal(first, second); Assert.Equal(1, inner.Calls); Assert.Equal(clock.Now.AddMinutes(55), first.RefreshOn);
    }

    [Fact]
    public async Task ConcurrentSyncAndAsyncCallersShareOneAcquisition()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Credential((_, _) => new(pending.Task)); var credential = new DevelopmentModelCredential(inner, clock);
        var first = credential.GetTokenAsync(Context("first"), default).AsTask();
        var sync = Task.Run(() => credential.GetToken(Context("sync"), default));
        var others = Enumerable.Range(0, 12).Select(_ => credential.GetTokenAsync(Context(), default).AsTask()).ToArray();
        Assert.Equal(1, inner.Calls); pending.SetResult(Token(clock.Now));
        var tokens = await Task.WhenAll(others.Append(first).Append(sync)); Assert.All(tokens, token => Assert.Equal("unit-token", token.Token));
        Assert.Equal(1, inner.Calls); Assert.False(inner.LastCancellation.CanBeCanceled);
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelTheFlightOrAnotherWaiterOrLaterCacheHit()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Credential((_, _) => new(pending.Task)); var credential = new DevelopmentModelCredential(inner, clock);
        using var cancellation = new CancellationTokenSource();
        var cancelled = credential.GetTokenAsync(Context(), cancellation.Token).AsTask(); var remaining = credential.GetTokenAsync(Context(), default).AsTask();
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); Assert.False(remaining.IsCompleted);
        Assert.False(inner.LastCancellation.CanBeCanceled); pending.SetResult(Token(clock.Now)); Assert.Equal("unit-token", (await remaining).Token);
        Assert.Equal("unit-token", credential.GetToken(Context(), default).Token); Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task PreCancelledSyncAndAsyncCallersNeverStartAcquisition()
    {
        var inner = new Credential((_, _) => throw new InvalidOperationException("Must not acquire.")); var credential = new DevelopmentModelCredential(inner);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => credential.GetToken(Context(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credential.GetTokenAsync(Context(), cancellation.Token).AsTask()); Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task SharedFailureIsNotCachedAndCancelledWaiterCannotPoisonLaterAcquisition()
    {
        var clock = new Clock(); var pending = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var inner = new Credential((_, _) => Interlocked.Increment(ref calls) == 1 ? new(pending.Task) : ValueTask.FromResult(Token(clock.Now, "recovered")));
        var credential = new DevelopmentModelCredential(inner, clock); using var cancellation = new CancellationTokenSource();
        var abandoned = credential.GetTokenAsync(Context(), cancellation.Token).AsTask(); var observer = credential.GetTokenAsync(Context(), default).AsTask();
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var expected = new AuthenticationFailedException("unit credential failure"); pending.SetException(expected);
        Assert.Same(expected, await Assert.ThrowsAsync<AuthenticationFailedException>(() => observer));
        Assert.Equal("recovered", (await credential.GetTokenAsync(Context(), default)).Token); Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task InnerCancellationDoesNotPoisonTheNextAcquisition()
    {
        var clock = new Clock(); var calls = 0;
        var inner = new Credential((_, _) => Interlocked.Increment(ref calls) == 1 ? ValueTask.FromCanceled<AccessToken>(new(true)) : ValueTask.FromResult(Token(clock.Now)));
        var credential = new DevelopmentModelCredential(inner, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credential.GetTokenAsync(Context(), default).AsTask());
        Assert.Equal("unit-token", credential.GetToken(Context(), default).Token); Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData("expired")] [InlineData("empty")]
    public async Task ExpiredOrEmptyTokensAreNeverServedOrCached(string kind)
    {
        var clock = new Clock(); var inner = new Credential((_, _) => ValueTask.FromResult(Token(clock.Now, kind == "empty" ? "" : "expired-token", kind == "expired" ? 0 : 60)));
        var credential = new DevelopmentModelCredential(inner, clock);
        for (var attempt = 0; attempt < 2; attempt++) await Assert.ThrowsAsync<AuthenticationFailedException>(() => credential.GetTokenAsync(Context(), default).AsTask());
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData("refresh", 10)] [InlineData("expiry", 25)] [InlineData("late-refresh", 25)]
    public async Task RefreshHintAndFiveMinuteExpirySafetyCauseExactlyOneFreshAcquisition(string mode, int boundary)
    {
        var clock = new Clock(); var start = clock.Now; var calls = 0;
        var inner = new Credential((_, _) => ValueTask.FromResult(Interlocked.Increment(ref calls) == 1
            ? Token(start, "first", 30, mode == "refresh" ? 10 : mode == "late-refresh" ? 40 : null) : Token(clock.Now, "second")));
        var credential = new DevelopmentModelCredential(inner, clock); var token = await credential.GetTokenAsync(Context(), default);
        Assert.Equal(start.AddMinutes(boundary), token.RefreshOn); Assert.Equal(start.AddMinutes(30), token.ExpiresOn);
        clock.Now = start.AddMinutes(boundary).AddTicks(-1); Assert.Equal("first", credential.GetToken(Context(), default).Token);
        clock.Now = start.AddMinutes(boundary); Assert.Equal("second", (await credential.GetTokenAsync(Context(), default)).Token); Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task NearExpiryOrPastRefreshHintValidTokensAreNotReused()
    {
        var clock = new Clock();
        foreach (var token in new[] { Token(clock.Now, expiresMinutes: 4), Token(clock.Now, refreshMinutes: -1) })
        {
            var inner = new Credential((_, _) => ValueTask.FromResult(token)); var credential = new DevelopmentModelCredential(inner, clock);
            await credential.GetTokenAsync(Context(), default); await credential.GetTokenAsync(Context(), default); Assert.Equal(2, inner.Calls);
        }
    }

    [Theory]
    [InlineData("other-scope")] [InlineData("case")] [InlineData("multiple")] [InlineData("claims")] [InlineData("tenant")]
    [InlineData("empty-claims")] [InlineData("empty-tenant")] [InlineData("empty-nonce")] [InlineData("empty-method")]
    [InlineData("cae")] [InlineData("pop")] [InlineData("nonce")] [InlineData("uri")] [InlineData("method")]
    public async Task NondefaultAuthorizationContextPassesThroughExactlyAndNeverSharesTheDefaultCache(string kind)
    {
        var clock = new Clock(); var inner = new Credential((_, _) => ValueTask.FromResult(Token(clock.Now))); var credential = new DevelopmentModelCredential(inner, clock);
        await credential.GetTokenAsync(Context(), default); // Warm the default cognitive token; it cannot satisfy a richer request.
        string[] scopes = kind == "other-scope" ? ["https://other.example/.default"] : kind == "multiple" ? [DevelopmentModelCredential.CognitiveScope, "other"]
            : kind == "case" ? [DevelopmentModelCredential.CognitiveScope.ToUpperInvariant()] : [DevelopmentModelCredential.CognitiveScope];
        var context = new TokenRequestContext(scopes, "correlation", kind == "claims" ? "unit-claims" : kind == "empty-claims" ? "" : null, kind == "tenant" ? "unit-tenant" : kind == "empty-tenant" ? "" : null,
            kind == "cae", kind == "pop", kind == "nonce" ? "unit-nonce" : kind == "empty-nonce" ? "" : null, kind == "uri" ? new Uri("https://unit-resource.openai.azure.com/") : null, kind == "method" ? "POST" : kind == "empty-method" ? "" : null);
        using var cancellation = new CancellationTokenSource();
        credential.GetToken(context, cancellation.Token); await credential.GetTokenAsync(context, cancellation.Token);
        Assert.Equal(3, inner.Calls); Assert.Equal(1, inner.SyncCalls); Assert.Same(scopes, inner.LastContext.Scopes);
        Assert.Equal(context, inner.LastContext); Assert.Equal(cancellation.Token, inner.LastCancellation);
        await credential.GetTokenAsync(Context(), default); Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task ActualRouterAndNarratorAdaptersShareAuthenticationWithoutChangingWireOrDisabledGuards()
    {
        var inner = new Credential((_, _) => ValueTask.FromResult(Token(DateTimeOffset.UtcNow))); var credential = new DevelopmentModelCredential(inner);
        using var handler = new ModelHandler(); using var http = new HttpClient(handler); var environment = new Environment();
        var router = new DecisionWorkflowRouter(http, Options.Create(new DecisionRoutingOptions { Enabled = true, Endpoint = "https://unit-resource.services.ai.azure.com/", Deployment = "unit" }), environment, credential);
        var narrator = new AzureMatchChatModel(http, Options.Create(new MatchChatOptions { Enabled = true, Endpoint = "https://unit-resource.openai.azure.com/", Deployment = "unit" }), environment, credential);
        var fixture = SharedFootballTests.Fixture(); var input = new MatchRoutingInput("Unit question", new(SharedFootballTests.Query(fixture, 0), 1), [], null);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(MatchRoutingStatus.Selected, (await router.RouteAsync(input, default)).Status);
            await narrator.CompleteAsync(new([new("system", "unit evidence"), new("user", "unit question")], ["coverage"]), default);
        }
        Assert.Equal(1, inner.Calls); Assert.Equal(2, handler.RouterCalls); Assert.Equal(2, handler.NarratorCalls);
        Assert.Equal(MatchRoutingStatus.Unavailable, (await new DecisionWorkflowRouter(http, Options.Create(new DecisionRoutingOptions()), environment, credential).RouteAsync(input, default)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AzureMatchChatModel(http, Options.Create(new MatchChatOptions()), environment, credential).CompleteAsync(new([new("user", "unit")], ["coverage"]), default));
        Assert.Equal(1, inner.Calls); Assert.Equal(4, handler.RouterCalls + handler.NarratorCalls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Credential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire) : TokenCredential
    {
        public int Calls, SyncCalls; public TokenRequestContext LastContext; public CancellationToken LastCancellation;
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken token)
        { Interlocked.Increment(ref SyncCalls); return GetTokenAsync(context, token).AsTask().GetAwaiter().GetResult(); }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken token)
        { Interlocked.Increment(ref Calls); LastContext = context; LastCancellation = token; return acquire(context, token); }
    }
    private sealed class ModelHandler : HttpMessageHandler
    {
        public int RouterCalls, NarratorCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer unit-token", request.Headers.Authorization?.ToString());
            string json;
            if (request.RequestUri!.AbsolutePath.Contains("systemone", StringComparison.Ordinal))
            {
                RouterCalls++; var probabilities = DecisionWorkflowRouter.Criteria.Keys.ToDictionary(id => id, id => id == "current_match_summary" ? 1.0 : 0.0);
                json = JsonSerializer.Serialize(new { answers = new { workflow = new { type = "choice", choice = "current_match_summary", probabilities } } });
            }
            else { NarratorCalls++; json = """{"id":"unit","object":"chat.completion","created":0,"model":"unit","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"prepared answer"}}]}"""; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "unit"; public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
