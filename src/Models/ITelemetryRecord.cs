/**
 * =============================================================================
 * AegisTelemetry - Core Record Interface
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Marker interface for telemetry data items queued for asynchronous batch persistence.
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public interface ITelemetryRecord
{
    DateTime TimestampUtc { get; }
    long SessionId { get; set; }
}
