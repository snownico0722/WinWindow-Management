namespace NeatWin.Core;

// Snapshot and disk recording share the same transition row. Identity
// can be missing when the suppression preceded the start of observation.
internal sealed record SurfaceEvent(int Version, string Session, long Tick, DateTimeOffset Time,
    string Action, string Rule, int? WindowId, int? HostId, string? ChildApp, string? HostApp,
    RectI ChildRect, RectI HostRect, bool Owned, bool NativeUtility, bool IndependentFrame, bool IsResizable,
    string? ReleaseReason = null, RectI? ReleasedChildRect = null, RectI? ReleasedHostRect = null, long? ContextPressEvent = null, RawWindowIdentity? ChildIdentity = null, RawWindowIdentity? HostIdentity = null, long? ContextReleaseEvent = null);
