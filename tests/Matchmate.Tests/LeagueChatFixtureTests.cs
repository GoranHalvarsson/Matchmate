using System.Security.Cryptography;
using System.Text.Json;
using Matchmate.Web.LeagueChat;
using Xunit;

namespace Matchmate.Tests;

public sealed class LeagueChatFixtureTests
{
    [Fact]
    public void FrozenHeldoutAndTuningRemainDistinctHashPinnedAndContextConsistent()
    {
        var root = HostingTestPaths.Root(); var questions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, count, hash) in new[] { ("heldout", 60, "11054509c9d1d2a72475e643ebe9cf712f9875fd8e55485a3dde766f34edd575"), ("tuning", 20, "401d80ea5baec9dc11ff3d5d51e49870d95526559937abfb865f2718266dc774") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, "tests", "Matchmate.Tests", "Fixtures", "league-chat-" + name + "-v1.json")); Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            using var data = JsonDocument.Parse(bytes); var cases = data.RootElement.GetProperty("cases").EnumerateArray().ToArray(); Assert.Equal(count, cases.Length);
            Assert.Equal(count, cases.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
            foreach (var item in cases)
            {
                Assert.Contains(item.GetProperty("expected").GetString(), LeagueWorkflowRegistry.Criteria.Keys.AsEnumerable()); Assert.True(questions.Add(item.GetProperty("question").GetString()!));
                var context = item.GetProperty("context"); var current = context.GetProperty("seasonKey").GetString() == "2026-27";
                Assert.Equal(current ? 17 : 38, context.GetProperty("dateIndex").GetInt32()); Assert.Equal(current ? "2026-09-20" : "2015-12-05", context.GetProperty("date").GetString());
            }
        }
    }
}
