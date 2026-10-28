using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AegisTelemetry.Database;
using AegisTelemetry.Models;
using AegisTelemetry.Statistics;
using AegisTelemetry.Utils;
using Xunit;

namespace AegisTelemetry.Tests;

public class TelemetryCoreTests
{
    [Fact]
    public void Pseudonymizer_ReturnsSteamIdDirectly_ForConsistentIdentification()
    {
        ulong steamId = 76561198012345678;
        string salt = "test-salt-1234";

        string id1 = Pseudonymizer.HashSteamId(steamId, salt);
        string id2 = Pseudonymizer.HashSteamId(steamId, "different-salt");

        Assert.Equal("76561198012345678", id1);
        Assert.Equal(id1, id2);
        Assert.Equal("anonymous", Pseudonymizer.HashSteamId(0, salt));
    }

    [Fact]
    public void BoundedChannel_EnforcesStrictCapacity_AndTracksDrops()
    {
        int capacity = 50;
        var writer = new BoundedChannelWriter(capacity);

        for (int i = 0; i < 100; i++)
        {
            writer.Enqueue(new HealthAuditRecord
            {
                SessionId = 1,
                TimestampUtc = DateTime.UtcNow,
                MetricsCollected = i,
                MetricsWritten = 0,
                MetricsDropped = 0,
                DatabaseErrors = 0,
                QueuePeakSize = 0
            });
        }

        Assert.Equal(100, writer.MetricsCollected);
        Assert.True(writer.CurrentCount <= capacity);
        Assert.True(writer.QueuePeakSize <= capacity);
    }

    [Fact]
    public void StatisticsCalculator_RejectsInsufficientSamples_AndCalculatesAccuratePercentiles()
    {
        // 1. Under 5 samples must return null (protects against premature calibration)
        float[] few = [120.0f, 150.0f, 180.0f];
        Assert.Null(TelemetryStatsCalculator.ComputeSummary(few));

        // 2. Normal sample distribution
        float[] samples = Enumerable.Range(1, 100).Select(x => (float)x).ToArray(); // 1 to 100
        var summary = TelemetryStatsCalculator.ComputeSummary(samples);

        Assert.NotNull(summary);
        var s = summary.Value;
        Assert.Equal(100, s.SampleCount);
        Assert.Equal(1.0f, s.Min);
        Assert.Equal(100.0f, s.Max);
        Assert.Equal(50.5f, s.Mean, 0.5f);
        Assert.Equal(50.5f, s.P50, 0.5f);
        Assert.InRange(s.P90, 89.0f, 92.0f);
        Assert.InRange(s.P95, 94.0f, 97.0f);
        Assert.InRange(s.P99, 98.0f, 100.0f);
    }

    [Fact]
    public void Codebase_HasZeroPunitiveActions_CompletelyPassive()
    {
        // Scan all source files in AegisTelemetry to verify complete absence of kick/ban/punish calls
        var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        if (!Directory.Exists(srcDir))
            return;

        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
        string[] forbiddenTerms = ["banid", "kickid", "ExecuteKick", "ExecuteBan", "Punish"];

        foreach (var file in csFiles)
        {
            string content = File.ReadAllText(file);
            foreach (var term in forbiddenTerms)
            {
                Assert.False(content.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"Forbidden punitive term '{term}' found in {Path.GetFileName(file)}! Telemetry must be strictly passive.");
            }
        }
    }

    [Theory]
    [InlineData(200.0f, 64.0f, true)]   // Close range aiming directly at Head
    [InlineData(200.0f, 46.0f, true)]   // Close range aiming directly at Chest
    [InlineData(200.0f, 8.0f, true)]    // Close range aiming directly at Feet
    [InlineData(500.0f, 64.0f, true)]   // Medium range aiming at Head
    [InlineData(1000.0f, 64.0f, true)]  // Long range aiming at Head
    public void Aimlock_AngularPreFilter_PassesLegitimateTargetingAcrossBodyPoints(float distance, float targetBodyHeight, bool expectedPass)
    {
        // Observer eye position at (0, 0, 64)
        float eyeX = 0.0f, eyeY = 0.0f, eyeZ = 64.0f;
        // Enemy origin at (distance, 0, 0)
        float enemyOriginX = distance, enemyOriginY = 0.0f, enemyOriginZ = 0.0f;

        // Player aiming directly at targetBodyHeight (e.g. Head: enemyOriginZ + 64.0f)
        float targetZ = enemyOriginZ + targetBodyHeight;
        float aimDx = enemyOriginX - eyeX;
        float aimDy = enemyOriginY - eyeY;
        float aimDz = targetZ - eyeZ;
        float aimLen = MathF.Sqrt(aimDx * aimDx + aimDy * aimDy + aimDz * aimDz);
        float forwardX = aimDx / aimLen;
        float forwardY = aimDy / aimLen;
        float forwardZ = aimDz / aimLen;

        float[] bodyHeights = [8.0f, 46.0f, 64.0f];
        float minDistSq = 200.0f * 200.0f;

        bool withinCone = false;
        for (int bp = 0; bp < bodyHeights.Length; bp++)
        {
            float bx = enemyOriginX - eyeX;
            float by = enemyOriginY - eyeY;
            float bz = (enemyOriginZ + bodyHeights[bp]) - eyeZ;
            float bDistSq = bx * bx + by * by + bz * bz;

            if (bDistSq < minDistSq - 1.0f)
                continue;

            float bDot = bx * forwardX + by * forwardY + bz * forwardZ;
            if (bDot <= 0.0f)
                continue;

            if (bDot * bDot >= 0.82f * bDistSq) // cos^2(25°) ≈ 0.821
            {
                withinCone = true;
                break;
            }
        }

        Assert.Equal(expectedPass, withinCone);
    }

    [Fact]
    public void Aimlock_AngularPreFilter_RejectsOffTargetAngles()
    {
        float eyeX = 0.0f, eyeY = 0.0f, eyeZ = 64.0f;
        float enemyOriginX = 500.0f, enemyOriginY = 0.0f, enemyOriginZ = 0.0f;

        // Player aiming 35 degrees away horizontally
        float rad = 35.0f * (MathF.PI / 180.0f);
        float forwardX = MathF.Cos(rad);
        float forwardY = MathF.Sin(rad);
        float forwardZ = 0.0f;

        float[] bodyHeights = [8.0f, 46.0f, 64.0f];
        float minDistSq = 200.0f * 200.0f;

        bool withinCone = false;
        for (int bp = 0; bp < bodyHeights.Length; bp++)
        {
            float bx = enemyOriginX - eyeX;
            float by = enemyOriginY - eyeY;
            float bz = (enemyOriginZ + bodyHeights[bp]) - eyeZ;
            float bDistSq = bx * bx + by * by + bz * bz;

            if (bDistSq < minDistSq)
                continue;

            float bDot = bx * forwardX + by * forwardY + bz * forwardZ;
            if (bDot <= 0.0f)
                continue;

            if (bDot * bDot >= 0.82f * bDistSq)
            {
                withinCone = true;
                break;
            }
        }

        Assert.False(withinCone, "Pre-filter should have rejected player looking 35 degrees away from target!");
    }
}
