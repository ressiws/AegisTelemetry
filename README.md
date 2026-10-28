# AegisTelemetry — Passive Telemetry Collector for AegisAC

<p align="center">
  <strong>Server-side passive telemetry and behavioral kinematics collector for CS2 (SwiftlyS2 / .NET 10).</strong><br>
  Harvests ground-truth human aiming distributions to accurately calibrate AegisAC thresholds and eliminate false positives.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/CS2-SwiftlyS2%201.5.0+-orange.svg" alt="SwiftlyS2" />
  <img src="https://img.shields.io/badge/.NET-10.0-purple.svg" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Storage-MariaDB%20%2F%20MySQL-blue.svg" alt="MariaDB" />
  <img src="https://img.shields.io/badge/Behavior-Strictly%20Passive-green.svg" alt="Passive" />
  <img src="https://img.shields.io/badge/License-Apache%202.0-yellow.svg" alt="License" />
</p>

---

## 1. Overview

**AegisTelemetry** is an independent, strictly **passive** server plugin engineered for Counter-Strike 2. Its sole objective is to collect real human player behavioral kinematics and aiming telemetry in production game servers to:
* Compute real-world statistical distributions (means, standard deviations, and percentiles $p50$, $p90$, $p95$, $p99$, $p99.9$);
* Mathematically calibrate detection thresholds for **AegisAC** (**Aimbot**, **Triggerbot**, and **Aimlock**);
* Establish verifiable physical limits for skilled human players during peeks, flickshots, and tracking duels to **eliminate false positives**.

> [!IMPORTANT]
> **STRICTLY PASSIVE & NON-PUNITIVE**: This plugin **never** executes bans, kicks, or punitive actions against players. It does not alter gameplay, modify packets, or interfere with game logic.

---

## 2. Collection & Eligibility Principles

To prevent statistical contamination from invalid or artificial data:
1. **Absolute Bot Exclusion**: Valve native BOTs, training bots, and GOTV clients (`IsFakeClient == true`) are filtered out across all ticks and game events.
2. **Exclusion of Dead Players & Spectators**: Only living human combatants (`IsAlive && LifeState == 0`) in an active team (Terrorist or Counter-Terrorist) generate telemetry samples.
3. **Per-Life Isolation**: Upon death (`EventPlayerDeath`), active tracking is immediately terminated. Upon respawn (`EventPlayerSpawn`), player tracking state is reset to prevent correlating actions across distinct lives or rounds.
4. **Privacy & GDPR Compliance (Pseudonymization)**: Player SteamIDs are never stored in plaintext. They are cryptographically hashed using a configurable server secret salt via `HMAC-SHA256(SteamID, Salt)` into a stable 32-character hexadecimal token.

---

## 3. Collected Metrics

### 3.1 Aimbot Telemetry (`telemetry_aimbot_shots`)
* `snap_angle`: Peak instantaneous angular deviation between consecutive commands in the 0.5s pre-shot window (°).
* `angular_velocity`: Maximum instantaneous angular rotation speed (°/second).
* `error_reduction`: Hitbox convergence rate between initial movement acceleration and the shot (`0.0` to `1.0`).
* `path_efficiency`: Ratio between direct linear angular displacement and total trajectory arc length (`0.0` to `1.0`).
* `monotonic_steps`: Number of consecutive steps exhibiting smoothly decelerating deceleration toward the target.
* `target_distance`: Euclidean distance to the opponent (CS2 units).
* `hit_confirmed`: Records both damaging hits (`true`) and misses (`false`), allowing statistical comparison of hit vs. unhit kinematics.

### 3.2 Triggerbot Telemetry (`telemetry_triggerbot_reactions`)
* `reaction_ticks` & `reaction_ms`: Time delta between estimated crosshair-hitbox contact and attack button press.
* `attacker_speed`: Horizontal velocity magnitude of the attacker on the fire tick ($\text{units/s}$) — **essential to distinguish aggressive strafe pre-fires from stationary holding**.
* `contact_mode`: `TargetDriven` (crosshair held static on a pixel waiting for an enemy) vs. `AimDriven` (crosshair actively moving onto an enemy).
* `prior_clicks`: Count of attack clicks in the preceding 5 ticks — **used to filter blind jitter-clicking / spamming on pistols**.
* `smoke_occluded`: Flag indicating whether the line of sight crossed an active smoke volume.

### 3.3 Aimlock Telemetry (`telemetry_aimlock_episodes`)
* `duration_ticks` & `duration_seconds`: Continuous tracking duration held inside the opponent's bounding box.
* `target_displacement`: Total angular traverse executed by the target across the screen (°).
* `on_target_ratio`: Percentage of samples held within the opponent's hitbox.
* `avg_distance`: Average engagement distance across the episode.
* `completed_normally`: Whether the engagement concluded naturally (crosshair left target) or was interrupted by death/disconnection.

---

## 4. Bounded Buffer Architecture & MariaDB Storage

```
Game Thread (CS2 Server)                     Background Worker Thread               MariaDB Server
┌───────────────────────┐                    ┌─────────────────────────┐           ┌──────────────┐
│  ProcessUsercmds      │                    │                         │           │              │
│  SetupMove            │ ── TryWrite() ───> │  BoundedChannelWriter   │ ── Bulk ─>│   Database   │
│  EventWeaponFire      │   (Zero I/O Block) │  (Max 10,000 in RAM)    │  Async    │              │
│  EventPlayerHurt      │                    │  DropOldest on outage   │           │ (phpMyAdmin) │
└───────────────────────┘                    └─────────────────────────┘           └──────────────┘
```

1. **Zero Game Lag**: All database communication occurs asynchronously on a background thread decoupled from the server Game Thread.
2. **Bounded Memory Queue**: In-memory channel is strictly capped (default: 10,000 records). If MariaDB becomes unreachable, the buffer drops the oldest records (`DropOldest`) without unbounded heap growth or GC pauses, while incrementing `MetricsDropped`.
3. **Bulk Transactions**: Inserts are batched into multi-row SQL transactions with parameterized queries to minimize database overhead.

---

## 5. Installation & Configuration

### 5.1 MariaDB Database Setup
Execute the migration script [`migrations/V1__initial_telemetry_schema.sql`](file:///D:/projects/CS2/Swiftly/AegisTelemetry/migrations/V1__initial_telemetry_schema.sql) on your MariaDB/MySQL server (via phpMyAdmin or MySQL CLI):

```sql
CREATE DATABASE IF NOT EXISTS `aegis_telemetry`;
CREATE USER IF NOT EXISTS 'aegis_user'@'localhost' IDENTIFIED BY 'YOUR_SECURE_PASSWORD';
GRANT SELECT, INSERT, UPDATE ON `aegis_telemetry`.* TO 'aegis_user'@'localhost';
FLUSH PRIVILEGES;
```

### 5.2 Server Deployment
1. Download the compiled release archive `AegisTelemetry.zip`.
2. Extract the archive into your server's plugin directory:
   `addons/swiftlys2/plugins/AegisTelemetry/`
3. Configure settings in:
   `addons/swiftlys2/configs/plugins/AegisTelemetry/config.jsonc`

```jsonc
{
  "AegisTelemetry": {
    "Enabled": true,
    "ServerId": "competitive-server-1",
    "PseudonymizationSalt": "generate-a-secure-random-salt-here",

    "Database": {
      "Host": "127.0.0.1",
      "Port": 3306,
      "Database": "aegis_telemetry",
      "User": "aegis_user",
      "Password": "YOUR_SECURE_PASSWORD",
      "ConnectionTimeoutSeconds": 5,
      "MaximumPoolSize": 10
    },

    "Queue": {
      "MaxQueueCapacity": 10000,
      "BatchSize": 100,
      "FlushIntervalSeconds": 3.0,
      "HealthAuditIntervalSeconds": 60.0
    },

    "EnableAimbotTelemetry": true,
    "EnableTriggerbotTelemetry": true,
    "EnableAimlockTelemetry": true
  }
}
```

---

## 6. Console Commands (Administrative)

All commands are administrative (`@css/root`) and strictly read-only:

| Command | Description |
| :--- | :--- |
| `aegis_telemetry_status` | Displays memory queue depth, peak RAM size, metrics collected, written, dropped, and database error counts. |
| `aegis_telemetry_stats` | Displays live statistical percentile summaries ($p50$, $p90$, $p95$, $p99$) for reaction times, snaps, and tracking episodes. |
| `aegis_telemetry_reload` | Hot-reloads configuration from `config.jsonc` without restarting the map or server. |

---

## 7. Useful phpMyAdmin Calibration Queries

### 7.1 Human Reaction Time Distribution (Triggerbot) by Weapon
```sql
SELECT 
    weapon,
    COUNT(*) as total_samples,
    ROUND(AVG(reaction_ms), 1) as avg_reaction_ms,
    ROUND(MIN(reaction_ms), 1) as min_reaction_ms,
    ROUND(AVG(attacker_speed), 1) as avg_attacker_speed
FROM telemetry_triggerbot_reactions
WHERE hit_confirmed = 1
GROUP BY weapon
HAVING total_samples >= 10
ORDER BY total_samples DESC;
```

### 7.2 Peak Angular Velocity and Path Efficiency (Aimbot)
```sql
SELECT 
    weapon,
    COUNT(*) as shots_evaluated,
    ROUND(MAX(angular_velocity), 1) as max_velocity_deg_s,
    ROUND(AVG(path_efficiency) * 100, 1) as avg_path_efficiency_pct,
    ROUND(MAX(path_efficiency) * 100, 1) as max_path_efficiency_pct
FROM telemetry_aimbot_shots
WHERE hit_confirmed = 1
GROUP BY weapon;
```

### 7.3 Real Engagement Tracking Duration (Aimlock)
```sql
SELECT 
    ROUND(duration_seconds, 1) as duration_sec,
    COUNT(*) as occurrence_count,
    ROUND(AVG(target_displacement), 1) as avg_displacement_deg,
    ROUND(AVG(on_target_ratio) * 100, 1) as avg_adherence_pct
FROM telemetry_aimlock_episodes
GROUP BY ROUND(duration_seconds, 1)
ORDER BY duration_sec ASC;
```

---

## 8. License & Authors

* **Copyright (C) 2026 swisser & sevon**
  * **swisser**: [github.com/ressiws](https://github.com/ressiws)
  * **sevon**: [github.com/victor201202](https://github.com/victor201202)
* Licensed under the [Apache License 2.0](http://www.apache.org/licenses/LICENSE-2.0).
