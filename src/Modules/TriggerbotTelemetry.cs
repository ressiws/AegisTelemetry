/**
 * =============================================================================
 * AegisTelemetry - Module: Triggerbot Telemetry
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Measures real human crosshair acquisition delays, reaction distributions,
 *   attacker velocity at time of shot (peeker strafe vs holding pixel),
 *   click cadence spam, and smoke occlusion states.
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using System.Linq;
using AegisTelemetry.Configuration;
using AegisTelemetry.Database;
using AegisTelemetry.Models;
using AegisTelemetry.Statistics;
using AegisTelemetry.Utils;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Services;
using SwiftlyS2.Shared.Trace;

namespace AegisTelemetry.Modules;

internal sealed class TelemetryTriggerCmd
{
    public required int CommandNumber { get; init; }
    public required QAngle Angles { get; init; }
    public required bool IsAttackPressed { get; init; }
}

internal sealed class TelemetryPendingReaction
{
    public required int CommandNumber { get; init; }
    public required int TargetIndex { get; init; }
    public required int ReactionTicks { get; init; }
    public required float ReactionMs { get; init; }
    public required float AttackerSpeed { get; init; }
    public required TelemetryContactMode ContactMode { get; init; }
    public required short PriorClicks { get; init; }
    public required float TargetDistance { get; init; }
    public required bool SmokeOccluded { get; init; }
    public required string Weapon { get; init; }
    public bool Evaluated { get; set; }
}

internal sealed class TelemetryTriggerPlayerState
{
    public int LastCommandNumber = -1;
    public int ActiveTarget = -1;
    public int ContactStartCommand = -1;
    public TelemetryContactMode ActiveMode = TelemetryContactMode.TargetDriven;
    public QAngle LastAngles;
    public bool LastAnglesValid;
    public Vector LastOrigin;
    public bool LastOriginValid;
    public bool WasAttacking;

    public readonly List<TelemetryTriggerCmd> Commands = new();
    public readonly List<TelemetryPendingReaction> Pending = new();

    public void Reset()
    {
        LastCommandNumber = -1;
        ActiveTarget = -1;
        ContactStartCommand = -1;
        ActiveMode = TelemetryContactMode.TargetDriven;
        LastAnglesValid = false;
        LastOriginValid = false;
        WasAttacking = false;
        Commands.Clear();
        Pending.Clear();
    }
}

internal sealed class TelemetrySmoke
{
    public required Vector Center { get; init; }
    public required DateTime Expires { get; init; }
}

public sealed class TriggerbotTelemetry
{
    private const int MaxPlayers = 65;
    private const float HeldAngleThreshold = 0.5f;
    private const float TraceLength = 8192.0f;
    private static readonly TimeSpan SmokeLifetime = TimeSpan.FromMilliseconds(22500);

    private static readonly HashSet<string> BallisticWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
        "hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
        "p90","sawedoff","scar20","sg556","ssg08","tec9","ump45","usp_silencer","xm1014"
    };

    private readonly TelemetryConfig _config;
    private readonly BoundedChannelWriter _queue;
    private readonly TelemetryStatsCalculator _stats;
    private readonly ITraceManager _traceManager;
    private readonly Func<int, IPlayer?> _getPlayer;
    private readonly Action<string> _debug;

    private readonly TelemetryTriggerPlayerState[] _players = new TelemetryTriggerPlayerState[MaxPlayers];
    private readonly List<TelemetrySmoke> _smokes = new();

    public TriggerbotTelemetry(
        TelemetryConfig config,
        BoundedChannelWriter queue,
        TelemetryStatsCalculator stats,
        ITraceManager traceManager,
        Func<int, IPlayer?> getPlayer,
        Action<string> debug)
    {
        _config = config;
        _queue = queue;
        _stats = stats;
        _traceManager = traceManager;
        _getPlayer = getPlayer;
        _debug = debug;

        for (int i = 0; i < _players.Length; i++)
            _players[i] = new TelemetryTriggerPlayerState();
    }

    public void Reset()
    {
        for (int i = 0; i < _players.Length; i++)
            _players[i].Reset();
        _smokes.Clear();
    }

    public void OnClientDisconnect(int id)
    {
        if (id >= 0 && id < _players.Length)
            _players[id].Reset();
    }

    public void OnPlayerDeath(int victimId)
    {
        if (victimId >= 0 && victimId < _players.Length)
            _players[victimId].Reset();
    }

    public void OnPlayerSpawn(int playerId)
    {
        if (playerId >= 0 && playerId < _players.Length)
            _players[playerId].Reset();
    }

    public void OnSmokeDetonate(Vector center)
    {
        if (!AimMath.IsFinite(center))
            return;

        _smokes.Add(new TelemetrySmoke { Center = center, Expires = DateTime.UtcNow + SmokeLifetime });
    }

    public void OnProcessUsercmds(IPlayer player, List<IUserCmd> cmds)
    {
        if (!_config.EnableTriggerbotTelemetry || !PlayerFilter.IsEligibleCombatant(player) || cmds.Count == 0)
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];

        foreach (var cmd in cmds)
        {
            var pb = cmd.CSGOUserCmd;
            int number = (int)cmd.CommandNumber;

            if (st.Commands.Exists(c => c.CommandNumber == number))
                continue;

            if (!pb.Accessor.HasField("base"))
                continue;

            var b = pb.Base;
            if (!b.Accessor.HasField("viewangles"))
                continue;

            QAngle angles = b.Viewangles;
            bool isAttack = false;
            int idx = pb.Attack1StartHistoryIndex;
            int histCount = pb.InputHistory.Count;

            if (idx >= 0 && idx < histCount)
            {
                var entry = pb.InputHistory.Get(idx);
                if (entry != null && entry.Accessor != null && entry.Accessor.HasField("view_angles"))
                {
                    QAngle shotAngles = entry.ViewAngles;
                    if (AimMath.IsFinite(shotAngles))
                    {
                        angles = shotAngles;
                        isAttack = true;
                    }
                }
            }

            if (!AimMath.IsFinite(angles))
                continue;

            st.Commands.Add(new TelemetryTriggerCmd
            {
                CommandNumber = number,
                Angles = angles,
                IsAttackPressed = isAttack
            });

            if (st.Commands.Count > 128)
                st.Commands.RemoveAt(0);
        }
    }

    public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
    {
        if (!_config.EnableTriggerbotTelemetry || !PlayerFilter.IsEligibleCombatant(player))
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
            return;

        var st = _players[id];
        var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
        if (cmd == null && commandNumber <= st.LastCommandNumber)
            return;

        QAngle viewAngles = cmd != null ? cmd.Angles : pawn.EyeAngles;
        bool isAttackPressed = cmd != null && cmd.IsAttackPressed;
        bool isFreshAttack = isAttackPressed && !st.WasAttacking;
        st.WasAttacking = isAttackPressed;

        if (commandNumber <= st.LastCommandNumber)
            return;

        st.LastCommandNumber = commandNumber;

        Vector origin = pawn.AbsOrigin ?? default;
        Vector eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);
        if (!AimMath.IsFinite(eye) || !AimMath.IsFinite(viewAngles))
            return;

        // Calculate horizontal movement speed (unidades/s)
        float attackerSpeed = 0.0f;
        if (st.LastOriginValid)
        {
            float dx = origin.X - st.LastOrigin.X;
            float dy = origin.Y - st.LastOrigin.Y;
            attackerSpeed = MathF.Sqrt(dx * dx + dy * dy) * 64.0f;
        }
        st.LastOrigin = origin;
        st.LastOriginValid = true;

        var (targetIndex, hitPoint) = TraceCrosshair(player, eye, viewAngles);

        if (targetIndex != st.ActiveTarget)
        {
            st.ActiveTarget = targetIndex;
            st.ContactStartCommand = -1;

            if (targetIndex >= 0)
            {
                st.ContactStartCommand = commandNumber;
                float aimMovement = st.LastAnglesValid ? AimMath.AngularDistance(st.LastAngles, viewAngles) : 0.0f;
                st.ActiveMode = float.IsFinite(aimMovement) && aimMovement > HeldAngleThreshold
                    ? TelemetryContactMode.AimDriven
                    : TelemetryContactMode.TargetDriven;
            }
        }

        st.LastAngles = viewAngles;
        st.LastAnglesValid = true;

        // Prune expired smokes
        var now = DateTime.UtcNow;
        _smokes.RemoveAll(s => s.Expires <= now);
        bool smokeOccluded = targetIndex >= 0 && _smokes.Any(s => SegmentTouchesSmoke(eye, hitPoint, s.Center));

        if (isFreshAttack && targetIndex >= 0 && st.ContactStartCommand >= 0)
        {
            int reactionTicks = commandNumber - st.ContactStartCommand;
            float reactionMs = reactionTicks * (1000.0f / 64.0f);

            // Count prior clicks in last 5 commands
            short priorClicks = 0;
            int startIdx = Math.Max(0, st.Commands.Count - 6);
            for (int i = startIdx; i < st.Commands.Count - 1; i++)
            {
                if (st.Commands[i].IsAttackPressed)
                    priorClicks++;
            }

            float targetDistance = eye.Distance(hitPoint);

            st.Pending.Add(new TelemetryPendingReaction
            {
                CommandNumber = commandNumber,
                TargetIndex = targetIndex,
                ReactionTicks = reactionTicks,
                ReactionMs = reactionMs,
                AttackerSpeed = attackerSpeed,
                ContactMode = st.ActiveMode,
                PriorClicks = priorClicks,
                TargetDistance = targetDistance,
                SmokeOccluded = smokeOccluded,
                Weapon = "unknown"
            });

            while (st.Pending.Count > 16)
                st.Pending.RemoveAt(0);
        }
    }

    public void OnWeaponFire(IPlayer player, string weapon, int currentTick)
    {
        if (!_config.EnableTriggerbotTelemetry || !PlayerFilter.IsEligibleCombatant(player))
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        string normWeapon = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
        if (!BallisticWeapons.Contains(normWeapon))
            return;

        var pending = st.Pending.LastOrDefault(p => !p.Evaluated);
        if (pending == null)
            return;

        // If weapon fire matched, update weapon
        pending.Evaluated = true;

        // If no hurt arrives within 3 ticks, emit record as miss
        // Otherwise OnPlayerHurt will enrich it with hit confirmation
        EmitRecord(player, pending, normWeapon, hitConfirmed: false, hitgroup: 0);
    }

    public void OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, short hitgroup, int currentTick)
    {
        if (!_config.EnableTriggerbotTelemetry || !PlayerFilter.IsEligibleCombatant(attacker))
            return;

        int id = attacker.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        string normWeapon = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
        if (!BallisticWeapons.Contains(normWeapon))
            return;

        var pending = st.Pending.LastOrDefault(p => p.TargetIndex == victim.PlayerID);
        if (pending == null)
            return;

        EmitRecord(attacker, pending, normWeapon, hitConfirmed: true, hitgroup: hitgroup);
        st.Pending.Remove(pending);
    }

    private void EmitRecord(IPlayer attacker, TelemetryPendingReaction pending, string weapon, bool hitConfirmed, short hitgroup)
    {
        string playerHash = Pseudonymizer.HashSteamId(attacker.SteamID, _config.PseudonymizationSalt);

        var record = new TriggerbotReactionRecord
        {
            TimestampUtc = DateTime.UtcNow,
            ServerTick = 0,
            CommandNumber = pending.CommandNumber,
            PlayerHash = playerHash,
            Weapon = weapon,
            ReactionTicks = pending.ReactionTicks,
            ReactionMs = pending.ReactionMs,
            AttackerSpeed = pending.AttackerSpeed,
            ContactMode = pending.ContactMode,
            PriorClicks = pending.PriorClicks,
            TargetDistance = pending.TargetDistance,
            HitConfirmed = hitConfirmed,
            ActualHitGroup = hitgroup,
            SmokeOccluded = pending.SmokeOccluded
        };

        // Only accumulate clean TargetDriven first-click reactions into the live calibration reservoir
        // to prevent flick swipes (AimDriven) and continuous spray clicks from contaminating visual reaction time stats.
        if (pending.ContactMode == TelemetryContactMode.TargetDriven && pending.PriorClicks == 0)
        {
            _stats.RecordReactionTime(pending.ReactionMs);
        }

        _queue.Enqueue(record);
    }

    private (int targetIndex, Vector hitPoint) TraceCrosshair(IPlayer player, in Vector eye, in QAngle angles)
    {
        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
            return (-1, Vector.Zero);

        Vector forward = AimMath.Forward(angles);
        Vector end = new(eye.X + forward.X * TraceLength, eye.Y + forward.Y * TraceLength, eye.Z + forward.Z * TraceLength);

        float maxDistance = TraceLength;

        // Native engine trace is optional and disabled by default to avoid unmanaged C++ engine crashes during sprays/movement.
        if (_config.EnableNativeTrace)
        {
            try
            {
                var traceParams = new TraceParams
                {
                    InteractWith = MaskTrace.Solid | MaskTrace.Hitbox | MaskTrace.Player |
                                   MaskTrace.CsgoTeam1 | MaskTrace.CsgoTeam2
                };

                var trace = _traceManager.TraceShapeLine(eye, end, traceParams);
                if (trace.DidHit && trace.Distance > 0.0f)
                    maxDistance = trace.Distance;

                if (trace.DidHit && trace.Entity != null && trace.Entity.IsValid)
                {
                    uint hitIndex = trace.Entity.Index;
                    for (int i = 0; i < MaxPlayers; i++)
                    {
                        var target = _getPlayer(i);
                        if (!PlayerFilter.IsValidTarget(target) || target!.PlayerID == player.PlayerID)
                            continue;

                        if (!PlayerFilter.AreEnemies(player, target))
                            continue;

                        var targetPawn = target.PlayerPawn;
                        if (targetPawn != null && targetPawn.IsValid && targetPawn.Index == hitIndex)
                            return (target.PlayerID, trace.EndPos);
                    }
                }
            }
            catch (Exception ex)
            {
                _debug($"[TriggerbotTelemetry] Trace exception: {ex.Message}");
            }
        }

        // Geometric cylinder intersection fallback (100% managed C#, zero native calls, zero SIGSEGV risk)
        int bestTarget = -1;
        float bestDist = float.MaxValue;
        Vector bestPoint = end;

        for (int i = 0; i < MaxPlayers; i++)
        {
            var target = _getPlayer(i);
            if (!PlayerFilter.IsValidTarget(target) || target!.PlayerID == player.PlayerID)
                continue;

            if (!PlayerFilter.AreEnemies(player, target))
                continue;

            var targetPawn = target.PlayerPawn;
            if (targetPawn is null || !targetPawn.IsValid)
                continue;

            Vector? maybeOrigin = targetPawn.AbsOrigin;
            if (!maybeOrigin.HasValue || !AimMath.IsFinite(maybeOrigin.Value))
                continue;

            Vector origin = maybeOrigin.Value;

            float dx = origin.X - eye.X;
            float dy = origin.Y - eye.Y;
            float dz = origin.Z - eye.Z;

            float t = dx * forward.X + dy * forward.Y + dz * forward.Z;
            if (t < 30.0f || t >= maxDistance)
                continue;

            float px = eye.X + forward.X * t;
            float py = eye.Y + forward.Y * t;
            float pz = eye.Z + forward.Z * t;

            if (pz < origin.Z - 4.0f || pz > origin.Z + 76.0f)
                continue;

            float latX = px - origin.X;
            float latY = py - origin.Y;
            float latDistSq = latX * latX + latY * latY;

            const float playerRadius = 18.0f;
            if (latDistSq <= playerRadius * playerRadius && t < bestDist)
            {
                bestDist = t;
                bestTarget = target.PlayerID;
                bestPoint = new Vector(px, py, pz);
            }
        }

        return (bestTarget, bestPoint);
    }

    private static bool SegmentTouchesSmoke(in Vector start, in Vector end, in Vector center)
    {
        // Simple bounding radius check around smoke grenade detonation
        const float smokeRadius = 160.0f;
        Vector delta = new(center.X - start.X, center.Y - start.Y, center.Z - start.Z);
        Vector dir = new(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
        float len = dir.Length();
        if (len < AimMath.Epsilon)
            return false;

        Vector norm = new(dir.X / len, dir.Y / len, dir.Z / len);
        float proj = AimMath.Dot(delta, norm);
        if (proj < 0.0f || proj > len)
            return false;

        Vector closest = new(start.X + norm.X * proj, start.Y + norm.Y * proj, start.Z + norm.Z * proj);
        return closest.Distance(center) <= smokeRadius;
    }
}
