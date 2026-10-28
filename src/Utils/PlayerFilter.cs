/**
 * =============================================================================
 * AegisTelemetry - Player Eligibility Filter
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Centralized player validation ensuring telemetry strictly includes
 *   real human players who are alive and in an active playing team (T or CT).
 * =============================================================================
 */

using SwiftlyS2.Shared.Players;

namespace AegisTelemetry.Utils;

public static class PlayerFilter
{
    /// <summary>
    /// Checks if the player is a valid connected human client (excluding bots, GOTV, and null entities).
    /// </summary>
    public static bool IsEligibleHuman(IPlayer? player)
    {
        return player is { IsValid: true, IsFakeClient: false } &&
               player.PlayerPawn is { IsValid: true };
    }

    /// <summary>
    /// Checks if the player is an eligible human combatant (alive, non-bot, active team, valid pawn).
    /// Strictly used for the player whose aim/actions are being measured.
    /// </summary>
    public static bool IsEligibleCombatant(IPlayer? player)
    {
        if (!IsEligibleHuman(player))
            return false;

        if (!player!.IsAlive)
            return false;

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
            return false;

        if (pawn.LifeState != 0)
            return false;

        int team = pawn.TeamNum;
        return team == 2 || team == 3; // 2: T, 3: CT
    }

    /// <summary>
    /// Checks if a player/bot is a valid combat target (alive, valid pawn, active playing team).
    /// Target can be human or bot, but cannot be dead or spectator.
    /// </summary>
    public static bool IsValidTarget(IPlayer? player)
    {
        if (player is null || !player.IsValid)
            return false;

        if (!player.IsAlive)
            return false;

        var pawn = player.PlayerPawn;
        if (pawn is null || !pawn.IsValid)
            return false;

        if (pawn.LifeState != 0)
            return false;

        int team = pawn.TeamNum;
        return team == 2 || team == 3; // 2: T, 3: CT
    }

    /// <summary>
    /// Checks if attacker is an eligible human combatant and victim is an alive enemy target (human or bot).
    /// </summary>
    public static bool AreEnemies(IPlayer? attacker, IPlayer? victim)
    {
        if (attacker is null || victim is null || attacker.PlayerID == victim.PlayerID)
            return false;

        if (!IsEligibleCombatant(attacker) || !IsValidTarget(victim))
            return false;

        var pA = attacker.PlayerPawn;
        var pB = victim.PlayerPawn;
        if (pA is null || pB is null)
            return false;

        return pA.TeamNum != pB.TeamNum;
    }
}
