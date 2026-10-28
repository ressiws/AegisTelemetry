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
    /// Checks if two players are valid human opponents.
    /// </summary>
    public static bool AreEnemies(IPlayer? attacker, IPlayer? victim)
    {
        if (attacker is null || victim is null || attacker.PlayerID == victim.PlayerID)
            return false;

        if (!IsEligibleCombatant(attacker) || !IsEligibleCombatant(victim))
            return false;

        var pA = attacker.PlayerPawn;
        var pB = victim.PlayerPawn;
        if (pA is null || pB is null)
            return false;

        return pA.TeamNum != pB.TeamNum;
    }
}
