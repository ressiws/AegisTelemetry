/**
 * =============================================================================
 * AegisTelemetry - Aimbot Shot Record
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Captures viewangle kinematics, snap magnitudes, error reductions,
 *   and trajectory smoothness for shots fired by eligible human players.
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public sealed record AimbotShotRecord : ITelemetryRecord
{
    public long SessionId { get; set; }
    public required DateTime TimestampUtc { get; init; }
    public required int ServerTick { get; init; }
    public required int CommandNumber { get; init; }
    public required string PlayerHash { get; init; }
    public required string Weapon { get; init; }
    public required float TargetDistance { get; init; }
    public required float SnapAngle { get; init; }
    public required float AngularVelocity { get; init; }
    public required float ErrorReduction { get; init; }
    public required float PathEfficiency { get; init; }
    public required short MonotonicSteps { get; init; }
    public required bool HitConfirmed { get; init; }
    public required short ActualHitGroup { get; init; }
    public required short DamageDealt { get; init; }
}
