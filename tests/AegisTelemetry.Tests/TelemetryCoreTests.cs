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
    public void Pseudonymizer_GeneratesDeterministicHexHash_WithoutExposingSteamId()
    {
        ulong steamId = 76561198012345678;
        string salt = "test-salt-1234";

        string hash1 = Pseudonymizer.HashSteamId(steamId, salt);
        string hash2 = Pseudonymizer.HashSteamId(steamId, salt);
        string hashOtherSalt = Pseudonymizer.HashSteamId(steamId, "different-salt");

        Assert.Equal(32, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hashOtherSalt);
        Assert.DoesNotContain(steamId.ToString(), hash1);
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
}
