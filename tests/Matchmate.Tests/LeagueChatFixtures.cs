using System.Security.Claims;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Chat;
using Matchmate.Web.LeagueChat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Matchmate.Tests;

internal static class LeagueChatFixtures
{
    public static readonly string[] Ids = ["arsenal", "aston-villa", "bournemouth", "brentford", "brighton", "chelsea", "coventry", "crystal-palace", "everton", "fulham", "hull", "ipswich", "leeds", "liverpool", "manchester-city", "manchester-united", "newcastle", "nottingham-forest", "sunderland", "tottenham"];
    private static readonly string[] Names = ["Arsenal", "Aston Villa", "AFC Bournemouth", "Brentford", "Brighton & Hove Albion", "Chelsea", "Coventry City", "Crystal Palace", "Everton", "Fulham", "Hull City", "Ipswich Town", "Leeds United", "Liverpool", "Manchester City", "Manchester United", "Newcastle United", "Nottingham Forest", "Sunderland", "Tottenham Hotspur"];
    public static LeagueSeasonModel Season(bool historical = false)
    {
        var clubs = Ids.Select((id, i) => new LeagueClub(id, Names[i], "TST", "Test town", "Test ground " + Names[i], 100 + i, 200, false, null)).ToArray();
        var games = new List<LeagueMatch>(); var year = historical ? 2015 : 2026;
        for (var leg = 0; leg < (historical ? 2 : 1); leg++)
        {
            var ring = Enumerable.Range(0, 20).ToList();
            for (var round = 0; round < (historical ? 19 : 5); round++)
            {
                var date = historical ? new DateOnly(year, 8, 1).AddDays(leg * 19 + round) : new DateOnly(year, 8, 21).AddDays(round == 4 ? 30 : round * 7);
                for (var pair = 0; pair < 10; pair++)
                {
                    var home = ring[pair]; var away = ring[19 - pair]; if (leg == 1) (home, away) = (away, home);
                    games.Add(new(Ids[home] + ":" + Ids[away], date.ToString("yyyy-MM-dd"), Ids[home], Ids[away], (home + round) % 3, (away + round) % 3, "Synthetic test round"));
                }
                var last = ring[^1]; ring.RemoveAt(19); ring.Insert(1, last);
            }
        }
        return new(new(historical ? "2015-16" : "2026-27", historical ? "2015/16" : "2026/27", clubs, games.ToArray(), historical ? "complete" : "ongoing", 380,
            historical ? null : "2026-09-20", historical ? null : "2026-10-23T12:15:59Z"), sources: [new("synthetic-test", "inline synthetic fixtures only", new string('a', 64), "Test only")], version: LeagueReferenceCodec.CurrentVersion);
    }
    public static LeagueSeasonSnapshot Snapshot(int? index = null, string? club = "everton", bool historical = false) => Season(historical).Snapshot(index, club);
    public static LeagueWorkflowExecutor Executor(Action<string, int?, string?>? reading = null) => new((season, index, club, _) =>
    { reading?.Invoke(season, index, club); return Task.FromResult(Snapshot(index, club, season == "2015-16")); });
    public static LeagueChatSession Session(Auth? auth = null, Router? router = null, Model? model = null, LeagueWorkflowExecutor? executor = null)
        => new(auth ?? new(), executor ?? Executor(), router ?? new(), model ?? new(), Options.Create(new MatchChatOptions { Enabled = true, TimeoutSeconds = 10 }));
    public sealed class Auth : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; private set; } = Principal("first");
        public Func<Task<AuthenticationState>>? Reading { get; set; }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Reading?.Invoke() ?? Task.FromResult(new AuthenticationState(User));
        public void Change(string? subject, bool notify = true) { User = subject is null ? new(new ClaimsIdentity()) : Principal(subject); if (notify) NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(User))); }
        private static ClaimsPrincipal Principal(string sub) => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, sub, ClaimValueTypes.String, "test-issuer"), new(ClaimTypes.Role, "Tester")], "Test"));
    }
    public sealed class Router : ILeagueWorkflowRouter
    {
        public bool Configured { get; set; } = true;
        public string Workflow { get; set; } = "club_standing";
        public List<LeagueRoutingInput> Inputs { get; } = [];
        public Func<LeagueRoutingInput, CancellationToken, Task<MatchRoutingResult>>? Routing { get; set; }
        public Task<MatchRoutingResult> RouteAsync(LeagueRoutingInput input, CancellationToken token)
        { Inputs.Add(input); return Routing?.Invoke(input, token) ?? Task.FromResult(new MatchRoutingResult(MatchRoutingStatus.Selected, Workflow)); }
    }
    public sealed class Model : ILeagueChatModel
    {
        public bool Configured { get; set; } = true;
        public List<LeagueNarratorRequest> Requests { get; } = [];
        public Func<LeagueNarratorRequest, CancellationToken, Task<MatchModelReply>>? Completing { get; set; }
        public Task<MatchModelReply> CompleteAsync(LeagueNarratorRequest request, CancellationToken token)
        {
            Requests.Add(request); if (Completing is not null) return Completing(request, token);
            var prepared = request.Messages.Single(m => m.Content.StartsWith("Prepared evidence for this turn only:", StringComparison.Ordinal));
            using var data = JsonDocument.Parse(prepared.Content[(prepared.Content.IndexOf('\n') + 1)..]); var first = data.RootElement[0];
            using var text = JsonDocument.Parse(first.GetProperty("Text").GetString()!);
            return Task.FromResult(new MatchModelReply(JsonSerializer.Serialize(new { answer = text.RootElement.GetProperty("sourceAnswer").GetString(), evidenceIds = new[] { first.GetProperty("Id").GetString() } }), []));
        }
    }
}
