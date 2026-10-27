using System.Diagnostics;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;

// Local maintenance, not a host. No network, source import, model, or fallback viewer rebuild.
if (args.Length != 3 || args[0] != "--store")
{
    Console.Error.WriteLine("Usage: Matchmate.ReadIndex --store <directory> --inspect|--prepare|--catalog|<query.json>"); return 2;
}
var directory = Path.GetFullPath(args[1]);
if (!Directory.Exists(Path.Combine(directory, "indexes"))) throw new ArgumentException("Existing journal indexes directory required.");
var store = new ChunkedLocalFootballStore(directory); var journal = store.Journal;
object Counters() => new { journal.MetadataReadBytes, journal.ReadViewReadCount, journal.ReadViewReadBytes, journal.PayloadReadCount, journal.PayloadReadBytes,
    journal.RecoveredRecordCount, journal.LastQueryHeaderCount, journal.RetainedTrackingCount, journal.ReadViewWriteCount, journal.ReadViewWriteBytes };
var timer = Stopwatch.StartNew(); object result;
if (args[2] == "--inspect")
{
    var roots = await new FileFootballIndexes(Path.Combine(directory, "indexes")).ListAsync(default);
    result = roots.Select(r => new { r.Value.DatasetId, r.Value.CommitCount, r.Value.PayloadBytes,
        sourceFenceHash = AzureFootballStore.SourceFenceHash(r.Value), r.Value.DerivedReadView,
        streams = r.Value.Streams.Select(s => new { s.Stream.StreamId, s.Stream.DatasetVersion, s.Head, s.Closed }) }).ToArray();
}
else if (args[2] == "--prepare") result = await store.PrepareReadViewsAsync(default);
else if (args[2] == "--catalog")
{
    var first = await store.CatalogAsync(default); var firstMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
    var repeat = await store.CatalogAsync(default); var repeatMs = timer.Elapsed.TotalMilliseconds;
    result = new { firstMs, repeatMs, datasets = first.Select(d => new { d.Stream.StreamId, d.Manifest.ManifestRevision, d.HeadSequence, d.Closed, people = d.People.Length, d.PendingRecords }), repeatCount = repeat.Length };
}
else
{
    if (new FileInfo(args[2]).Length > 8192) throw new ArgumentException("Query document too large.");
    var query = JsonSerializer.Deserialize<FootballQuery>(await File.ReadAllTextAsync(args[2]), FootballCodec.Json) ?? throw new ArgumentException("Query required.");
    timer.Restart(); var first = await store.QueryAsync(query, default); var firstMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
    var repeat = await store.QueryAsync(query, default); var repeatMs = timer.Elapsed.TotalMilliseconds;
    result = new { firstMs, repeatMs, query, first.Dataset.HeadSequence, first.Dataset.Closed, players = first.Frame?.Players.Count(p => p.Availability == "present"),
        first.TrackingGap, frame = first.Frame?.SourceFrameId, events = first.Events.Length, possessions = first.Possessions.Length, first.HomeGoals, first.AwayGoals, repeatFrame = repeat.Frame?.SourceFrameId };
}
using var process = Process.GetCurrentProcess(); process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new { result, elapsedMs = timer.Elapsed.TotalMilliseconds, counters = Counters(), workingSetBytes = process.WorkingSet64,
    peakWorkingSetBytes = process.PeakWorkingSet64, cpuMs = process.TotalProcessorTime.TotalMilliseconds, osCache = "Process-cold only; OS cache not cleared." }, FootballCodec.Json));
return 0;
