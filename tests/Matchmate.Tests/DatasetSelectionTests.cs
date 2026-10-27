using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web;
using Matchmate.Web.Components.Pages;
using Xunit;

namespace Matchmate.Tests;

public sealed class DatasetSelectionTests
{
    [Fact]
    public void SwitchingRejectsLateResultsEvenWhenDifferentDatasetsShareMatchAndVersion()
    {
        var snapshot = new ReplayProcessor().Process(ReplayPipelineTests.Source());
        var first = new ReplayDataset("first-recording", 1, snapshot.Metadata, DateTimeOffset.UtcNow);
        using var selection = new DatasetSelection(); var old = selection.Change(first.DatasetId);
        Assert.True(selection.AcceptMetadata(old, first));
        var current = selection.Change("next-recording");
        Assert.True(old.Token.IsCancellationRequested); Assert.Null(selection.Dataset);
        Assert.False(selection.AcceptMetadata(old, first));
        Assert.False(selection.AcceptFrame(old, ReplayQuery.At(snapshot, 0)));
        var next = first with { DatasetId = "next-recording" };
        Assert.True(selection.AcceptMetadata(current, next));
        Assert.False(selection.AcceptMetadata(current, first));
        Assert.True(selection.AcceptFrame(current, ReplayQuery.At(snapshot, .1)));
        Assert.False(selection.AcceptFrame(current, ReplayQuery.At(snapshot, .1) with { Version = "obsolete" }));
        Assert.False(selection.AcceptFrame(current, ReplayQuery.At(snapshot, .1) with { MatchId = 99 }));
    }

    [Fact]
    public async Task ActualPageSwitchClearsOldFramePlayerCursorAndPlaybackBeforeMetadataReturns()
    {
        var snapshot = new ReplayProcessor().Process(ReplayPipelineTests.Source());
        var descriptor = new ReplayDataset("next-recording", 1, snapshot.Metadata, DateTimeOffset.UtcNow);
        var metadataReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new PendingMetadataHandler(metadataReply, ReplayQuery.At(snapshot, 0));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await using var page = new Home(); var cursor = new ViewerCursor(snapshot.Metadata); cursor.Play();
        Set(page, "Api", new ReplayApiClient(http)); Set(page, "_metadata", snapshot.Metadata);
        Set(page, "_cursor", cursor); Set(page, "_frame", ReplayQuery.At(snapshot, 0)); Set(page, "_selected", 1);
        var pending = (Task)typeof(Home).GetMethod("SwitchDatasetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [descriptor.DatasetId])!;
        Assert.False(pending.IsCompleted); Assert.False(cursor.Playing);
        foreach (var field in new[] { "_metadata", "_cursor", "_frame", "_selected" }) Assert.Null(Get(page, field));
        Assert.Equal("next-recording", Get(page, "_activeDatasetId"));
        metadataReply.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(descriptor) });
        await pending; Assert.Equal(snapshot.Metadata.Version, ((FrameView)Get(page, "_frame")!).Version);
        Assert.Contains("/api/datasets/next-recording/frame", handler.LastFramePath);
    }

    private static object? Get(Home page, string name) => typeof(Home).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static void Set(Home page, string name, object? value)
    {
        var property = typeof(Home).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (property is not null) property.SetValue(page, value);
        else typeof(Home).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    }
    private sealed class PendingMetadataHandler(TaskCompletionSource<HttpResponseMessage> metadata, FrameView frame) : HttpMessageHandler
    {
        public string LastFramePath = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal)) return metadata.Task;
            LastFramePath = request.RequestUri.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(frame) });
        }
    }
}
