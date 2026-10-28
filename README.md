# AegisTelemetry — Passive Telemetry Collector for AegisAC

<p align="center">
  <strong>Server-side passive telemetry and behavioral kinematics collector for CS2 (SwiftlyS2 / .NET 10).</strong><br>
  Designed to harvest ground-truth human aiming distributions to accurately calibrate AegisAC thresholds and eliminate false positives.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/CS2-SwiftlyS2%201.5.0+-orange.svg" alt="SwiftlyS2" />
  <img src="https://img.shields.io/badge/.NET-10.0-purple.svg" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Storage-MariaDB%20%2F%20MySQL-blue.svg" alt="MariaDB" />
  <img src="https://img.shields.io/badge/Behavior-Strictly%20Passive-green.svg" alt="Passive" />
  <img src="https://img.shields.io/badge/License-Apache%202.0-yellow.svg" alt="License" />
</p>

---

## 1. Visão Geral

O **AegisTelemetry** é um plugin independente e estritamente **passivo** criado para correr em servidores de Counter-Strike 2. O seu único propósito é recolher telemetria comportamental e cinemática de mira de jogadores reais para:
* Determinar distribuições estatísticas reais (médias, desvios e percentis $p50$, $p90$, $p95$, $p99$, $p99.9$);
* Calibrar matematicamente os limiares de deteção do **AegisAC** (**Aimbot**, **Triggerbot** e **Aimlock**);
* Identificar limites físicos de jogadores humanos experientes em peeks, flickshots e duelos de mira, **eliminando falsos positivos**.

> [!IMPORTANT]
> **COMPLETAMENTE PASSIVO & NÃO PUNITIVO**: Este plugin **nunca** executa bans, kicks ou sanções. Não interfere com a jogabilidade, não altera pacotes e não altera o comportamento do jogo.

---

## 2. Princípios de Recolha & Elegibilidade

Para evitar contaminação das distribuições de calibração:
1. **Exclusão Absoluta de Bots**: BOTs nativos da Valve, bots de treino e GOTV (`IsFakeClient == true`) são 100% filtrados em todos os hooks e eventos.
2. **Exclusão de Jogadores Mortos & Espectadores**: Apenas combatentes vivos (`IsAlive && LifeState == 0`) e em equipa ativa (T ou CT) são monitorizados.
3. **Isolamento por Vida**: Ao morrer (`EventPlayerDeath`), o tracking é cancelado imediatamente. Ao renascer (`EventPlayerSpawn`), o estado do jogador é reinicializado para que eventos de vidas passadas não se misturem.
4. **Privacidade & GDPR (Pseudonimização)**: O SteamID do jogador nunca é gravado em texto simples. É gerado um identificador criptográfico (`HMAC-SHA256(SteamID, Salt)`) estável por servidor, garantindo privacidade.

---

## 3. Métricas Recolhidas

### 3.1 Aimbot Telemetry (`telemetry_aimbot_shots`)
* `snap_angle`: Maior desvio angular instantâneo entre comandos no intervalo de 0.5s antes do tiro (°).
* `angular_velocity`: Velocidade angular instantânea máxima (°/segundo).
* `error_reduction`: Taxa de aproximação à hitbox entre o início da aceleração e o disparo (`0.0` a `1.0`).
* `path_efficiency`: Relação entre a distância linear direta e o comprimento total do arco percorrido pela mira (`0.0` a `1.0`).
* `monotonic_steps`: Quantidade de passos de desaceleração suave contínua em direção ao alvo.
* `target_distance`: Distância euclidiana até ao adversário (unidades CS2).
* `hit_confirmed`: Regista tanto tiros que causaram dano (`true`) como disparos normais que falharam (`false`), permitindo comparar a precisão e velocidade de tiros acertados vs falhados.

### 3.2 Triggerbot Telemetry (`telemetry_triggerbot_reactions`)
* `reaction_ticks` & `reaction_ms`: Intervalo de tempo entre o primeiro contacto da mira na hitbox do adversário e o envio do comando de disparo.
* `attacker_speed`: Velocidade horizontal do jogador no tick do tiro ($\text{unidades/s}$) — **indispensável para diferenciar pré-fires em strafe de reações estáticas**.
* `contact_mode`: `TargetDriven` (mira parada num pixel à espera do inimigo) vs `AimDriven` (mira a movimentar-se ativamente em direção ao inimigo).
* `prior_clicks`: Contagem de cliques de ataque nos 5 ticks anteriores — **usado para filtrar spam de cliques / jitter-click cego com pistolas**.
* `smoke_occluded`: Indicador se a linha de tiro cruzou uma granada de fumo ativa.

### 3.3 Aimlock Telemetry (`telemetry_aimlock_episodes`)
* `duration_ticks` & `duration_seconds`: Duração contínua de acompanhamento sobre a hitbox do adversário.
* `target_displacement`: Deslocamento angular percorrido pelo alvo no ecrã (°).
* `on_target_ratio`: Percentagem de tempo mantido dentro da bounding box do adversário.
* `avg_distance`: Distância média de combate durante o episódio.
* `completed_normally`: Se o duelo terminou naturalmente (alvo saiu da mira) ou se foi interrompido por morte/desconexão.

---

## 4. Arquitetura de Buffers e Armazenamento MariaDB

```
Game Thread (CS2 Server)                     Background Worker Thread               MariaDB Server
┌───────────────────────┐                    ┌─────────────────────────┐           ┌──────────────┐
│  ProcessUsercmds      │                    │                         │           │              │
│  SetupMove            │ ── TryWrite() ───> │  BoundedChannelWriter   │ ── Bulk ─>│   Database   │
│  EventWeaponFire      │   (Zero I/O Block) │  (Max 10,000 in RAM)    │  Async    │              │
│  EventPlayerHurt      │                    │  DropOldest on outage   │           │ (phpMyAdmin) │
└───────────────────────┘                    └─────────────────────────┘           └──────────────┘
```

1. **Zero Game Lag**: Toda a inserção na base de dados ocorre numa thread assíncrona desacoplada da Game Thread.
2. **Buffer Limitado (RAM Capped)**: O canal em memória possui limite rígido de capacidade (default: 10.000 itens). Se a MariaDB cair temporariamente, o plugin descarta os registos mais antigos (`DropOldest`) e incrementa o contador atómico `MetricsDropped`.
3. **Bulk Transactions**: As inserções são agrupadas em lotes periódicos com transações SQL parametrizadas, reduzindo a carga no servidor da base de dados.

---

## 5. Instalação & Configuração

### 5.1 Criar a Base de Dados MariaDB
Execute o script [`migrations/V1__initial_telemetry_schema.sql`](file:///d:/projects/CS2/Swiftly/AegisTelemetry/migrations/V1__initial_telemetry_schema.sql) na sua base de dados MariaDB/MySQL (através do phpMyAdmin ou cliente MySQL):

```sql
CREATE DATABASE IF NOT EXISTS `aegis_telemetry`;
CREATE USER IF NOT EXISTS 'aegis_user'@'localhost' IDENTIFIED BY 'SUA_SENHA_AQUI';
GRANT SELECT, INSERT, UPDATE ON `aegis_telemetry`.* TO 'aegis_user'@'localhost';
FLUSH PRIVILEGES;
```

### 5.2 Instalação no Servidor
1. Descarregue o ficheiro compilado `AegisTelemetry.zip`.
2. Extraia o conteúdo para a pasta de plugins do seu servidor:
   `addons/swiftlys2/plugins/AegisTelemetry/`
3. Ajuste o ficheiro de configuração em:
   `addons/swiftlys2/configs/plugins/AegisTelemetry/config.jsonc`

```jsonc
{
  "AegisTelemetry": {
    "Enabled": true,
    "ServerId": "servidor-competitivo-1",
    "PseudonymizationSalt": "gerar-um-salt-secreto-aleatorio-aqui",

    "Database": {
      "Host": "127.0.0.1",
      "Port": 3306,
      "Database": "aegis_telemetry",
      "User": "aegis_user",
      "Password": "SUA_SENHA_AQUI",
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

## 6. Comandos de Consola (Administrativos)

Todos os comandos são administrativos (`@css/root`) e exclusivamente de consulta (somente leitura):

| Comando | Descrição |
| :--- | :--- |
| `aegis_telemetry_status` | Exibe o estado da fila em memória, pico de tamanho, métricas recolhidas, gravadas, descartadas e erros da BD. |
| `aegis_telemetry_stats` | Apresenta resumos de percentis ao vivo ($p50$, $p90$, $p95$, $p99$) para tempos de reação, snaps e durações de tracking. |
| `aegis_telemetry_reload` | Recarrega as configurações do `config.jsonc` sem reiniciar o mapa ou o servidor. |

---

## 7. Consultas Úteis para Calibração no phpMyAdmin

### 7.1 Distribuição de Tempo de Reação (Triggerbot) por Arma
```sql
SELECT 
    weapon,
    COUNT(*) as total_samples,
    ROUND(AVG(reaction_ms), 1) as media_ms,
    ROUND(MIN(reaction_ms), 1) as minimo_ms,
    ROUND(AVG(attacker_speed), 1) as velocidade_media
FROM telemetry_triggerbot_reactions
WHERE hit_confirmed = 1
GROUP BY weapon
HAVING total_samples >= 10
ORDER BY total_samples DESC;
```

### 7.2 Velocidade Angular Máxima e Eficiência de Trajetória (Aimbot)
```sql
SELECT 
    weapon,
    COUNT(*) as tiros_analisados,
    ROUND(MAX(angular_velocity), 1) as vel_max_graus_s,
    ROUND(AVG(path_efficiency) * 100, 1) as eficiencia_media_pct,
    ROUND(MAX(path_efficiency) * 100, 1) as eficiencia_max_pct
FROM telemetry_aimbot_shots
WHERE hit_confirmed = 1
GROUP BY weapon;
```

### 7.3 Duração Real de Tracking (Aimlock)
```sql
SELECT 
    ROUND(duration_seconds, 1) as duracao_s,
    COUNT(*) as quantidade_ocorrencias,
    ROUND(AVG(target_displacement), 1) as deslocamento_medio_graus,
    ROUND(AVG(on_target_ratio) * 100, 1) as aderencia_media_pct
FROM telemetry_aimlock_episodes
GROUP BY ROUND(duration_seconds, 1)
ORDER BY duracao_s ASC;
```

---

## 8. Licença & Autoria

* **Copyright (C) 2026 swisser & sevon**
  * **swisser**: [github.com/ressiws](https://github.com/ressiws)
  * **sevon**: [github.com/victor201202](https://github.com/victor201202)
* Licenciado sob a [Apache License 2.0](http://www.apache.org/licenses/LICENSE-2.0).
