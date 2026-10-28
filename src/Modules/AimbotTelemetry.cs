/**
 * =============================================================================
 * AegisTelemetry - Module: Aimbot Telemetry
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Passively records real player viewangle dynamics, angular velocities,
 *   snap magnitudes, path efficiencies, and error convergence for shots fired.
 *   Strictly ignores bots and dead/ineligible players.
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

namespace AegisTelemetry.Modules;

internal sealed class TelemetryAimCommand
{
    public int CommandNumber;
    public int ClientTick;
    public int ServerTick = -1;
    public QAngle Angles;
    public bool IsShot;
    public bool Simulated;
    public bool Consumed;
}

internal sealed class TelemetryShotRecord
{
    public required TelemetryAimCommand Command { get; init; }
    public required int FireTick { get; init; }
    public required string Weapon { get; init; }
    public int VictimId { get; set; } = -1;
    public bool HurtSeen { get; set; }
    public short ActualHitGroup { get; set; }
    public short DamageDealt { get; set; }
    public bool Evaluated { get; set; }
}

internal struct TelemetryTrackedPosition
{
    public Vector Origin;
    public Vector Eye;
    public int Team;
    public bool Valid;
    public bool Alive;
}

internal sealed class TelemetryPositionFrame
{
    public int ServerTick = -1;
    public readonly TelemetryTrackedPosition[] Players = new TelemetryTrackedPosition[65];
}

internal sealed class TelemetryAimbotPlayerState
{
    public readonly List<TelemetryAimCommand> Commands = new();
    public readonly List<TelemetryShotRecord> Shots = new();
    public int LastUnhitEvaluatedTick = -1;

    public void Reset()
    {
        Commands.Clear();
        Shots.Clear();
        LastUnhitEvaluatedTick = -1;
    }
}

public sealed class AimbotTelemetry
{
    private const int MaxPlayers = 65;
    private const int CommandHistorySize = 128;
    private const int PositionHistoryLimit = 128;
    private const int SnapWindowTicks = 32; // 0.5s at 64-tick

    private static readonly HashSet<string> BallisticWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "ak47","aug","awp","bizon","cz75a","deagle","elite","famas","fiveseven","g3sg1","galilar","glock",
        "hkp2000","m249","m4a1","m4a1_silencer","mac10","mag7","mp5sd","mp7","mp9","negev","nova","p250",
        "p90","revolver","sawedoff","scar20","sg556","ssg08","taser","tec9","ump45","usp_silencer","xm1014"
    };

    private readonly TelemetryConfig _config;
    private readonly BoundedChannelWriter _queue;
    private readonly TelemetryStatsCalculator _stats;
    private readonly Func<int, IPlayer?> _getPlayer;
    private readonly Action<string> _debug;

    private readonly TelemetryAimbotPlayerState[] _players = new TelemetryAimbotPlayerState[MaxPlayers];
    private readonly List<TelemetryPositionFrame> _frames = new();

    public AimbotTelemetry(TelemetryConfig config, BoundedChannelWriter queue, TelemetryStatsCalculator stats, Func<int, IPlayer?> getPlayer, Action<string> debug)
    {
        _config = config;
        _queue = queue;
        _stats = stats;
        _getPlayer = getPlayer;
        _debug = debug;

        for (int i = 0; i < _players.Length; i++)
            _players[i] = new TelemetryAimbotPlayerState();
    }

    public void Reset()
    {
        for (int i = 0; i < _players.Length; i++)
            _players[i].Reset();
        _frames.Clear();
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

    public void OnProcessUsercmds(IPlayer player, List<IUserCmd> cmds)
    {
        if (!_config.EnableAimbotTelemetry || !PlayerFilter.IsEligibleCombatant(player) || cmds.Count == 0)
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
            bool isShot = false;
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
                        isShot = true;
                    }
                }
            }

            if (!AimMath.IsFinite(angles))
                continue;

            st.Commands.Add(new TelemetryAimCommand
            {
                CommandNumber = number,
                ClientTick = b.ClientTick,
                Angles = angles,
                IsShot = isShot
            });

            if (st.Commands.Count > CommandHistorySize)
                st.Commands.RemoveAt(0);
        }
    }

    public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
    {
        if (!_config.EnableAimbotTelemetry || !PlayerFilter.IsEligibleCombatant(player))
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        var cmd = st.Commands.FindLast(c => c.CommandNumber == commandNumber);
        if (cmd == null)
            return;

        cmd.ServerTick = currentTick;
        cmd.Simulated = true;
    }

    public void OnGameFrame(int currentTick)
    {
        CaptureFrame(currentTick);

        if (!_config.EnableAimbotTelemetry)
            return;

        // Check pending shots that timed out without hurt (misses) to record unhit distribution
        for (int id = 0; id < MaxPlayers; id++)
        {
            var p = _getPlayer(id);
            if (!PlayerFilter.IsEligibleCombatant(p))
            {
                _players[id].Reset();
                continue;
            }

            var st = _players[id];
            for (int s = 0; s < st.Shots.Count; s++)
            {
                var shot = st.Shots[s];
                if (shot.Evaluated)
                    continue;

                // If 3 ticks passed without OnPlayerHurt, evaluate as unhit shot
                if (currentTick - shot.FireTick >= 3)
                {
                    shot.Evaluated = true;

                    // Throttle continuous unhit sprays (e.g. 150-bullet Negev spray)
                    // Sample at most 1 unhit shot per 12 ticks (~200ms) per player to preserve server tick rate.
                    if (st.LastUnhitEvaluatedTick >= 0 && currentTick - st.LastUnhitEvaluatedTick < 12)
                        continue;

                    st.LastUnhitEvaluatedTick = currentTick;
                    EvaluateShot(p!, st, shot, hitConfirmed: false);
                }
            }

            st.Shots.RemoveAll(s => currentTick - s.FireTick > 4);
        }
    }

    private void CaptureFrame(int tick)
    {
        var frame = new TelemetryPositionFrame { ServerTick = tick };

        for (int id = 0; id < MaxPlayers; id++)
        {
            var p = _getPlayer(id);
            if (p == null || !p.IsValid || !p.IsAlive)
                continue;

            var pawn = p.PlayerPawn;
            if (pawn == null || !pawn.IsValid)
                continue;

            Vector? maybeOrigin = pawn.AbsOrigin;
            if (!maybeOrigin.HasValue || !AimMath.IsFinite(maybeOrigin.Value))
                continue;

            Vector origin = maybeOrigin.Value;
            var eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);

            frame.Players[id] = new TelemetryTrackedPosition
            {
                Origin = origin,
                Eye = eye,
                Team = pawn.TeamNum,
                Valid = true,
                Alive = pawn.LifeState == 0
            };
        }

        if (_frames.Count > 0 && _frames[^1].ServerTick == tick)
            _frames[^1] = frame;
        else
        {
            _frames.Add(frame);
            if (_frames.Count > PositionHistoryLimit)
                _frames.RemoveAt(0);
        }
    }

    public void OnWeaponFire(IPlayer player, string weapon, int currentTick)
    {
        if (!_config.EnableAimbotTelemetry || !PlayerFilter.IsEligibleCombatant(player))
            return;

        string normWeapon = weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weapon[7..] : weapon;
        if (!BallisticWeapons.Contains(normWeapon))
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        var candidates = st.Commands
            .Where(c => c.IsShot && c.Simulated && !c.Consumed && (long)currentTick - c.ServerTick is >= -1 and <= 1)
            .ToArray();

        if (candidates.Length != 1)
        {
            foreach (var c in candidates)
                c.Consumed = true;
            return;
        }

        var command = candidates[0];
        command.Consumed = true;

        st.Shots.Add(new TelemetryShotRecord
        {
            Command = command,
            FireTick = currentTick,
            Weapon = normWeapon
        });

        if (st.Shots.Count > 16)
            st.Shots.RemoveAt(0);
    }

    public void OnPlayerHurt(IPlayer attacker, IPlayer victim, string weapon, int damage, short hitgroup, int currentTick)
    {
        if (!_config.EnableAimbotTelemetry || !PlayerFilter.IsEligibleCombatant(attacker) || !victim.IsValid || attacker.PlayerID == victim.PlayerID)
            return;

        int attackerId = attacker.PlayerID;
        if (attackerId < 0 || attackerId >= _players.Length)
            return;

        var st = _players[attackerId];
        var match = st.Shots.FindLast(s => !s.HurtSeen && !s.Evaluated && (long)currentTick - s.FireTick is >= 0 and <= 2);
        if (match == null)
            return;

        match.HurtSeen = true;
        match.VictimId = victim.PlayerID;
        match.DamageDealt = (short)Math.Clamp(damage, 0, 500);
        match.ActualHitGroup = hitgroup;
        match.Evaluated = true;

        EvaluateShot(attacker, st, match, hitConfirmed: true);
    }

    private void EvaluateShot(IPlayer attacker, TelemetryAimbotPlayerState st, TelemetryShotRecord shot, bool hitConfirmed)
    {
        try
        {
            var cmd = shot.Command;
            if (!cmd.Simulated)
                return;

            var frame = _frames.Find(f => f.ServerTick == cmd.ServerTick);
            if (frame == null)
                return;

            int attackerId = attacker.PlayerID;
            if (attackerId < 0 || attackerId >= frame.Players.Length)
                return;

            var attackerPos = frame.Players[attackerId];
            if (!attackerPos.Valid)
                return;

            // Determine target position: if hit, use victim; otherwise, find closest enemy in view
            int targetId = shot.VictimId;
            TelemetryTrackedPosition targetPos = default;
            bool haveTarget = false;

            if (targetId >= 0 && targetId < frame.Players.Length && frame.Players[targetId].Valid)
            {
                targetPos = frame.Players[targetId];
                haveTarget = true;
            }
            else
            {
                float bestAngle = 90.0f;
                for (int i = 0; i < frame.Players.Length; i++)
                {
                    if (i == attackerId || !frame.Players[i].Valid || !frame.Players[i].Alive)
                        continue;

                    if (frame.Players[i].Team == attackerPos.Team)
                        continue;

                    float err = AimMath.AimError(attackerPos.Eye, cmd.Angles, frame.Players[i].Origin);
                    if (err < bestAngle)
                    {
                        bestAngle = err;
                        targetPos = frame.Players[i];
                        targetId = i;
                        haveTarget = true;
                    }
                }
            }

            if (!haveTarget || targetId < 0 || targetId >= frame.Players.Length)
                return;

            float distance = targetPos.Origin.Distance(attackerPos.Eye);
            if (distance < 1.0f || !float.IsFinite(distance))
                return;

            // Walk backwards through commands in the 0.5s window
            float largestSnap = 0.0f;
            float beforeError = 0.0f;
            float afterError = 0.0f;
            float totalPath = 0.0f;
            var stepDistances = new List<float>();

            var newer = cmd;
            int stepCount = 0;
            while (++stepCount <= SnapWindowTicks)
            {
                var older = st.Commands.FindLast(c => c.CommandNumber == newer.CommandNumber - 1 && c.Simulated);
                if (older == null)
                    break;

                if (cmd.ServerTick - older.ServerTick > SnapWindowTicks)
                    break;

                var olderFrame = _frames.Find(f => f.ServerTick == older.ServerTick);
                if (olderFrame == null ||
                    targetId >= olderFrame.Players.Length || attackerId >= olderFrame.Players.Length ||
                    !olderFrame.Players[targetId].Valid || !olderFrame.Players[attackerId].Valid)
                    break;

                float stepSnap = AimMath.AngularDistance(older.Angles, newer.Angles);
                if (!float.IsFinite(stepSnap))
                    break;

                totalPath += stepSnap;
                stepDistances.Add(stepSnap);

                if (stepSnap > largestSnap)
                {
                    largestSnap = stepSnap;
                    beforeError = AimMath.NearestBodyAimError(olderFrame.Players[attackerId].Eye, older.Angles, olderFrame.Players[targetId].Origin);
                    afterError = AimMath.NearestBodyAimError(frame.Players[attackerId].Eye, newer.Angles, frame.Players[targetId].Origin);
                }

                newer = older;
            }

            float angularVelocity = largestSnap * 64.0f; // degrees/sec
            float directMovement = stepDistances.Count > 0 ? AimMath.AngularDistance(newer.Angles, cmd.Angles) : largestSnap;
            float pathEfficiency = totalPath > AimMath.Epsilon ? Math.Clamp(directMovement / totalPath, 0.0f, 1.0f) : 1.0f;
            float errorReduction = beforeError > AimMath.Epsilon ? Math.Clamp(1.0f - (afterError / beforeError), 0.0f, 1.0f) : 0.0f;

            // Count monotonic decelerating steps
            short monotonicSteps = 0;
            float prev = float.PositiveInfinity;
            for (int i = 0; i < stepDistances.Count; i++)
            {
                if (stepDistances[i] < prev)
                {
                    monotonicSteps++;
                    prev = stepDistances[i];
                }
                else break;
            }

            string playerHash = Pseudonymizer.HashSteamId(attacker.SteamID, _config.PseudonymizationSalt);

            var record = new AimbotShotRecord
            {
                TimestampUtc = DateTime.UtcNow,
                ServerTick = cmd.ServerTick,
                CommandNumber = cmd.CommandNumber,
                PlayerHash = playerHash,
                Weapon = shot.Weapon,
                TargetDistance = distance,
                SnapAngle = largestSnap,
                AngularVelocity = angularVelocity,
                ErrorReduction = errorReduction,
                PathEfficiency = pathEfficiency,
                MonotonicSteps = monotonicSteps,
                HitConfirmed = hitConfirmed,
                ActualHitGroup = shot.ActualHitGroup,
                DamageDealt = shot.DamageDealt
            };

            _stats.RecordSnapAngle(record.SnapAngle);
            _stats.RecordPathEfficiency(record.PathEfficiency);
            _queue.Enqueue(record);
        }
        catch (Exception ex)
        {
            _debug($"[AimbotTelemetry] EvaluateShot error: {ex.Message}");
        }
    }
}
