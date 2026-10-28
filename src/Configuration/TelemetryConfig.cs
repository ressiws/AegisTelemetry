/**
 * =============================================================================
 * AegisTelemetry - Configuration
 *
 * Copyright (C) 2026 swisser & sevon
 * Authors:
 *   - swisser (https://github.com/ressiws)
 *   - sevon   (https://github.com/victor201202)
 *
 * Description:
 *   Strongly-typed configuration model for AegisTelemetry.
 * =============================================================================
 */

using System.ComponentModel;

namespace AegisTelemetry.Configuration;

public sealed class DatabaseConfig
{
    [Description("MariaDB/MySQL server host address.")]
    public string Host { get; set; } = "127.0.0.1";

    [Description("MariaDB/MySQL server port.")]
    public int Port { get; set; } = 3306;

    [Description("Database schema name.")]
    public string Database { get; set; } = "aegis_telemetry";

    [Description("Database user.")]
    public string User { get; set; } = "aegis_user";

    [Description("Database password.")]
    public string Password { get; set; } = "";

    [Description("Connection timeout in seconds.")]
    public int ConnectionTimeoutSeconds { get; set; } = 5;

    [Description("Maximum connection pool size.")]
    public int MaximumPoolSize { get; set; } = 10;

    public string BuildConnectionString()
    {
        return $"Server={Host};Port={Port};Database={Database};User ID={User};Password={Password};" +
               $"Connection Timeout={ConnectionTimeoutSeconds};MaximumPoolsize={MaximumPoolSize};" +
               $"SslMode=Preferred;DefaultCommandTimeout=10;";
    }
}

public sealed class QueueConfig
{
    [Description("Maximum number of telemetry records held in memory before oldest records are dropped during DB outages.")]
    public int MaxQueueCapacity { get; set; } = 10000;

    [Description("Target batch size for asynchronous bulk database insertions.")]
    public int BatchSize { get; set; } = 100;

    [Description("Maximum seconds to wait before flushing pending records to database.")]
    public float FlushIntervalSeconds { get; set; } = 3.0f;

    [Description("Interval in seconds between system health audit heartbeats to database.")]
    public float HealthAuditIntervalSeconds { get; set; } = 60.0f;
}

public sealed class TelemetryConfig
{
    [Description("Global enable or disable switch for AegisTelemetry.")]
    public bool Enabled { get; set; } = true;

    [Description("Unique identifier for this server instance in reports.")]
    public string ServerId { get; set; } = "cs2-server-1";

    [Description("Secret salt used for HMAC-SHA256 pseudonymization of player SteamIDs.")]
    public string PseudonymizationSalt { get; set; } = "aegis-telemetry-secret-salt";

    [Description("MariaDB database connection settings.")]
    public DatabaseConfig Database { get; set; } = new();

    [Description("Memory buffer and batch processing settings.")]
    public QueueConfig Queue { get; set; } = new();

    [Description("Enable Aimbot metrics telemetry collection.")]
    public bool EnableAimbotTelemetry { get; set; } = true;

    [Description("Enable Triggerbot metrics telemetry collection.")]
    public bool EnableTriggerbotTelemetry { get; set; } = true;

    [Description("Enable Aimlock metrics telemetry collection.")]
    public bool EnableAimlockTelemetry { get; set; } = true;

    [Description("Treat teammates as enemies (useful for deathmatch or FFA servers).")]
    public bool TeammatesAreEnemies { get; set; } = false;

    [Description("Use engine native trace for triggerbot crosshair raycasting. When false, uses 100% managed geometric cylinder raycasting to avoid native engine crashes.")]
    public bool EnableNativeTrace { get; set; } = false;
}
