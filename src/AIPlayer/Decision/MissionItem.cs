// <copyright file="MissionItem.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.AIPlayer.Scripting;

/// <summary>
/// 看板任务条目 — "今天要做的一件事"。
/// 树形阶段结构，支持任务中断恢复和 Repeatable 判断。
/// </summary>
public sealed class MissionItem
{
    /// <summary>任务ID (唯一标识)。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Quest 组号。</summary>
    public short QuestGroup { get; set; }

    /// <summary>Quest 编号。</summary>
    public short QuestNumber { get; set; }

    /// <summary>是否可重复接 (对应游戏 QuestDefinition.Repeatable)。</summary>
    public bool Repeatable { get; set; }

    /// <summary>优先级 (0=最高，越大越优先)。</summary>
    public int Priority { get; set; }

    /// <summary>任务类型。</summary>
    public MissionType Type { get; set; } = MissionType.Quest;

    /// <summary>任务状态。</summary>
    public MissionStatus Status { get; set; } = MissionStatus.Pending;

    /// <summary>依赖的前置任务ID列表 —— 全部完成后本任务才加入候选。</summary>
    public string[] Dependencies { get; set; } = Array.Empty<string>();

    /// <summary>关联的任务定义 (Quest 类任务)，仅用于初始化时读取游戏配置。</summary>
    public QuestDefinition? QuestDef { get; set; }

    /// <summary>执行模块标识 (quest_executor / survival / item_farm)。</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>任务条件 — 满足时标记为可执行。</summary>
    public MissionCondition? Condition { get; set; }

    /// <summary>要监听的事件类型，发生时会触发条件重评。</summary>
    public string[] TriggerEvents { get; set; } = Array.Empty<string>();

    /// <summary>脚本引用 — 看板不生成脚本，只引用已存在的脚本。</summary>
    public BehaviorScript? Script { get; set; }

    // ========== 树形阶段 ==========

    /// <summary>有序的阶段列表 (接→杀→交等)。</summary>
    public List<TaskStage> Stages { get; set; } = new();

    /// <summary>当前执行到的阶段索引 (用于掉线恢复)。</summary>
    public int CurrentStageIndex { get; set; }

    // ========== 执行进度 ==========

    /// <summary>
    /// 杀怪进度列表（仅用于看板历史记录，AI 不依赖此数据做决策或执行）。
    /// 任务进度判断全部走系统 API（GetActiveQuests().RequiredKills[].Current），
    /// 来源：QuestMonsterKillRequirementState.KillCount（系统数据库）。
    /// 此列表在 BuildStagesForEntry 中从系统 API 构建一次后不再刷新。
    /// AR-24: 看板纯数据 — 此字段保留作统计用，不参与执行。
    /// </summary>
    public List<KillProgress> KillProgress { get; set; } = new();

    /// <summary>掉线恢复标记 (如 "walk_to_npc:257")。</summary>
    public string? ResumeAction { get; set; }

    // ========== 分类体系 ==========

    /// <summary>任务类别 (主线/支线/日常/随机/事件/生存/自定义/群体)。</summary>
    public QuestCategory Category { get; set; } = QuestCategory.SideQuest;

    /// <summary>任务目的 (杀怪/收集/对话/转职/生存/副本/PvP)。多选按位组合。</summary>
    public QuestGoal Goal { get; set; }

    /// <summary>任务来源 (游戏系统/AI自定义/AI群体)。</summary>
    public QuestSource Source { get; set; } = QuestSource.GameSystem;

    /// <summary>
    /// 最高等级限制。0 表示不限制。
    /// 游戏配置中 QuestDefinition.MaximumCharacterLevel 直接映射。
    /// </summary>
    public int MaxLevel { get; set; }

    /// <summary>失败后是否可重接。</summary>
    public bool FailureRetryable { get; set; } = true;

    /// <summary>已重复次数 (-1 = 不限/不跟踪)。</summary>
    public int RepeatCount { get; set; }

    /// <summary>最大重复次数 (-1 = 无限, 0 = 单次不可重复)。</summary>
    public int MaxRepeatCount { get; set; } = -1;

    // ========== AR-25 失败重试字段 ==========

    /// <summary>失败原因 (仅在 Status == Failed 时有意义)。</summary>
    public FailureReason? FailureReason { get; set; }

    /// <summary>已重试次数。</summary>
    public int RetryCount { get; set; }

    /// <summary>首次失败时间 (UTC)。</summary>
    public DateTime? FirstFailedAt { get; set; }

    /// <summary>是否已标记为死任务 (永不再重试)。</summary>
    public bool IsDeadTask { get; set; }
}

/// <summary>
/// 任务阶段 — 树形任务分解的叶子节点。
/// 每个阶段对应一个可中断/恢复的步骤。
/// </summary>
public sealed class TaskStage
{
    /// <summary>阶段ID (如 "accept" / "hunt" / "submit")。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名称 (如 "接任务" / "狩猎" / "提交")。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>阶段状态。</summary>
    public TaskStageStatus Status { get; set; } = TaskStageStatus.Pending;

    /// <summary>进入本阶段的条件 (可选)。</summary>
    public string? Condition { get; set; }

    /// <summary>本阶段的入口动作 (作为脚本入口点)。</summary>
    public string? Action { get; set; }

    /// <summary>本阶段的自定义上下文数据。</summary>
    public Dictionary<string, object> Context { get; set; } = new();
}

/// <summary>杀怪进度。</summary>
public sealed class KillProgress
{
    /// <summary>怪物编号。</summary>
    public short MonsterNumber { get; set; }

    /// <summary>怪物名称。</summary>
    public string MonsterName { get; set; } = string.Empty;

    /// <summary>当前击杀数。</summary>
    public int CurrentKills { get; set; }

    /// <summary>需击杀数。</summary>
    public int RequiredKills { get; set; }
}

/// <summary>任务类型。</summary>
public enum MissionType
{
    Quest,       // 游戏任务
    Survival,    // 生存刷怪
    ItemFarm,    // 物品收集
    Emergency,   // 紧急事件 (死亡/卡住)
}

/// <summary>任务状态。</summary>
public enum MissionStatus
{
    Pending,     // 等待条件
    Active,      // 执行中
    Completed,   // 已完成
    Failed,      // 失败
    Suspended,   // 被中断
}

/// <summary>阶段状态。</summary>
public enum TaskStageStatus
{
    Pending,     // 未开始
    Wip,         // 进行中
    Completed,   // 已完成
    Failed,      // 失败
}

/// <summary>任务条件委托 — 返回 true 时任务可推进。</summary>
public sealed class MissionCondition
{
    /// <summary>是否有活跃任务。</summary>
    public bool HasActiveQuest { get; set; }

    /// <summary>是否无活跃任务。</summary>
    public bool NoActiveQuest { get; set; }

    /// <summary>任务杀怪条件是否满足。</summary>
    public bool QuestKillsDone { get; set; }

    /// <summary>是否可以接任务 (NPC对话已打开)。</summary>
    public bool CanAcceptQuest { get; set; }

    /// <summary>血量是否低于阈值。</summary>
    public bool? HpBelowThreshold { get; set; }

    /// <summary>是否在安全区。</summary>
    public bool? InSafeZone { get; set; }

    /// <summary>经验值条件委托 (自定义函数)。预留。</summary>
    public Func<ValueTask<bool>>? Custom { get; set; }

    /// <summary>评估所有条件。</summary>
    public async ValueTask<bool> EvaluateAsync()
    {
        if (Custom is not null && !await Custom()) return false;
        return true;
    }
}

/// <summary>任务类别 — 对应游戏 QuestDefinition.Group 的语义映射。</summary>
public enum QuestCategory
{
    /// <summary>主线任务 (Group=0)。推动角色成长的核心任务链，含转职。</summary>
    MainStory,
    /// <summary>支线任务 (Group=15)。可选但提供有价值奖励。</summary>
    SideQuest,
    /// <summary>日常任务 (Group=18)。可重复的任务。</summary>
    Daily,
    /// <summary>随机任务 (Group=19)。非固定出现的高等级任务。</summary>
    Random,
    /// <summary>副本事件 (血色城堡/恶魔广场等)。</summary>
    InstanceEvent,
    /// <summary>生存刷怪 — 兜底的自由行为。</summary>
    Survival,
    /// <summary>AI 系统自定义任务。</summary>
    AiCustom,
    /// <summary>AI 群体任务 (多个 AI 配合完成)。</summary>
    AiGroup,
}

/// <summary>任务目的 — 看板筛选和决策依据。多选按位组合。</summary>
[Flags]
public enum QuestGoal
{
    /// <summary>杀怪 (有 RequiredMonsterKills)。</summary>
    Kill = 1,
    /// <summary>收集物品 (有 RequiredItems)。</summary>
    Collect = 2,
    /// <summary>对话/NPC 交互 (RequiresClientAction=true)。</summary>
    Dialogue = 4,
    /// <summary>转职 (奖励包含 CharacterEvolution)。</summary>
    ClassAdvancement = 8,
    /// <summary>生存/自由刷怪。</summary>
    Survival = 16,
    /// <summary>副本 (MiniGame 类型)。</summary>
    Instance = 32,
    /// <summary>PvP (预留，战盟战争等)。</summary>
    Pvp = 64,
}

/// <summary>任务来源 — 谁创建了这个任务。</summary>
public enum QuestSource
{
    /// <summary>游戏系统配置的任务 (从 QuestDefinition 映射)。</summary>
    GameSystem,
    /// <summary>AI 玩家自定义任务 (由 AI 决策系统生成)。</summary>
    AiCustom,
    /// <summary>AI 群体系统发布的任务 (由 AIODS/群体协调器下发)。</summary>
    AiGroup,
}

/// <summary>AR-25: 任务失败原因 — 每 tick 三层扫描决策依据。</summary>
public enum FailureReason
{
    /// <summary>无活跃 Quest 且无杀怪需求，无法执行。</summary>
    NotExecutable,

    /// <summary>找不到目标怪物。</summary>
    NoTarget,

    /// <summary>任务不可重复 (Repeatable=false)，失败后永不重试。</summary>
    NotRetryable,

    /// <summary>找不到任务 NPC。</summary>
    CannotReachNpc,

    /// <summary>类别配额当天已满。</summary>
    CategoryQuotaFull,

    /// <summary>任务执行超时。</summary>
    Timeout,

    /// <summary>副本模块未实现。</summary>
    InstanceModuleMissing,
}

/// <summary>每类任务的等级配额配置。</summary>
public sealed class QuestCategoryQuota
{
    /// <summary>目标类别。</summary>
    public QuestCategory Category { get; init; }

    /// <summary>此类别任务的最大同时出现数。</summary>
    public int MaxActive { get; init; }

    /// <summary>在此等级以上才启用配额 (0=总是启用)。</summary>
    public int EffectiveLevel { get; init; }
}
