/**
 * =============================================================================
 * AegisTelemetry - Database Manager
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Asynchronous background database worker executing batched, parameterized
 *   inserts into MariaDB. Handles connection recovery and session lifecycle.
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AegisTelemetry.Configuration;
using AegisTelemetry.Models;
using MySqlConnector;

namespace AegisTelemetry.Database;

public sealed class DatabaseManager : IAsyncDisposable
{
    private readonly TelemetryConfig _config;
    private readonly BoundedChannelWriter _queue;
    private readonly Action<string> _log;
    private readonly Action<string> _debug;

    private readonly CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private long _currentSessionId = -1;
    private DateTime _lastHealthAudit = DateTime.UtcNow;

    public DatabaseManager(TelemetryConfig config, BoundedChannelWriter queue, Action<string> log, Action<string> debug)
    {
        _config = config;
        _queue = queue;
        _log = log;
        _debug = debug;
    }

    public long CurrentSessionId => Interlocked.Read(ref _currentSessionId);

    public void Start()
    {
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public async Task<long> StartSessionAsync(string mapName, short tickRate = 64)
    {
        string connStr = _config.Database.BuildConnectionString();
        try
        {
            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = @"
                INSERT INTO `telemetry_sessions` (`server_id`, `map_name`, `start_time_utc`, `tick_rate`)
                VALUES (@serverId, @mapName, @startTime, @tickRate);
                SELECT LAST_INSERT_ID();";

            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@serverId", _config.ServerId);
            cmd.Parameters.AddWithValue("@mapName", mapName);
            cmd.Parameters.AddWithValue("@startTime", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@tickRate", tickRate);

            object? idObj = await cmd.ExecuteScalarAsync();
            long id = Convert.ToInt64(idObj);
            Interlocked.Exchange(ref _currentSessionId, id);
            _log($"[AegisTelemetry] Started telemetry session #{id} (map: {mapName}, server: {_config.ServerId}).");
            return id;
        }
        catch (Exception ex)
        {
            _queue.RecordDatabaseError();
            _log($"[AegisTelemetry] Failed to start database session: {ex.Message}");
            return -1;
        }
    }

    public async Task EndSessionAsync(long sessionId)
    {
        if (sessionId <= 0)
            return;

        string connStr = _config.Database.BuildConnectionString();
        try
        {
            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = "UPDATE `telemetry_sessions` SET `end_time_utc` = @endTime WHERE `id` = @sessionId;";
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@endTime", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);

            await cmd.ExecuteNonQueryAsync();
            _debug($"[AegisTelemetry] Closed telemetry session #{sessionId}.");
        }
        catch (Exception ex)
        {
            _queue.RecordDatabaseError();
            _debug($"[AegisTelemetry] Failed to update session end time: {ex.Message}");
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var reader = _queue.Reader;
        var batch = new List<ITelemetryRecord>(_config.Queue.BatchSize);
        var flushInterval = TimeSpan.FromSeconds(Math.Max(1.0f, _config.Queue.FlushIntervalSeconds));
        var nextFlush = DateTime.UtcNow + flushInterval;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (reader.TryRead(out var item))
                {
                    batch.Add(item);
                    if (batch.Count >= _config.Queue.BatchSize)
                        break;
                }

                bool timeToFlush = DateTime.UtcNow >= nextFlush;
                if (batch.Count >= _config.Queue.BatchSize || (timeToFlush && batch.Count > 0))
                {
                    await FlushBatchAsync(batch, ct);
                    batch.Clear();
                    nextFlush = DateTime.UtcNow + flushInterval;
                }

                // Check periodic health audit
                if (DateTime.UtcNow - _lastHealthAudit >= TimeSpan.FromSeconds(_config.Queue.HealthAuditIntervalSeconds))
                {
                    _lastHealthAudit = DateTime.UtcNow;
                    long sid = CurrentSessionId;
                    if (sid > 0)
                    {
                        var audit = new HealthAuditRecord
                        {
                            SessionId = sid,
                            TimestampUtc = DateTime.UtcNow,
                            MetricsCollected = _queue.MetricsCollected,
                            MetricsWritten = _queue.MetricsWritten,
                            MetricsDropped = _queue.MetricsDropped,
                            DatabaseErrors = _queue.DatabaseErrors,
                            QueuePeakSize = _queue.QueuePeakSize
                        };
                        _queue.Enqueue(audit);
                    }
                }

                if (batch.Count == 0)
                {
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _queue.RecordDatabaseError();
                _debug($"[AegisTelemetry] Background worker iteration error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }

        // Drain remainder on shutdown
        while (reader.TryRead(out var remaining))
            batch.Add(remaining);

        if (batch.Count > 0)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushBatchAsync(batch, timeoutCts.Token);
        }
    }

    private async Task FlushBatchAsync(List<ITelemetryRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        var aimbotShots = new List<AimbotShotRecord>();
        var triggerReactions = new List<TriggerbotReactionRecord>();
        var aimlockEpisodes = new List<AimlockEpisodeRecord>();
        var healthAudits = new List<HealthAuditRecord>();

        long fallbackSessionId = CurrentSessionId;
        if (fallbackSessionId <= 0 && batch.Count > 0)
        {
            long newSession = await StartSessionAsync("unknown");
            if (newSession > 0)
                fallbackSessionId = newSession;
        }

        foreach (var r in batch)
        {
            if (r.SessionId <= 0)
                r.SessionId = fallbackSessionId;

            if (r.SessionId <= 0)
                continue; // Cannot persist without session

            switch (r)
            {
                case AimbotShotRecord a: aimbotShots.Add(a); break;
                case TriggerbotReactionRecord t: triggerReactions.Add(t); break;
                case AimlockEpisodeRecord l: aimlockEpisodes.Add(l); break;
                case HealthAuditRecord h: healthAudits.Add(h); break;
            }
        }

        string connStr = _config.Database.BuildConnectionString();
        try
        {
            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            if (aimbotShots.Count > 0)
                await InsertAimbotShotsAsync(conn, tx, aimbotShots, ct);

            if (triggerReactions.Count > 0)
                await InsertTriggerReactionsAsync(conn, tx, triggerReactions, ct);

            if (aimlockEpisodes.Count > 0)
                await InsertAimlockEpisodesAsync(conn, tx, aimlockEpisodes, ct);

            if (healthAudits.Count > 0)
                await InsertHealthAuditsAsync(conn, tx, healthAudits, ct);

            await tx.CommitAsync(ct);
            _queue.MarkWritten(batch.Count);
            _debug($"[AegisTelemetry] Flushed {batch.Count} telemetry records to MariaDB.");
        }
        catch (Exception ex)
        {
            _queue.RecordDatabaseError();
            _debug($"[AegisTelemetry] Database flush failure: {ex.Message}");
        }
    }

    private static async Task InsertAimbotShotsAsync(MySqlConnection conn, MySqlTransaction tx, List<AimbotShotRecord> list, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(@"INSERT INTO `telemetry_aimbot_shots`
            (`session_id`, `timestamp_utc`, `server_tick`, `command_number`, `player_hash`, `weapon`,
             `target_distance`, `snap_angle`, `angular_velocity`, `error_reduction`, `path_efficiency`,
             `monotonic_steps`, `hit_confirmed`, `actual_hitgroup`, `damage_dealt`) VALUES ");

        await using var cmd = new MySqlCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;

        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@sid{i}, @ts{i}, @tick{i}, @cmd{i}, @p{i}, @w{i}, @dist{i}, @snap{i}, @vel{i}, @err{i}, @eff{i}, @steps{i}, @hit{i}, @hg{i}, @dmg{i})");

            cmd.Parameters.AddWithValue($"@sid{i}", r.SessionId);
            cmd.Parameters.AddWithValue($"@ts{i}", r.TimestampUtc);
            cmd.Parameters.AddWithValue($"@tick{i}", r.ServerTick);
            cmd.Parameters.AddWithValue($"@cmd{i}", r.CommandNumber);
            cmd.Parameters.AddWithValue($"@p{i}", r.PlayerHash);
            cmd.Parameters.AddWithValue($"@w{i}", r.Weapon);
            cmd.Parameters.AddWithValue($"@dist{i}", r.TargetDistance);
            cmd.Parameters.AddWithValue($"@snap{i}", r.SnapAngle);
            cmd.Parameters.AddWithValue($"@vel{i}", r.AngularVelocity);
            cmd.Parameters.AddWithValue($"@err{i}", r.ErrorReduction);
            cmd.Parameters.AddWithValue($"@eff{i}", r.PathEfficiency);
            cmd.Parameters.AddWithValue($"@steps{i}", r.MonotonicSteps);
            cmd.Parameters.AddWithValue($"@hit{i}", r.HitConfirmed);
            cmd.Parameters.AddWithValue($"@hg{i}", r.ActualHitGroup);
            cmd.Parameters.AddWithValue($"@dmg{i}", r.DamageDealt);
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertTriggerReactionsAsync(MySqlConnection conn, MySqlTransaction tx, List<TriggerbotReactionRecord> list, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(@"INSERT INTO `telemetry_triggerbot_reactions`
            (`session_id`, `timestamp_utc`, `server_tick`, `command_number`, `player_hash`, `weapon`,
             `reaction_ticks`, `reaction_ms`, `attacker_speed`, `contact_mode`, `prior_clicks`,
             `target_distance`, `hit_confirmed`, `actual_hitgroup`, `smoke_occluded`) VALUES ");

        await using var cmd = new MySqlCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;

        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@sid{i}, @ts{i}, @tick{i}, @cmd{i}, @p{i}, @w{i}, @rt{i}, @rms{i}, @spd{i}, @mode{i}, @clk{i}, @dist{i}, @hit{i}, @hg{i}, @smk{i})");

            cmd.Parameters.AddWithValue($"@sid{i}", r.SessionId);
            cmd.Parameters.AddWithValue($"@ts{i}", r.TimestampUtc);
            cmd.Parameters.AddWithValue($"@tick{i}", r.ServerTick);
            cmd.Parameters.AddWithValue($"@cmd{i}", r.CommandNumber);
            cmd.Parameters.AddWithValue($"@p{i}", r.PlayerHash);
            cmd.Parameters.AddWithValue($"@w{i}", r.Weapon);
            cmd.Parameters.AddWithValue($"@rt{i}", r.ReactionTicks);
            cmd.Parameters.AddWithValue($"@rms{i}", r.ReactionMs);
            cmd.Parameters.AddWithValue($"@spd{i}", r.AttackerSpeed);
            cmd.Parameters.AddWithValue($"@mode{i}", r.ContactMode.ToString());
            cmd.Parameters.AddWithValue($"@clk{i}", r.PriorClicks);
            cmd.Parameters.AddWithValue($"@dist{i}", r.TargetDistance);
            cmd.Parameters.AddWithValue($"@hit{i}", r.HitConfirmed);
            cmd.Parameters.AddWithValue($"@hg{i}", r.ActualHitGroup);
            cmd.Parameters.AddWithValue($"@smk{i}", r.SmokeOccluded);
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAimlockEpisodesAsync(MySqlConnection conn, MySqlTransaction tx, List<AimlockEpisodeRecord> list, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(@"INSERT INTO `telemetry_aimlock_episodes`
            (`session_id`, `timestamp_utc`, `start_tick`, `end_tick`, `player_hash`,
             `duration_ticks`, `duration_seconds`, `target_displacement`, `on_target_ratio`,
             `avg_distance`, `completed_normally`) VALUES ");

        await using var cmd = new MySqlCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;

        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@sid{i}, @ts{i}, @st{i}, @et{i}, @p{i}, @dt{i}, @ds{i}, @disp{i}, @ratio{i}, @dist{i}, @comp{i})");

            cmd.Parameters.AddWithValue($"@sid{i}", r.SessionId);
            cmd.Parameters.AddWithValue($"@ts{i}", r.TimestampUtc);
            cmd.Parameters.AddWithValue($"@st{i}", r.StartTick);
            cmd.Parameters.AddWithValue($"@et{i}", r.EndTick);
            cmd.Parameters.AddWithValue($"@p{i}", r.PlayerHash);
            cmd.Parameters.AddWithValue($"@dt{i}", r.DurationTicks);
            cmd.Parameters.AddWithValue($"@ds{i}", r.DurationSeconds);
            cmd.Parameters.AddWithValue($"@disp{i}", r.TargetDisplacement);
            cmd.Parameters.AddWithValue($"@ratio{i}", r.OnTargetRatio);
            cmd.Parameters.AddWithValue($"@dist{i}", r.AvgDistance);
            cmd.Parameters.AddWithValue($"@comp{i}", r.CompletedNormally);
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertHealthAuditsAsync(MySqlConnection conn, MySqlTransaction tx, List<HealthAuditRecord> list, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(@"INSERT INTO `telemetry_health_audit`
            (`session_id`, `timestamp_utc`, `metrics_collected`, `metrics_written`, `metrics_dropped`, `database_errors`, `queue_peak_size`) VALUES ");

        await using var cmd = new MySqlCommand();
        cmd.Connection = conn;
        cmd.Transaction = tx;

        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (i > 0) sb.Append(',');
            sb.Append($"(@sid{i}, @ts{i}, @c{i}, @w{i}, @d{i}, @e{i}, @p{i})");

            cmd.Parameters.AddWithValue($"@sid{i}", r.SessionId);
            cmd.Parameters.AddWithValue($"@ts{i}", r.TimestampUtc);
            cmd.Parameters.AddWithValue($"@c{i}", r.MetricsCollected);
            cmd.Parameters.AddWithValue($"@w{i}", r.MetricsWritten);
            cmd.Parameters.AddWithValue($"@d{i}", r.MetricsDropped);
            cmd.Parameters.AddWithValue($"@e{i}", r.DatabaseErrors);
            cmd.Parameters.AddWithValue($"@p{i}", r.QueuePeakSize);
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Complete();
        if (_workerTask != null)
        {
            try { await _workerTask; } catch { }
        }
        _cts.Dispose();
    }
}
