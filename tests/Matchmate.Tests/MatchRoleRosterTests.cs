#pragma warning disable BL0006 // Render-tree assertions follow the existing component tests; no production reliance on these APIs.
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Matchmate.Contracts;
using Matchmate.Infrastructure;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Matchmate.Tests;

public sealed class MatchRoleRosterTests
{
    [Fact]
    public async Task ExactFixtureHasTwentyFourScopedDescriptionsAndSixteenExplicitUnknowns()
    {
        var view = await GeneratedPlayerDistanceTests.ExistingView(0);
        var before = JsonSerializer.Serialize(view);
        var roles = MatchRoleCatalog.Embedded.Project(view);
        Assert.Equal(24, roles.Count); Assert.Equal(16, view.Dataset.People.Count(p => !roles.ContainsKey(p.PersonId)));
        Assert.Equal(18, roles.Values.Count(r => r.Scope == MatchRoleScope.ListedCategory));
        Assert.Equal(4, roles.Values.Count(r => r.Scope == MatchRoleScope.MatchReportDescription));
        Assert.Single(roles.Values, r => r.Scope == MatchRoleScope.StartingPosition);
        Assert.Single(roles.Values, r => r.Scope == MatchRoleScope.StartingLine);
        Assert.Equal((MatchRole.RightBack, MatchRoleScope.StartingPosition), (roles[B("kayode")].Role, roles[B("kayode")].Scope));
        Assert.Equal((MatchRole.Midfield, MatchRoleScope.StartingLine), (roles[B("damsgaard")].Role, roles[B("damsgaard")].Scope));
        Assert.Equal(MatchRole.Midfielder, roles[B("yarmoliuk")].Role);
        Assert.Equal(MatchRole.Striker, roles[B("thiago")].Role); Assert.Equal(MatchRole.Forward, roles[B("schade")].Role);
        Assert.Equal(MatchRole.Winger, roles[C("neto")].Role);
        Assert.False(roles.ContainsKey(B("kelleher"))); Assert.False(roles.ContainsKey(C("martinez")));
        Assert.Equal(MatchRole.Goalkeeper, roles[B("balcombe")].Role); Assert.Equal(MatchRole.Midfielder, roles[C("quenda")].Role);
        Assert.Equal(before, JsonSerializer.Serialize(view));
        Assert.Equal(roles.OrderBy(x => x.Key), MatchRoleCatalog.Embedded.Project(await GeneratedPlayerDistanceTests.ExistingView(5880000)).OrderBy(x => x.Key));
    }

    [Theory]
    [InlineData("version")] [InlineData("stream")] [InlineData("fixture")] [InlineData("dataset")]
    [InlineData("mode")] [InlineData("asof")] [InlineData("roster-name")] [InlineData("roster-shirt")]
    [InlineData("roster-team")] [InlineData("source")] [InlineData("revision")] [InlineData("delivery")]
    public async Task ChangedBindingCannotReuseRoleEvidence(string change)
    {
        var v = await GeneratedPlayerDistanceTests.ExistingView(0); var d = v.Dataset; var q = v.Context;
        v = change switch
        {
            "version" => v with { Context = q with { DatasetVersion = "other" } },
            "stream" => v with { Context = q with { StreamId = "other" } },
            "fixture" => v with { Dataset = d with { Manifest = d.Manifest with { FixtureId = "other" } } },
            "dataset" => v with { Dataset = d with { Manifest = d.Manifest with { DatasetId = "other" } } },
            "mode" => v with { Context = q with { KnowledgeMode = "known_then" } },
            "asof" => v with { Context = q with { KnowledgeAsOfUtc = DateTimeOffset.UnixEpoch } },
            "roster-name" => v with { Dataset = d with { People = d.People.Select((p, i) => i == 0 ? p with { Name = "Other" } : p).ToArray() } },
            "roster-shirt" => v with { Dataset = d with { People = d.People.Select((p, i) => i == 0 ? p with { Shirt = 99 } : p).ToArray() } },
            "roster-team" => v with { Dataset = d with { People = d.People.Select((p, i) => i == 0 ? p with { TeamId = "other" } : p).ToArray() } },
            "source" => v with { Dataset = d with { Manifest = d.Manifest with { Sources = d.Manifest.Sources.Where(s => s.SourceId != "official-record").ToArray() } } },
            "revision" => v with { Dataset = d with { Manifest = d.Manifest with { ManifestRevision = 7 } } },
            "delivery" => v with { Dataset = d with { Stream = d.Stream with { DeliveryMode = "live" } } }, _ => throw new Exception()
        };
        Assert.Empty(MatchRoleCatalog.Embedded.Project(v));
        Assert.All(new PlayerRosterProjection(v).Rows, r => Assert.Null(r.Role));
    }

    [Fact]
    public async Task EmbeddedRolesRetainSourceLocatorsAndExactlyMatchOfficialPersonIdentity()
    {
        var root = LocalPaths.FindRoot(); var roles = MatchRoleCatalog.Embedded.Project(await GeneratedPlayerDistanceTests.ExistingView(0));
        using var official = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data/official-match-records/brentford-chelsea-2026-09-18.json")));
        var officialPeople = official.RootElement.GetProperty("players").EnumerateArray().ToArray();
        using var resource = typeof(MatchRoleCatalog).Assembly.GetManifestResourceStream("Matchmate.MatchRoles.v1.json")!;
        using var pack = JsonDocument.Parse(resource); var rows = pack.RootElement.GetProperty("roles").EnumerateArray().ToArray();
        Assert.Equal(24, rows.Length);
        foreach (var row in rows)
        {
            var id = row.GetProperty("personId").GetString()!; var info = roles[id];
            var person = Assert.Single(officialPeople, p => "person-pl-reference-" + p.GetProperty("id").GetString()!.Replace(':', '-') == id);
            Assert.NotEmpty(info.Locator); Assert.DoesNotContain('"', info.Locator); // Locators paraphrase evidence; no article passages copied.
            if (info.Scope == MatchRoleScope.ListedCategory)
            {
                Assert.Equal("bench", person.GetProperty("status").GetString());
                Assert.Equal(row.GetProperty("role").GetString(), person.GetProperty("lineupEvidence").GetProperty("locator").GetString()!.Split(';')[^1].Trim().ToLowerInvariant());
                Assert.Equal("https://www.premierleague.com/en/match/2645236/brentford-vs-chelsea/lineups", info.SourceUrl);
            }
            else Assert.StartsWith("https://www.brentfordfc.com/en/news/match-reports-", info.SourceUrl);
        }
        Assert.Equal(2, pack.RootElement.GetProperty("sources").GetArrayLength());
        Assert.DoesNotContain("formation", string.Join(' ', rows.Select(r => r.ToString())), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NumericCentimetresSortBeforeRoundingWithStableTiesAndNullAlwaysLast(bool ascending)
    {
        var projection = new PlayerRosterProjection(SortView());
        var expected = ascending ? new[] { "home-zero", "home-low", "home-tie", "home-high", "home-null", "home-absent" }
            : new[] { "home-high", "home-low", "home-tie", "home-zero", "home-null", "home-absent" };
        Assert.Equal(expected, projection.Team("team-one", true, ascending).Select(r => r.Person.PersonId));
        Assert.Equal(149, projection.Rows[0].CumulativeCentimetres); Assert.Equal(101, projection.Rows[1].CumulativeCentimetres); // Both display as 1m.
        Assert.Null(projection.Rows[4].CumulativeCentimetres); Assert.Null(projection.Rows[5].CumulativeCentimetres);
        Assert.All(projection.Team("team-one", true, ascending), r => Assert.Equal("team-one", r.Person.TeamId));
        Assert.Equal(ascending ? new[] { "away-low", "away-high", "away-null" } : new[] { "away-high", "away-low", "away-null" },
            projection.Team("team-two", true, ascending).Select(r => r.Person.PersonId));
    }

    [Fact]
    public async Task EachTeamSortIsIndependentAndSurvivesClockUpdatesWithoutSelectionOrCallbackChanges()
    {
        var view = SortView(); var before = JsonSerializer.Serialize(view); var selected = "home-high"; var callbacks = 0;
        var component = new RenderableRoster(); Set(component, nameof(PlayerRoster.View), view); Set(component, nameof(PlayerRoster.SelectedPerson), selected);
        Set(component, nameof(PlayerRoster.Select), EventCallback.Factory.Create<string>(new object(), (string _) => callbacks++));
        var initial = Frames(component); Assert.Equal(4, initial.Count(f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "descending")));
        Sort(component, "team-one"); var changed = Frames(component);
        Assert.Equal(2, changed.Count(f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "ascending")));
        Assert.Equal(2, changed.Count(f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "descending")));
        Assert.Equal(new[] { "home-zero", "home-low", "home-tie", "home-high", "home-null" }, PersonIds(changed).Take(5));
        Assert.Equal("away-high", PersonIds(changed).Skip(5).First());
        Set(component, nameof(PlayerRoster.View), view with { Context = view.Context with { CursorMs = 700 }, GeneratedDistance = view.GeneratedDistance! with { CursorMs = 700 } });
        var tick = Frames(component); Assert.Equal(PersonIds(changed), PersonIds(tick));
        Sort(component, "team-two"); Assert.Equal(4, Frames(component).Count(f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "ascending")));
        Sort(component, "team-one"); Assert.Equal(2, Frames(component).Count(f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "descending")));
        Assert.Equal(selected, component.SelectedPerson); Assert.Equal(0, callbacks); Assert.Equal(before, JsonSerializer.Serialize(view));
        await Select(component, "home-low"); Assert.Equal(1, callbacks); Assert.Equal(selected, component.SelectedPerson); // Parent still owns selected ID.
    }

    [Fact]
    public async Task DisabledHandlersBlockSortAndSelectionAndRecordingChangeResetsSort()
    {
        var view = SortView(); var component = new RenderableRoster(); var callbacks = 0;
        Set(component, nameof(PlayerRoster.View), view); Set(component, nameof(PlayerRoster.Disabled), true);
        Set(component, nameof(PlayerRoster.Select), EventCallback.Factory.Create<string>(new object(), (string _) => callbacks++));
        var before = PersonIds(Frames(component)); Sort(component, "team-one"); await Select(component, "home-low");
        Assert.Equal(before, PersonIds(Frames(component))); Assert.Equal(0, callbacks);
        Set(component, nameof(PlayerRoster.Disabled), false); Sort(component, "team-one");
        Assert.Contains(Frames(component), f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "ascending"));
        Set(component, nameof(PlayerRoster.View), view with { Context = view.Context with { StreamId = "different" } });
        Assert.DoesNotContain(Frames(component), f => f.AttributeName == "aria-sort" && Equals(f.AttributeValue, "ascending"));
    }

    [Fact]
    public async Task DeparturesPersistWhileRewindWithholdsFutureParticipantsFromMainRanking()
    {
        var pack = PreparedGeneratedDistanceStore.LoadEmbedded();
        var start = new PlayerRosterProjection(pack.Attach(await GeneratedPlayerDistanceTests.ExistingView(0)));
        var end = new PlayerRosterProjection(pack.Attach(await GeneratedPlayerDistanceTests.ExistingView(5880000)));
        var rewind = new PlayerRosterProjection(pack.Attach(await GeneratedPlayerDistanceTests.ExistingView(0)));
        var startRows = MainRows(start); var endRows = MainRows(end);
        Assert.Equal(22, startRows.Length); Assert.Equal(31, endRows.Length); Assert.Equal(22, end.OnPitchCount);
        Assert.Equal(9, endRows.Count(r => !r.OnPitch)); Assert.All(endRows.Where(r => !r.OnPitch), r => Assert.NotNull(r.CumulativeCentimetres));
        Assert.All(end.Rows.Where(r => !endRows.Contains(r)), r => Assert.Null(r.CumulativeCentimetres));
        Assert.Equal(40, end.Rows.Count); Assert.Equal(startRows.Select(r => r.Person.PersonId), MainRows(rewind).Select(r => r.Person.PersonId));
        Assert.Contains(endRows, r => r.Person.PersonId == B("damsgaard") && !r.OnPitch);
        Assert.DoesNotContain(startRows, r => r.Person.PersonId == B("carvalho")); Assert.Contains(start.Rows, r => r.Person.PersonId == B("carvalho"));
    }

    [Theory]
    [InlineData(MatchLanguage.English)] [InlineData(MatchLanguage.Swedish)] [InlineData(MatchLanguage.Spanish)]
    public async Task LocalizedRoleAndDistanceColumnsUseNativeAccessibleSortAndScopedSource(MatchLanguage language)
    {
        var view = PreparedGeneratedDistanceStore.LoadEmbedded().Attach(await GeneratedPlayerDistanceTests.ExistingView(5880000));
        var html = await Render(view, language, B("kayode"));
        Assert.Equal(4, Regex.Matches(html, "aria-sort=\"descending\"").Count);
        Assert.Contains(ReplayText.Get(language, "GeneratedDistanceColumn"), html);
        Assert.Contains(ReplayText.Get(language, "RoleWithScope", ReplayText.Get(language, "RoleRightBack"), ReplayText.Get(language, "RoleStartingPosition")), html);
        Assert.Contains(ReplayText.Get(language, "RoleEvidenceBoundary"), html); Assert.Contains(ReplayText.Get(language, "RoleSourceBrentford"), html);
        Assert.Contains(ReplayText.Get(language, "RoleUnverified"), html); Assert.DoesNotContain("Illustrated slot", html);
        Assert.DoesNotContain(ReplayText.Get(language, "GeneratedDistanceCompact", ""), html);
        Assert.Contains("type=\"button\" class=\"distance-sort\"", html);
        Assert.Matches("<caption[^>]*>Brentford</caption>", html); Assert.Matches("<caption[^>]*>Chelsea</caption>", html);
        var all = html[html.IndexOf("<details", StringComparison.Ordinal)..];
        Assert.Contains("data-roster=\"all\"", all); Assert.Contains("data-team=\"team-pl-reference-chelsea\"", all);
        foreach (var key in Enum.GetValues<MatchRole>().Select(MatchRoleCatalog.RoleKey).Concat(Enum.GetValues<MatchRoleScope>().Select(MatchRoleCatalog.ScopeKey)))
        {
            var path = Path.Combine(LocalPaths.FindRoot(), "src/Matchmate.Web/Localization/ReplayText" + (language == MatchLanguage.English ? "" : language == MatchLanguage.Swedish ? ".sv" : ".es") + ".resx");
            Assert.Single(XDocument.Load(path).Root!.Elements("data"), d => (string?)d.Attribute("name") == key);
        }
        var selectedUnknown = await Render(view, language, B("kelleher"));
        Assert.Matches("class=\"selected-role\"[^>]*>" + Regex.Escape(ReplayText.Get(language, "RoleUnverified")), selectedUnknown);
        Assert.DoesNotContain("class=\"role-source\"", selectedUnknown);
    }

    [Fact]
    public async Task RecordedExtractRetainsObservationEstimateAndGapSemanticsWithoutGeneratedSort()
    {
        var datasets = await GeneratedPlayerDistanceTests.ExistingStore.CatalogAsync(default);
        var d = Assert.Single(datasets, x => x.Manifest.DatasetId == "australian-recorded");
        var view = await GeneratedPlayerDistanceTests.ExistingStore.QueryAsync(new(d.Manifest.DatasetId, d.Manifest.DatasetVersion,
            d.Stream.StreamId, d.Manifest.FixtureId, d.Schedule!.Segments[0].CursorStartMs, "retrospective_replay", d.Schedule.PolicyId, d.Schedule.PolicyRevision), default);
        Assert.Empty(MatchRoleCatalog.Embedded.Project(view));
        var html = await Render(view, MatchLanguage.English, view.Dataset.People[0].PersonId);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "RecordedRosterNote"), html);
        Assert.DoesNotContain("distance-sort", html); Assert.DoesNotContain("aria-sort", html); Assert.DoesNotContain("generated-distance-value", html);
        foreach (var row in new PlayerRosterProjection(view).Rows.Where(r => r.OnPitch && r.Observation is not null))
            Assert.Contains(ReplayText.Value(MatchLanguage.English, row.Observation!.Method) + " / " + ReplayText.Value(MatchLanguage.English, row.Observation.Availability), html);
        var gap = view with { Frame = null, TrackingGap = true };
        Assert.Empty(MainRows(new PlayerRosterProjection(gap))); Assert.Contains("Not in this active sample", await Render(gap, MatchLanguage.English, view.Dataset.People[0].PersonId));
    }

    [Fact]
    public async Task MissingCurrentSampleKeepsKnownDistanceWithoutCallingDepartedStatusKnown()
    {
        var view = PreparedGeneratedDistanceStore.LoadEmbedded().Attach(await GeneratedPlayerDistanceTests.ExistingView(5880000));
        var html = await Render(view with { Frame = null, TrackingGap = true }, MatchLanguage.English, B("damsgaard"));
        Assert.Equal(71, Regex.Matches(html, "class=\"generated-distance-value\"").Count);
        Assert.DoesNotContain(ReplayText.Get(MatchLanguage.English, "RosterOffPitch"), html);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "PositionUnavailable"), html);
        Assert.Contains(ReplayText.Get(MatchLanguage.English, "CurrentOnPitch", "—"), html);
    }

    private static FootballView SortView()
    {
        var d = GeneratedPlayerDistanceTests.UnitDataset() with { People = [
            new("home-high", "team-one", "Higher", 1, "starter"), new("home-low", "team-one", "Lower", 2, "starter"),
            new("home-tie", "team-one", "Tie", 3, "starter"), new("home-zero", "team-one", "Zero", 4, "starter"),
            new("home-null", "team-one", "Unavailable active", 5, "starter"), new("home-absent", "team-one", "Unused bench", 6, "bench"),
            new("away-low", "team-two", "Away lower", 1, "starter"), new("away-high", "team-two", "Away higher", 2, "starter"), new("away-null", "team-two", "Away bench", 3, "bench")] };
        var view = GeneratedPlayerDistanceTests.UnitView(d, 600);
        return view with { Frame = new("frame", null, [], GeneratedPlayerDistanceTests.Observation("ball"), ["home-null", "away-high"], "unit"),
            GeneratedDistance = new(1, "unit", new string('a', 64), 600, [new("home-high", 149), new("home-low", 101), new("home-tie", 101),
                new("home-zero", 0), new("home-null", null), new("away-low", 900), new("away-high", 10000), new("away-null", null)]) };
    }
    private static PlayerRosterRow[] MainRows(PlayerRosterProjection p) => p.Team("team-pl-reference-brentford", false, false).Concat(p.Team("team-pl-reference-chelsea", false, false)).ToArray();
    private static string B(string id) => "person-pl-reference-brentford-" + id;
    private static string C(string id) => "person-pl-reference-chelsea-" + id;
    private sealed class RenderableRoster : PlayerRoster { public void Render(RenderTreeBuilder b) => BuildRenderTree(b); }
    private static void Set(PlayerRoster c, string name, object value) => typeof(PlayerRoster).GetProperty(name)!.SetValue(c, value);
    private static void Sort(PlayerRoster c, string team) => typeof(PlayerRoster).GetMethod("ToggleDistanceSort", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, [team]);
    private static Task Select(PlayerRoster c, string id) => (Task)typeof(PlayerRoster).GetMethod("SelectPerson", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, [id])!;
    private static RenderTreeFrame[] Frames(RenderableRoster c) { using var tree = new RenderTreeBuilder(); c.Render(tree); return tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray(); }
    private static string[] PersonIds(RenderTreeFrame[] frames) => frames.Where(f => f.AttributeName == "data-person").Select(f => (string)f.AttributeValue).ToArray();
    private static async Task<string> Render(FootballView view, MatchLanguage language, string selected)
    {
        using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(new LinkTestNavigationManager()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment fragment = b => { b.OpenComponent<PlayerRoster>(0); b.AddAttribute(1, nameof(PlayerRoster.View), view); b.AddAttribute(2, nameof(PlayerRoster.SelectedPerson), selected); b.CloseComponent(); };
            var component = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(CascadingValue<MatchLanguage>.Value)] = language, [nameof(CascadingValue<MatchLanguage>.ChildContent)] = fragment }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
