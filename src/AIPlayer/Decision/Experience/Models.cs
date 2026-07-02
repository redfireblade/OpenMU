// <copyright file="Models.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Experience;

using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// 什么触发了这次AI行为。
/// </summary>
public enum TriggerSource
{
    /// <summary>系统公告触发（BOSS/黄金怪/活动）。</summary>
    SystemAnnouncement,
    /// <summary>规则引擎匹配触发。</summary>
    RuleMatch,
    /// <summary>用户手动指令。</summary>
    UserCommand,
    /// <summary>定时任务。</summary>
    Timer,
    /// <summary>事件中断（死亡/HP低等）。</summary>
    EventInterrupt,
}

/// <summary>
/// AI 行为类型 — 所有可能的经验来源。
/// </summary>
public enum BehaviorType
{
    /// <summary>狩猎/打怪（含BOSS/黄金怪）。</summary>
    Hunting,
    /// <summary>进入副本（血色/恶魔/死亡城堡等）。</summary>
    MiniGame,
    /// <summary>合成物品（翅膀/门票/装备）。</summary>
    Crafting,
    /// <summary>交易行为（买/卖物品）。</summary>
    Trading,
    /// <summary>PK（玩家间对战）。</summary>
    Pvp,
    /// <summary>跨地图移动/探索。</summary>
    Travel,
    /// <summary>任务执行（游戏任务）。</summary>
    Quest,
    /// <summary>其他未分类。</summary>
    Other,
}

/// <summary>
/// AI 行为的结果。
/// </summary>
public enum BehaviorResult
{
    InProgress,
    Completed,
    Failed,
    Interrupted,
    NoTarget,
}

/// <summary>
/// 一次AI行为的完整记录。"开始"时创建，"结束"时补充结果并提交到群体库。
/// 所有类型的经验（狩猎/PK/交易/合成）都走这个模型。
/// </summary>
public sealed record ActionLog
{
    public Guid LogId { get; init; } = Guid.NewGuid();
    public DateTime StartTime { get; init; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }

    // AI 角色上下文
    public string AiRoleName { get; init; } = string.Empty;
    public int AiLevel { get; init; }
    public string AiClass { get; init; } = string.Empty;

    // 行为类型
    public BehaviorType BehaviorType { get; init; }

    // ===== 狩猎/打怪相关 =====
    public ushort? MapId { get; init; }
    public short? MonsterNumber { get; init; }
    public int? MonsterLevel { get; init; }

    // ===== 交易相关 =====
    public string? ItemName { get; init; }
    public int? ItemPrice { get; init; }
    public bool? IsBuy { get; init; }   // true=买, false=卖

    // ===== PK相关 =====
    public bool? IsPvpVictory { get; init; }
    public string? OpponentName { get; init; }
    public int? OpponentLevel { get; init; }
    public string? OpponentClass { get; init; }

    // ===== 合成相关 =====
    public string? CraftRecipeName { get; init; }
    public bool? CraftSuccess { get; init; }

    // ===== 结果 =====
    public BehaviorResult Result { get; set; }
    public int MonstersKilled { get; set; }
    public int GoldEarned { get; set; }
    public int DeathCount { get; set; }
    public int DurationSeconds { get; set; }
    public string? FailureReason { get; set; }

    // 事后判断
    public bool WorthRepeating { get; set; }

    public int ElapsedSeconds =>
        this.EndTime.HasValue
            ? (int)(this.EndTime.Value - this.StartTime).TotalSeconds
            : 0;
}

/// <summary>
/// 按(事件类型+地图+怪物)聚合的经验统计。
/// 聚合结果直接对应一条规则候选的"条件部分"。
/// </summary>
public sealed record AggregatedExperience
{
    public string DimensionKey { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;

    // 维度
    public ushort MapId { get; init; }
    public short MonsterNumber { get; init; }
    public string MonsterName { get; init; } = string.Empty;
    public int MonsterLevel { get; init; }

    // 统计
    public int TotalAttempts { get; init; }
    public int SuccessCount { get; init; }
    public int FailCount { get; init; }
    public int TotalDeaths { get; init; }
    public float AvgDurationSeconds { get; init; }

    // 收益统计
    public float AvgGoldEarned { get; init; }
    public float AvgMonstersKilled { get; init; }
    public Dictionary<string, int> TopDrops { get; init; } = new();

    // 等级分布
    public int MinPlayerLevel { get; init; }
    public int MaxPlayerLevel { get; init; }
    public float AvgPlayerLevel { get; init; }

    // 综合评分 0~100
    public float WorthScore { get; init; }

    public DateTime LastUpdated { get; init; }
}

/// <summary>
/// 规则候选 — 一条待确认的规则，由经验聚合生成。
/// 置信度 ≥ ConfidenceThreshold 时自动写入规则表。
/// 和 RuleDef 结构相同（因为经验=未成熟的规则）。
/// </summary>
public sealed record RuleCandidate
{
    public string RuleId { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public string ScriptId { get; init; } = string.Empty;
    public int SuggestedPriority { get; init; }
    public int MinLevel { get; init; }
    public int MaxLevel { get; init; }
    public Dictionary<string, string>? Parameters { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    // 经验溯源
    public float Confidence { get; init; }
    public int SampleSize { get; init; }
    public string DimensionKey { get; init; } = string.Empty;
    public string EvidenceSummary { get; init; } = string.Empty;

    // 是否已自动写入规则表
    public bool PromotedToRule { get; init; }

    public const float ConfidenceThreshold = 0.6f;
    public const float PromoteThreshold = 0.9f;

    public bool IsUsable => this.Confidence >= ConfidenceThreshold;
    public bool ShouldPromote => this.Confidence >= PromoteThreshold;

    /// <summary>
    /// 转换为标准 RuleDef。
    /// </summary>
    public RuleDef ToRuleDef() => new(
        this.RuleId,
        this.SuggestedPriority,
        this.Category,
        this.Condition,
        this.ScriptId,
        this.MinLevel,
        this.MaxLevel,
        null,
        null,
        this.Description,
        this.Parameters,
        AutoGenerated: true,
        Confidence: this.Confidence);
}
