namespace NeatWin.Core;

/// <summary>
/// Small online preference model shared by the two Smart behaviors. The population/human-factors
/// model always remains the base score. Personalization only adds a bounded residual so a handful
/// of accidental gestures cannot teach the planner something pathological.
/// </summary>
public static class SmartPersonalizationLearner
{
    internal static double ScoreAutoCandidate(
        SmartPersonalizationState state,
        AutoLayoutArchetype archetype,
        IReadOnlyList<double> features)
    {
        state = state.Normalize();
        var score = state.AutoArchetypeBias[(int)archetype];
        var count = Math.Min(features.Count, state.AutoFeatureWeights.Length);
        for (var index = 0; index < count; index++)
        {
            score += state.AutoFeatureWeights[index] * features[index];
        }

        // Personalization is intentionally capped: human-factor safety/legibility must still win.
        return Math.Clamp(score, -3.0, 3.0);
    }

    public static SmartPersonalizationState LearnAutoCorrection(
        SmartPersonalizationState state,
        SmartLearningSnapshot chosen,
        AutoLayoutArchetype correctedArchetype,
        IReadOnlyList<double> correctedFeatures,
        double confidence = 1.0)
    {
        state = state.Normalize();
        confidence = Math.Clamp(confidence, 0, 1);
        if (confidence <= 0.05)
        {
            return state;
        }

        var weights = state.AutoFeatureWeights.ToArray();
        var bias = state.AutoArchetypeBias.ToArray();
        var rate = Math.Clamp(0.16 / Math.Sqrt(1.0 + (state.AutoCorrectionSamples * 0.08)), 0.035, 0.16) * confidence;
        var featureCount = Math.Min(
            weights.Length,
            Math.Min(chosen.Features.Length, correctedFeatures.Count));

        // Online pairwise preference/perceptron update: move the residual model in the direction
        // of the user's corrected layout and away from the layout NeatWin produced.
        for (var index = 0; index < featureCount; index++)
        {
            var delta = Math.Clamp(correctedFeatures[index] - chosen.Features[index], -2.0, 2.0);
            weights[index] = Math.Clamp((weights[index] * 0.999) + (rate * delta), -2.5, 2.5);
        }

        var chosenIndex = (int)chosen.Archetype;
        var correctedIndex = (int)correctedArchetype;
        if (chosenIndex != correctedIndex)
        {
            bias[chosenIndex] = Math.Clamp(bias[chosenIndex] - (rate * 0.65), -2.0, 2.0);
            bias[correctedIndex] = Math.Clamp(bias[correctedIndex] + (rate * 0.65), -2.0, 2.0);
        }

        return state with
        {
            AutoFeatureWeights = weights,
            AutoArchetypeBias = bias,
            AutoCorrectionSamples = state.AutoCorrectionSamples + 1,
        };
    }

    public static SmartPersonalizationState LearnFollowGesture(
        SmartPersonalizationState state,
        FollowHandTargetKind observedKind,
        double? observedGapPixels = null,
        double? observedMainRatio = null,
        double confidence = 1.0)
    {
        state = state.Normalize();
        confidence = Math.Clamp(confidence, 0, 1);
        if (confidence <= 0.05)
        {
            return state;
        }

        var bias = state.FollowTargetBias.ToArray();
        var rate = Math.Clamp(0.20 / Math.Sqrt(1.0 + (state.FollowGestureSamples * 0.045)), 0.04, 0.20) * confidence;

        // Slow decay keeps the model able to follow changing habits without abruptly forgetting.
        for (var index = 0; index < bias.Length; index++)
        {
            bias[index] *= 0.997;
        }

        if ((int)observedKind >= 0 && (int)observedKind < bias.Length)
        {
            bias[(int)observedKind] = Math.Clamp(bias[(int)observedKind] + rate, -1.5, 1.5);
        }

        var gap = state.PreferredGapPixels;
        if (observedGapPixels is double rawGap && double.IsFinite(rawGap))
        {
            var alpha = Math.Clamp(0.22 / Math.Sqrt(1.0 + (state.FollowGestureSamples * 0.025)), 0.045, 0.22) * confidence;
            gap += alpha * (Math.Clamp(rawGap, 0, 48) - gap);
        }

        var ratio = state.PreferredMainRatio;
        if (observedMainRatio is double rawRatio && double.IsFinite(rawRatio))
        {
            var alpha = Math.Clamp(0.16 / Math.Sqrt(1.0 + (state.FollowGestureSamples * 0.03)), 0.035, 0.16) * confidence;
            ratio += alpha * (Math.Clamp(rawRatio, 0.50, 0.78) - ratio);
        }

        return state with
        {
            FollowTargetBias = bias,
            PreferredGapPixels = Math.Clamp(gap, 0, 48),
            PreferredMainRatio = Math.Clamp(ratio, 0.50, 0.78),
            FollowGestureSamples = state.FollowGestureSamples + 1,
        };
    }

    internal static double FollowBias(
        SmartPersonalizationState state,
        FollowHandTargetKind kind) =>
        state.Normalize().FollowTargetBias[(int)kind];
}
