using Matchmate.Contracts;

namespace Matchmate.Core;

public sealed record DatasetRequest(string DatasetId, long Generation, CancellationToken Token);
// Circuit-local selection identity. Cancellation alone is insufficient: a transport may finish after cancellation.
public sealed class DatasetSelection : IDisposable
{
    private CancellationTokenSource _request = new();
    private long _generation;
    private string _datasetId = "";
    private bool _disposed;
    public ReplayDataset? Dataset { get; private set; }
    public DatasetRequest Current => new(_datasetId, _generation, _request.Token);
    public DatasetRequest Change(string datasetId)
    {
        if (!DatasetIdentity.IsValid(datasetId)) throw new ArgumentException("Invalid dataset identity.", nameof(datasetId));
        _request.Cancel(); _request.Dispose(); _request = new();
        _datasetId = datasetId; _generation++; Dataset = null;
        return Current;
    }
    public bool IsCurrent(DatasetRequest request) => !_disposed && !request.Token.IsCancellationRequested
        && request.Generation == _generation && request.DatasetId == _datasetId;
    public bool AcceptMetadata(DatasetRequest request, ReplayDataset dataset)
    {
        if (!IsCurrent(request) || dataset.DatasetId != request.DatasetId || dataset.Metadata is null || string.IsNullOrWhiteSpace(dataset.Metadata.Version)) return false;
        Dataset = dataset; return true;
    }
    public bool AcceptFrame(DatasetRequest request, FrameView frame) => IsCurrent(request) && Dataset is not null
        && frame.Version == Dataset.Metadata.Version && frame.MatchId == Dataset.Metadata.MatchId;
    public void Dispose() { _disposed = true; _request.Cancel(); _request.Dispose(); }
}
