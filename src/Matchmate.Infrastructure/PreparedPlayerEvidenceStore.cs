using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Matchmate.Contracts;
using Matchmate.Core;

namespace Matchmate.Infrastructure;

public sealed class PreparedPlayerEvidenceStore
{
    private readonly PlayerEvidenceProjection _projection;
    public PreparedPlayerEvidenceStore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > PlayerEvidenceLimits.PackBytes) throw new InvalidDataException("Invalid prepared player pack size.");
        PreparedPlayerEvidencePack pack;
        try { pack = JsonSerializer.Deserialize<PreparedPlayerEvidencePack>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20 }) ?? throw new InvalidDataException("Missing prepared player pack."); }
        catch (JsonException e) { throw new InvalidDataException("Invalid prepared player pack JSON.", e); }
        _projection = new(pack, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    public static PreparedPlayerEvidenceStore LoadEmbedded()
    {
        using var stream = typeof(PreparedPlayerEvidenceStore).Assembly.GetManifestResourceStream("Matchmate.PlayerBriefs.v1.json")
            ?? throw new InvalidDataException("Missing embedded prepared player evidence.");
        if (stream.Length is < 1 or > PlayerEvidenceLimits.PackBytes) throw new InvalidDataException("Invalid embedded player evidence size.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return new(bytes);
    }
    public FootballView Attach(FootballView view) => _projection.Attach(view);
}
