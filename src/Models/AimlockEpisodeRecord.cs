/**
 * =============================================================================
 * AegisTelemetry - Aimlock Episode Record
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Measures real human tracking durations, on-target adherence ratios,
 *   and target screen displacements during continuous engagements.
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public sealed record AimlockEpisodeRecord : ITelemetryRecord
{
    public long SessionId { get; set; }
    public required DateTime TimestampUtc { get; init; }
    public required int StartTick { get; init; }
    public required int EndTick { get; init; }
    public required string PlayerHash { get; init; }
    public required int DurationTicks { get; init; }
    public required float DurationSeconds { get; init; }
    public required float TargetDisplacement { get; init; }
    public required float OnTargetRatio { get; init; }
    public required float AvgDistance { get; init; }
    public required bool CompletedNormally { get; init; }
}
