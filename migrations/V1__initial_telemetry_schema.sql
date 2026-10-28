-- =============================================================================
-- AegisTelemetry — MariaDB Database Migration V1
--
-- Copyright (C) 2026 swisser & sevon
-- Authors:
--   - swisser (https://github.com/ressiws)
--   - sevon   (https://github.com/victor201202)
--
-- Description:
--   Initial relational schema for collecting calibration telemetry for
--   AegisAC (Aimbot, Triggerbot, Aimlock) from live game servers.
-- =============================================================================

CREATE TABLE IF NOT EXISTS `telemetry_sessions` (
    `id` BIGINT AUTO_INCREMENT PRIMARY KEY,
    `server_id` VARCHAR(64) NOT NULL,
    `map_name` VARCHAR(64) NOT NULL,
    `start_time_utc` DATETIME(3) NOT NULL,
    `end_time_utc` DATETIME(3) NULL,
    `tick_rate` SMALLINT NOT NULL DEFAULT 64,
    `created_at` TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    INDEX `idx_server_map` (`server_id`, `map_name`),
    INDEX `idx_start_time` (`start_time_utc`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `telemetry_aimbot_shots` (
    `id` BIGINT AUTO_INCREMENT PRIMARY KEY,
    `session_id` BIGINT NOT NULL,
    `timestamp_utc` DATETIME(3) NOT NULL,
    `server_tick` INT NOT NULL,
    `command_number` INT NOT NULL,
    `player_hash` VARCHAR(64) NOT NULL,
    `weapon` VARCHAR(32) NOT NULL,
    `target_distance` FLOAT NOT NULL,
    `snap_angle` FLOAT NOT NULL,
    `angular_velocity` FLOAT NOT NULL,
    `error_reduction` FLOAT NOT NULL,
    `path_efficiency` FLOAT NOT NULL,
    `monotonic_steps` SMALLINT NOT NULL,
    `hit_confirmed` BOOLEAN NOT NULL,
    `actual_hitgroup` SMALLINT NOT NULL DEFAULT 0,
    `damage_dealt` SMALLINT NOT NULL DEFAULT 0,
    INDEX `idx_aimbot_session` (`session_id`),
    INDEX `idx_aimbot_weapon` (`weapon`),
    INDEX `idx_aimbot_hit_snap` (`hit_confirmed`, `snap_angle`),
    CONSTRAINT `fk_aimbot_session` FOREIGN KEY (`session_id`) REFERENCES `telemetry_sessions` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `telemetry_triggerbot_reactions` (
    `id` BIGINT AUTO_INCREMENT PRIMARY KEY,
    `session_id` BIGINT NOT NULL,
    `timestamp_utc` DATETIME(3) NOT NULL,
    `server_tick` INT NOT NULL,
    `command_number` INT NOT NULL,
    `player_hash` VARCHAR(64) NOT NULL,
    `weapon` VARCHAR(32) NOT NULL,
    `reaction_ticks` INT NOT NULL,
    `reaction_ms` FLOAT NOT NULL,
    `attacker_speed` FLOAT NOT NULL,
    `contact_mode` ENUM('TargetDriven', 'AimDriven') NOT NULL,
    `prior_clicks` SMALLINT NOT NULL DEFAULT 0,
    `target_distance` FLOAT NOT NULL,
    `hit_confirmed` BOOLEAN NOT NULL,
    `actual_hitgroup` SMALLINT NOT NULL DEFAULT 0,
    `smoke_occluded` BOOLEAN NOT NULL DEFAULT FALSE,
    INDEX `idx_trigger_session` (`session_id`),
    INDEX `idx_trigger_reaction` (`reaction_ms`),
    INDEX `idx_trigger_weapon_speed` (`weapon`, `attacker_speed`),
    CONSTRAINT `fk_trigger_session` FOREIGN KEY (`session_id`) REFERENCES `telemetry_sessions` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `telemetry_aimlock_episodes` (
    `id` BIGINT AUTO_INCREMENT PRIMARY KEY,
    `session_id` BIGINT NOT NULL,
    `timestamp_utc` DATETIME(3) NOT NULL,
    `start_tick` INT NOT NULL,
    `end_tick` INT NOT NULL,
    `player_hash` VARCHAR(64) NOT NULL,
    `duration_ticks` INT NOT NULL,
    `duration_seconds` FLOAT NOT NULL,
    `target_displacement` FLOAT NOT NULL,
    `on_target_ratio` FLOAT NOT NULL,
    `avg_distance` FLOAT NOT NULL,
    `completed_normally` BOOLEAN NOT NULL,
    INDEX `idx_aimlock_session` (`session_id`),
    INDEX `idx_aimlock_duration` (`duration_seconds`),
    INDEX `idx_aimlock_displacement` (`target_displacement`),
    CONSTRAINT `fk_aimlock_session` FOREIGN KEY (`session_id`) REFERENCES `telemetry_sessions` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS `telemetry_health_audit` (
    `id` BIGINT AUTO_INCREMENT PRIMARY KEY,
    `session_id` BIGINT NOT NULL,
    `timestamp_utc` DATETIME(3) NOT NULL,
    `metrics_collected` BIGINT NOT NULL,
    `metrics_written` BIGINT NOT NULL,
    `metrics_dropped` BIGINT NOT NULL,
    `database_errors` INT NOT NULL,
    `queue_peak_size` INT NOT NULL,
    INDEX `idx_health_session` (`session_id`),
    CONSTRAINT `fk_health_session` FOREIGN KEY (`session_id`) REFERENCES `telemetry_sessions` (`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
