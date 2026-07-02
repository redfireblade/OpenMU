// <copyright file="QuestDifficultyAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>
/// 智能任务难度适配器 + 死亡数字地图记忆。
/// 根据角色的死亡记录动态调整任务难度，并将每次死亡作为经验存入 AI 的数字地图记忆。
///
/// 学习循环：
///   3连死 → 记录死亡快照(坐标/属性/技能/怪物等级) → 挂起任务 → 打低级怪
///   升3级后 → 对比当前属性 vs 死亡快照
///     ├ < 20%提升 → 继续打低级怪
///     └ ≥ 20%提升 → 恢复挂起的任务
///   再3连死 → 再记录/挂起 → 重复
///   3次挂起(9死) → 永久放弃该任务
///
/// 核心原则：每一次死亡都变成经验，AI 越死越强。
/// </summary>
public sealed class QuestDifficultyAdapter
{
    private sealed class QuestState
    {
        public short QuestGroup { get; init; }
        public short QuestNumber { get; init; }
        public int SuspendCount { get; set; }
        public int LevelAtSuspend { get; set; }
        public double MaxItemScoreAtSuspend { get; set; }
        public bool IsSuspended { get; set; }
        public int DeathsThisAttempt { get; set; }
        public bool IsAbandoned { get; set; }
        public string Label => $"Q{QuestGroup}/{QuestNumber}";

        /// <summary>挂起时记录的死亡快照（数字地图记忆）。</summary>
        public DeathSnapshot? DeathSnapshot { get; set; }

        /// <summary>恢复后需要等待的升级次数。</summary>
        public int LevelUpsNeeded { get; set; } = 3;

        /// <summary>恢复后已经升的级数。</summary>
        public int LevelUpsSinceResume { get; set; }

        /// <summary>该任务是否曾有过死亡记录（持久标记，挂起/重置不清除）。</summary>
        public bool HasEverDied { get; set; }
    }

    private readonly ConcurrentDictionary<(short Group, short Number), QuestState> _questStates = new();
    private readonly ILogger _logger;
    private readonly Func<int> _getLevel;
    private readonly Func<double> _getMaxItemScore;
    private readonly Func<AiPlayer?> _getPlayer;

    /// <summary>所有已记录的死亡快照（数字地图记忆库）。</summary>
    private readonly List<DeathSnapshot> _deathMemories = new();

    /// <summary>
    /// 获取所有死亡记忆，供外部持久化。
    /// </summary>
    public IReadOnlyList<DeathSnapshot> DeathMemories => _deathMemories.AsReadOnly();

    public (short Group, short Number)? ActiveQuest { get; set; }
    public int QuestDeathCount { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="QuestDifficultyAdapter"/> class.
    /// </summary>
    public QuestDifficultyAdapter(ILogger logger, Func<int> getLevel, Func<double> getMaxItemScore, Func<AiPlayer?> getPlayer)
    {
        this._logger = logger;
        this._getLevel = getLevel;
        this._getMaxItemScore = getMaxItemScore;
        this._getPlayer = getPlayer;
    }

    /// <summary>
    /// 记录一次任务相关死亡。返回 true 表示需要挂起任务。
    /// 第3次死亡时自动记录死亡快照到数字地图记忆。
    /// </summary>
    public bool RecordQuestDeath(short questGroup, short questNumber, short? monsterNumber = null, short? monsterLevel = null)
    {
        this.ActiveQuest = (questGroup, questNumber);
        this.QuestDeathCount++;

        var state = GetOrCreateState(questGroup, questNumber);
        state.DeathsThisAttempt++;

        // 第3次死亡 — 挂起任务 + 记录死亡快照到数字地图记忆
        if (this.QuestDeathCount >= 3 && !state.IsSuspended)
        {
            CaptureDeathSnapshot(state, monsterNumber, monsterLevel);
            SuspendQuest(state);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 检查活跃任务是否应该挂起。
    /// </summary>
    public bool ShouldSuspendQuest(short questGroup, short questNumber)
    {
        var state = GetOrCreateState(questGroup, questNumber);
        return state.IsSuspended || state.IsAbandoned;
    }

    /// <summary>
    /// 检查挂起的任务是否可以恢复。
    /// 使用死亡快照对比当前属性，需要 20% 以上综合提升。
    /// </summary>
    public bool ShouldResumeQuest(short questGroup, short questNumber)
    {
        var state = GetOrCreateState(questGroup, questNumber);
        if (!state.IsSuspended || state.IsAbandoned)
        {
            return false;
        }

        // 升够 3 级了吗？
        if (state.LevelUpsSinceResume < state.LevelUpsNeeded)
        {
            var leveled = state.LevelUpsSinceResume;
            var need = state.LevelUpsNeeded;
            this._logger.LogInformation(
                "[QuestAdapt] {Label}: 还需升{Remain}级 (已升{Leveled}/{Need}) 才能检查恢复条件",
                state.Label, need - leveled, leveled, need);
            return false;
        }

        // 有死亡快照 → 用 20% 属性对比
        if (state.DeathSnapshot is not null)
        {
            var player = this._getPlayer();
            if (player is null) return false;

            var improvement = state.DeathSnapshot.CalcImprovementPercent(player);
            this._logger.LogInformation(
                "[QuestAdapt] {Label}: 📊 死亡记忆对比 — 综合属性提升 {Improvement:F1}% (需 ≥20%)",
                state.Label, improvement);

            if (improvement < 20.0)
            {
                // 提升不足，重置升级计数再练
                state.LevelUpsSinceResume = 0;
                this._logger.LogInformation(
                    "[QuestAdapt] {Label}: ⏳ 属性提升不足 ({Improvement:F1}% < 20%) — 继续打低级怪升级",
                    state.Label, improvement);
                return false;
            }
        }

        // 条件满足 → 恢复任务
        this._logger.LogInformation(
            "[QuestAdapt] {Label}: ✅ 条件满足 — 恢复任务",
            state.Label);
        ResumeQuest(state);
        return true;
    }

    /// <summary>
    /// 记录一次升级事件，用于升级计数。
    /// </summary>
    public void RecordLevelUp()
    {
        // 找到所有挂起任务，递增 LevelUpsSinceResume
        foreach (var state in _questStates.Values)
        {
            if (state.IsSuspended && !state.IsAbandoned)
            {
                state.LevelUpsSinceResume++;
            }
        }
    }

    /// <summary>
    /// 该任务是否曾有过死亡记录（持久标记，不受挂起/重置影响）。
    /// 用于脚本条件 <c>quest_has_deaths</c>，让 AI 知道此任务死过。
    /// </summary>
    public bool HasQuestEverDied(short questGroup, short questNumber)
    {
        var state = GetOrCreateState(questGroup, questNumber);
        return state.HasEverDied;
    }

    /// <summary>
    /// 记录一次击杀（非死亡），重置连死计数。
    /// </summary>
    public void RecordKill()
    {
        this.QuestDeathCount = 0;
    }

    public List<(short Group, short Number)> GetSuspendedQuests()
    {
        return _questStates.Values
            .Where(s => s.IsSuspended && !s.IsAbandoned)
            .Select(s => (s.QuestGroup, s.QuestNumber))
            .ToList();
    }

    public List<(short Group, short Number)> GetAbandonedQuests()
    {
        return _questStates.Values
            .Where(s => s.IsAbandoned)
            .Select(s => (s.QuestGroup, s.QuestNumber))
            .ToList();
    }

    private QuestState GetOrCreateState(short questGroup, short questNumber)
    {
        return _questStates.GetOrAdd((questGroup, questNumber), _ =>
        {
            this._logger.LogInformation("[QuestAdapt] 开始跟踪任务 Q{Group}/{Number}", questGroup, questNumber);
            return new QuestState
            {
                QuestGroup = questGroup,
                QuestNumber = questNumber,
            };
        });
    }

    /// <summary>
    /// 捕获死亡快照并存入数字地图记忆。
    /// </summary>
    private void CaptureDeathSnapshot(QuestState state, short? monsterNumber, short? monsterLevel)
    {
        var player = this._getPlayer();
        if (player is null) return;

        var deathPos = player.Position;
        var snapshot = DeathSnapshot.Capture(player, state.QuestGroup, state.QuestNumber, deathPos, monsterNumber, monsterLevel);

        state.DeathSnapshot = snapshot;
        state.HasEverDied = true; // 持久标记，挂起/重置不清除
        _deathMemories.Add(snapshot);

        this._logger.LogWarning(
            "[QuestAdapt] {Label}: 💾 死亡快照已记录 — Lv{Level} 坐标({X},{Y}) 攻={PhysAtk:F0}/{WizAtk:F0} 防={Def:F0} 怪=#{Monster}Lv{MonsterLv}",
            state.Label, snapshot.Level, snapshot.DeathX, snapshot.DeathY,
            snapshot.AvgPhysicalAttack, snapshot.AvgWizardryAttack, snapshot.Defense,
            snapshot.MonsterNumber, snapshot.MonsterLevel);
    }

    private void SuspendQuest(QuestState state)
    {
        state.IsSuspended = true;
        state.SuspendCount++;
        state.LevelAtSuspend = this._getLevel();
        state.MaxItemScoreAtSuspend = this._getMaxItemScore();
        state.LevelUpsSinceResume = 0;
        this.QuestDeathCount = 0;

        this._logger.LogWarning(
            "[QuestAdapt] {Label}: ⏸️ 挂起 (第{SuspendCount}次) — Lv{Level}, 需要升{Need}级后对比属性提升≥20%",
            state.Label, state.SuspendCount, state.LevelAtSuspend, state.LevelUpsNeeded);

        if (state.SuspendCount >= 3)
        {
            state.IsAbandoned = true;
            state.IsSuspended = false;
            this._logger.LogWarning(
                "[QuestAdapt] {Label}: 🚫 已挂起3次 — 永久放弃此任务，数字地图记忆保留供后续参考",
                state.Label);
        }
    }

    private void ResumeQuest(QuestState state)
    {
        state.IsSuspended = false;
        state.DeathsThisAttempt = 0;
        state.LevelUpsSinceResume = 0;
        this.QuestDeathCount = 0;

        this._logger.LogInformation(
            "[QuestAdapt] {Label}: ▶️ 恢复任务 (第{Attempt}次尝试) — 属性提升达标",
            state.Label, state.SuspendCount + 1);
    }
}
