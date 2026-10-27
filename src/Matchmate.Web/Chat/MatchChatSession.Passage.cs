using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;

namespace Matchmate.Web.Chat;

public sealed partial class MatchChatSession
{
    // Dedicated app-owned scope: no Decision-1 choice, model call, or user-supplied interval.
    private async Task<MatchChatResult> AskPassageCoreAsync(MatchChatContext context, string question, CancellationToken cancellation)
    {
        if (!MatchPlayerResolver.ValidText(question) || question.Trim().Length is < 1 or > MatchChatLimits.InputChars)
            return new("Please use a question of at most 1,000 characters.", false, MatchChatOutcome.InvalidRequest);
        long generation; CancellationTokenSource request; MatchLanguage language;
        lock (_gate)
        {
            if (_disposed || _context != context) return Stale();
            CancelLocked(); generation = _generation; language = _language;
            request = _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            request.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        }
        try
        {
            var owner = Owner((await TimeAsync(MatchChatTimingStage.Authentication,
                () => _authentication.GetAuthenticationStateAsync().WaitAsync(request.Token))).User);
            MatchWorkflowSnapshot snapshot;
            lock (_gate)
            {
                if (!Current(generation, context)) return Stale();
                if (owner is null) { ClearHistoryLocked(); _owner = null; return new("Sign in with your test account to chat.", false, MatchChatOutcome.Disconnected); }
                if (_owner != owner) { ClearHistoryLocked(); _owner = owner; }
                snapshot = new(owner, generation, context, MatchWorkflowTemporalMode.ReplayAtCursor);
                if (!MatchWorkflowExecutor.IsValidSnapshot(snapshot) || context.SelectedPassage is null)
                    return new(ReplayText.Get(language, "StoryUnavailable"), false, MatchChatOutcome.InvalidRequest);
            }
            var query = context.Query with { ApplicationSequence = context.Query.ApplicationSequence ?? context.HeadSequence };
            var view = await TimeAsync(MatchChatTimingStage.Retrieval, () => _passageApi.FootballViewAsync(query, request.Token).WaitAsync(request.Token));
            if (!Current(snapshot)) return Stale(); request.Token.ThrowIfCancellationRequested();
            if (view.Context != query || view.Dataset.HeadSequence != context.HeadSequence || !ReplayStory.Bound(view)) return Stale();
            var item = ReplayStory.Find(view, context.SelectedPassage, language);
            if (item is null) return new(ReplayText.Get(language, "StoryUnavailable"), false, MatchChatOutcome.Unavailable);
            var source = MatchIllustrationStatistics.Source(view);
            var text = JsonSerializer.Serialize(new { scope = "selected_generated_passage", passage = item.Passage,
                source.SourceId, source.ArtifactHash, origin = "synthetic", method = "generated", claimStatus = "authored" }, FootballCodec.Json);
            if (text.Length > MatchChatLimits.EvidenceChars) return new(ReplayText.Get(language, "StoryUnavailable"), false, MatchChatOutcome.RetrievalFailure);
            var evidence = new MatchChatEvidence("selected_passage", "selected_passage", text, MatchReplyValidation.Numbers(text).Distinct(StringComparer.Ordinal).ToArray());
            return await CommitAsync(snapshot, new(question.Trim(), ReplayStory.Answer(item, language, question), context, item.Team, [evidence], true),
                new("Reply grounded in the selected workflow evidence.", true), request.Token);
        }
        catch (OperationCanceledException) { return new("Reply cancelled.", false, MatchChatOutcome.Cancelled); }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or InvalidOperationException)
        { return new(ReplayText.Get(language, "StoryUnavailable"), false, MatchChatOutcome.RetrievalFailure); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_pending, request)) _pending = null;
            request.Dispose();
        }
    }
}
