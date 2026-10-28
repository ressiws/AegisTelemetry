/**
 * =============================================================================
 * AegisTelemetry - Module: Aimlock Telemetry
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Records tracking durations, adherence ratios, and screen displacements
 *   of real human crosshairs on moving enemies across visual lag hypotheses.
 *   Strictly ignores bots and dead/ineligible players.
 * =============================================================================
 */

using System;
using System.Collections.Generic;
using AegisTelemetry.Configuration;
using AegisTelemetry.Database;
using AegisTelemetry.Models;
using AegisTelemetry.Statistics;
using AegisTelemetry.Utils;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;

namespace AegisTelemetry.Modules;

internal struct TelemetryAimlockSample
{
    public int ServerTick;
    public QAngle Angles;
    public Vector EyePosition;
    public bool Valid;
}

internal sealed class TelemetryAimlockTrack
{
    public int TargetIndex = -1;
    public int StartServerTick;
    public int LastServerTick;
    public int Samples;
    public int OnTargetSamples;
    public QAngle StartBearing;
    public float MaxDisplacement;
    public float TotalDistance;

    public void Clear()
    {
        TargetIndex = -1;
        StartServerTick = 0;
        LastServerTick = 0;
        Samples = 0;
        OnTargetSamples = 0;
        StartBearing = default;
        MaxDisplacement = 0.0f;
        TotalDistance = 0.0f;
    }
}

internal sealed class TelemetryAimlockPlayerData
{
    public TelemetryAimlockSample Pending;
    public readonly TelemetryAimlockTrack Track = new();
    public int LastProcessedTick = -1;
    public readonly List<QAngle> CommandAngles = new();

    public void Reset()
    {
        Pending = default;
        Track.Clear();
        LastProcessedTick = -1;
        CommandAngles.Clear();
    }
}

public sealed class AimlockTelemetry
{
    private const int MaxPlayers = 64;
    private const float PlayerHalfWidth = 16.0f;
    private const int MinEpisodeTicks = 8; // Ignore noise under 8 ticks (~125ms)

    private readonly TelemetryConfig _config;
    private readonly BoundedChannelWriter _queue;
    private readonly TelemetryStatsCalculator _stats;
    private readonly Func<int, IPlayer?> _getPlayer;
    private readonly Action<string> _debug;

    private readonly TelemetryAimlockPlayerData[] _players = new TelemetryAimlockPlayerData[MaxPlayers + 1];

    public AimlockTelemetry(TelemetryConfig config, BoundedChannelWriter queue, TelemetryStatsCalculator stats, Func<int, IPlayer?> getPlayer, Action<string> debug)
    {
        _config = config;
        _queue = queue;
        _stats = stats;
        _getPlayer = getPlayer;
        _debug = debug;

        for (int i = 0; i < _players.Length; i++)
            _players[i] = new TelemetryAimlockPlayerData();
    }

    public void Reset()
    {
        for (int i = 0; i < _players.Length; i++)
            _players[i].Reset();
    }

    public void OnClientDisconnect(int id)
    {
        if (id >= 0 && id < _players.Length)
        {
            EndEpisode(id, completedNormally: false);
            _players[id].Reset();
        }
    }

    public void OnPlayerDeath(int victimId)
    {
        if (victimId >= 0 && victimId < _players.Length)
        {
            // If the victim was tracking someone, end it
            EndEpisode(victimId, completedNormally: false);
            _players[victimId].Reset();

            // If anyone was tracking the victim, end their track
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (_players[i].Track.TargetIndex == victimId)
                    EndEpisode(i, completedNormally: false);
            }
        }
    }

    public void OnPlayerSpawn(int playerId)
    {
        if (playerId >= 0 && playerId < _players.Length)
            _players[playerId].Reset();
    }

    public void OnProcessUsercmds(IPlayer player, List<IUserCmd> cmds)
    {
        if (!_config.EnableAimlockTelemetry || !PlayerFilter.IsEligibleCombatant(player) || cmds.Count == 0)
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        foreach (var cmd in cmds)
        {
            var pb = cmd.CSGOUserCmd;
            if (!pb.Accessor.HasField("base"))
                continue;

            var b = pb.Base;
            if (!b.Accessor.HasField("viewangles"))
                continue;

            QAngle angles = b.Viewangles;
            if (AimMath.IsFinite(angles))
            {
                st.CommandAngles.Add(angles);
                if (st.CommandAngles.Count > 64)
                    st.CommandAngles.RemoveAt(0);
            }
        }
    }

    public void OnSetupMove(IPlayer player, int commandNumber, int currentTick)
    {
        if (!_config.EnableAimlockTelemetry || !PlayerFilter.IsEligibleCombatant(player))
            return;

        int id = player.PlayerID;
        if (id < 0 || id >= _players.Length)
            return;

        var st = _players[id];
        if (currentTick == st.LastProcessedTick)
            return;

        var pawn = player.PlayerPawn;
        if (pawn is null)
            return;

        Vector origin = pawn.AbsOrigin ?? default;
        Vector eye = pawn.EyePosition ?? new Vector(origin.X, origin.Y, origin.Z + 64.0f);
        QAngle angles = st.CommandAngles.Count > 0 ? st.CommandAngles[^1] : pawn.EyeAngles;

        if (!AimMath.IsFinite(angles) || !AimMath.IsFinite(eye))
            return;

        st.Pending = new TelemetryAimlockSample
        {
            ServerTick = currentTick,
            Angles = angles,
            EyePosition = eye,
            Valid = true
        };
    }

    public void OnGameFrame(int currentTick)
    {
        if (!_config.EnableAimlockTelemetry)
            return;

        for (int i = 0; i < MaxPlayers; i++)
        {
            var p = _getPlayer(i);
            if (!PlayerFilter.IsEligibleCombatant(p))
            {
                EndEpisode(i, completedNormally: false);
                _players[i].Reset();
                continue;
            }

            var st = _players[i];
            if (!st.Pending.Valid)
                continue;

            var sample = st.Pending;
            st.Pending = default;

            if (sample.ServerTick > currentTick || currentTick - sample.ServerTick > 2 || sample.ServerTick <= st.LastProcessedTick)
                continue;

            st.LastProcessedTick = sample.ServerTick;
            Evaluate(p!, st, sample);
        }
    }

    private void Evaluate(IPlayer player, TelemetryAimlockPlayerData data, TelemetryAimlockSample sample)
    {
        int playerId = player.PlayerID;
        Vector aimForward = AimMath.Forward(sample.Angles);

        // If currently tracking a target, verify continuity
        if (data.Track.TargetIndex >= 0)
        {
            int targetId = data.Track.TargetIndex;
            var target = _getPlayer(targetId);

            if (!PlayerFilter.IsEligibleCombatant(target) || !PlayerFilter.AreEnemies(player, target))
            {
                EndEpisode(playerId, completedNormally: false);
                return;
            }

            var targetPawn = target!.PlayerPawn;
            Vector targetOrigin = targetPawn?.AbsOrigin ?? default;
            float dist = sample.EyePosition.Distance(targetOrigin);

            if (!AimMath.IsFinite(targetOrigin) || dist < 1.0f)
            {
                EndEpisode(playerId, completedNormally: false);
                return;
            }

            // Check if still on target (feet, chest, or head)
            bool onTarget = false;
            for (int bp = 0; bp < AimMath.BodyPointCount; bp++)
            {
                Vector bpPos = new(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + AimMath.BodyHeight(bp));
                float err = AimMath.AimError(sample.EyePosition, sample.Angles, bpPos);
                float maxErr = MathF.Atan2(PlayerHalfWidth, dist) * (180.0f / MathF.PI);
                if (err <= maxErr)
                {
                    onTarget = true;
                    break;
                }
            }

            int deltaTicks = sample.ServerTick - data.Track.LastServerTick;
            if (deltaTicks < 1 || deltaTicks > 3)
            {
                EndEpisode(playerId, completedNormally: false);
                return;
            }

            data.Track.Samples += deltaTicks;
            if (onTarget)
                data.Track.OnTargetSamples += deltaTicks;

            data.Track.TotalDistance += dist * deltaTicks;
            data.Track.LastServerTick = sample.ServerTick;

            QAngle currentBearing = AimMath.Bearing(sample.EyePosition, targetOrigin);
            float disp = AimMath.AngularDistance(data.Track.StartBearing, currentBearing);
            if (float.IsFinite(disp) && disp > data.Track.MaxDisplacement)
                data.Track.MaxDisplacement = disp;

            // If crosshair completely left target for more than 4 consecutive ticks, finalize episode
            if (!onTarget && (data.Track.Samples - data.Track.OnTargetSamples >= 6))
            {
                EndEpisode(playerId, completedNormally: true);
            }

            return;
        }

        // Search candidate to start tracking
        for (int i = 0; i < MaxPlayers; i++)
        {
            if (i == playerId)
                continue;

            var target = _getPlayer(i);
            if (!PlayerFilter.IsEligibleCombatant(target) || !PlayerFilter.AreEnemies(player, target))
                continue;

            var targetPawn = target!.PlayerPawn;
            Vector targetOrigin = targetPawn?.AbsOrigin ?? default;
            float dist = sample.EyePosition.Distance(targetOrigin);
            if (!AimMath.IsFinite(targetOrigin) || dist < 1.0f)
                continue;

            float maxErr = MathF.Atan2(PlayerHalfWidth, dist) * (180.0f / MathF.PI);
            bool candidateFound = false;

            for (int bp = 0; bp < AimMath.BodyPointCount; bp++)
            {
                Vector bpPos = new(targetOrigin.X, targetOrigin.Y, targetOrigin.Z + AimMath.BodyHeight(bp));
                float err = AimMath.AimError(sample.EyePosition, sample.Angles, bpPos);
                if (err <= maxErr)
                {
                    candidateFound = true;
                    break;
                }
            }

            if (candidateFound)
            {
                data.Track.TargetIndex = i;
                data.Track.StartServerTick = sample.ServerTick;
                data.Track.LastServerTick = sample.ServerTick;
                data.Track.Samples = 1;
                data.Track.OnTargetSamples = 1;
                data.Track.StartBearing = AimMath.Bearing(sample.EyePosition, targetOrigin);
                data.Track.MaxDisplacement = 0.0f;
                data.Track.TotalDistance = dist;
                break;
            }
        }
    }

    private void EndEpisode(int playerId, bool completedNormally)
    {
        if (playerId < 0 || playerId >= _players.Length)
            return;

        var st = _players[playerId];
        var trk = st.Track;

        if (trk.TargetIndex < 0 || trk.Samples < MinEpisodeTicks)
        {
            trk.Clear();
            return;
        }

        var player = _getPlayer(playerId);
        if (player == null)
        {
            trk.Clear();
            return;
        }

        float durationSeconds = trk.Samples / 64.0f;
        float onTargetRatio = trk.Samples > 0 ? (float)trk.OnTargetSamples / trk.Samples : 0.0f;
        float avgDistance = trk.Samples > 0 ? trk.TotalDistance / trk.Samples : 0.0f;
        string playerHash = Pseudonymizer.HashSteamId(player.SteamID, _config.PseudonymizationSalt);

        var record = new AimlockEpisodeRecord
        {
            TimestampUtc = DateTime.UtcNow,
            StartTick = trk.StartServerTick,
            EndTick = trk.LastServerTick,
            PlayerHash = playerHash,
            DurationTicks = trk.Samples,
            DurationSeconds = durationSeconds,
            TargetDisplacement = trk.MaxDisplacement,
            OnTargetRatio = onTargetRatio,
            AvgDistance = avgDistance,
            CompletedNormally = completedNormally
        };

        _stats.RecordTrackingDuration(record.DurationSeconds);
        _queue.Enqueue(record);
        trk.Clear();
    }
}
