/**
 * =============================================================================
 * AegisTelemetry - System Health Audit Record
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Periodic performance and reliability heartbeat recording memory queue
 *   depths, database failures, and dropped records.
 * =============================================================================
 */

using System;

namespace AegisTelemetry.Models;

public sealed record HealthAuditRecord : ITelemetryRecord
{
    public long SessionId { get; set; }
    public required DateTime TimestampUtc { get; init; }
    public required long MetricsCollected { get; init; }
    public required long MetricsWritten { get; init; }
    public required long MetricsDropped { get; init; }
    public required int DatabaseErrors { get; init; }
    public required int QueuePeakSize { get; init; }
}
