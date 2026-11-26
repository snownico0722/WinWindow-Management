using System.Text.Json;
using NeatWin.Core;

namespace NeatWin.App;

internal sealed class SettingsStore
{
    private readonly string _settingsPath;

    internal SettingsStore(string settingsPath) => _settingsPath = settingsPath;

    internal AutomaticLayoutMode LoadAutomaticLayoutMode()
    {
        var settings = LoadStoredSettings();
        return AutomaticLayoutPolicy.Read(settings.AutomaticLayoutMode, settings.AutoTidyEnabled);
    }

    internal void SaveAutomaticLayoutMode(AutomaticLayoutMode mode)
    {
        if (ReadStoredSettings() is not { } settings) return;
        mode = AutomaticLayoutPolicy.Read((int)mode, false);
        settings.AutomaticLayoutMode = (int)mode;
        settings.AutoTidyEnabled = mode != AutomaticLayoutMode.Off; // Older versions keep their toggle.
        WriteStoredSettings(settings);
    }

    internal bool LoadAutoTidyEnabled() => LoadAutomaticLayoutMode() != AutomaticLayoutMode.Off;

    internal TidyOptions LoadTidyOptions()
    {
        var stored = LoadStoredSettings();
        var defaults = new TidyOptions();

        return defaults with
        {
            AlgorithmMode = ReadEnum(stored.AlgorithmMode, defaults.AlgorithmMode),
            SmartStrength = ReadEnum(stored.SmartStrength, defaults.SmartStrength),
            SmartHitTendency = SmartHitTendency.Balanced,
            SmartSizeTendency = SmartSizeTendency.Balanced,
            PreserveLayoutWeight = Math.Clamp(stored.PreserveLayoutWeight ?? defaults.PreserveLayoutWeight, 0.10, 5.0),
            ResizeResistanceWeight = Math.Clamp(stored.ResizeResistanceWeight ?? defaults.ResizeResistanceWeight, 0.0, 5.0),
            OrderlinessWeight = Math.Clamp(stored.OrderlinessWeight ?? defaults.OrderlinessWeight, 0.10, 5.0),
            SpaceUsageWeight = Math.Clamp(stored.SpaceUsageWeight ?? defaults.SpaceUsageWeight, 0.0, 5.0),
            SmartIterations = Math.Clamp(stored.SmartIterations ?? defaults.SmartIterations, 4, 128),
            NeighborSnapDistance = Math.Clamp(stored.NeighborSnapDistance ?? defaults.NeighborSnapDistance, 0, 240),
            AlignmentSnapDistance = Math.Clamp(stored.AlignmentSnapDistance ?? defaults.AlignmentSnapDistance, 0, 120),
            ScreenSnapDistance = Math.Clamp(stored.ScreenSnapDistance ?? defaults.ScreenSnapDistance, 0, 240),
            MaximumEdgeAdjustment = Math.Clamp(stored.MaximumEdgeAdjustment ?? defaults.MaximumEdgeAdjustment, 0, 480),
            MaximumSizeChangeRatio = Math.Clamp(stored.MaximumSizeChangeRatio ?? defaults.MaximumSizeChangeRatio, 0, 0.50),
            RescueOffscreenWindows = stored.RescueOffscreenWindows ?? defaults.RescueOffscreenWindows,
            Passes = Math.Clamp(stored.Passes ?? defaults.Passes, 1, 5),
        };
    }

    internal SmartBehaviorOptions LoadSmartBehaviorOptions()
    {
        var stored = LoadStoredSettings();
        var defaults = new SmartBehaviorOptions();
        return defaults with
        {
            PreferReversibleVerticalFill = stored.PreferReversibleVerticalFill ?? defaults.PreferReversibleVerticalFill,
            OverlapAvoidance = SmartOverlapAvoidance.Balanced,
            RemoveVideoBlackBars = stored.RemoveVideoBlackBars ?? defaults.RemoveVideoBlackBars,
            VideoBlackBarTendency = ReadEnum(stored.VideoBlackBarTendency, defaults.VideoBlackBarTendency),
        };
    }

    internal SmartPersonalizationState LoadSmartPersonalization()
    {
        var stored = LoadStoredSettings();
        var defaults = SmartPersonalizationState.Default;
        return new SmartPersonalizationState(
            stored.AutoFeatureWeights ?? defaults.AutoFeatureWeights,
            stored.AutoArchetypeBias ?? defaults.AutoArchetypeBias,
            stored.FollowTargetBias ?? defaults.FollowTargetBias,
            stored.PreferredGapPixels ?? defaults.PreferredGapPixels,
            stored.PreferredMainRatio ?? defaults.PreferredMainRatio,
            stored.AutoCorrectionSamples ?? defaults.AutoCorrectionSamples,
            stored.FollowGestureSamples ?? defaults.FollowGestureSamples).Normalize();
    }

    internal BehaviorConfiguration LoadBehaviorConfiguration() => LoadStoredSettings().Behavior ?? new();

    internal void SaveBehaviorConfiguration(BehaviorConfiguration behavior)
    {
        if (ReadStoredSettings() is not { } settings) return;
        settings.Behavior = behavior;
        WriteStoredSettings(settings);
    }

    internal bool UseImportedPlacement
    {
        get => LoadStoredSettings().UseImportedPlacement ?? true;
        set { if (ReadStoredSettings() is not { } settings) return; settings.UseImportedPlacement = value; WriteStoredSettings(settings); }
    }

    internal bool VisualSampling
    {
        get => LoadStoredSettings().VisualSampling;
        set { if (ReadStoredSettings() is not { } settings) return; settings.VisualSampling = value; WriteStoredSettings(settings); }
    }

    internal bool RecordingEnabled
    {
        get => LoadStoredSettings().RecordingEnabled;
        set
        {
            if (ReadStoredSettings() is not { } settings) return;
            settings.RecordingEnabled = value;
            WriteStoredSettings(settings);
        }
    }

    internal void SaveAutoTidyEnabled(bool enabled) =>
        SaveAutomaticLayoutMode(enabled ? AutomaticLayoutMode.LightAssist : AutomaticLayoutMode.Off);

    internal void SaveLayoutOptions(TidyOptions options, SmartBehaviorOptions behavior)
    {
        if (ReadStoredSettings() is not { } settings) return;
        settings.AlgorithmMode = (int)options.AlgorithmMode;
        settings.SmartStrength = (int)options.SmartStrength;
        settings.SmartHitTendency = (int)SmartHitTendency.Balanced;
        settings.SmartSizeTendency = (int)SmartSizeTendency.Balanced;
        settings.PreserveLayoutWeight = options.PreserveLayoutWeight;
        settings.ResizeResistanceWeight = options.ResizeResistanceWeight;
        settings.OrderlinessWeight = options.OrderlinessWeight;
        settings.SpaceUsageWeight = options.SpaceUsageWeight;
        settings.SmartIterations = options.SmartIterations;
        settings.NeighborSnapDistance = options.NeighborSnapDistance;
        settings.AlignmentSnapDistance = options.AlignmentSnapDistance;
        settings.ScreenSnapDistance = options.ScreenSnapDistance;
        settings.MaximumEdgeAdjustment = options.MaximumEdgeAdjustment;
        settings.MaximumSizeChangeRatio = options.MaximumSizeChangeRatio;
        settings.RescueOffscreenWindows = options.RescueOffscreenWindows;
        settings.Passes = options.Passes;
        settings.PreferReversibleVerticalFill = behavior.PreferReversibleVerticalFill;
        settings.SmartOverlapAvoidance = (int)SmartOverlapAvoidance.Balanced;
        settings.RemoveVideoBlackBars = behavior.RemoveVideoBlackBars;
        settings.VideoBlackBarTendency = (int)behavior.VideoBlackBarTendency;
        WriteStoredSettings(settings);
    }

    internal void SaveSmartPersonalization(SmartPersonalizationState state)
    {
        state = state.Normalize();
        if (ReadStoredSettings() is not { } settings) return;
        settings.AutoFeatureWeights = state.AutoFeatureWeights;
        settings.AutoArchetypeBias = state.AutoArchetypeBias;
        settings.FollowTargetBias = state.FollowTargetBias;
        settings.PreferredGapPixels = state.PreferredGapPixels;
        settings.PreferredMainRatio = state.PreferredMainRatio;
        settings.AutoCorrectionSamples = state.AutoCorrectionSamples;
        settings.FollowGestureSamples = state.FollowGestureSamples;
        WriteStoredSettings(settings);
    }

    private StoredSettings LoadStoredSettings() => ReadStoredSettings() ?? StoredSettings.CreateDefault();

    // 只有文件确实不存在才从默认值开始写。读失败时界面可以用默认值，但不能用它覆盖原文件。
    private StoredSettings? ReadStoredSettings()
    {
        try { return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_settingsPath)); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { return StoredSettings.CreateDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceWarning($"Layout settings unavailable: {e.Message}");
            return null;
        }
    }

    private void WriteStoredSettings(StoredSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _settingsPath, overwrite: true);
    }

    private static TEnum ReadEnum<TEnum>(int? raw, TEnum fallback)
        where TEnum : struct, Enum
    {
        return raw is int value && Enum.IsDefined(typeof(TEnum), value)
            ? (TEnum)Enum.ToObject(typeof(TEnum), value)
            : fallback;
    }

    private sealed class StoredSettings
    {
        public bool RecordingEnabled { get; set; }
        public bool? UseImportedPlacement { get; set; }
        public BehaviorConfiguration? Behavior { get; set; }
        public bool VisualSampling { get; set; }
        public bool? AutoTidyEnabled { get; set; }
        public int? AutomaticLayoutMode { get; set; }

        public int? AlgorithmMode { get; set; }
        public int? SmartStrength { get; set; }
        public int? SmartHitTendency { get; set; }
        public int? SmartSizeTendency { get; set; }
        public int? SmartOverlapAvoidance { get; set; }
        public bool? PreferReversibleVerticalFill { get; set; }
        public bool? RemoveVideoBlackBars { get; set; }
        public int? VideoBlackBarTendency { get; set; }

        public double? PreserveLayoutWeight { get; set; }
        public double? ResizeResistanceWeight { get; set; }
        public double? OrderlinessWeight { get; set; }
        public double? SpaceUsageWeight { get; set; }
        public int? SmartIterations { get; set; }
        public int? NeighborSnapDistance { get; set; }
        public int? AlignmentSnapDistance { get; set; }
        public int? ScreenSnapDistance { get; set; }
        public int? MaximumEdgeAdjustment { get; set; }
        public double? MaximumSizeChangeRatio { get; set; }
        public bool? RescueOffscreenWindows { get; set; }
        public int? Passes { get; set; }

        // Personalized model state. These are geometry-only residual parameters; no window titles,
        // process names, raw pointer history or screenshots are persisted.
        public double[]? AutoFeatureWeights { get; set; }
        public double[]? AutoArchetypeBias { get; set; }
        public double[]? FollowTargetBias { get; set; }
        public double? PreferredGapPixels { get; set; }
        public double? PreferredMainRatio { get; set; }
        public int? AutoCorrectionSamples { get; set; }
        public int? FollowGestureSamples { get; set; }

        internal static StoredSettings CreateDefault() => new() { AutoTidyEnabled = false };
    }
}
