/**
 * =============================================================================
 * AegisTelemetry - Bounded Channel Writer
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   High-throughput lock-free bounded memory queue decoupling game hooks
 *   from database I/O. Strictly caps memory and tracks drop statistics.
 * =============================================================================
 */

using System;
using System.Threading;
using System.Threading.Channels;
using AegisTelemetry.Models;

namespace AegisTelemetry.Database;

public sealed class BoundedChannelWriter
{
    private readonly Channel<ITelemetryRecord> _channel;
    private long _metricsCollected;
    private long _metricsWritten;
    private long _metricsDropped;
    private int _databaseErrors;
    private int _queuePeakSize;

    public BoundedChannelWriter(int capacity)
    {
        var options = new BoundedChannelOptions(Math.Max(10, capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<ITelemetryRecord>(options);
    }

    public ChannelReader<ITelemetryRecord> Reader => _channel.Reader;

    public long MetricsCollected => Interlocked.Read(ref _metricsCollected);
    public long MetricsWritten => Interlocked.Read(ref _metricsWritten);
    public long MetricsDropped => Interlocked.Read(ref _metricsDropped);
    public int DatabaseErrors => Volatile.Read(ref _databaseErrors);
    public int QueuePeakSize => Volatile.Read(ref _queuePeakSize);
    public int CurrentCount => _channel.Reader.Count;

    public bool Enqueue(ITelemetryRecord record)
    {
        Interlocked.Increment(ref _metricsCollected);

        int current = _channel.Reader.Count;
        int peak = Volatile.Read(ref _queuePeakSize);
        if (current > peak)
        {
            Interlocked.CompareExchange(ref _queuePeakSize, current, peak);
        }

        bool written = _channel.Writer.TryWrite(record);
        if (!written)
        {
            Interlocked.Increment(ref _metricsDropped);
        }

        return written;
    }

    public void MarkWritten(int count)
    {
        Interlocked.Add(ref _metricsWritten, count);
    }

    public void RecordDatabaseError()
    {
        Interlocked.Increment(ref _databaseErrors);
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
