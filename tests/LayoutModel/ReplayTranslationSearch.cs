namespace NeatWin.Core;

internal static partial class IntentLayoutPlanner
{
    static partial void SearchReplayTranslations(TaskLayoutContext context, VisibleWindow[] group, RectI area,
        Func<Candidate> winner, Action<Candidate> evaluate)
    {
        if (!context.SearchReplayPositions || group.Length < 2 || context.LocalPlacementWindow != 0 ||
            context.AllowGestureAssist || context.FirstPlacementWindow != 0) return;

        // 以正常求解器的胜出方案为起点；只试后台平移，保留每窗工作尺寸及组内层级。
        // 三个粒度分别发现大块空白、较小的边缘槽和近邻调整。限制数量便于跨录制复现。
        int proposed = 0;
        foreach (int divisor in new[] { 4, 12, 36 })
        {
            int dx = Math.Max(1, area.Width / divisor), dy = Math.Max(1, area.Height / divisor);
            for (int i = 0; i < group.Length; i++)
            {
                if (group[i].Window.IsForeground || !group[i].Window.IsManageable || group[i].Window.IsTopmost) continue;
                var seed = winner();
                var rect = seed.Rects[i];
                if (rect.Width > area.Width || rect.Height > area.Height) continue;
                foreach (int x in new[] { -dx, 0, dx })
                foreach (int y in new[] { -dy, 0, dy })
                {
                    var moved = rect with { X = Math.Clamp(rect.X + x, area.Left, area.Right - rect.Width),
                        Y = Math.Clamp(rect.Y + y, area.Top, area.Bottom - rect.Height) };
                    if (moved == rect) continue;
                    var frames = (RectI[])seed.Rects.Clone();
                    frames[i] = moved;
                    evaluate(new("replay-translation", frames, seed.Order));
                    if (++proposed == 192) return;
                }
            }
        }
    }
}
