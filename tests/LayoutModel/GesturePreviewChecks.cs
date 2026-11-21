using NeatWin.Core;

internal static class GesturePreviewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var area = new RectI(0, 0, 1920, 1040);
        var start = new RectI(200, 100, 540, 530);
        var end = start with { X = 390 };
        WindowSnapshot Window(int id, RectI rect) => new(id, rect, rect, area, 1, default, true, id == 1, true,
            id - 1, 96, (uint)id);
        var original = new[] { Window(1, start), Window(2, new(948, 140, 550, 525)), Window(3, new(1500, 790, 300, 245)) };
        var gesture = new ManualWindowGesture(1, start, end, area, ManualGestureKind.Move, [], 230, 400) { ProcessId = 1 };
        var options = new TidyOptions();
        WindowSnapshot[] At(RectI released) => [Window(1, released), original[1], original[2]];
        var desktop = new GesturePreviewDesktop(original, null);
        var preview = desktop.Prepare(gesture, options.SmartStrength, 500)!;
        var live = At(end);
        var visible = new VisibilityAnalyzer().SelectVisibleWorkingSet(live);
        FollowHandDecision Decide(ManualWindowGesture g, IReadOnlyList<VisibleWindow> views, GesturePreview? cached) =>
            FollowHandAssistant.Decide(g, views, options, SmartInteractionContext.Empty, SmartPersonalizationState.Default, cached);
        check(preview.Matches(gesture, live, 1, 550) &&
            preview.For(gesture, 2, options.SmartStrength, 8) is { Count: > 0 } &&
            Decide(gesture, visible, preview) == Decide(gesture, visible, null) &&
            Decide(gesture, visible, preview).ShouldApply, "cold first drag precomputes concrete neighbor targets before release");
        var jitter = gesture with { EndRect = end with { X = end.X + 7 } };
        var movedVisible = new VisibilityAnalyzer().SelectVisibleWorkingSet(At(jitter.EndRect));
        check(preview.Matches(jitter, At(jitter.EndRect), 1, 585) && !preview.Exact(jitter) &&
            Decide(jitter, movedVisible, preview) == Decide(jitter, movedVisible, null),
            "release drift reuses the shortlist and recomputes precise geometry at mouse-up");
        var fast = gesture with { EndSpeedPixelsPerSecond = 3500 };
        var fastPreview = desktop.Prepare(gesture, options.SmartStrength, 500)!;
        check(!Decide(fast, visible, fastPreview).ShouldApply && !fastPreview.Used &&
            Decide(fast, visible, fastPreview) == Decide(fast, visible, null),
            "cached geometry cannot override release speed or attention scoring");
        var task = new TaskLayoutContext(RecentGesture: gesture, GestureAnchor: 1, Focus: new(1, 2, 1));
        var active = GesturePlacement.Resolve(task, visible, options, null, preview);
        var solo = task with { Focus = new(1, 0, 1) };
        check(active.GestureTarget != null && active == GesturePlacement.Resolve(task, visible, options, null) &&
            !GesturePlacement.Resolve(solo, visible, options, null, preview).GestureTarget.HasValue,
            "full planner uses only neighbors eligible under current attention when consuming preview");
        check(!preview.Matches(gesture, live, 2, 550) && !preview.Matches(gesture, live, 1, 1000) &&
            !preview.Matches(gesture, At(end with { Y = 15 }), 1, 550) &&
            !preview.Matches(gesture, [live[0], live[1] with { IsResizable = false }, live[2]], 1, 550) &&
            !preview.Matches(gesture, [live[0], live[1] with { ProcessId = 25 }, live[2]], 1, 550) &&
            !preview.Matches(gesture, [live[0] with { Dpi = 144 }, live[1], live[2]], 1, 550) &&
            !preview.Matches(gesture, [live[0] with { MonitorHandle = 2 }, live[1], live[2]], 1, 550) &&
            !preview.Matches(gesture, [live[0], live[1]], 1, 550),
            "stale time, real frame, display, focus, peer identity, membership or state invalidates preview");
        var session = new TaskLayoutSession();
        var clock = DateTimeOffset.UnixEpoch;
        session.Gesture(gesture, clock);
        var reusedHandle = new[] { live[0] with { ProcessId = 90 }, live[1], live[2] };
        check(session.Capture(reusedHandle, new(), [], null, clock) is { GestureAnchor: 0, RecentGesture: null },
            "a reused HWND cannot inherit the released process's task anchor");
        var resized = gesture with { EndRect = start with { Width = 735 }, Kind = ManualGestureKind.Resize };
        var resizePreview = desktop.Prepare(resized, options.SmartStrength, 500)!;
        var rv = new VisibilityAnalyzer().SelectVisibleWorkingSet(At(resized.EndRect));
        var resizeDecision = Decide(resized, rv, resizePreview);
        check(resizePreview.For(resized, 2, options.SmartStrength, 8) is { Count: > 0 } &&
            resizeDecision == Decide(resized, rv, null) && resizeDecision.TargetRect.Width == resized.EndRect.Width,
            "resize precomputes translations while preserving the final manual size");

        // The broad phase may discard a distant peer. Compare to full enumeration across
        // monitor shapes, DPIs, nearby edges and release drift, including floating overlap.
        var random = new Random(20261121);
        bool identical = true;
        for (int i = 0; i < 240 && identical; i++)
        {
            var display = area with { Width = i % 2 == 0 ? 1920 : 2560 };
            uint dpi = i % 3 == 0 ? 144u : 96u;
            var sampleRect = new RectI(random.Next(130, 950), random.Next(100, 425),
                random.Next(290, 680), random.Next(230, 590));
            var sample = gesture with { EndRect = sampleRect, WorkArea = display, Kind = (ManualGestureKind)(i % 3) };
            var rows = Enumerable.Range(1, 7).Select(id => Window(id, id switch
            {
                1 => start,
                2 => new(sampleRect.Right + random.Next(-32, 33), sampleRect.Top + 11, 510, 535),
                _ => new(random.Next(0, display.Width - 500), random.Next(55, 771), 480, 305)
            }) with { WorkArea = display, Dpi = dpi }).ToArray();
            var personal = SmartPersonalizationState.Default with { PreferredGapPixels = dpi == 144 ? 12 : 8 };
            var prepared = new GesturePreviewDesktop(rows, null).Prepare(sample, options.SmartStrength, 20)!;
            var endpoint = sampleRect with { X = sampleRect.X + random.Next(-14, 15), Width = sampleRect.Width + random.Next(-7, 8) };
            var release = sample with { EndRect = endpoint, EndSpeedPixelsPerSecond = random.Next(40, 2400) };
            rows[0] = rows[0] with { VisualRect = endpoint, OuterRect = endpoint };
            if (!prepared.Matches(release, rows, 1, 40)) continue;
            var peers = new VisibilityAnalyzer().SelectVisibleWorkingSet(rows);
            identical = FollowHandAssistant.Decide(release, peers, options, SmartInteractionContext.Empty, personal, prepared) ==
                FollowHandAssistant.Decide(release, peers, options, SmartInteractionContext.Empty, personal);
        }
        check(identical, "release-time shortlist matches full enumeration across 16:9 and ultrawide move/resize samples");

        var cache = new GesturePreviewCache();
        int captures = 0;
        using var started = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        cache.Begin(options.SmartStrength, () =>
        {
            Interlocked.Increment(ref captures);
            started.Set();
            if (!resume.Wait(3000)) throw new TimeoutException();
            return new(original, null);
        });
        var first = cache.Offer(gesture, 500);
        bool activeWorker = started.Wait(3000);
        _ = cache.Offer(gesture with { EndRect = start with { X = 25 } }, 520);
        var last = cache.Offer(jitter, 550);
        resume.Set();
        var done = last.Wait(3000);
        var result = cache.Take(jitter, At(jitter.EndRect), 1, 565);
        check(activeWorker && done && ReferenceEquals(first, last) && captures == 1 && result?.Exact(jitter) == true &&
            cache.Take(jitter, At(jitter.EndRect), 1, 570) == null,
            "one worker captures the cold desktop once, replaces queued frames and consumes only its latest candidate");
        cache.Begin(options.SmartStrength, () => new(original, null));
        cache.Offer(gesture, 600).Wait(3000);
        var heartbeat = cache.Offer(gesture, 1800);
        var held = cache.Take(gesture, live, 1, 1850);
        check(heartbeat.IsCompletedSuccessfully && held?.SampledAt == 600,
            "holding position refreshes the same geometry without another worker or desktop capture");
        cache.Begin(options.SmartStrength, () => new(original, null));
        _ = cache.Offer(gesture, 600);
        cache.Clear();
        check(cache.Take(gesture, live, 1, 610) == null, "cancelled gesture cannot publish an old result");
    }
}
