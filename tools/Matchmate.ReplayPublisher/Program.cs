using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.ReplayPublisher;

const string homeTenant = "800542d5-c10f-4db2-97fb-618f0ce3bcd8";
const string blobEndpoint = "https://stmmdev7ntncaxgzqagy.blob.core.windows.net/";
const string cosmosEndpoint = "https://cosmos-matchmate-dev-7ntncaxgzqagy.documents.azure.com:443/";
const string eventHost = "evhns-matchmate-dev-7ntncaxgzqagy.servicebus.windows.net";
const string eventHub = "match-observations";
if (args.FirstOrDefault() == "--export-paced-demo")
{
    using var cancellation = PacedCancellation();
    var result = await PacedReplayDemo.ExportAsync(PacedReplayExportOptions.Parse(args), cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result.Readiness, FootballCodec.Json));
    Console.WriteLine("Offline paced demo export complete. No cloud calls; generated replay delivery is not a measured live feed."); return;
}
if (args.Length == 2 && args[0] == "--preflight-paced")
{
    using var cancellation = PacedCancellation();
    Console.WriteLine(JsonSerializer.Serialize((await PacedReplayDemo.ValidateAsync(Path.GetFullPath(args[1]), cancellation.Token)).Readiness, FootballCodec.Json)); return;
}
if (args.FirstOrDefault() == "--publish-paced")
{
    var options = PacedReplayPublishOptions.Parse(args); using var cancellation = PacedCancellation();
    // All offline integrity/progression checks finish before creating any Azure clients.
    var validated = await PacedReplayDemo.ValidateAsync(options.Directory, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { mode = options.DryRun ? "paced-dry-run" : "paced-publish", options.DelayMs,
        scheduledDelayMs = validated.Readiness.ScheduledDelayMs / PacedReplayPublishOptions.DefaultDelayMs * options.DelayMs,
        readiness = validated.Readiness }, FootballCodec.Json));
    if (options.DryRun) return;
    var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = homeTenant });
    var archive = new AzureReplayBlobs(new BlobServiceClient(new Uri(blobEndpoint), credential).GetBlobContainerClient("recordings"));
    foreach (var artifact in validated.Manifest.Artifacts)
        await archive.PutImmutableAsync("football/source-artifacts/" + artifact.Hash + "/" + Path.GetFileName(artifact.File),
            await ReplayExport.ReadArtifactAsync(options.Directory, artifact, cancellation.Token), cancellation.Token);
    await using var producer = new EventHubProducerClient(eventHost, eventHub, credential,
        new EventHubProducerClientOptions { ConnectionOptions = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpWebSockets } });
    try
    {
        await PacedReplayDemo.PublishAsync(options.Directory, options.DelayMs, async (batch, transport, token) =>
            await producer.SendAsync([new EventData(transport.Bytes) { ContentType = transport.ContentType, MessageId = PacedReplayDemo.MessageId(batch) }],
                new SendEventOptions { PartitionKey = batch.Stream.DatasetId }, token),
            receipt => Console.WriteLine(JsonSerializer.Serialize(receipt, FootballCodec.Json)), cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.WriteLine("Paced publish cancelled. Already accepted batches remain; rerun the identical export for stable retries."); return;
    }
    Console.WriteLine("Paced sends complete; durable Worker acceptance and hosted UI verification remain separate gates."); return;
}
if (args.Length == 2 && args[0] == "--publish-reference")
{
    var bytes = await BoundedFile.ReadAsync(Path.GetFullPath(args[1]), LeagueReferenceCodec.MaxBytes, default);
    var reference = LeagueReferenceCodec.Decode(bytes);
    var referencePath = reference.Version == LeagueReferenceCodec.CurrentVersion ? LeagueReferenceCodec.CurrentBlobPath : LeagueReferenceCodec.BlobPath;
    var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = homeTenant });
    var archive = new AzureReplayBlobs(new BlobServiceClient(new Uri(blobEndpoint), credential).GetBlobContainerClient("recordings"));
    await archive.PutImmutableAsync(referencePath, bytes, default);
    Console.WriteLine(JsonSerializer.Serialize(new { reference.Version, blob = referencePath, bytes = bytes.Length,
        sha256 = ReplayBatchCodec.Hash(bytes), seasons = reference.Seasons.Select(s => new { s.SeasonKey, clubs = s.Clubs.Length, matches = s.Matches.Length }) }, LeagueReferenceCodec.Json));
    return;
}
if (args.FirstOrDefault() == "--export-local")
{
    var options = ReplayExportOptions.Parse(args);
    Console.WriteLine($"Offline export uses explicit Brentford manifest: {options.BrentfordManifest}");
    var validated = await ReplayExport.ExportAsync(options, LocalPaths.FindRoot());
    Console.WriteLine(JsonSerializer.Serialize(validated.Readiness, FootballCodec.Json));
    Console.WriteLine("Offline export/preflight complete. No cloud calls; durable hosted acceptance remains a separate gate."); return;
}
if (args.Length == 2 && args[0] is "--preflight" or "--validate-export")
{
    var validated = await ReplayExport.ValidateAsync(Path.GetFullPath(args[1]));
    Console.WriteLine(JsonSerializer.Serialize(validated.Readiness, FootballCodec.Json)); return;
}
if (args.Length == 2 && args[0] == "--publish")
{
    var directory = Path.GetFullPath(args[1]); var manifest = (await ReplayExport.ValidateAsync(directory)).Manifest;
    var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = homeTenant });
    var archive = new AzureReplayBlobs(new BlobServiceClient(new Uri(blobEndpoint), credential).GetBlobContainerClient("recordings"));
    // Preserve permitted source material privately, once by hash. No service image contains these files.
    foreach (var artifact in manifest.Artifacts)
        await archive.PutImmutableAsync("football/source-artifacts/" + artifact.Hash + "/" + Path.GetFileName(artifact.File), await ReplayExport.ReadArtifactAsync(directory, artifact), default);
    await using var producer = new EventHubProducerClient(eventHost, eventHub, credential,
        new EventHubProducerClientOptions { ConnectionOptions = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpWebSockets } });
    foreach (var chunk in manifest.Chunks)
    {
        var bytes = await ReplayExport.ReadChunkAsync(directory, chunk); var decoded = FootballTransport.Decode(bytes, chunk.ContentType);
        await producer.SendAsync([new EventData(bytes) { ContentType = chunk.ContentType, MessageId = decoded.Stream.StreamId + ":" + decoded.Sequence + ":" + decoded.Digest }], new SendEventOptions { PartitionKey = decoded.Stream.DatasetId });
        Console.WriteLine($"Published {decoded.Stream.DatasetId}/{decoded.Stream.StreamId}/{decoded.Sequence} digest={decoded.Digest} bytes={bytes.Length}");
    }
    Console.WriteLine("Send complete; durable Worker acceptance/catalog verification is a separate gate."); return;
}
if (args.Length == 1 && args[0] == "--inspect")
{
    using var resources = new AzureFootballResources(blobEndpoint, cosmosEndpoint, "football", "replays", new AzureCliCredential(new AzureCliCredentialOptions { TenantId = homeTenant }));
    foreach (var index in await resources.Indexes.ListAsync(default))
        Console.WriteLine(JsonSerializer.Serialize(new { index.Value.DatasetId, index.ETag, index.Value.CommitCount, index.Value.PayloadBytes,
            streams = index.Value.Streams, pages = index.Value.Pages.Select(p => new { p.Number, p.Hash, p.Count, p.Bytes }) }, FootballCodec.Json));
    foreach (var dataset in await resources.Store.CatalogAsync(default))
    {
        Console.WriteLine(JsonSerializer.Serialize(new { dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Closed, dataset.HeadSequence,
            people = dataset.People.Length, dataset.PendingRecords, dataset.Manifest.Capabilities, dataset.Manifest.Normalization }, FootballCodec.Json));
        if (dataset.Schedule is null) continue;
        var schedule = dataset.Schedule; var start = schedule.Segments[0].CursorStartMs; var end = schedule.Segments[^1].CursorEndMs;
        foreach (var cursor in schedule.Milestones.Select(m => m.CursorMs).Concat(new[] { start, start + (end - start) / 2, end }).Distinct().Order())
        {
            var view = await resources.Store.QueryAsync(new(dataset.Manifest.DatasetId, dataset.Manifest.DatasetVersion, dataset.Stream.StreamId, dataset.Manifest.FixtureId, cursor, "retrospective_replay", schedule.PolicyId, schedule.PolicyRevision), default);
            Console.WriteLine(JsonSerializer.Serialize(new { dataset.Stream.StreamId, cursor, view.PeriodId, view.LocalMs, view.TrackingGap,
                frame = view.Frame?.SourceFrameId, positions = view.Frame?.Players.Length, active = view.Frame?.ActiveSubjectIds.Length,
                payloadHash = view.Frame is null ? null : ReplayBatchCodec.Hash(JsonSerializer.SerializeToUtf8Bytes(view.Frame, FootballCodec.Json)),
                events = view.Events.Select(e => e.RecordId), possessions = view.Possessions.Length }, FootballCodec.Json));
        }
    }
    return;
}
throw new ArgumentException("Use --export-local DIRECTORY --brentford-manifest FILE [--dataset brentford-existing-illustration], --preflight DIRECTORY (alias --validate-export), --publish DIRECTORY, --export-paced-demo DIRECTORY --source-export DIRECTORY [--source-dataset ID], --preflight-paced DIRECTORY, --publish-paced DIRECTORY [--delay-ms 100..2500] [--dry-run], --inspect, or --publish-reference BUNDLE_FILE. Publishing/inspection use the existing Azure CLI operator session; no connection strings or automatic login.");

static CancellationTokenSource PacedCancellation()
{
    var source = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; source.Cancel(); };
    return source;
}
