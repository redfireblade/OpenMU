// <copyright file="BehaviorScript.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// A priority-chain behavior script — the 1D projection of the 8-dimensional
/// decision space. Defines a list of prioritized nodes where the first node
/// with a satisfied condition executes each tick.
///
/// This is the runtime representation of a script file (JSON on disk).
/// </summary>
public sealed class BehaviorScript
{
    /// <summary>Script identifier (e.g. "basic_hunting_loop").</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable Chinese DSL source code (.mu script).
    /// When set, the debug UI displays this instead of raw JSON.
    /// Mutually redundant with <see cref="Paragraphs"/> — one can be
    /// compiled from the other via <see cref="MuScriptCompiler"/>.
    /// </summary>
    [JsonPropertyName("dslSource")]
    public string? DslSource { get; set; }

    /// <summary>Semantic version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    /// <summary>Draft | Candidate | Verified | Stable | Archived.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "draft";

    /// <summary>
    /// Nodes ordered by priority (index 0 = highest priority).
    /// The first node whose condition evaluates to true executes.
    /// </summary>
    [JsonPropertyName("priorityChain")]
    public List<PriorityNode> PriorityChain { get; set; } = new();

    /// <summary>
    /// Default parameters. Can be overridden per-node.
    /// </summary>
    [JsonPropertyName("parameters")]
    public ScriptParameters Parameters { get; set; } = new();

    /// <summary>
    /// Paragraph-style script sections (optional). When present, the executor
    /// runs the priority chain of the current paragraph each tick, with
    /// support for <c>goto @label</c> jumps between paragraphs.
    /// If null or empty, the executor runs in traditional linear mode
    /// using <see cref="PriorityChain"/>.
    /// </summary>
    [JsonPropertyName("paragraphs")]
    public List<ScriptParagraph>? Paragraphs { get; set; }

    /// <summary>
    /// Tracks how many times each branch (if/elif/else) has been hit during execution.
    /// Keys are in the format: "nodeName.if", "nodeName.elif.0", "nodeName.elif.1", "nodeName.else"
    /// </summary>
    [JsonPropertyName("branchHitCounts")]
    public Dictionary<string, int> BranchHitCounts { get; set; } = new();

    /// <summary>
    /// Optional orchestrator config. When present with mode="dag", the ScriptExecutor
    /// runs in DAG mode instead of the default priority-chain mode.
    /// </summary>
    [JsonPropertyName("orchestrator")]
    public OrchestratorConfig? OrchestratorConfig { get; set; }
}

/// <summary>
/// Configuration for DAG execution mode.
/// When set on a BehaviorScript, the executor uses the Orchestrator library
/// instead of the linear priority chain.
/// </summary>
public sealed class OrchestratorConfig
{
    /// <summary>Execution mode identifier. When "dag", enables DAG-based execution.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "dag";

    /// <summary>Maximum concurrent nodes (reserved for future parallel execution).</summary>
    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>
    /// When true, node failures are isolated (one failing node doesn't stop subsequent nodes).
    /// Default: true.
    /// </summary>
    [JsonPropertyName("errorIsolation")]
    public bool ErrorIsolation { get; set; } = true;
}

/// <summary>
/// A single paragraph in a paragraph-style script.
/// Each paragraph has a label and a priority chain of nodes.
/// The executor runs the highest-priority satisfied node in
/// the current paragraph's chain each tick, and supports
/// <c>goto @label</c> to switch to another paragraph.
/// </summary>
public sealed class ScriptParagraph
{
    /// <summary>Label identifier for goto jumps (e.g. "start", "hunt").</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Priority chain of nodes for this paragraph.
    /// The first node with a satisfied condition executes each tick.
    /// </summary>
    [JsonPropertyName("nodes")]
    public List<PriorityNode> Nodes { get; set; } = new();
}

/// <summary>
/// A single node in the priority chain. Has a condition (when to run)
/// and an action (what to do). Only the highest-priority satisfied
/// node executes each tick.
/// </summary>
public sealed class PriorityNode
{
    /// <summary>Human-readable name for debugging (e.g. "hp_check").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Condition expression evaluated against player state.
    /// Supported values: "always", "hp_below_threshold", "mp_below_threshold",
    /// "is_dead", "inventory_full", "has_target_in_range", "has_target_not_in_range",
    /// "no_target", "in_safe_zone".
    /// </summary>
    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "always";

    /// <summary>
    /// Action to execute when condition is satisfied.
    /// Supported values: "use_hp_potion", "use_mp_potion", "wait_respawn",
    /// "return_and_sell", "attack_target", "walk_to_target",
    /// "find_nearest_monster", "random_patrol", "walk_route", "discover_trail".
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "random_patrol";

    /// <summary>Per-node parameter overrides (merged with global parameters).</summary>
    [JsonPropertyName("parameters")]
    public ScriptParameters? Parameters { get; set; }

    /// <summary>
    /// Optional ELIF branches evaluated in order when the main condition is false.
    /// Each has its own condition and action. The first whose condition is true executes.
    /// </summary>
    [JsonPropertyName("elifNodes")]
    public List<PriorityNode>? ElifNodes { get; set; }

    /// <summary>
    /// Optional ELSE branch executed when the main condition and all ELIF conditions are false.
    /// </summary>
    [JsonPropertyName("elseNode")]
    public PriorityNode? ElseNode { get; set; }
}

/// <summary>
/// Tunable parameters for behavior scripts. Global defaults can be
/// overridden per-node.
/// </summary>
public sealed class ScriptParameters
{
    /// <summary>HP ratio threshold for potion use (0.0-1.0). Default 0.35.</summary>
    [JsonPropertyName("hpThreshold")]
    public float HpThreshold { get; set; } = 0.35f;

    /// <summary>MP ratio threshold for potion use (0.0-1.0). Default 0.20.</summary>
    [JsonPropertyName("mpThreshold")]
    public float MpThreshold { get; set; } = 0.20f;

    /// <summary>Maximum level difference when selecting monster targets.</summary>
    [JsonPropertyName("maxLevelDiff")]
    public int MaxLevelDiff { get; set; } = 15;

    /// <summary>Patrol radius in tiles from current position.</summary>
    [JsonPropertyName("patrolRadius")]
    public int PatrolRadius { get; set; } = 30;

    /// <summary>Attack range in tiles (weapon-dependent, default for melee).</summary>
    [JsonPropertyName("attackRange")]
    public float AttackRange { get; set; } = 2.5f;

    /// <summary>Item pickup filter: "all", "rare_and_above", "none".</summary>
    [JsonPropertyName("pickupFilter")]
    public string PickupFilter { get; set; } = "all";

    /// <summary>Whether to return to town when inventory is full.</summary>
    [JsonPropertyName("returnWhenInventoryFull")]
    public bool ReturnWhenInventoryFull { get; set; } = true;

    /// <summary>Search range for finding attackables (tiles).</summary>
    [JsonPropertyName("searchRange")]
    public int SearchRange { get; set; } = 20;

    /// <summary>Strength allocation weight (0.0-1.0).</summary>
    [JsonPropertyName("strWeight")]
    public float StrWeight { get; set; }

    /// <summary>Agility allocation weight (0.0-1.0).</summary>
    [JsonPropertyName("agiWeight")]
    public float AgiWeight { get; set; }

    /// <summary>Vitality allocation weight (0.0-1.0).</summary>
    [JsonPropertyName("vitWeight")]
    public float VitWeight { get; set; }

    /// <summary>Energy allocation weight (0.0-1.0).</summary>
    [JsonPropertyName("eneWeight")]
    public float EneWeight { get; set; }

    /// <summary>Potion cooldown in milliseconds. Default 2000 (2 seconds).</summary>
    [JsonPropertyName("potionCooldownMs")]
    public int PotionCooldownMs { get; set; } = 2000;

    /// <summary>
    /// Number of ticks before a blacklisted target is eligible again.
    /// Default 50 (~20 seconds at 400ms/tick).
    /// </summary>
    [JsonPropertyName("blacklistExpiryTicks")]
    public int BlacklistExpiryTicks { get; set; } = 50;

    /// <summary>
    /// Number of consecutive ticks with the same target before considering it stuck.
    /// Default 25 (~10 seconds at 400ms/tick).
    /// </summary>
    [JsonPropertyName("stuckDetectionTicks")]
    public int StuckDetectionTicks { get; set; } = 25;

    /// <summary>
    /// Variable expression for &quot;variable&quot; condition type.
    /// Format: &quot;attackCount > 3&quot;
    /// Supports operators: ==, >, &lt;, >=, &lt;=
    /// </summary>
    [JsonPropertyName("variableExpr")]
    public string? VariableExpression { get; set; }

    /// <summary>
    /// Variable operation for "variable_op" action type.
    /// Format: "inc attackCount" | "set attackCount 0" | "dec attackCount 2"
    /// </summary>
    [JsonPropertyName("variableOp")]
    public string? VariableOp { get; set; }

    /// <summary>
    /// Hysteresis margin for potion threshold, as a ratio of the threshold.
    /// E.g. margin=0.20, threshold=0.35 - drink at 35%, stop at 42%.
    /// Default 0.20 (20%).
    /// </summary>
    [JsonPropertyName("potionHysteresisMargin")]
    public float PotionHysteresisMargin { get; set; } = 0.10f;

    /// <summary>
    /// 耐久度阈值 — 装备耐久低于此比例时触发维修。默认 0.1 (10%)。
    /// </summary>
    [JsonPropertyName("durabilityThreshold")]
    public float DurabilityThreshold { get; set; } = 0.1f;

    /// <summary>
    /// Explicit skill number for use_skill action.
    /// When set, the script will use this specific skill instead of
    /// auto-selecting the best skill.
    /// </summary>
    [JsonPropertyName("skillNumber")]
    public ushort? SkillNumber { get; set; }

    /// <summary>
    /// Number of ticks before the patrol center resets to current position.
    /// Default 50 (~20 seconds at 400ms/tick).
    /// </summary>
    [JsonPropertyName("patrolCenterResetTicks")]
    public int PatrolCenterResetTicks { get; set; } = 50;

    /// <summary>
    /// Number of consecutive ticks at the same position before forcing patrol.
    /// Default 50 (~20 seconds at 400ms/tick).
    /// </summary>
    [JsonPropertyName("positionStuckThreshold")]
    public int PositionStuckThreshold { get; set; } = 50;

    /// <summary>
    /// Number of ticks to wait after a successful pickup before attempting
    /// pickup_nearby again. Prevents the AI from being stuck in a permanent
    /// pickup loop when items constantly drop within scan range.
    /// Default 10 (~4 seconds at 400ms/tick).
    /// </summary>
    [JsonPropertyName("pickupCooldownTicks")]
    public int PickupCooldownTicks { get; set; } = 10;

    /// <summary>NPC 黑名单 — 永不攻击的怪物 ID 列表。</summary>
    [JsonPropertyName("npcBlacklist")]
    public List<ushort>? NpcBlacklist { get; set; }

    /// <summary>路线 ID 用于 walk_route 动作。</summary>
    [JsonPropertyName("routeId")]
    public string? RouteId { get; set; }

    /// <summary>随机偏移范围用于 walk_route 动作（默认 3）。</summary>
    [JsonPropertyName("jitter")]
    public byte Jitter { get; set; } = 3;

    /// <summary>药水数量阈值 — 低于此值触发补药。默认5。</summary>
    [JsonPropertyName("potionThreshold")]
    public int PotionThreshold { get; set; } = 5;

    /// <summary>每次回城购买数量。默认5。</summary>
    [JsonPropertyName("buyPotionCount")]
    public int BuyPotionCount { get; set; } = 5;

    /// <summary>Quest group number for start_quest/complete_quest actions.</summary>
    [JsonPropertyName("questGroup")]
    public short? QuestGroup { get; set; }

    /// <summary>Quest number within group for start_quest/complete_quest actions.</summary>
    [JsonPropertyName("questNumber")]
    public short? QuestNumber { get; set; }

    /// <summary>Quest NPC definition number for walk_to_quest_npc action.</summary>
    [JsonPropertyName("questNpcNumber")]
    public short? QuestNpcNumber { get; set; }

    /// <summary>Target map number for travel_to_map action.</summary>
    [JsonPropertyName("targetMapNumber")]
    public ushort? TargetMapNumber { get; set; }

    /// <summary>Cooldown after a successful return_and_sell cycle. Default 60 seconds.</summary>
    [JsonPropertyName("returnCooldownSec")]
    public int ReturnCooldownSec { get; set; } = 60;

    // ===== v2.0 Bot Features =====

    /// <summary>
    /// 热点狩猎坐标列表。无怪时可依次 relocate 到这些热点。
    /// </summary>
    [JsonPropertyName("hotspots")]
    public List<HotspotDef>? Hotspots { get; set; }

    /// <summary>
    /// 技能优先级列表（按 skill number）。攻击时按顺序尝试每个技能，
    /// 选第一个可用的。空列表或 null 时保持现有 auto-select 行为。
    /// </summary>
    [JsonPropertyName("skillPriority")]
    public List<ushort>? SkillPriority { get; set; }

    /// <summary>
    /// 无怪物计数器阈值。超过此值触发 relocate_to_hotspot。
    /// 默认 30 tick (~12 秒)。
    /// </summary>
    [JsonPropertyName("noMonsterTicksLimit")]
    public int NoMonsterTicksLimit { get; set; } = 30;

    /// <summary>
    /// 目标随机化数量：从最近的 N 个怪物中随机选一个作为目标。
    /// 默认 3，设为 1 则退化为绝对最近（原行为）。
    /// </summary>
    [JsonPropertyName("targetRandomizationCount")]
    public int TargetRandomizationCount { get; set; } = 3;

    /// <summary>
    /// 热点占用半径：热点周围此范围内有其他非队友玩家时，
    /// 视为被占用并自动跳过。
    /// 默认 8 格。
    /// </summary>
    [JsonPropertyName("hotspotOccupiedRadius")]
    public int HotspotOccupiedRadius { get; set; } = 8;

    // ===== 熔断保护 (Anti-Idle) =====

    /// <summary>
    /// 空闲软限（tick 数）。连续无进展超过此值时触发软熔断：
    /// 日志警告 + 强制换热点/巡逻复位。
    /// 默认 300 tick ≈ 2 分钟 (400ms/tick)。
    /// </summary>
    [JsonPropertyName("idleSoftLimitTicks")]
    public int IdleSoftLimitTicks { get; set; } = 300;

    /// <summary>
    /// 空闲硬限（tick 数）。连续无进展超过此值时触发硬熔断：
    /// 暂停执行 IdleRecoveryTicks 个 tick，避免空转。
    /// 默认 900 tick ≈ 6 分钟。
    /// </summary>
    [JsonPropertyName("idleHardLimitTicks")]
    public int IdleHardLimitTicks { get; set; } = 900;

    /// <summary>
    /// 硬熔断恢复等待 tick 数。触发硬限后暂停这么多 tick 再恢复。
    /// 默认 75 tick ≈ 30 秒。
    /// </summary>
    [JsonPropertyName("idleRecoveryTicks")]
    public int IdleRecoveryTicks { get; set; } = 75;

    /// <summary>Target item group for kg_can_craft condition.</summary>
    [JsonPropertyName("craftTargetItemGroup")]
    public int? CraftTargetItemGroup { get; set; }

    /// <summary>Target item number for kg_can_craft condition.</summary>
    [JsonPropertyName("craftTargetItemNumber")]
    public int? CraftTargetItemNumber { get; set; }
}

/// <summary>
/// 热点定义 — 脚本可以配置多个狩猎热点坐标，AI 在无怪时依次换点。
/// </summary>
public sealed class HotspotDef
{
    /// <summary>X 坐标。</summary>
    [JsonPropertyName("x")]
    public byte X { get; set; }

    /// <summary>Y 坐标。</summary>
    [JsonPropertyName("y")]
    public byte Y { get; set; }

    /// <summary>热点所在地图编号。</summary>
    [JsonPropertyName("map")]
    public ushort MapNumber { get; set; } = ushort.MaxValue;

    /// <summary>热点名称（可选，用于日志）。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>刷怪矩形 X 范围下限（来自 MonsterSpawn.X1）。</summary>
    [JsonPropertyName("x1")]
    public byte X1 { get; set; }

    /// <summary>刷怪矩形 X 范围上限（来自 MonsterSpawn.X2）。</summary>
    [JsonPropertyName("x2")]
    public byte X2 { get; set; }

    /// <summary>刷怪矩形 Y 范围下限（来自 MonsterSpawn.Y1）。</summary>
    [JsonPropertyName("y1")]
    public byte Y1 { get; set; }

    /// <summary>刷怪矩形 Y 范围上限（来自 MonsterSpawn.Y2）。</summary>
    [JsonPropertyName("y2")]
    public byte Y2 { get; set; }

    /// <summary>兜底占位标记—所有刷点均未找到时使用，供 Blocked 判断。</summary>
    [JsonIgnore]
    public bool IsNoSpawnFallback { get; set; }
}
