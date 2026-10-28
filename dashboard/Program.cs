/**
 * =============================================================================
 * AegisTelemetry Dashboard - Web Server & Calibration API
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Interactive web dashboard to visualize AegisAC telemetry metrics,
 *   percentile distributions, and auto-generate threshold calibrations.
 * =============================================================================
 */

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());

// Configuration State
var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
DbConfig dbConfig = LoadDbConfig(configPath, builder.Configuration);

string BuildConnectionString(DbConfig cfg)
{
    var b = new MySqlConnectionStringBuilder
    {
        Server = cfg.Host,
        Port = (uint)cfg.Port,
        Database = cfg.Database,
        UserID = cfg.User,
        Password = cfg.Password,
        SslMode = Enum.TryParse<MySqlSslMode>(cfg.SslMode, true, out var ssl) ? ssl : MySqlSslMode.None,
        ConnectionTimeout = 5,
        DefaultCommandTimeout = 30
    };
    return b.ConnectionString;
}

DbConfig LoadDbConfig(string path, IConfiguration fallback)
{
    try
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Database", out var dbProp))
            {
                return new DbConfig
                {
                    Host = dbProp.TryGetProperty("Host", out var h) ? h.GetString() ?? "localhost" : "localhost",
                    Port = dbProp.TryGetProperty("Port", out var p) ? p.GetInt32() : 3306,
                    Database = dbProp.TryGetProperty("Database", out var d) ? d.GetString() ?? "aegis_telemetry" : "aegis_telemetry",
                    User = dbProp.TryGetProperty("User", out var u) ? u.GetString() ?? "root" : "root",
                    Password = dbProp.TryGetProperty("Password", out var pw) ? pw.GetString() ?? "" : "",
                    SslMode = dbProp.TryGetProperty("SslMode", out var s) ? s.GetString() ?? "None" : "None"
                };
            }
        }
    }
    catch { }

    return new DbConfig
    {
        Host = fallback["Database:Host"] ?? "localhost",
        Port = int.TryParse(fallback["Database:Port"], out int pt) ? pt : 3306,
        Database = fallback["Database:Database"] ?? "aegis_telemetry",
        User = fallback["Database:User"] ?? "root",
        Password = fallback["Database:Password"] ?? "",
        SslMode = fallback["Database:SslMode"] ?? "None"
    };
}

void SaveDbConfig(string path, DbConfig cfg)
{
    try
    {
        var model = new { Database = cfg, Logging = new { LogLevel = new { Default = "Information" } } };
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
    catch { }
}

// -----------------------------------------------------------------------------
// API Endpoints
// -----------------------------------------------------------------------------

app.MapGet("/api/config", () =>
{
    return Results.Ok(new
    {
        Host = dbConfig.Host,
        Port = dbConfig.Port,
        Database = dbConfig.Database,
        User = dbConfig.User,
        HasPassword = !string.IsNullOrEmpty(dbConfig.Password),
        SslMode = dbConfig.SslMode
    });
});

app.MapPost("/api/config", ([FromBody] DbConfig input) =>
{
    dbConfig.Host = input.Host;
    dbConfig.Port = input.Port;
    dbConfig.Database = input.Database;
    dbConfig.User = input.User;
    if (!string.IsNullOrEmpty(input.Password))
        dbConfig.Password = input.Password;
    dbConfig.SslMode = input.SslMode;

    SaveDbConfig(configPath, dbConfig);
    return Results.Ok(new { success = true });
});

app.MapGet("/api/test", async () =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM `telemetry_sessions`;", conn);
        long sessionsCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        return Results.Ok(new { success = true, sessionsCount, message = "Connected successfully to MariaDB!" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/summary", async ([FromQuery] string? server, [FromQuery] string? map) =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        string sessionFilter = "";
        if (!string.IsNullOrEmpty(server)) sessionFilter += " AND server_id = @server";
        if (!string.IsNullOrEmpty(map)) sessionFilter += " AND map_name = @map";

        // Sessions summary
        var sqlSessions = $@"
            SELECT 
                COUNT(*) as total_sessions,
                COUNT(DISTINCT server_id) as total_servers,
                COUNT(DISTINCT map_name) as total_maps,
                MIN(start_time_utc) as first_session,
                MAX(start_time_utc) as last_session
            FROM `telemetry_sessions`
            WHERE 1=1 {sessionFilter};";

        await using var cmdSessions = new MySqlCommand(sqlSessions, conn);
        if (!string.IsNullOrEmpty(server)) cmdSessions.Parameters.AddWithValue("@server", server);
        if (!string.IsNullOrEmpty(map)) cmdSessions.Parameters.AddWithValue("@map", map);

        long totalSessions = 0, totalServers = 0, totalMaps = 0;
        DateTime? firstSession = null, lastSession = null;

        await using (var r = await cmdSessions.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                totalSessions = r.GetInt64("total_sessions");
                totalServers = r.GetInt64("total_servers");
                totalMaps = r.GetInt64("total_maps");
                firstSession = r.IsDBNull(r.GetOrdinal("first_session")) ? null : r.GetDateTime("first_session");
                lastSession = r.IsDBNull(r.GetOrdinal("last_session")) ? null : r.GetDateTime("last_session");
            }
        }

        // Aimbot counts
        var sqlAimbot = @"
            SELECT 
                COUNT(*) as total_shots,
                SUM(CASE WHEN hit_confirmed = 1 THEN 1 ELSE 0 END) as hit_shots,
                COUNT(DISTINCT player_hash) as distinct_players
            FROM `telemetry_aimbot_shots`;";

        await using var cmdAimbot = new MySqlCommand(sqlAimbot, conn);
        long totalShots = 0, hitShots = 0, distinctAimbotPlayers = 0;
        await using (var r = await cmdAimbot.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                totalShots = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                hitShots = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                distinctAimbotPlayers = r.IsDBNull(2) ? 0 : r.GetInt64(2);
            }
        }

        // Triggerbot counts
        var sqlTrigger = @"
            SELECT 
                COUNT(*) as total_reactions,
                SUM(CASE WHEN contact_mode = 'TargetDriven' THEN 1 ELSE 0 END) as target_driven,
                SUM(CASE WHEN contact_mode = 'AimDriven' THEN 1 ELSE 0 END) as aim_driven,
                SUM(CASE WHEN smoke_occluded = 1 THEN 1 ELSE 0 END) as smoke_occluded
            FROM `telemetry_triggerbot_reactions`;";

        await using var cmdTrigger = new MySqlCommand(sqlTrigger, conn);
        long totalReactions = 0, targetDriven = 0, aimDriven = 0, smokeOccluded = 0;
        await using (var r = await cmdTrigger.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                totalReactions = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                targetDriven = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                aimDriven = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                smokeOccluded = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            }
        }

        // Aimlock counts
        var sqlAimlock = @"
            SELECT 
                COUNT(*) as total_episodes,
                AVG(duration_seconds) as avg_duration,
                MAX(duration_seconds) as max_duration
            FROM `telemetry_aimlock_episodes`;";

        await using var cmdAimlock = new MySqlCommand(sqlAimlock, conn);
        long totalEpisodes = 0;
        float avgLockDuration = 0, maxLockDuration = 0;
        await using (var r = await cmdAimlock.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                totalEpisodes = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                avgLockDuration = r.IsDBNull(1) ? 0 : Convert.ToSingle(r.GetValue(1));
                maxLockDuration = r.IsDBNull(2) ? 0 : Convert.ToSingle(r.GetValue(2));
            }
        }

        return Results.Ok(new
        {
            success = true,
            sessions = new { total = totalSessions, servers = totalServers, maps = totalMaps, first = firstSession, last = lastSession },
            aimbot = new { totalShots, hitShots, hitRate = totalShots > 0 ? (float)hitShots / totalShots * 100f : 0f, distinctPlayers = distinctAimbotPlayers },
            triggerbot = new { totalReactions, targetDriven, aimDriven, smokeOccluded },
            aimlock = new { totalEpisodes, avgDuration = avgLockDuration, maxDuration = maxLockDuration }
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/aimbot", async ([FromQuery] string? weapon) =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        string filter = !string.IsNullOrEmpty(weapon) ? "WHERE weapon = @weapon" : "";
        string sql = $"SELECT snap_angle, angular_velocity, path_efficiency, error_reduction, monotonic_steps, hit_confirmed, weapon FROM `telemetry_aimbot_shots` {filter};";

        await using var cmd = new MySqlCommand(sql, conn);
        if (!string.IsNullOrEmpty(weapon)) cmd.Parameters.AddWithValue("@weapon", weapon);

        var snapList = new List<float>();
        var velList = new List<float>();
        var effList = new List<float>();
        var errRedList = new List<float>();
        var stepsList = new List<float>();

        var weaponStats = new Dictionary<string, (int count, int hits, List<float> snaps)>(StringComparer.OrdinalIgnoreCase);

        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                float snap = r.GetFloat("snap_angle");
                float vel = r.GetFloat("angular_velocity");
                float eff = r.GetFloat("path_efficiency");
                float red = r.GetFloat("error_reduction");
                float steps = r.GetInt16("monotonic_steps");
                bool hit = r.GetBoolean("hit_confirmed");
                string w = r.GetString("weapon");

                snapList.Add(snap);
                velList.Add(vel);
                effList.Add(eff);
                errRedList.Add(red);
                stepsList.Add(steps);

                if (!weaponStats.TryGetValue(w, out var tuple))
                {
                    tuple = (0, 0, new List<float>());
                    weaponStats[w] = tuple;
                }
                tuple.count++;
                if (hit) tuple.hits++;
                tuple.snaps.Add(snap);
                weaponStats[w] = tuple;
            }
        }

        // Histograms
        var snapHistogram = new Dictionary<string, int>
        {
            ["0° - 2°"] = snapList.Count(s => s < 2f),
            ["2° - 5°"] = snapList.Count(s => s >= 2f && s < 5f),
            ["5° - 10°"] = snapList.Count(s => s >= 5f && s < 10f),
            ["10° - 15°"] = snapList.Count(s => s >= 10f && s < 15f),
            ["15° - 20°"] = snapList.Count(s => s >= 15f && s < 20f),
            ["> 20°"] = snapList.Count(s => s >= 20f)
        };

        var effHistogram = new Dictionary<string, int>
        {
            ["< 40%"] = effList.Count(e => e < 0.40f),
            ["40% - 60%"] = effList.Count(e => e >= 0.40f && e < 0.60f),
            ["60% - 80%"] = effList.Count(e => e >= 0.60f && e < 0.80f),
            ["80% - 90%"] = effList.Count(e => e >= 0.80f && e < 0.90f),
            ["90% - 95%"] = effList.Count(e => e >= 0.90f && e < 0.95f),
            ["95% - 100%"] = effList.Count(e => e >= 0.95f)
        };

        var weaponsTable = weaponStats.Select(kv =>
        {
            var s = CalculatePercentiles(kv.Value.snaps);
            return new
            {
                weapon = kv.Key,
                count = kv.Value.count,
                hits = kv.Value.hits,
                hitRate = kv.Value.count > 0 ? (float)kv.Value.hits / kv.Value.count * 100f : 0f,
                p50Snap = s?.P50 ?? 0f,
                p95Snap = s?.P95 ?? 0f,
                p99Snap = s?.P99 ?? 0f
            };
        }).OrderByDescending(w => w.count).Take(15).ToList();

        return Results.Ok(new
        {
            success = true,
            count = snapList.Count,
            snap = CalculatePercentiles(snapList),
            angularVelocity = CalculatePercentiles(velList),
            pathEfficiency = CalculatePercentiles(effList),
            errorReduction = CalculatePercentiles(errRedList),
            monotonicSteps = CalculatePercentiles(stepsList),
            snapHistogram,
            effHistogram,
            weapons = weaponsTable
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/triggerbot", async () =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        const string sql = "SELECT reaction_ms, attacker_speed, contact_mode, prior_clicks, smoke_occluded, hit_confirmed FROM `telemetry_triggerbot_reactions`;";
        await using var cmd = new MySqlCommand(sql, conn);

        var allReactions = new List<float>();
        var targetDriven = new List<float>();
        var aimDriven = new List<float>();
        var stationary = new List<float>();
        var moving = new List<float>();
        var running = new List<float>();
        var smoke = new List<float>();
        var clear = new List<float>();

        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                float ms = r.GetFloat("reaction_ms");
                float spd = r.GetFloat("attacker_speed");
                string mode = r.GetString("contact_mode");
                bool isSmoke = r.GetBoolean("smoke_occluded");

                allReactions.Add(ms);
                if (mode == "TargetDriven") targetDriven.Add(ms);
                else aimDriven.Add(ms);

                if (spd < 30.0f) stationary.Add(ms);
                else if (spd < 150.0f) moving.Add(ms);
                else running.Add(ms);

                if (isSmoke) smoke.Add(ms);
                else clear.Add(ms);
            }
        }

        var histogram = new Dictionary<string, int>
        {
            ["< 100 ms"] = allReactions.Count(m => m < 100f),
            ["100 - 140 ms"] = allReactions.Count(m => m >= 100f && m < 140f),
            ["140 - 180 ms"] = allReactions.Count(m => m >= 140f && m < 180f),
            ["180 - 220 ms"] = allReactions.Count(m => m >= 180f && m < 220f),
            ["220 - 280 ms"] = allReactions.Count(m => m >= 220f && m < 280f),
            ["> 280 ms"] = allReactions.Count(m => m >= 280f)
        };

        return Results.Ok(new
        {
            success = true,
            count = allReactions.Count,
            overall = CalculatePercentiles(allReactions),
            targetDriven = CalculatePercentiles(targetDriven),
            aimDriven = CalculatePercentiles(aimDriven),
            stationary = CalculatePercentiles(stationary),
            moving = CalculatePercentiles(moving),
            running = CalculatePercentiles(running),
            smoke = CalculatePercentiles(smoke),
            clear = CalculatePercentiles(clear),
            histogram
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/aimlock", async () =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        const string sql = "SELECT duration_seconds, target_displacement, on_target_ratio, avg_distance, completed_normally FROM `telemetry_aimlock_episodes`;";
        await using var cmd = new MySqlCommand(sql, conn);

        var durationList = new List<float>();
        var displacementList = new List<float>();
        var ratioList = new List<float>();
        var distanceList = new List<float>();

        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                durationList.Add(r.GetFloat("duration_seconds"));
                displacementList.Add(r.GetFloat("target_displacement"));
                ratioList.Add(r.GetFloat("on_target_ratio"));
                distanceList.Add(r.GetFloat("avg_distance"));
            }
        }

        var durationHistogram = new Dictionary<string, int>
        {
            ["< 0.25 s"] = durationList.Count(d => d < 0.25f),
            ["0.25 - 0.50 s"] = durationList.Count(d => d >= 0.25f && d < 0.50f),
            ["0.50 - 0.75 s"] = durationList.Count(d => d >= 0.50f && d < 0.75f),
            ["0.75 - 1.00 s"] = durationList.Count(d => d >= 0.75f && d < 1.00f),
            ["1.00 - 1.50 s"] = durationList.Count(d => d >= 1.00f && d < 1.50f),
            ["> 1.50 s"] = durationList.Count(d => d >= 1.50f)
        };

        return Results.Ok(new
        {
            success = true,
            count = durationList.Count,
            duration = CalculatePercentiles(durationList),
            displacement = CalculatePercentiles(displacementList),
            onTargetRatio = CalculatePercentiles(ratioList),
            distance = CalculatePercentiles(distanceList),
            durationHistogram
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/calibration-report", async () =>
{
    try
    {
        await using var conn = new MySqlConnection(BuildConnectionString(dbConfig));
        await conn.OpenAsync();

        // 1. Aimbot Shots
        var snapList = new List<float>();
        var effList = new List<float>();
        await using (var cmd = new MySqlCommand("SELECT snap_angle, path_efficiency FROM `telemetry_aimbot_shots`;", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                snapList.Add(r.GetFloat(0));
                effList.Add(r.GetFloat(1));
            }
        }

        // 2. Triggerbot
        var reactList = new List<float>();
        await using (var cmd = new MySqlCommand("SELECT reaction_ms FROM `telemetry_triggerbot_reactions`;", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
                reactList.Add(r.GetFloat(0));
        }

        // 3. Aimlock
        var lockList = new List<float>();
        var dispList = new List<float>();
        await using (var cmd = new MySqlCommand("SELECT duration_seconds, target_displacement FROM `telemetry_aimlock_episodes`;", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                lockList.Add(r.GetFloat(0));
                dispList.Add(r.GetFloat(1));
            }
        }

        var snapStats = CalculatePercentiles(snapList);
        var effStats = CalculatePercentiles(effList);
        var reactStats = CalculatePercentiles(reactList);
        var lockStats = CalculatePercentiles(lockList);
        var dispStats = CalculatePercentiles(dispList);

        // Derive safe thresholds
        float suggestedSnapThreshold = snapStats != null ? MathF.Max(20.0f, snapStats.P99 * 1.25f) : 22.0f;
        float suggestedMinSmoothMovement = snapStats != null ? MathF.Max(12.0f, snapStats.P95 * 1.3f) : 13.0f;
        float suggestedSmoothEfficiency = effStats != null ? MathF.Min(0.96f, MathF.Max(0.92f, effStats.P99 * 0.98f)) : 0.94f;
        float suggestedReactionMs = reactStats != null && reactStats.SampleCount >= 5 ? MathF.Max(90.0f, reactStats.Min * 0.75f) : 120.0f;
        float suggestedLockDuration = lockStats != null ? MathF.Max(1.6f, lockStats.P99 * 1.35f) : 2.0f;

        var sb = new StringBuilder();
        sb.AppendLine("# 🛡️ AegisAC Calibration Report");
        sb.AppendLine($"*Generated at: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*\n");
        sb.AppendLine("## 📊 Summary of Collected Live Telemetry");
        sb.AppendLine($"- **Aimbot Shots Sampled**: {snapList.Count:N0}");
        sb.AppendLine($"- **Triggerbot Reactions Sampled**: {reactList.Count:N0}");
        sb.AppendLine($"- **Aimlock Tracking Episodes**: {lockList.Count:N0}\n");

        sb.AppendLine("## 🎯 1. Aimbot Calibrations");
        if (snapStats != null)
        {
            sb.AppendLine($"| Metric | Samples | Mean | P50 (Median) | P90 | P95 | P99 | Max |");
            sb.AppendLine($"|---|---|---|---|---|---|---|---|");
            sb.AppendLine($"| **Snap Angle (°)** | {snapStats.SampleCount} | {snapStats.Mean:F2}° | {snapStats.P50:F2}° | {snapStats.P90:F2}° | {snapStats.P95:F2}° | {snapStats.P99:F2}° | {snapStats.Max:F2}° |");
            if (effStats != null)
                sb.AppendLine($"| **Path Efficiency** | {effStats.SampleCount} | {effStats.Mean * 100:F1}% | {effStats.P50 * 100:F1}% | {effStats.P90 * 100:F1}% | {effStats.P95 * 100:F1}% | {effStats.P99 * 100:F1}% | {effStats.Max * 100:F1}% |");
            sb.AppendLine();
            sb.AppendLine($"> **Recommended AegisAC Settings**:");
            sb.AppendLine($"> - `Aimbot.SnapAngleThreshold`: **{suggestedSnapThreshold:F1}°** (P99 + 25% safety margin)");
            sb.AppendLine($"> - `Aimbot.SmoothMinimumMovement`: **{suggestedMinSmoothMovement:F1}°**");
            sb.AppendLine($"> - `Aimbot.SmoothMinimumPathEfficiency`: **{suggestedSmoothEfficiency * 100:F1}%**\n");
        }
        else
        {
            sb.AppendLine("*Insufficient aimbot samples gathered yet.*\n");
        }

        sb.AppendLine("## ⚡ 2. Triggerbot Calibrations");
        if (reactStats != null && reactStats.SampleCount >= 5)
        {
            sb.AppendLine($"| Metric | Samples | Min (Fastest) | Mean | P50 | P90 | P95 | P99 |");
            sb.AppendLine($"|---|---|---|---|---|---|---|---|");
            sb.AppendLine($"| **Reaction Time (ms)** | {reactStats.SampleCount} | {reactStats.Min:F1}ms | {reactStats.Mean:F1}ms | {reactStats.P50:F1}ms | {reactStats.P90:F1}ms | {reactStats.P95:F1}ms | {reactStats.P99:F1}ms |");
            sb.AppendLine();
            sb.AppendLine($"> **Recommended AegisAC Settings**:");
            sb.AppendLine($"> - `Triggerbot.MaxReactionTimeMs`: **{suggestedReactionMs:F1} ms** (Below human P01/Min with 25% buffer)");
            sb.AppendLine($"> - `Triggerbot.EvidenceThreshold`: **3 incidents in 5 min**\n");
        }
        else
        {
            sb.AppendLine("*Insufficient reaction samples (need >= 5 to prevent bias). Default suggested: 120.0 ms*\n");
        }

        sb.AppendLine("## 🔒 3. Aimlock Calibrations");
        if (lockStats != null && lockStats.SampleCount >= 5)
        {
            sb.AppendLine($"| Metric | Samples | Mean | P50 (Median) | P90 | P95 | P99 | Max |");
            sb.AppendLine($"|---|---|---|---|---|---|---|---|");
            sb.AppendLine($"| **Lock Duration (s)** | {lockStats.SampleCount} | {lockStats.Mean:F2}s | {lockStats.P50:F2}s | {lockStats.P90:F2}s | {lockStats.P95:F2}s | {lockStats.P99:F2}s | {lockStats.Max:F2}s |");
            if (dispStats != null)
                sb.AppendLine($"| **Target Displacement** | {dispStats.SampleCount} | {dispStats.Mean:F1}° | {dispStats.P50:F1}° | {dispStats.P90:F1}° | {dispStats.P95:F1}° | {dispStats.P99:F1}° | {dispStats.Max:F1}° |");
            sb.AppendLine();
            sb.AppendLine($"> **Recommended AegisAC Settings**:");
            sb.AppendLine($"> - `Aimlock.TrackingDurationSeconds`: **{suggestedLockDuration:F2} s** (Exceeds 99% of normal sprays)");
            sb.AppendLine($"> - `Aimlock.EvidenceThreshold`: **3 incidents in 5 min**\n");
        }
        else
        {
            sb.AppendLine("*Insufficient aimlock samples gathered yet. Default suggested: 2.0 s*\n");
        }

        return Results.Ok(new
        {
            success = true,
            markdown = sb.ToString(),
            recommendations = new
            {
                snapThreshold = suggestedSnapThreshold,
                smoothMovement = suggestedMinSmoothMovement,
                smoothEfficiency = suggestedSmoothEfficiency,
                reactionMs = suggestedReactionMs,
                lockDuration = suggestedLockDuration
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = ex.Message });
    }
});

// Helper for Percentile Calculation
static StatsResult? CalculatePercentiles(List<float> list)
{
    if (list.Count == 0) return null;
    list.Sort();

    float min = list[0];
    float max = list[^1];
    float sum = 0f;
    for (int i = 0; i < list.Count; i++) sum += list[i];
    float mean = sum / list.Count;

    float sumSq = 0f;
    for (int i = 0; i < list.Count; i++)
    {
        float diff = list[i] - mean;
        sumSq += diff * diff;
    }
    float stdDev = MathF.Sqrt(sumSq / list.Count);

    float GetP(float p)
    {
        if (list.Count == 1) return list[0];
        float rank = (p / 100f) * (list.Count - 1);
        int low = (int)MathF.Floor(rank);
        int high = (int)MathF.Ceiling(rank);
        float frac = rank - low;
        return list[low] + frac * (list[high] - list[low]);
    }

    return new StatsResult(
        list.Count,
        min,
        max,
        mean,
        stdDev,
        GetP(10f),
        GetP(25f),
        GetP(50f),
        GetP(75f),
        GetP(90f),
        GetP(95f),
        GetP(99f)
    );
}

// Open browser on launch if available
var url = "http://localhost:5050";
_ = Task.Run(async () =>
{
    await Task.Delay(1000);
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch { }
});

Console.WriteLine($"========================================================");
Console.WriteLine($" AegisTelemetry Web Dashboard running at {url}");
Console.WriteLine($" Open your browser to view live data & calibration.");
Console.WriteLine($"========================================================");

app.Run(url);

public record DbConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "aegis_telemetry";
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
    public string SslMode { get; set; } = "None";
}

public record StatsResult(
    int SampleCount,
    float Min,
    float Max,
    float Mean,
    float StandardDeviation,
    float P10,
    float P25,
    float P50,
    float P75,
    float P90,
    float P95,
    float P99
);
