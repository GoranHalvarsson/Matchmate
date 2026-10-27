using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Matchmate.Contracts;
using Matchmate.Web;
using Matchmate.Web.Components.Football;
using Matchmate.Web.Components.Pages;
using Matchmate.Web.Experience;
using FootballPitchComponent = Matchmate.Web.Components.Football.FootballPitch;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Xunit;

namespace Matchmate.Tests;

#pragma warning disable BL0006 // Actual component markup/lifecycle, not a browser substitute.
public sealed class GeneratedPitchMotionTests
{
    [Fact]
    public void AcceptedArrivalSharesBoundedDurationAndUnrelatedRendersCannotRestartIt()
    {
        var motion = new GeneratedPitchMotion(); var first = View(0); var next = View(100, 2);
        var before = JsonSerializer.Serialize(new[] { first, next });
        Assert.Equal(0, Observe(motion, first, 1000).DurationMs);
        var plan = Observe(motion, next, 1120);
        Assert.Equal(150, plan.DurationMs); Assert.Single(plan.Players); Assert.True(plan.Ball);
        Assert.Same(plan, Observe(motion, next, 9999));
        Assert.Equal(400, Observe(motion, View(200, 3), 1700).DurationMs);
        Assert.Equal(before, JsonSerializer.Serialize(new[] { first, next }));
    }

    [Theory]
    [InlineData("pause")] [InlineData("epoch")] [InlineData("seek")] [InlineData("rewind")]
    [InlineData("speed")] [InlineData("period")] [InlineData("head")] [InlineData("version")]
    [InlineData("gap")] [InlineData("missing")] [InlineData("evidence")] [InlineData("identity")]
    [InlineData("phase")]
    public void DiscontinuitiesNeverTween(string change)
    {
        var motion = new GeneratedPitchMotion(); var first = View(0); Observe(motion, first, 1000);
        var next = View(100, 2);
        next = change switch
        {
            "seek" => next with { Context = next.Context with { CursorMs = 1000 } },
            "rewind" => next with { Context = next.Context with { CursorMs = 0 } },
            "period" => next with { PeriodId = "different-period" },
            "head" => next with { Dataset = next.Dataset with { HeadSequence = next.Dataset.HeadSequence + 1 } },
            "version" => next with { Context = next.Context with { DatasetVersion = "another-version" } },
            "gap" => next with { TrackingGap = true },
            "missing" => next with { Frame = null },
            "evidence" => next with { Dataset = next.Dataset with { Manifest = next.Dataset.Manifest with { Capabilities = [] } } },
            "identity" => next with { Frame = next.Frame! with { Players = [next.Frame.Players[0] with { SubjectId = "substitute" }] } },
            _ => next
        };
        var plan = Observe(motion, next, 1120, playing: change != "pause", epoch: change == "epoch" ? 1 : 0,
            step: change == "speed" ? 400 : 100, phase: change == "phase" ? "next-goal-step" : "same-stage");
        Assert.Equal(0, plan.DurationMs); Assert.Empty(plan.Players); Assert.False(plan.Ball);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("missing")] [InlineData("jump")]
    public void OneDiscontinuousObjectSnapsWhileTheOtherContinuousObjectCanMove(string change)
    {
        var motion = new GeneratedPitchMotion(); Observe(motion, View(0), 1000);
        var next = View(100, 2); var player = next.Frame!.Players[0];
        player = change switch
        {
            "unknown" => player with { Method = "unknown" },
            "missing" => player with { Availability = "missing", XM = null, YM = null },
            _ => player with { XM = 90 }
        };
        next = next with { Frame = next.Frame with { Players = [player] } };
        var plan = Observe(motion, next, 1120);
        Assert.Empty(plan.Players); Assert.True(plan.Ball);
        var ballJump = View(200, 3); ballJump = ballJump with { Frame = ballJump.Frame! with { Ball = ballJump.Frame.Ball with { XM = 90 } } };
        Assert.False(Observe(motion, ballJump, 1240).Ball);
    }

    [Fact]
    public void ResumeUsesFreshArrivalCadenceAndStillRequiresConsecutiveAcceptedFrames()
    {
        var motion = new GeneratedPitchMotion(); var first = View(0); Observe(motion, first, 1000);
        Observe(motion, first, 1100, playing: false, epoch: 1);
        Assert.Equal(0, Observe(motion, first, 10000, epoch: 1).DurationMs);
        Assert.Equal(150, Observe(motion, View(100, 2), 10120, epoch: 1).DurationMs);
    }

    [Fact]
    public void ChangingTheScenePhaseOnTheSameAcceptedFrameImmediatelyDropsTheTween()
    {
        var motion = new GeneratedPitchMotion(); Observe(motion, View(0), 1000); var next = View(100, 2);
        Assert.NotEmpty(Observe(motion, next, 1120).Players);
        var snapped = Observe(motion, next, 1140, phase: "different-goal-step");
        Assert.Equal(0, snapped.DurationMs); Assert.Empty(snapped.Players); Assert.False(snapped.Ball);
        Assert.Same(snapped, Observe(motion, next, 1200, phase: "different-goal-step"));
    }

    [Fact]
    public void GeneratedPlayerNumberHitAndSelectionRingShareOneTranslatedGroupAndEvidenceStaysAbsolute()
    {
        var generated = View(0); var pitch = new RenderablePitch(); SetPitch(pitch, "View", generated); SetPitch(pitch, "SelectedPerson", "person-one"); SetPitch(pitch, "Playing", true);
        using (var tree = new RenderTreeBuilder())
        {
            pitch.Render(tree); var frames = Frames(tree); var marker = Marker(frames);
            Assert.Contains("generated-marker", Attributes(frames, marker)["class"]!.ToString());
            Assert.Equal("translate(7 8.75)", Attributes(frames, marker)["transform"]);
            Assert.Contains(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && Equals(f.AttributeValue, "ball estimated generated-ball"));
            var children = frames.Skip(marker + 1).Take(frames[marker].ElementSubtreeLength - 1).ToArray();
            Assert.Contains(children, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && Equals(f.AttributeValue, "selection-ring"));
            Assert.All(children.Where(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName is "cx" or "cy" or "x"), f => Assert.Equal("0", f.AttributeValue));
            Assert.Contains(children, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "y" && Equals(f.AttributeValue, "0.65"));
        }
        SetPitch(pitch, "View", View(100, 2));
        using (var tree = new RenderTreeBuilder()) { pitch.Render(tree); var frames = Frames(tree); Assert.Contains("pitch-motion", Attributes(frames, Marker(frames))["class"]!.ToString()); }
        SetPitch(pitch, "Playing", false);
        using (var tree = new RenderTreeBuilder()) { pitch.Render(tree); var frames = Frames(tree); Assert.DoesNotContain("pitch-motion", Attributes(frames, Marker(frames))["class"]!.ToString()); }
        var source = generated with { Dataset = generated.Dataset with { Manifest = generated.Dataset.Manifest with { Capabilities = [] } } };
        SetPitch(pitch, "View", source);
        using (var tree = new RenderTreeBuilder()) { pitch.Render(tree); var frames = Frames(tree); Assert.False(Attributes(frames, Marker(frames)).ContainsKey("transform")); Assert.DoesNotContain(frames, f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "class" && f.AttributeValue.ToString()!.Contains("generated-ball", StringComparison.Ordinal)); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PauseRejectsAnIgnoredLatePlaybackResponseAndDoesNotCancelAnIndependentSeek(bool seek)
    {
        var view = View(0); var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seekEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseSeek = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default, seekToken = default; var count = 0;
        using var handler = new Handler(async (request, token) =>
        {
            var query = (await request.Content!.ReadFromJsonAsync<FootballQuery>(token))!;
            if (++count > 1) { seekToken = token; seekEntered.SetResult(); await releaseSeek.Task; return new(HttpStatusCode.OK) { Content = JsonContent.Create(view with { Context = query }) }; }
            observed = token; entered.SetResult();
            try { return await pending.Task; } finally { completed.SetResult(); }
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://unit.invalid/") };
        await using var page = new FootballReplay();
        SetPage(page, "Api", new ReplayApiClient(http)); SetPage(page, "_view", view); SetPage(page, "_catalog", new[] { view.Dataset }); SetPage(page, "_selectedStream", view.Context.StreamId);
        var playing = InvokePage(page, "ToggleAsync"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        typeof(FootballReplay).GetMethod("Pause", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        await playing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(observed.IsCancellationRequested); Assert.Same(view, GetPage(page, "_view")); Assert.Equal(0L, GetPage(page, "_cursorMs"));
        Assert.False((bool)GetPage(page, "_updating")!); Assert.False((bool)GetPage(page, "_contextUnavailable")!);
        if (seek)
        {
            var seeking = InvokePage(page, "SeekAsync", 5000L); await seekEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            typeof(FootballReplay).GetMethod("Pause", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
            Assert.False(seekToken.IsCancellationRequested); releaseSeek.SetResult(); await seeking;
        }
        var accepted = GetPage(page, "_view"); pending.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(View(100, 2)) }); await completed.Task;
        Assert.Same(accepted, GetPage(page, "_view")); Assert.Equal(seek ? 5000L : 0L, GetPage(page, "_cursorMs"));
    }

    private static GeneratedPitchMotionPlan Observe(GeneratedPitchMotion motion, FootballView view, double arrival, bool playing = true, long epoch = 0, int step = 100, string phase = "same-stage")
        => motion.Observe(view, MatchPitchPresenter.Project(view, [], true, false), playing, epoch, step, phase, arrival);
    private static FootballView View(long cursor, double x = 1)
    {
        var fixture = SharedFootballTests.Fixture(); var ledger = SharedFootballTests.Ledger(fixture); SharedFootballTests.Add(ledger, fixture, 2, SharedFootballTests.Frame(fixture, 0));
        var view = ledger.Query(SharedFootballTests.Query(fixture, 0));
        return view with { Context = view.Context with { CursorMs = cursor }, Dataset = view.Dataset with { Manifest = view.Dataset.Manifest with { Capabilities = ["complete-existing-generated-100ms-model"] } },
            Frame = view.Frame! with { SourceFrameId = cursor.ToString(), Players = [view.Frame.Players[0] with { Method = "generated", SlotId = "slot-one", XM = x }], Ball = view.Frame.Ball with { Method = "generated", XM = x + 1 } } };
    }
    private static RenderTreeFrame[] Frames(RenderTreeBuilder tree) => tree.GetFrames().Array.Take(tree.GetFrames().Count).ToArray();
    private static int Marker(RenderTreeFrame[] frames) => Enumerable.Range(0, frames.Length).First(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == "g" && Attributes(frames, i).GetValueOrDefault("role")?.ToString() == "button");
    private static Dictionary<string, object?> Attributes(RenderTreeFrame[] frames, int index) => frames.Skip(index + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToDictionary(f => f.AttributeName, f => (object?)f.AttributeValue);
    private static void SetPitch(FootballPitchComponent pitch, string name, object? value) => typeof(FootballPitchComponent).GetProperty(name)!.SetValue(pitch, value);
    private static void SetPage(FootballReplay page, string name, object value) { var property = typeof(FootballReplay).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance); if (property is not null) property.SetValue(page, value); else typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value); }
    private static object? GetPage(FootballReplay page, string name) => typeof(FootballReplay).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
    private static Task InvokePage(FootballReplay page, string name, params object[] args) => (Task)typeof(FootballReplay).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args)!;
    private sealed class RenderablePitch : FootballPitchComponent { public void Render(RenderTreeBuilder tree) => BuildRenderTree(tree); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}
#pragma warning restore BL0006
