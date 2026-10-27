using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

// One immutable accepted batch is the write-ahead record and membership commit. Readers never see temporary files.
public sealed class LocalFootballStore(string directory) : IFootballStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AcceptedFootballBatch[] _accepted = [];
    private string _signature = "";
    private FootballLedger _ledger = new();
    public async Task<bool> AppendAsync(FootballBatch batch, CancellationToken token)
    {
        FootballCodec.Validate(batch); Directory.CreateDirectory(directory);
        await _gate.WaitAsync(token);
        try
        {
            // A second producer cannot race the local writer. API processes only read immutable commits.
            await using var writer = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await LoadAsync(token);
            var candidate = new FootballLedger(); foreach (var accepted in _accepted) candidate.Accept(accepted);
            var next = new AcceptedFootballBatch(batch, DateTimeOffset.UtcNow);
            if (!candidate.Accept(next)) return false;
            var destination = Path.Combine(directory, $"{batch.Stream.StreamId}--{batch.Sequence:D12}.json");
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next, FootballCodec.Json);
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(flushToDisk: true); }
                token.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: false);
                _signature = ""; // Publication is visible only after durable atomic rename.
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
    public async Task<FootballLedger> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await LoadAsync(token); return _ledger; }
        finally { _gate.Release(); }
    }
    private async Task LoadAsync(CancellationToken token)
    {
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray() : [];
        var signature = string.Join('|', files.Select(path => path + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + new FileInfo(path).Length));
        if (signature == _signature) return;
        var ledger = new FootballLedger(); var accepted = new List<AcceptedFootballBatch>();
        foreach (var path in files)
        {
            var bytes = await BoundedFile.ReadAsync(path, FootballCodec.MaxBatchBytes + 256, token);
            var commit = JsonSerializer.Deserialize<AcceptedFootballBatch>(bytes, FootballCodec.Json) ?? throw new RecordingValidationException("Missing durable batch.");
            FootballCodec.Validate(commit.Batch);
            if (Path.GetFileName(path) != $"{commit.Batch.Stream.StreamId}--{commit.Batch.Sequence:D12}.json") throw new RecordingValidationException("Commit filename/binding mismatch.");
            ledger.Accept(commit); accepted.Add(commit);
        }
        _accepted = accepted.ToArray(); _ledger = ledger; _signature = signature;
    }
}
