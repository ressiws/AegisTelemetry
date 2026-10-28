/**
 * =============================================================================
 * AegisTelemetry - Session Model
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public sealed class SessionRecord
{
    public long Id { get; set; }
    public required string ServerId { get; init; }
    public required string MapName { get; init; }
    public required DateTime StartTimeUtc { get; init; }
    public DateTime? EndTimeUtc { get; set; }
    public short TickRate { get; init; } = 64;
}
