using Azure.Core;
using Azure.Identity;

namespace Matchmate.Web.Chat;

// Local Development registration only. One default cognitive audience, one memory-only token/flight.
// Richer authorization contexts remain the inner credential's responsibility and are never shared.
public sealed class DevelopmentModelCredential(TokenCredential inner, TimeProvider? clock = null) : TokenCredential
{
    public const string CognitiveScope = "https://cognitiveservices.azure.com/.default";
    private static readonly TimeSpan ExpirySafety = TimeSpan.FromMinutes(5);
    private readonly TokenCredential _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private AccessToken? _cached;
    private Task<AccessToken>? _flight;

    public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!DefaultContext(context)) return _inner.GetToken(context, cancellationToken);
        return Shared(context).WaitAsync(cancellationToken).GetAwaiter().GetResult();
    }
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!DefaultContext(context)) return await _inner.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        return await Shared(context).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private static bool DefaultContext(TokenRequestContext context) => context.Scopes is { Length: 1 }
        && string.Equals(context.Scopes[0], CognitiveScope, StringComparison.Ordinal)
        && context.Claims is null && context.TenantId is null
        && !context.IsCaeEnabled && !context.IsProofOfPossessionEnabled && context.ProofOfPossessionNonce is null
        && context.ResourceRequestUri is null && context.ResourceRequestMethod is null;

    private Task<AccessToken> Shared(TokenRequestContext context)
    {
        TaskCompletionSource<AccessToken> completion;
        lock (_gate)
        {
            if (_cached is { } cached && Fresh(cached, _clock.GetUtcNow())) return Task.FromResult(cached);
            if (_flight is not null) return _flight;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _flight = completion.Task;
        }
        // Snapshot the one allowed scope; ParentRequestId is correlation, not an authorization boundary.
        _ = AcquireAsync(new([CognitiveScope], context.ParentRequestId), completion);
        return completion.Task;
    }
    private static DateTimeOffset RefreshAt(AccessToken token)
    {
        var safeExpiry = token.ExpiresOn - ExpirySafety;
        return token.RefreshOn is { } hint && hint < safeExpiry ? hint : safeExpiry;
    }
    private static bool Fresh(AccessToken token, DateTimeOffset now) => !string.IsNullOrEmpty(token.Token) && now < RefreshAt(token);
    private async Task AcquireAsync(TokenRequestContext context, TaskCompletionSource<AccessToken> completion)
    {
        try
        {
            // No waiter owns shared acquisition cancellation. The inner credential keeps its existing timeout.
            var token = await _inner.GetTokenAsync(context, CancellationToken.None).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            if (string.IsNullOrEmpty(token.Token) || token.ExpiresOn <= now) throw new AuthenticationFailedException("The local model credential returned an unusable token.");
            // Give the SDK the same conservative refresh boundary; the actual expiry is unchanged.
            token = new(token.Token, token.ExpiresOn, RefreshAt(token), token.TokenType);
            lock (_gate)
            {
                _cached = Fresh(token, now) ? token : null;
                if (ReferenceEquals(_flight, completion.Task)) _flight = null;
            }
            completion.TrySetResult(token);
        }
        catch (OperationCanceledException exception)
        {
            Clear(completion); completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            Clear(completion); completion.TrySetException(exception);
            _ = completion.Task.Exception; // Observe failures even when every individual waiter has cancelled.
        }
    }
    private void Clear(TaskCompletionSource<AccessToken> completion)
    {
        lock (_gate)
        {
            _cached = null;
            if (ReferenceEquals(_flight, completion.Task)) _flight = null;
        }
    }
}
