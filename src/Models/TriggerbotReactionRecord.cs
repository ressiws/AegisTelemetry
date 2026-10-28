/**
 * =============================================================================
 * AegisTelemetry - Triggerbot Reaction Record
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Records reaction time deltas, attacker velocity (for pre-fire filtering),
 *   contact mode, and click spam cadences for crosshair acquisitions.
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public enum TelemetryContactMode
{
    TargetDriven,
    AimDriven
}

public sealed record TriggerbotReactionRecord : ITelemetryRecord
{
    public long SessionId { get; set; }
    public required DateTime TimestampUtc { get; init; }
    public required int ServerTick { get; init; }
    public required int CommandNumber { get; init; }
    public required string PlayerHash { get; init; }
    public required string Weapon { get; init; }
    public required int ReactionTicks { get; init; }
    public required float ReactionMs { get; init; }
    public required float AttackerSpeed { get; init; }
    public required TelemetryContactMode ContactMode { get; init; }
    public required short PriorClicks { get; init; }
    public required float TargetDistance { get; init; }
    public required bool HitConfirmed { get; init; }
    public required short ActualHitGroup { get; init; }
    public required bool SmokeOccluded { get; init; }
}
