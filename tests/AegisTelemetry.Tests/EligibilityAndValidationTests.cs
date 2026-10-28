using System;
using AegisTelemetry.Utils;
using SwiftlyS2.Shared.Natives;
using Xunit;

namespace AegisTelemetry.Tests;

public class EligibilityAndValidationTests
{
    [Fact]
    public void AimMath_HandlesNonFiniteVectorsAndAnglesGracefully()
    {
        var nanAngle = new QAngle(float.NaN, 0, 0);
        var infAngle = new QAngle(0, float.PositiveInfinity, 0);
        var validAngle = new QAngle(0, 90.0f, 0);

        Assert.False(AimMath.IsFinite(nanAngle));
        Assert.False(AimMath.IsFinite(infAngle));
        Assert.True(AimMath.IsFinite(validAngle));

        var nanVec = new Vector(float.NaN, 0, 0);
        var validVec = new Vector(100, 200, 300);

        Assert.False(AimMath.IsFinite(nanVec));
        Assert.True(AimMath.IsFinite(validVec));

        // Angular distance with NaN must return NaN, not throw exception
        float dist = AimMath.AngularDistance(nanAngle, validAngle);
        Assert.True(float.IsNaN(dist));
    }

    [Fact]
    public void AimMath_ForwardVector_HasUnitLength()
    {
        var angles = new[]
        {
            new QAngle(0, 0, 0),
            new QAngle(45, 90, 0),
            new QAngle(-30, 180, 0),
            new QAngle(89, -45, 0)
        };

        foreach (var a in angles)
        {
            var fwd = AimMath.Forward(a);
            float len = fwd.Length();
            Assert.Equal(1.0f, len, 0.001f);
        }
    }

    [Fact]
    public void AimMath_AngularDistance_ComputesCorrectDegrees()
    {
        var a1 = new QAngle(0, 0, 0);
        var a2 = new QAngle(0, 90, 0);
        var a3 = new QAngle(0, 180, 0);

        Assert.Equal(90.0f, AimMath.AngularDistance(a1, a2), 0.01f);
        Assert.Equal(180.0f, AimMath.AngularDistance(a1, a3), 0.01f);
        Assert.Equal(0.0f, AimMath.AngularDistance(a1, a1), 0.01f);
    }

    [Fact]
    public void AimMath_AimError_ReturnsZero_WhenAimDirectlyAtTarget()
    {
        Vector eye = new(0, 0, 64);
        Vector target = new(500, 0, 64); // Directly ahead on X axis
        QAngle aim = new(0, 0, 0);       // Pitch 0, Yaw 0

        float error = AimMath.AimError(eye, aim, target);
        Assert.Equal(0.0f, error, 0.01f);

        QAngle aimOff90 = new(0, 90, 0); // Aiming 90 degrees right
        float error90 = AimMath.AimError(eye, aimOff90, target);
        Assert.Equal(90.0f, error90, 0.01f);
    }
}
