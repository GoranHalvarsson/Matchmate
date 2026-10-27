using Matchmate.Contracts;
using System.Diagnostics.CodeAnalysis;

namespace Matchmate.Core;

public sealed class RecordingValidationException(string message) : Exception(message);
public sealed record SourceDocument(int SchemaVersion, string Source, string Pin, int MatchId,
    string Label, string Perspective, string Competition, string DateTime, Team Home, Team Away,
    Pitch Pitch, int Period, double Start, double End, double Cadence, Player[] Players,
    RecordedFrame[] Frames, string EventPolicy, string Limits);
public sealed record SourceRecording(SourceDocument Document, string Hash, string Origin,
    string License, string SourceUrl, long Bytes, long GzipBytes);
public interface IReplaySource { Task<SourceRecording> ReadAsync(CancellationToken cancellationToken); }
public sealed record ObservationStream(SourceRecording Header, IAsyncEnumerable<RecordedFrame> Frames);
public interface IObservationSource { Task<ObservationStream> OpenAsync(CancellationToken cancellationToken); }
public sealed class RecordingAccumulator(SourceRecording header)
{
    private readonly List<RecordedFrame> _frames = [];
    private readonly HashSet<int> _ids = [];
    public void Accept(RecordedFrame frame)
    {
        ReplayProcessor.ValidateSource(header.Document with { Frames = [frame] });
        if (_frames.Count >= 50_001 || (_frames.Count > 0 && frame.Clock <= _frames[^1].Clock) || !_ids.Add(frame.FrameId))
            throw new RecordingValidationException("Duplicate or out-of-order normalized frame.");
        _frames.Add(frame);
    }
    public ReplaySnapshot Complete() => new ReplayProcessor().Process(header with { Document = header.Document with { Frames = _frames.ToArray() } });
}
public interface IReplayProcessor { ReplaySnapshot Process(SourceRecording source); }
public interface IReplayStore
{
    Task PublishAsync(ReplaySnapshot snapshot, CancellationToken cancellationToken);
    Task<ReplaySnapshot?> ReadAsync(CancellationToken cancellationToken);
}

public sealed class ReplayProcessor : IReplayProcessor
{
    public ReplaySnapshot Process(SourceRecording source)
    {
        var d = source.Document;
        var gaps = new List<CoverageGap>();
        ValidateSource(d);
        var frames = d.Frames.ToDictionary(f => (int)Math.Round((f.Clock - d.Start) / d.Cadence));
        for (var slot = 0; slot <= (int)Math.Round((d.End - d.Start) / d.Cadence); slot++)
        {
            if (frames.TryGetValue(slot, out var frame) && frame.Players.Any(p => p.X.HasValue)) continue;
            var clock = Math.Round(d.Start + slot * d.Cadence, 8);
            if (gaps.Count > 0 && Math.Abs(gaps[^1].End + d.Cadence - clock) < 1e-7)
                gaps[^1] = gaps[^1] with { End = clock, Slots = gaps[^1].Slots + 1 };
            else gaps.Add(new($"gap-{gaps.Count}", clock, clock, 1));
        }
        Require(source.Origin is "real" or "synthetic", "Data origin must be explicit.");
        Require(source.Hash is { Length: 64 } && source.Hash.All(Uri.IsHexDigit), "Invalid source hash.");
        var metadata = new ReplayMetadata(d.MatchId, $"local-replay-v1-{source.Hash}", source.Hash,
            d.Source, d.Pin, source.Origin, d.Competition, d.DateTime, d.Home, d.Away, d.Pitch,
            d.Period, d.Start, d.End, d.Cadence, d.Players, d.Frames.Length,
            d.Frames.Count(f => f.Players.Length == 0), gaps.ToArray(), source.Bytes, source.GzipBytes,
            source.License, source.SourceUrl, d.Limits,
            "Accelerated recorded import; complete immutable version, not wall-clock live ingestion.");
        return new(1, metadata, d.Frames);
    }

    public static void ValidateSource(SourceDocument d)
    {
        Require(d is not null, "Missing recording.");
        Require(d!.SchemaVersion == 1 && d.MatchId > 0 && d.Period > 0, "Unsupported recording identity/schema.");
        Require(!string.IsNullOrWhiteSpace(d.Source) && !string.IsNullOrWhiteSpace(d.Pin)
            && !string.IsNullOrWhiteSpace(d.Competition) && !string.IsNullOrWhiteSpace(d.Limits), "Missing provenance.");
        Require(double.IsFinite(d.Start) && double.IsFinite(d.End) && double.IsFinite(d.Cadence)
            && d.Start >= 0 && d.End >= d.Start && d.Cadence > 0 && (d.End - d.Start) / d.Cadence <= 50_000,
            "Invalid time range/cadence.");
        Require(d.Pitch is not null && double.IsFinite(d.Pitch.Length) && double.IsFinite(d.Pitch.Width)
            && d.Pitch.Length > 0 && d.Pitch.Width > 0 && d.Pitch.Units == "metres" && d.Pitch.Origin == "centre",
            "Unsupported coordinate semantics.");
        Require(d.Home is not null && d.Away is not null && d.Home.Id != d.Away.Id, "Invalid teams.");
        Require(d.Players is { Length: > 0 and <= 64 } && d.Players.All(p => p is not null)
            && d.Players.Select(p => p.Id).Distinct().Count() == d.Players.Length,
            "Invalid or duplicate roster.");
        Require(d.Players!.All(p => p.Id > 0 && (p.TeamId == d.Home!.Id || p.TeamId == d.Away!.Id)
            && !string.IsNullOrWhiteSpace(p.Name) && p.Name.Length <= 150), "Invalid player identity.");
        Require(d.Frames is { Length: > 0 and <= 50_001 }, "Invalid frame count.");
        var roster = d.Players.Select(p => p.Id).ToHashSet();
        var last = double.NegativeInfinity;
        var ids = new HashSet<int>();
        foreach (var frame in d.Frames!)
        {
            Require(frame is not null, "Null frame.");
            Require(double.IsFinite(frame.Clock) && frame.Clock >= d.Start && frame.Clock <= d.End
                && frame.Clock > last && frame.Period == d.Period && ids.Add(frame.FrameId), "Invalid frame time/identity/order.");
            var slot = (frame.Clock - d.Start) / d.Cadence;
            Require(Math.Abs(slot - Math.Round(slot)) < 1e-6, "Frame is outside source cadence.");
            Require(frame.Players is not null && frame.Players.All(p => p is not null) && frame.Players.Length <= roster.Count
                && frame.Players.Select(p => p.PlayerId).Distinct().Count() == frame.Players.Length,
                "Invalid/duplicate frame players.");
            foreach (var p in frame.Players!)
                Require(roster.Contains(p.PlayerId) && Coordinates(p.X, p.Y), "Unknown player or invalid coordinates.");
            Require(frame.Ball is not null && Coordinates(frame.Ball.X, frame.Ball.Y), "Invalid ball coordinates.");
            Require(frame.FrameState == (frame.Players.Length == 0 ? "empty-positions" : "positions-present"), "Frame-state mismatch.");
            last = frame.Clock;
        }
    }

    private static bool Coordinates(double? x, double? y) =>
        x is null && y is null || x.HasValue && y.HasValue && double.IsFinite(x.Value) && double.IsFinite(y.Value);
    private static void Require([DoesNotReturnIf(false)] bool valid, string message)
    { if (!valid) throw new RecordingValidationException(message); }
}

public static class ReplayQuery
{
    public static FrameView At(ReplaySnapshot recording, double cursor)
    {
        var m = recording.Metadata;
        if (!double.IsFinite(cursor) || cursor < m.Start || cursor > m.End)
            throw new ArgumentOutOfRangeException(nameof(cursor));
        var low = 0; var high = recording.Frames.Length - 1;
        RecordedFrame? frame = null;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (recording.Frames[mid].Clock <= cursor) { frame = recording.Frames[mid]; low = mid + 1; }
            else high = mid - 1;
        }
        if (frame is not null && cursor >= Math.Round(frame.Clock + m.Cadence, 8)) frame = null;
        var positions = frame?.Players.ToDictionary(p => p.PlayerId) ?? [];
        var players = m.Players.Select(p => new PlayerView(p,
            positions.TryGetValue(p.Id, out var observed)
                ? Observe(observed.X, observed.Y, observed.IsDetected, observed.MissingReason, m.Pitch)
                : Observe(null, null, null, "entity-not-in-frame", m.Pitch))).ToArray();
        var count = players.Count(p => p.Position.Available);
        var state = frame is null ? "missing-frame" : frame.Players.Length == 0 ? "empty-positions"
            : count == 0 ? "no-valid-positions" : count < players.Length ? "partial-positions" : "available-positions";
        var b = frame?.Ball;
        return new(m.Version, m.MatchId, cursor, frame?.Clock, frame?.FrameId, state, count == 0, count,
            players, Observe(b?.X, b?.Y, b?.IsDetected, b?.MissingReason, m.Pitch));
    }

    private static Observation Observe(double? x, double? y, bool? detected, string? reason, Pitch pitch)
    {
        var present = x.HasValue && y.HasValue;
        return new(x, y, present, !present ? "missing" : detected == true ? "detected" : detected == false ? "estimated" : "unknown",
            present ? null : reason ?? "entity-not-in-frame",
            present && (Math.Abs(x!.Value) > pitch.Length / 2 || Math.Abs(y!.Value) > pitch.Width / 2));
    }
}
