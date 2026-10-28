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

        byte[] keyBytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(salt) ? "aegis-default-salt" : salt);
        byte[] dataBytes = BitConverter.GetBytes(steamId);

        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(dataBytes);

        // Return first 16 bytes formatted as 32 hex chars for compact indexing
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }
}
