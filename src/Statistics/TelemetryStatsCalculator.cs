/**
 * =============================================================================
 * AegisTelemetry - Statistical Engine
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Computes sample sizes, means, standard deviations, and percentiles
 *   (p50, p90, p95, p99, p99.9) across live telemetry reservoirs for calibration.
 * =============================================================================
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AegisTelemetry.Statistics;

public readonly record struct StatisticalSummary(
    int SampleCount,
    float Min,
    float Max,
    float Mean,
    float StandardDeviation,
    float P50,
    float P90,
    float P95,
    float P99,
    float P99_9);

public sealed class TelemetryStatsCalculator
{
    private const int MaxReservoir = 5000;

    private readonly ConcurrentQueue<float> _reactionTimesMs = new();
    private readonly ConcurrentQueue<float> _snapAnglesDeg = new();
    private readonly ConcurrentQueue<float> _pathEfficiencies = new();
    private readonly ConcurrentQueue<float> _trackingDurationsSec = new();

    public void RecordReactionTime(float ms) => AddToReservoir(_reactionTimesMs, ms);
    public void RecordSnapAngle(float deg) => AddToReservoir(_snapAnglesDeg, deg);
    public void RecordPathEfficiency(float eff) => AddToReservoir(_pathEfficiencies, eff);
    public void RecordTrackingDuration(float sec) => AddToReservoir(_trackingDurationsSec, sec);

    private static void AddToReservoir(ConcurrentQueue<float> q, float val)
    {
        if (!float.IsFinite(val) || val < 0.0f)
            return;

        q.Enqueue(val);
        while (q.Count > MaxReservoir && q.TryDequeue(out _)) { }
    }

    public StatisticalSummary? GetReactionTimeStats() => ComputeSummary(_reactionTimesMs.ToArray());
    public StatisticalSummary? GetSnapAngleStats() => ComputeSummary(_snapAnglesDeg.ToArray());
    public StatisticalSummary? GetPathEfficiencyStats() => ComputeSummary(_pathEfficiencies.ToArray());
    public StatisticalSummary? GetTrackingDurationStats() => ComputeSummary(_trackingDurationsSec.ToArray());

    public static StatisticalSummary? ComputeSummary(float[] values)
    {
        if (values.Length < 5)
            return null;

        Array.Sort(values);
        int n = values.Length;

        float min = values[0];
        float max = values[^1];

        double sum = 0;
        for (int i = 0; i < n; i++)
            sum += values[i];
        float mean = (float)(sum / n);

        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            double diff = values[i] - mean;
            sumSq += diff * diff;
        }
        float stdDev = (float)Math.Sqrt(sumSq / n);

        float p50 = GetPercentile(values, 0.50f);
        float p90 = GetPercentile(values, 0.90f);
        float p95 = GetPercentile(values, 0.95f);
        float p99 = GetPercentile(values, 0.99f);
        float p99_9 = n >= 1000 ? GetPercentile(values, 0.999f) : p99;

        return new StatisticalSummary(n, min, max, mean, stdDev, p50, p90, p95, p99, p99_9);
    }

    private static float GetPercentile(float[] sorted, float percentile)
    {
        if (sorted.Length == 0) return 0.0f;
        if (percentile <= 0.0f) return sorted[0];
        if (percentile >= 1.0f) return sorted[^1];

        float rank = percentile * (sorted.Length - 1);
        int low = (int)MathF.Floor(rank);
        int high = (int)MathF.Ceiling(rank);
        float weight = rank - low;

        return sorted[low] * (1.0f - weight) + sorted[high] * weight;
    }
}
