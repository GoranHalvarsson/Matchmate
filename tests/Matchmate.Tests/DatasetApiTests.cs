using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Xunit;

namespace Matchmate.Tests;

public sealed class DatasetApiTests
{
    [Fact]
    public async Task PublishedPipelineCatalogAndVersionedFramesUseTheExistingServiceAuthorization()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        await ReplayPipelineTests.Publish(blobs, catalog, ReplayPipelineTests.Source(), 1);
        await using var api = ApiServiceAuthenticationTests.Factory(new BlobReplayDatasetStore(blobs, catalog));
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/datasets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/datasets/unit-replay/frame?cursor=0&version=x")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        var dataset = Assert.Single((await client.GetFromJsonAsync<ReplayDataset[]>("/api/datasets"))!);
        Assert.Equal("unit-replay", dataset.DatasetId); Assert.Equal("synthetic", dataset.Metadata.Origin);
        var metadata = await client.GetFromJsonAsync<ReplayDataset>("/api/datasets/unit-replay/metadata");
        Assert.Equal(dataset.DatasetId, metadata!.DatasetId); Assert.Equal(dataset.Metadata.Version, metadata.Metadata.Version);
        var version = dataset.Metadata.Version;
        var before = await client.GetFromJsonAsync<FrameView>($"/api/datasets/unit-replay/frame?cursor=0.099999&version={version}");
        Assert.Equal(0, before!.SourceFrameId); Assert.Equal(0, before.SourceClock);
        var first = await client.GetFromJsonAsync<FrameView>($"/api/datasets/unit-replay/frame?cursor=0&version={version}");
        Assert.Equal("detected", first!.Players[0].Position.Quality); Assert.Equal("estimated", first.Players[1].Position.Quality);
        var gap = await client.GetFromJsonAsync<FrameView>($"/api/datasets/unit-replay/frame?cursor=3.1&version={version}");
        Assert.True(gap!.FullGap); Assert.All(gap.Players, player => Assert.False(player.Position.Available));
        Assert.Null(gap.Ball.X); Assert.False(gap.Ball.Available);
        var end = await client.GetFromJsonAsync<FrameView>($"/api/datasets/unit-replay/frame?cursor=12.9&version={version}");
        Assert.Equal(129, end!.SourceFrameId);
        // Legacy test DTO data cannot make the common canonical pipeline ready.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task DatasetQueriesRejectInvalidIdentityTimeAndStaleVersionAndDoNotExposeCorruptStorage()
    {
        var blobs = new MemoryReplayBlobs(); var catalog = new MemoryReplayCatalog();
        await ReplayPipelineTests.Publish(blobs, catalog, ReplayPipelineTests.Source(), 1);
        await using var api = ApiServiceAuthenticationTests.Factory(new BlobReplayDatasetStore(blobs, catalog));
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        var version = (await catalog.ReadAsync("unit-replay", default))!.Value.Version;
        foreach (var path in new[] { "BAD/metadata", "unit-replay/frame?cursor=NaN&version=x", "unit-replay/frame?cursor=0", $"unit-replay/frame?cursor=13&version={version}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/datasets/" + path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/datasets/unknown/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/datasets/unit-replay/frame?cursor=0&version=old")).StatusCode);
        // A new store has no cached snapshot, so real gzip/integrity checks must reject this object.
        var pointer = (await catalog.ReadAsync("unit-replay", default))!.Value;
        blobs.Content[pointer.SnapshotBlob][0] ^= 0xff;
        var response = await client.GetAsync($"/api/datasets/unit-replay/frame?cursor=0&version={version}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(pointer.SnapshotBlob, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmptyHostedCatalogAndLegacyAustralianRoutesRemainExplicitlyUnavailable()
    {
        await using var api = ApiServiceAuthenticationTests.Factory(); using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiServiceAuthenticationTests.Token());
        Assert.Empty((await client.GetFromJsonAsync<ReplayDataset[]>("/api/datasets"))!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/api/datasets/{DatasetIdentity.Australian}/metadata")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/matches/1874553/metadata")).StatusCode);
    }
}
