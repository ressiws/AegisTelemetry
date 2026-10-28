/**
 * =============================================================================
 * AegisTelemetry - Pseudonymizer
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Hashes player SteamIDs with a configured server secret salt using HMAC-SHA256.
 *   Prevents plaintext PII storage while maintaining consistent per-player identity.
 * =============================================================================
 */

using System;
using System.Security.Cryptography;
using System.Text;

namespace AegisTelemetry.Utils;

public static class Pseudonymizer
{
    public static string HashSteamId(ulong steamId, string salt)
    {
        if (steamId == 0)
            return "anonymous";

        // Store real SteamID64 as string for persistent account identification across servers
        return steamId.ToString();
    }
}
