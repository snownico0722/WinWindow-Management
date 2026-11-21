using NeatWin.Core;

namespace NeatWin.Tests;

public sealed class GestureInterruptionTests
{
    private static readonly RectI Work = new(0, 0, 1920, 1040);
    private static WindowSnapshot Window(int id, uint pid, bool foreground) =>
        new(id, new(100, 100, 800, 640), new(100, 100, 800, 640), Work, 1, default,
            true, foreground, true, foreground ? 0 : 1, 96, pid);

    private static ManualWindowGesture Drag(WindowSnapshot start, RectI end, ManualGestureKind kind = ManualGestureKind.Move) =>
        new(start.Handle, start.VisualRect, end, start.WorkArea, kind, [], 650, 0);

    [Fact]
    public void TextContinuityFollowsTheRealForegroundAndAuthorizesOneReleaseEndpoint()
    {
        var editor = Window(13, 13001, true);
        var edge = editor.VisualRect with { X = editor.VisualRect.X - 12 };
        var plan = new IntentLayoutPlan([new(editor, edge)], [], []);
        var typing = new TaskLayoutContext(Focus: new(editor.Handle, 0, 0),
            WindowHints: [new(editor.Handle, TextFocus: true)], GestureAnchor: editor.Handle, GestureTarget: edge);
        Assert.False(IntentLayoutPlanner.TextViewAcceptsPlan([editor], plan, typing with { AllowGestureAssist = false }));
        Assert.True(IntentLayoutPlanner.TextViewAcceptsPlan([editor], plan, typing with { AllowGestureAssist = true }));
        var unrelated = plan with { Moves = [new(editor, edge with { X = edge.X + 60 })] };
        Assert.False(IntentLayoutPlanner.TextViewAcceptsPlan([editor], unrelated, typing with { AllowGestureAssist = true }));

        var readingChild = Window(27, 27001, true) with { Owner = editor.Handle };
        var parent = editor with { IsForeground = false };
        var childPlan = new IntentLayoutPlan([new(readingChild, readingChild.VisualRect with { X = 310 })], [], []);
        var newTask = typing with { Focus = new(readingChild.Handle, parent.Handle, 1, "handoff", [parent.Handle]), AllowGestureAssist = false };
        Assert.True(IntentLayoutPlanner.TextViewAcceptsPlan([readingChild, parent], childPlan, newTask));
    }

    [Fact]
    public void AReleaseEndpointDoesNotEntitleAnotherWindowToCoverTheNewTextPosition()
    {
        var editor = Window(13, 13001, true) with { ZOrder = 1 };
        var floating = Window(29, 29001, false) with { ZOrder = 0, VisualRect = new(1400, 150, 280, 250) };
        var edge = editor.VisualRect with { X = editor.VisualRect.X + 12 };
        var context = new TaskLayoutContext(Focus: new(editor.Handle, 0, 0), WindowHints: [new(editor.Handle, TextFocus: true)],
            GestureAnchor: editor.Handle, GestureTarget: edge) { AllowGestureAssist = true };
        var align = new IntentLayoutPlan([new(editor, edge)], [], []);
        Assert.True(IntentLayoutPlanner.TextViewAcceptsPlan([editor, floating], align, context));
        var alignAndCover = align with { Moves = [.. align.Moves, new(floating, floating.VisualRect with { X = 370 })] };
        Assert.False(IntentLayoutPlanner.TextViewAcceptsPlan([editor, floating], alignAndCover, context));
    }

    [Fact]
    public void APreparedFrameIsOnlyGeometryAndDoesNotOverrideFileDragOwnershipOrANewChild()
    {
        var editor = Window(13, 13001, true);
        var end = editor.VisualRect with { X = editor.VisualRect.X + 60 };
        var release = Drag(editor, end) with { ProcessId = editor.ProcessId, ReleasedTick = 850 };
        var frame = new GesturePreviewDesktop([editor], null).Prepare(release, SmartTidyStrength.Balanced, 780);
        Assert.NotNull(frame);
        var moved = editor with { VisualRect = end };
        Assert.True(frame.Matches(release, [moved], editor.Handle, 860));
        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(AutomaticLayoutMode.FullAssist, [editor], 20);
        scheduler.GestureStarted(editor.Handle);
        scheduler.GestureCompleted(release, 851);
        Assert.Null(scheduler.Observe([moved], 860, busy: true));
        var child = Window(27, 27001, true) with { Owner = moved.Handle };
        WindowSnapshot[] newTask = [child, moved with { IsForeground = false }];
        Assert.False(frame.Matches(release, newTask, child.Handle, 881));
        Assert.Null(scheduler.Observe(newTask, 881, busy: true));
        Assert.Null(scheduler.Observe([moved], 900, busy: false));
    }

    [Fact]
    public void ALongFileDragCanOutliveTheEligibilityOfAnEarlierWindowRelease()
    {
        var a = Window(13, 13001, true);
        var moved = a with { VisualRect = a.VisualRect with { X = a.VisualRect.X + 80 } };
        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(AutomaticLayoutMode.FullAssist, [a], 20);
        scheduler.GestureStarted(a.Handle);
        var release = Drag(a, moved.VisualRect) with { ProcessId = a.ProcessId, ReleasedTick = 850 };
        scheduler.GestureCompleted(release, 860);
        Assert.Null(scheduler.Observe([moved], 900, busy: true));
        Assert.Null(scheduler.Observe([moved], 4110, busy: true));
        Assert.Null(scheduler.Observe([moved], 5550, busy: false));
        Assert.False(scheduler.HasPending);
    }

    [Theory]
    [InlineData(ManualGestureKind.Move, false)]
    [InlineData(ManualGestureKind.Resize, false)]
    [InlineData(ManualGestureKind.Resize, true)]
    public void CancellingRestoresTheOriginalFrameAndPreservesOnlyIndependentWorkspaceChanges(
        ManualGestureKind kind, bool anotherWindowOpened)
    {
        var a = Window(13, 13001, true);
        var bystander = Window(27, 27001, false);
        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(AutomaticLayoutMode.AutoFullAssist, [a], 0);
        scheduler.GestureStarted(a.Handle);
        WindowSnapshot[] Frame(WindowSnapshot w, bool includeBystander) => includeBystander ? [w, bystander] : [w];
        for (int frame = 1; frame <= 40; frame++)
        {
            var interim = kind == ManualGestureKind.Move ? a.VisualRect with { X = a.VisualRect.X + frame * 4 }
                : a.VisualRect with { Width = a.VisualRect.Width + frame * 4 };
            Assert.Null(scheduler.Observe(Frame(a with { VisualRect = interim }, anotherWindowOpened && frame >= 20), frame * 15, busy: true));
        }

        scheduler.GestureCompleted(Drag(a, a.VisualRect, kind), 620);
        var restored = Frame(a, anotherWindowOpened);
        Assert.Null(scheduler.Observe(restored, 640, false));
        var request = scheduler.Observe(restored, 1550, false);
        Assert.Equal(anotherWindowOpened ? LayoutTrigger.WorkspaceChange : (LayoutTrigger?)null, request?.Trigger);
        Assert.Null(scheduler.Observe(restored, 2800, false));
    }

    [Theory]
    [InlineData(AutomaticLayoutMode.AutoFullAssist)]
    [InlineData(AutomaticLayoutMode.FullAssist)]
    public void AChildTakingFocusAfterReleaseCannotInheritTheParentsFastGesturePass(AutomaticLayoutMode mode)
    {
        var a = Window(13, 13001, true);
        var b = Window(27, 27001, true) with { Owner = a.Handle };
        var moved = a with { VisualRect = a.VisualRect with { X = 260 }, IsForeground = false };
        var history = new ForegroundHistory();
        history.Visit(a.Handle, a.ProcessId, 10);
        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(mode, [a], 50);
        scheduler.GestureStarted(a.Handle);
        scheduler.GestureCompleted(Drag(a, moved.VisualRect), 800);
        history.Created(b.Handle, b.ProcessId, 811);
        history.Visit(b.Handle, b.ProcessId, 825);
        WindowSnapshot[] childFront = [b, moved];
        var navigation = history.NavigationFor(childFront, 850);
        Assert.NotNull(navigation);
        Assert.Null(scheduler.Observe(childFront, 850, false, navigationSettleUntil: navigation.SettleUntil));
        Assert.Null(scheduler.Observe(childFront, navigation.SettleUntil - 1, false, navigationSettleUntil: navigation.SettleUntil));
        var after = scheduler.Observe(childFront, navigation.SettleUntil, false, navigationSettleUntil: navigation.SettleUntil);
        Assert.Equal(mode == AutomaticLayoutMode.AutoFullAssist ? LayoutTrigger.WorkspaceChange : (LayoutTrigger?)null, after?.Trigger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnrepresentedForegroundOrReusedHandleInvalidatesPendingRelease(bool reused)
    {
        var a = Window(13, 13001, true);
        var scheduler = new AutomaticLayoutScheduler();
        scheduler.Reset(AutomaticLayoutMode.FullAssist, [a], 100);
        scheduler.GestureStarted(a.Handle);
        var moved = a with { VisualRect = a.VisualRect with { Width = 960 } };
        scheduler.GestureCompleted(Drag(a, moved.VisualRect, ManualGestureKind.Resize), 800);
        var other = reused ? moved with { ProcessId = a.ProcessId + 1 } : moved with { IsForeground = false };
        Assert.Null(scheduler.Observe([other], 815, false));
        Assert.Null(scheduler.Observe([other], 1500, false));
        Assert.Null(scheduler.Observe([moved], 1700, false));
        Assert.Null(scheduler.Observe([moved], 2500, false));
    }
}
