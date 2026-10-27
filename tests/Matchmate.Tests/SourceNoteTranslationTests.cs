using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matchmate.Contracts;
using Matchmate.Core;
using Matchmate.Infrastructure;
using Matchmate.Web.Chat;
using Matchmate.Web.Components.Experience;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Experience;
using Matchmate.Web.Localization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FootballPitch = Matchmate.Web.Components.Football.FootballPitch;

namespace Matchmate.Tests;

public sealed class SourceNoteTranslationTests
{
    private const string LiverpoolQualification = "Named action is reported; geometry and offsets are generated. Pixels do not independently identify each contact; exact source seconds are unknown.";
    private static readonly Lazy<Task<FootballLedger>> Chelsea = new(() => ReadMetadata("chelsea"));
    private static readonly Lazy<Task<FootballLedger>> Liverpool = new(() => ReadMetadata("liverpool"));
    private static readonly Regex Originals = new("<details class=\"source-note-originals\"[^>]*>.*?</details>", RegexOptions.Singleline);
    public static IEnumerable<object[]> MatchesAndLanguages => from match in new[] { "chelsea", "liverpool" }
        from language in MatchLanguages.Supported select new object[] { match, language };

    [Theory, MemberData(nameof(MatchesAndLanguages))]
    public async Task BothCanonicalMatchesRenderReviewedNotesAndExactOriginalsWithoutChangingEvidenceOrReplay(string match, MatchLanguage language)
    {
        var ledger = await (match == "chelsea" ? Chelsea : Liverpool).Value;
        var dataset = Assert.Single(ledger.Catalog());
        var end = ledger.Query(FullFootballSourceTests.Query(dataset, match == "chelsea" ? 5880000 : 6540000));
        var records = end.Events.Where(r => r.Payload is MatchEventPayload { Sequence: not null }).ToArray();
        Assert.Equal(match == "chelsea" ? 14 : 11, records.Length);
        foreach (var record in records)
        {
            var payload = (MatchEventPayload)record.Payload;
            var release = dataset.Schedule!.Releases.Single(r => r.RecordId == record.RecordId).CursorMs;
            var view = ledger.Query(FullFootballSourceTests.Query(dataset, release));
            var context = new MatchChatContext(view.Context, dataset.HeadSequence,
                new(record.RecordId, release, record.FootballTime!.ReportedClock!, payload.EventKind));
            var visual = Assert.IsType<MatchEventVisual>(MatchEventVisual.Create(view.Dataset, record, context));
            var scene = Assert.IsType<MatchSequenceProjection>(MatchSequenceProjector.Event(view.Dataset, record, release, payload.EventKind == "goal"));
            var snapshot = JsonSerializer.Serialize(new { view, visual, scene });
            var evidence = new MatchEvidenceProjection(view).Get("current_events").Text;
            var projection = MatchPitchPresenter.Project(view, [scene], true, false, language);
            var englishProjection = MatchPitchPresenter.Project(view, [scene], true, false);
            Assert.Equal(JsonSerializer.Serialize(englishProjection.Frame), JsonSerializer.Serialize(projection.Frame));
            Assert.Equal(englishProjection.Path, projection.Path);
            Assert.Equal(englishProjection.Contact, projection.Contact);

            var diagram = await Render<EventSequenceDiagram>(language, new() { [nameof(EventSequenceDiagram.Visual)] = visual });
            var pitch = await Render<FootballPitch>(language, new() { [nameof(FootballPitch.View)] = view, [nameof(FootballPitch.Sequences)] = new[] { scene } });
            var visibleDiagram = Originals.Replace(diagram, "");
            var visiblePitch = Originals.Replace(pitch, "");
            foreach (var step in scene.Sequence.Steps)
            {
                var note = SourceNoteText.Get(step.Kind, step.Text, step.Uncertainty, language);
                Assert.False(note.IsFallback, $"Missing {match}/{record.RecordId}/{step.Id}/{language}");
                Assert.Equal(language != MatchLanguage.English, note.IsTranslated);
                Assert.Equal(MatchLanguages.Code(language), note.LanguageCode);
                Assert.Contains(note.Text, TextInLanguage(visibleDiagram, note.LanguageCode));
                Assert.Contains(note.Text, TextInLanguage(visiblePitch, note.LanguageCode));
                if (note.Uncertainty is { } qualification)
                {
                    Assert.Contains(qualification, TextInLanguage(visibleDiagram, note.LanguageCode));
                    Assert.Contains(qualification, TextInLanguage(visiblePitch, note.LanguageCode));
                }
                if (language != MatchLanguage.English)
                {
                    Assert.NotEqual(step.Text, note.Text);
                    Assert.DoesNotContain(step.Text, visibleDiagram);
                    Assert.DoesNotContain(step.Text, visiblePitch);
                    Assert.Contains(step.Text, TextInLanguage(Originals.Match(diagram).Value, "en"));
                    Assert.Contains(step.Text, TextInLanguage(string.Concat(Originals.Matches(pitch).Select(m => m.Value)), "en"));
                    if (step.Uncertainty is { } originalQualification)
                        Assert.Contains(originalQualification, TextInLanguage(Originals.Match(diagram).Value, "en"));
                }
            }
            Assert.Equal(language == MatchLanguage.English ? 0 : 1, Originals.Matches(diagram).Count);
            Assert.DoesNotContain(ReplayText.Get(language, "SourceNoteOriginalEnglish"), diagram + pitch);
            if (language != MatchLanguage.English)
            {
                Assert.Matches("<summary\\b[^>]*>" + Regex.Escape(ReplayText.Get(language, "ShowOriginalSourceNotes")) + "</summary>", diagram);
                Assert.True(diagram.IndexOf("class=\"source-note-originals\"", StringComparison.Ordinal) > diagram.IndexOf("class=\"sequence-steps\"", StringComparison.Ordinal));
            }
            if (match == "liverpool" && payload.EventKind == "goal")
            {
                var qualification = SourceNoteText.Get(scene.Sequence.Steps[0].Kind, scene.Sequence.Steps[0].Text, LiverpoolQualification, language).Uncertainty!;
                Assert.Single(Regex.Matches(visibleDiagram, Regex.Escape(qualification)));
                Assert.Contains(ReplayText.Get(language, "SourceNoteSteps", "1, 2, 3"), visibleDiagram);
            }
            Assert.Equal(snapshot, JsonSerializer.Serialize(new { view, visual, scene }));
            Assert.Equal(evidence, new MatchEvidenceProjection(view).Get("current_events").Text);
            Assert.Equal(release, context.SelectedEvent!.ReleaseCursorMs);
            Assert.Equal(release, view.Context.CursorMs);
        }
    }

    [Theory, InlineData(MatchLanguage.Swedish), InlineData(MatchLanguage.Spanish)]
    public void AllRetainedChelseaAndCommonNotesHaveReviewedTranslations(MatchLanguage language)
    {
        foreach (var version in new[] { "v1", "v2" })
        {
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(LocalPaths.FindRoot(), "data", "match-enrichment", "brentford-chelsea-2026-09-18-sequences-" + version + ".json")));
            foreach (var step in source.RootElement.GetProperty("events").EnumerateArray().SelectMany(e => e.GetProperty("steps").EnumerateArray()))
            {
                var text = step.GetProperty("text").GetString()!;
                var uncertainty = step.TryGetProperty("uncertainty", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetString() : null;
                var note = SourceNoteText.Get(step.GetProperty("kind").GetString()!, text, uncertainty, language);
                Assert.True(note.IsTranslated, $"Unmapped {version}: {text} / {uncertainty}");
                Assert.NotEqual(text, note.Text);
                Assert.Equal(uncertainty is null, note.Uncertainty is null);
            }
        }
        foreach (var (kind, text, uncertainty) in new (string, string, string?)[]
        {
            ("reported_assist", "Officially credited assist; delivery type unknown.", null),
            ("reported_finish", "Officially reported goal; actual contact unknown.", null),
            ("completed_possession", "Completed source-annotated possession interval; not possession now.", null),
            ("completed_possession", "Completed source-annotated possession interval; not possession now.", "One or both annotated interval endpoints are not matched to a tracking frame.")
        }) Assert.True(SourceNoteText.Get(kind, text, uncertainty, language).IsTranslated);
    }

    [Theory, InlineData(MatchLanguage.Swedish), InlineData(MatchLanguage.Spanish)]
    public async Task UnknownOrChangedTupleStaysExactEnglishAndCannotBorrowAKnownQualification(MatchLanguage language)
    {
        const string text = "Club report: Pass to Thiago.";
        foreach (var (kind, changedText, uncertainty) in new (string, string, string?)[]
        {
            ("unknown_kind", text, LiverpoolQualification), ("release_pass", " " + text, LiverpoolQualification),
            ("release_pass", text, null), ("release_pass", text, LiverpoolQualification + " "),
            ("release_pass", "Unknown\n  original note.", "  Unknown qualification.\n")
        })
        {
            var note = SourceNoteText.Get(kind, changedText, uncertainty, language);
            Assert.True(note.IsFallback); Assert.False(note.IsTranslated); Assert.Equal("en", note.LanguageCode);
            Assert.Equal(changedText, note.Text); Assert.Equal(uncertainty, note.Uncertainty);
        }
        var (view, context) = MatchGoalVisualTests.Unit();
        var record = Assert.Single(view.Events); var payload = (MatchEventPayload)record.Payload;
        var steps = payload.Sequence!.Steps.Select((step, i) => step with
        {
            Kind = i == 0 ? "release_pass" : step.Kind, Text = i == 0 ? text : step.Text,
            Uncertainty = i == 0 ? LiverpoolQualification + " " : step.Uncertainty
        }).ToArray();
        record = record with { Payload = payload with { Sequence = payload.Sequence with { Steps = steps } } };
        var visual = Assert.IsType<MatchEventVisual>(MatchEventVisual.Create(view.Dataset, record, context));
        var html = await Render<EventSequenceDiagram>(language, new() { [nameof(EventSequenceDiagram.Visual)] = visual });
        Assert.Contains(ReplayText.Get(language, "SourceNoteOriginalEnglish"), html);
        Assert.Contains(text, TextInLanguage(html, "en"));
        Assert.Contains(LiverpoolQualification + " ", TextInLanguage(html, "en"));
        Assert.Empty(Originals.Matches(html)); // All notes are already visible originals.
    }

    [Fact]
    public void SpanishHeaderTranslationKeepsTheBallAcrossTheGoalInsteadOfThroughIt()
    {
        var note = SourceNoteText.Get("headed_assist", "Reported Schuster header across goal.",
            "Wide review does not independently identify the intermediate actor or contact.", MatchLanguage.Spanish);
        Assert.Equal("Según el informe, Schuster prolonga de cabeza el balón frente a la portería.", note.Text);
        Assert.Equal("Reported Schuster header across goal.", note.OriginalText);
    }

    [Fact]
    public void QualificationGroupingUsesExactOriginalAndReviewedResultAndKeepsStepAssociations()
    {
        var known = SourceNoteText.Get("release_pass", "Club report: Pass to Thiago.", LiverpoolQualification, MatchLanguage.Swedish);
        var other = SourceNoteText.Get("cross", "Club report: Cross into the centre.", LiverpoolQualification, MatchLanguage.Swedish);
        var grouped = Assert.Single(SourceNoteText.Qualifications([known, other, known]));
        Assert.Equal(new[] { 1, 2, 3 }, grouped.Steps);
        var changed = SourceNoteText.Get("cross", "Changed note", LiverpoolQualification, MatchLanguage.Swedish);
        var separate = SourceNoteText.Qualifications([known, changed, other]);
        Assert.Equal(2, separate.Length); Assert.Equal(new[] { 1, 3 }, separate[0].Steps);
        Assert.Equal(new[] { 2 }, separate[1].Steps); Assert.Equal("en", separate[1].LanguageCode); Assert.True(separate[1].IsFallback);
        var whitespace = SourceNoteText.Get("cross", "Changed note", LiverpoolQualification + " ", MatchLanguage.Swedish);
        Assert.Equal(3, SourceNoteText.Qualifications([known, changed, whitespace]).Length);
        var english = SourceNoteText.Get("release_pass", known.OriginalText, known.OriginalUncertainty, MatchLanguage.English);
        var invalid = SourceNoteText.Get("release_pass", known.OriginalText, known.OriginalUncertainty, (MatchLanguage)999);
        Assert.Equal(english, invalid); Assert.Equal(known.OriginalText, english.Text); Assert.False(english.IsFallback);
    }

    private static async Task<FootballLedger> ReadMetadata(string match)
    {
        IStreamingFootballSource source = match == "chelsea"
            ? new FullBrentfordFootballSource(Path.Combine(LocalPaths.FindRoot(), ".local", "shared-inputs", "brentford-full-v4", "manifest.json"))
            : new LiverpoolFootballSource(Path.Combine(LocalPaths.FindRoot(), "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json"));
        var ledger = new FootballLedger(); var count = 0;
        await foreach (var batch in source.ReadBatchesAsync(default))
        {
            ledger.Accept(new(batch, DateTimeOffset.UtcNow));
            count += batch.Records.Count(r => r.Payload is MatchEventPayload);
            if (count == (match == "chelsea" ? 14 : 23)) break;
        }
        Assert.Equal(match == "chelsea" ? 14 : 23, count);
        return ledger;
    }

    private static string[] TextInLanguage(string html, string language) => Regex.Matches(html,
        "<(?:span|small)\\b[^>]*\\blang=\"" + language + "\"[^>]*>([^<]*)</(?:span|small)>")
        .Select(match => match.Groups[1].Value).ToArray();

    private static async Task<string> Render<T>(MatchLanguage language, Dictionary<string, object?> parameters) where T : IComponent
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment content = builder =>
            {
                builder.OpenComponent<T>(0);
                builder.AddMultipleAttributes(1, parameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
                builder.CloseComponent();
            };
            var rendered = await renderer.RenderComponentAsync<CascadingValue<MatchLanguage>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CascadingValue<MatchLanguage>.Value)] = language,
                [nameof(CascadingValue<MatchLanguage>.ChildContent)] = content
            }));
            return WebUtility.HtmlDecode(rendered.ToHtmlString());
        });
    }
}
