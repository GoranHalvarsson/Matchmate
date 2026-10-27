using System.Globalization;
using System.Reflection;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;

namespace Matchmate.Tests;

// Inspect the actual generated Home render tree without starting a host or its async API lifecycle.
#pragma warning disable BL0006 // Deliberate framework render-tree inspection for F-001 regression.
public sealed class HomeRenderingTests(RecordingFixture fixture) : IClassFixture<RecordingFixture>
{
    [Theory]
    [InlineData("en-SE")]
    [InlineData("sv-SE")]
    public async Task NumericAttributesUseInvariantSyntaxWhileDetailsRemainLocalized(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        await using var component = new RenderableHome();
        var language = new MatchLanguageState(); language.SetLanguage(MatchLanguage.Swedish);
        typeof(Home).GetProperty("LanguageState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, language);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
            var metadata = fixture.Snapshot.Metadata with { Start = 60.25, End = 240.75, Gaps = [] };
            var cursor = new ViewerCursor(metadata); cursor.Seek(60.35);
            var players = new[]
            {
                new PlayerView(metadata.Players[0], new(1.25, -2.5, true, "detected", null, false)),
                new PlayerView(metadata.Players[1], new(-3.125, 4.25, true, "estimated", null, false)),
                new PlayerView(metadata.Players[2], new(6.5, -1.75, true, "unknown", null, false))
            };
            Set(component, "_metadata", metadata); Set(component, "_cursor", cursor);
            Set(component, "_selected", players[0].Player.Id);
            Set(component, "_frame", new FrameView(metadata.Version, metadata.MatchId, cursor.Time, 60.3, 610,
                "partial-positions", false, 3, players, new(4.125, -3.25, true, "estimated", null, false)));

            using var tree = new RenderTreeBuilder(); component.Render(tree);
            var range = tree.GetFrames(); var frames = range.Array.Take(range.Count).ToArray();
            var elements = new List<(string Name, Dictionary<string, object> Attributes)>();
            for (var i = 0; i < frames.Length; i++)
            {
                if (frames[i].FrameType != RenderTreeFrameType.Element) continue;
                var attributes = new Dictionary<string, object>();
                for (var j = i + 1; j < frames.Length && frames[j].FrameType == RenderTreeFrameType.Attribute; j++)
                    attributes[frames[j].AttributeName] = frames[j].AttributeValue;
                elements.Add((frames[i].ElementName, attributes));
            }

            // Detected/estimated circles, unknown square, all jersey labels and independent ball.
            Assert.Contains(elements, e => Has(e, "circle", "cx", "59.75", "cy", "42.5"));
            Assert.Contains(elements, e => Has(e, "circle", "cx", "55.375", "cy", "35.75"));
            Assert.Contains(elements, e => Has(e, "rect", "x", "63.3", "y", "40.05"));
            Assert.Contains(elements, e => Has(e, "text", "x", "59.75", "y", "43.15"));
            Assert.Contains(elements, e => Has(e, "text", "x", "55.375", "y", "36.4"));
            Assert.Contains(elements, e => Has(e, "text", "x", "65", "y", "42.4"));
            Assert.Contains(elements, e => Has(e, "circle", "cx", "62.625", "cy", "43.25")
                && Equals(e.Attributes["class"], "ball estimated"));
            var slider = Assert.Single(elements, e => e.Name == "input" && Equals(e.Attributes.GetValueOrDefault("type"), "range"));
            Assert.Equal("60.25", slider.Attributes["min"]); Assert.Equal("240.75", slider.Attributes["max"]);
            Assert.Equal("0.1", slider.Attributes["step"]); Assert.Equal("60.35", slider.Attributes["value"]);
            var numericNames = new HashSet<string> { "x", "y", "cx", "cy", "min", "max", "step", "value" };
            foreach (var attribute in elements.SelectMany(e => e.Attributes).Where(a => numericNames.Contains(a.Key)))
            {
                var value = Assert.IsType<string>(attribute.Value);
                Assert.DoesNotContain(",", value);
                Assert.True(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            }
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Text
                && f.TextContent.Contains("x 1,25 m", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; CultureInfo.CurrentUICulture = previousUiCulture; }
    }

    private static bool Has((string Name, Dictionary<string, object> Attributes) element, string name,
        string first, string firstValue, string second, string secondValue) => element.Name == name
        && Equals(element.Attributes.GetValueOrDefault(first), firstValue)
        && Equals(element.Attributes.GetValueOrDefault(second), secondValue);
    private static void Set(Home component, string field, object value) =>
        typeof(Home).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);
    private sealed class RenderableHome : Home
    {
        public void Render(RenderTreeBuilder builder) => BuildRenderTree(builder);
    }
}
#pragma warning restore BL0006
