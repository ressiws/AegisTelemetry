/**
 * =============================================================================
 * AegisTelemetry - Main Plugin Entry Point
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Passive telemetry collection plugin for SwiftlyS2 to calibrate AegisAC.
 *   Strictly non-punitive, thread-isolated, and MariaDB-backed.
 * =============================================================================
 */

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Plugins;
using AegisTelemetry.Configuration;
using AegisTelemetry.Database;
using AegisTelemetry.Modules;
using AegisTelemetry.Statistics;
using AegisTelemetry.Utils;

namespace AegisTelemetry;

[PluginMetadata(
    Id = "AegisTelemetry",
    Version = "1.0.0",
    Name = "Aegis Telemetry",
    Author = "swisser & sevon",
    Description = "Passive telemetry collector for AegisAC threshold calibration.",
    MinimumAPIVersion = "1.1.6")]
public sealed class AegisTelemetryPlugin : BasePlugin
{
    private TelemetryConfig _config = new();
    private BoundedChannelWriter _queue = null!;
    private DatabaseManager _dbManager = null!;
    private AimbotTelemetry _aimbot = null!;
    private TriggerbotTelemetry _triggerbot = null!;
    private AimlockTelemetry _aimlock = null!;
    private TelemetryStatsCalculator _stats = null!;
    private IDisposable? _configChangeToken;

    public AegisTelemetryPlugin(ISwiftlyCore core) : base(core) { }

    public override void Load(bool hotReload)
    {
        Core.Configuration
            .InitializeJsonWithModel<TelemetryConfig>("config.jsonc", "AegisTelemetry", addMissingKeys: true)
            .Configure(builder =>
            {
                builder.AddJsonFile(Core.Configuration.GetConfigPath("config.jsonc"), optional: false, reloadOnChange: true);
            });

        LoadConfig();
        _configChangeToken = ChangeToken.OnChange(() => Core.Configuration.Manager.GetReloadToken(), LoadConfig);

        _queue = new BoundedChannelWriter(_config.Queue.MaxQueueCapacity);
        _stats = new TelemetryStatsCalculator();

        _dbManager = new DatabaseManager(
            _config,
            _queue,
            msg => Core.Logger.LogInformation(msg),
            msg => Core.Logger.LogDebug(msg));

        _dbManager.Start();

        _aimbot = new AimbotTelemetry(
            _config,
            _queue,
            _stats,
            id => Core.PlayerManager.GetPlayer(id),
            msg => Core.Logger.LogDebug(msg));

        _triggerbot = new TriggerbotTelemetry(
            _config,
            _queue,
            _stats,
            Core.Trace,
            id => Core.PlayerManager.GetPlayer(id),
            msg => Core.Logger.LogDebug(msg));

        _aimlock = new AimlockTelemetry(
            _config,
            _queue,
            _stats,
            id => Core.PlayerManager.GetPlayer(id),
            msg => Core.Logger.LogDebug(msg));

        Core.GameHooks.Controller.ProcessUsercmds.Pre += OnProcessUsercmds;
        Core.GameHooks.Movement.SetupMove.Pre += OnSetupMove;
        Core.Event.OnTick += OnTick;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapLoad += OnMapLoad;

        if (hotReload)
        {
            string map = GetCurrentMapName();
            if (!string.IsNullOrEmpty(map) && map != "unknown")
            {
                _ = _dbManager.StartSessionAsync(map);
            }
        }

        Core.Logger.LogInformation("[AegisTelemetry] Initialized. Commands: aegis_telemetry_status, aegis_telemetry_stats, aegis_telemetry_reload");
    }

    public override void Unload()
    {
        _configChangeToken?.Dispose();
        Core.GameHooks.Controller.ProcessUsercmds.Pre -= OnProcessUsercmds;
        Core.GameHooks.Movement.SetupMove.Pre -= OnSetupMove;
        Core.Event.OnTick -= OnTick;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapLoad -= OnMapLoad;

        long sessionId = _dbManager.CurrentSessionId;
        _ = _dbManager.EndSessionAsync(sessionId);
        _ = _dbManager.DisposeAsync();
    }

    private void LoadConfig()
    {
        var section = Core.Configuration.Manager.GetSection("AegisTelemetry");
        var loaded = section.Get<TelemetryConfig>();
        if (loaded != null)
        {
            _config = loaded;
        }
    }

    private int CurrentTick
    {
        get
        {
            try
            {
                return Core.Engine.GlobalVars.TickCount;
            }
            catch
            {
                return 0;
            }
        }
    }

    private string GetCurrentMapName()
    {
        try
        {
            string? name = Core.Engine.GlobalVars.MapName;
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }
        catch
        {
            return "unknown";
        }
    }

    private void OnProcessUsercmds(ref ProcessUsercmdsPreContext ctx)
    {
        try
        {
            var player = ctx.Params.Player;
            var cmds = ctx.Params.Usercmds;
            if (player == null || !player.IsValid || cmds == null)
                return;

            _aimbot.OnProcessUsercmds(player, cmds);
            _triggerbot.OnProcessUsercmds(player, cmds);
            _aimlock.OnProcessUsercmds(player, cmds);
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in ProcessUsercmds: {ex.Message}");
        }
    }

    private void OnSetupMove(ref SetupMoveMovementPreContext ctx)
    {
        try
        {
            var ucmd = ctx.Params.UserCmd;
            var player = ctx.Params.Player;
            if (ucmd == null || player == null || !player.IsValid)
                return;

            int cmdNum = (int)ucmd.CommandNumber;
            int tick = CurrentTick;
            _aimbot.OnSetupMove(player, cmdNum, tick);
            _triggerbot.OnSetupMove(player, cmdNum, tick);
            _aimlock.OnSetupMove(player, cmdNum, tick);
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in SetupMove: {ex.Message}");
        }
    }

    private void OnTick()
    {
        try
        {
            int tick = CurrentTick;
            _aimbot.OnGameFrame(tick);
            _aimlock.OnGameFrame(tick);
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnTick: {ex.Message}");
        }
    }

    private void OnClientDisconnected(SwiftlyS2.Shared.Events.IOnClientDisconnectedEvent e)
    {
        try
        {
            _aimbot.OnClientDisconnect(e.PlayerId);
            _triggerbot.OnClientDisconnect(e.PlayerId);
            _aimlock.OnClientDisconnect(e.PlayerId);
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in ClientDisconnected: {ex.Message}");
        }
    }

    private void OnMapLoad(SwiftlyS2.Shared.Events.IOnMapLoadEvent e)
    {
        try
        {
            _aimbot.Reset();
            _triggerbot.Reset();
            _aimlock.Reset();

            long oldSession = _dbManager.CurrentSessionId;
            if (oldSession > 0)
            {
                _ = _dbManager.EndSessionAsync(oldSession);
            }

            string map = GetCurrentMapName();
            _ = _dbManager.StartSessionAsync(map);
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnMapLoad: {ex.Message}");
        }
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnWeaponFire(EventWeaponFire e)
    {
        try
        {
            var player = e.UserIdPlayer;
            if (player != null && PlayerFilter.IsEligibleCombatant(player))
            {
                string weapon = e.Weapon ?? "";
                int tick = CurrentTick;
                _aimbot.OnWeaponFire(player, weapon, tick);
                _triggerbot.OnWeaponFire(player, weapon, tick);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnWeaponFire: {ex.Message}");
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerHurt(EventPlayerHurt e)
    {
        try
        {
            var attacker = e.AttackerPlayer;
            var victim = e.UserIdPlayer;
            if (attacker != null && victim != null && PlayerFilter.AreEnemies(attacker, victim))
            {
                string weapon = e.Weapon ?? "";
                int dmg = e.ActualDmgHealth;
                short hg = (short)e.ActualHitGroup;
                int tick = CurrentTick;
                _aimbot.OnPlayerHurt(attacker, victim, weapon, dmg, hg, tick);
                _triggerbot.OnPlayerHurt(attacker, victim, weapon, hg, tick);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnPlayerHurt: {ex.Message}");
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDeath(EventPlayerDeath e)
    {
        try
        {
            var victim = e.UserIdPlayer;
            if (victim != null && victim.IsValid)
            {
                int victimId = victim.PlayerID;
                _aimbot.OnPlayerDeath(victimId);
                _triggerbot.OnPlayerDeath(victimId);
                _aimlock.OnPlayerDeath(victimId);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnPlayerDeath: {ex.Message}");
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn e)
    {
        try
        {
            var player = e.UserIdPlayer;
            if (player != null && player.IsValid)
            {
                int id = player.PlayerID;
                _aimbot.OnPlayerSpawn(id);
                _triggerbot.OnPlayerSpawn(id);
                _aimlock.OnPlayerSpawn(id);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnPlayerSpawn: {ex.Message}");
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnSmokeDetonate(EventSmokegrenadeDetonate e)
    {
        try
        {
            _triggerbot.OnSmokeDetonate(new Vector(e.X, e.Y, e.Z));
        }
        catch (Exception ex)
        {
            Core.Logger.LogDebug($"[AegisTelemetry] Error in OnSmokeDetonate: {ex.Message}");
        }
        return HookResult.Continue;
    }

    [Command("aegis_telemetry_status", registerRaw: true, permission: "@css/root")]
    public void StatusCommand(ICommandContext context)
    {
        context.Reply($"[AegisTelemetry] Session: #{_dbManager.CurrentSessionId} | Server: {_config.ServerId}");
        context.Reply($"[AegisTelemetry] Queue: {_queue.CurrentCount}/{_config.Queue.MaxQueueCapacity} (peak: {_queue.QueuePeakSize})");
        context.Reply($"[AegisTelemetry] Metrics: collected={_queue.MetricsCollected}, written={_queue.MetricsWritten}, dropped={_queue.MetricsDropped}, db_errors={_queue.DatabaseErrors}");
    }

    [Command("aegis_telemetry_stats", registerRaw: true, permission: "@css/root")]
    public void StatsCommand(ICommandContext context)
    {
        context.Reply("=== AegisTelemetry Live Calibration Distributions ===");

        var trigStats = _stats.GetReactionTimeStats();
        if (trigStats.HasValue)
        {
            var s = trigStats.Value;
            context.Reply($"[Triggerbot Reaction (ms)] N={s.SampleCount} | Min={s.Min:F1} | Mean={s.Mean:F1} (±{s.StandardDeviation:F1}) | p50={s.P50:F1} | p90={s.P90:F1} | p95={s.P95:F1} | p99={s.P99:F1}");
        }
        else
        {
            context.Reply("[Triggerbot Reaction] Insufficient samples (need >= 5).");
        }

        var snapStats = _stats.GetSnapAngleStats();
        if (snapStats.HasValue)
        {
            var s = snapStats.Value;
            context.Reply($"[Aimbot Snap (°)] N={s.SampleCount} | Min={s.Min:F2} | Mean={s.Mean:F2} (±{s.StandardDeviation:F2}) | p50={s.P50:F2} | p90={s.P90:F2} | p95={s.P95:F2} | p99={s.P99:F2}");
        }
        else
        {
            context.Reply("[Aimbot Snap] Insufficient samples (need >= 5).");
        }

        var effStats = _stats.GetPathEfficiencyStats();
        if (effStats.HasValue)
        {
            var s = effStats.Value;
            context.Reply($"[Aimbot Path Efficiency] N={s.SampleCount} | Mean={s.Mean * 100:F1}% | p50={s.P50 * 100:F1}% | p90={s.P90 * 100:F1}% | p95={s.P95 * 100:F1}% | p99={s.P99 * 100:F1}%");
        }

        var lockStats = _stats.GetTrackingDurationStats();
        if (lockStats.HasValue)
        {
            var s = lockStats.Value;
            context.Reply($"[Aimlock Duration (s)] N={s.SampleCount} | Min={s.Min:F2}s | Mean={s.Mean:F2}s | p50={s.P50:F2}s | p90={s.P90:F2}s | p95={s.P95:F2}s | p99={s.P99:F2}s");
        }
        else
        {
            context.Reply("[Aimlock Duration] Insufficient samples (need >= 5).");
        }
    }

    [Command("aegis_telemetry_reload", registerRaw: true, permission: "@css/root")]
    public void ReloadCommand(ICommandContext context)
    {
        LoadConfig();
        context.Reply("[AegisTelemetry] Configuration reloaded successfully.");
    }
}
