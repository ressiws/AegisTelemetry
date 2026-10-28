/**
 * =============================================================================
 * AegisTelemetry - Geometric and Vector Utilities
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Vector operations, forward projection, and angular distance calculations.
 * =============================================================================
 */

using System;
using SwiftlyS2.Shared.Natives;

namespace AegisTelemetry.Utils;

public static class AimMath
{
    private const float Deg2Rad = MathF.PI / 180.0f;
    private const float Rad2Deg = 180.0f / MathF.PI;
    public const float Epsilon = 1e-6f;

    public static bool IsFinite(in QAngle a) => float.IsFinite(a.Pitch) && float.IsFinite(a.Yaw) && float.IsFinite(a.Roll);
    public static bool IsFinite(in Vector v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public static Vector Forward(in QAngle a)
    {
        float pitch = a.Pitch * Deg2Rad;
        float yaw = a.Yaw * Deg2Rad;
        float cp = MathF.Cos(pitch);
        return new Vector(cp * MathF.Cos(yaw), cp * MathF.Sin(yaw), -MathF.Sin(pitch));
    }

    public static float Dot(in Vector a, in Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static float AngularDistance(in QAngle a, in QAngle b)
    {
        if (!IsFinite(a) || !IsFinite(b))
            return float.NaN;

        float dot = Math.Clamp(Dot(Forward(a), Forward(b)), -1.0f, 1.0f);
        return MathF.Acos(dot) * Rad2Deg;
    }

    public static float AimError(in Vector eye, in QAngle angles, in Vector target)
    {
        Vector dir = new(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z);
        float lenSq = dir.LengthSquared();
        if (!IsFinite(dir) || lenSq < Epsilon)
            return 0.0f;

        float len = MathF.Sqrt(lenSq);
        Vector norm = new(dir.X / len, dir.Y / len, dir.Z / len);
        float dot = Math.Clamp(Dot(Forward(angles), norm), -1.0f, 1.0f);
        return MathF.Acos(dot) * Rad2Deg;
    }

    private static readonly float[] BodyHeights = [8.0f, 46.0f, 64.0f]; // Feet, Chest, Head
    public static int BodyPointCount => BodyHeights.Length;
    public static float BodyHeight(int i) => BodyHeights[i];

    public static float NearestBodyAimError(in Vector eye, in QAngle angles, in Vector feet)
    {
        float best = 180.0f;
        for (int i = 0; i < BodyHeights.Length; i++)
        {
            Vector target = new(feet.X, feet.Y, feet.Z + BodyHeights[i]);
            float err = AimError(eye, angles, target);
            if (err < best)
                best = err;
        }
        return best;
    }

    public static QAngle Bearing(in Vector eye, in Vector target)
    {
        Vector delta = new(target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z);
        float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        return new QAngle(
            -MathF.Atan2(delta.Z, horizontal) * Rad2Deg,
            MathF.Atan2(delta.Y, delta.X) * Rad2Deg,
            0.0f);
    }
}
