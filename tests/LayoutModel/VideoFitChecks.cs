using NeatWin.Core;

internal static class VideoFitChecks
{
    internal static void Run(Action<bool, string> check)
    {
        BrowserVideoEvidenceChecks.Run(check);
        var area = new RectI(-1920, 0, 1920, 1040);
        var frame = new RectI(-1920, 45, 1280, 900);
        var window = new WindowSnapshot(2, frame, frame, area, 1, default, true, false, true, 1, 96, 20, SurfaceState: "windowed");
        var view = new RectI(frame.X + 10, frame.Y + 100, 1240, 780);
        var hint = new VideoBlackBarHint(window.Handle, VideoBlackBarOrientation.Horizontal, 2, view, .96);
        var fit = VideoViewportFit.From(frame, hint)!;
        var target = frame with { Height = 740 };
        var page = new VideoPageIdentity(20, 123, "browser", "video A");
        var atTarget = window with { VisualRect = target };
        var session = new VideoFitSession();
        void Submit() => session.Submitted(window, target, page, fit, 0);
        Submit();
        check(session.AwaitingGeometry([window], _ => page, 117) && !session.AwaitingGeometry([atTarget], _ => page, 117) &&
            !session.AwaitingGeometry([window], _ => page, 900) && !session.AwaitingGeometry([window], _ => page with { Caption = "video B" }, 117),
            "an early opening waits only for the same outstanding native resize and releases a refused or obsolete request");
        check(session.Read([window], _ => page) == null, "an unfulfilled resize never establishes a video fit");
        Submit();
        var fits = session.Read([atTarget], _ => page);
        check(fits?.GetValueOrDefault(window.Handle) == fit && session.Repeat(atTarget, page, fit, 120),
            "the next opening can use the observed fit immediately without recapturing video");
        check(!fit.Preserves(target, target with { Height = target.Height + 18 }) && fit.Fits(target),
            "an exact fit cannot drift back inside the initial detector tolerance");
        var scaled = fit.Inside(new(area.Left + 800, 50, 900, 880), target);
        check(scaled.Width == 900 && scaled.Height == 550 && fit.Preserves(target, scaled),
            "size a proposed column using asymmetric browser margins and the picture ratio");
        session.Follow([new(atTarget, scaled)], 120);
        var repositioned = atTarget with { VisualRect = scaled with { Y = scaled.Y + 10 } };
        check(session.Read([repositioned], _ => page)?.Count == 1, "a proportional resize and later translation retain the fit");
        check(session.Read([repositioned], _ => page with { Caption = "video B" }) == null,
            "a changed page invalidates the prior content even at identical outer dimensions");
        Submit();
        var reflow = fit with { Aspect = 1.5, Insets = fit.Insets with { Top = 20 } };
        check(session.Repeat(atTarget, page, reflow, 120) && session.Repeat(atTarget, page, reflow, 25_000) &&
            session.Read([atTarget], _ => page)?.GetValueOrDefault(window.Handle) == fit,
            "remeasuring a settled viewport at the app's resized geometry cannot authorize an endless resize");
        var changed = reflow with { Aspect = 1.8 };
        check(session.Repeat(atTarget, page, changed, 25_300) &&
            session.Repeat(atTarget, page, reflow, 25_600) &&
            session.Repeat(atTarget, page, changed, 25_900) &&
            !session.Repeat(atTarget, page, changed, 26_200) && session.Read([atTarget], _ => page) == null,
            "a repeatable independently moving picture change at the same geometry supersedes the fit; a transient does not");
        Submit();
        check(!session.Repeat(atTarget, page with { Caption = "video B" }, changed, 120),
            "a new page with independently verified picture motion can immediately establish a new fit");
        Submit();
        check(session.Read([atTarget with { VisualRect = target with { Width = 1100 } }], _ => page) == null,
            "an external resize withdraws the fit without asking the user to undo");
        Submit();
        check(session.Read([atTarget], _ => page with { Started = 456 }) == null, "a new browser lifetime cannot inherit a fit");
        Submit();
        check(session.SettledViewport(atTarget, page, 120) == null &&
            session.SettledViewport(atTarget, page, 950) is { Height: 620, Width: 1240 } &&
            session.SettledViewport(window, page, 950) == null,
            "only settled native target geometry can establish the no-bars baseline");
        session.ObserveUnbarred(atTarget, page, 950);
        check(session.Read([atTarget], _ => page)?.GetValueOrDefault(window.Handle) == fit,
            "a paused picture at the verified unbarred fit keeps its current fit without new playback evidence");
        check(!session.Repeat(atTarget, page, changed, 1_950),
            "after confirmed bar removal, one new three-frame moving barred picture on the same page may resize");
        var clamped = window with { VisualRect = frame with { Height = 800 } };
        Submit();
        check(session.Read([clamped], _ => page) == null && session.Repeat(clamped, page, fit, 950) &&
            session.Repeat(clamped, page, fit, 1_950) && session.Read([clamped], _ => page) == null,
            "a native minimum-size clamp never establishes a fit and never repeats the unreachable request");
        Submit();
        check(session.Read([window], _ => page) == null && session.Repeat(window, page, fit, 950),
            "an unchanged native window refuses the same previously submitted resize without teaching a fit");
        var context = new TaskLayoutContext { VideoFits = fits };
        var signals = RecordedTaskSignals.From(context, new Dictionary<nint, int> { [window.Handle] = 7 }, [atTarget]);
        check(signals.Video == null && signals.ApplyToReplay(new()).VideoFits?.GetValueOrDefault(7) == fit,
            "replay distinguishes a retained viewport from newly sampled bars without storing page identity");

        foreach (bool left in new[] { true, false })
        {
            var source = atTarget with { VisualRect = target with { X = left ? area.Left : area.Right - target.Width } };
            var incoming = source with { Handle = 1, ProcessId = 10, IsForeground = true, ZOrder = 0,
                VisualRect = new(area.Left + 420, 90, 1170, 870) };
            WindowSnapshot[] scene = [incoming, source];
            var open = context with { Focus = new(1, 2, 1, left ? "opening-resident-left" : "opening-resident-right"),
                Attention = new Dictionary<nint, LayoutAttention> { [1] = new(1, .9, .9, 0, true), [2] = new(.8, .7, .5, 0, false) },
                UseWorkingPairs = true, IsWorkspaceObservation = true, WorkspaceCause = WorkspaceChangeCause.Membership };
            var visible = new VisibilityAnalyzer().SelectForManualArrange(scene, true);
            var plan = LayoutDispatch.Create(LayoutRoute.Smart, visible, new(SmartStrength: SmartTidyStrength.Assertive), null, scene, open);
            var after = TaskLayoutSession.Project(scene, plan);
            check(fit.Preserves(source.VisualRect, after[1].VisualRect) &&
                after[0].VisualRect.Intersect(after[1].VisualRect).Area < incoming.VisualRect.Intersect(source.VisualRect).Area,
                $"opening still frees space on a crowded desktop while preserving the video fit (left: {left})");
        }
    }
}
