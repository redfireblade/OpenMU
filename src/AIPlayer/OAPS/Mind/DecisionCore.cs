// <copyright file="DecisionCore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using OAPS.World;

/// <summary>
/// 双轨决策核心 — OAPS Layer 5 心智引擎核心组件。
/// - 快速轨道(90%)：生存规则 + 行为惯性 + 效用系统，<1ms
/// - 深度轨道(10%)：LLM 推理，仅在升级/死亡/稀有掉落/复杂社交时触发
/// 参考 OAPS v3.0 6.3。
/// </summary>
public sealed class DecisionCore
{
    private readonly BehaviorInertiaEngine _inertia = new();
    private readonly FastDecisionEngine _fast = new();
    private readonly DeepDecisionEngine? _deep;

    /// <summary>
    /// 初始化决策核心。
    /// </summary>
    /// <param name="deepEngine">深度推理引擎（LLM）。为 null 时仅使用快速轨道。</param>
    public DecisionCore(DeepDecisionEngine? deepEngine = null)
    {
        _deep = deepEngine;
    }

    /// <summary>
    /// 做出决策。
    /// </summary>
    /// <param name="state">当前世界状态。</param>
    /// <param name="context">感知上下文（用于记忆检索）。</param>
    /// <param name="personality">个性引擎（用于调制）。</param>
    /// <returns>决策结果。</returns>
    public async ValueTask<DecisionResult> Decide(WorldState state, PerceptContext context, PersonalityEngine personality)
    {
        // 快速轨道
        var fastAction = _fast.Decide(state, personality);

        // 快速轨道有结果且不需要深度推理 → 直接返回
        if (fastAction is not null && !this.ShouldDeepReason(state, context))
        {
            return fastAction;
        }

        // 深度轨道（预留 LLM 接口）
        if (_deep is not null && this.ShouldDeepReason(state, context))
        {
            var deepResult = await _deep.Decide(state, context, personality).ConfigureAwait(false);
            return deepResult
                   ?? fastAction
                   ?? new DecisionResult { Action = DecisionAction.Wait, Reason = "fallback" };
        }

        return fastAction ?? new DecisionResult { Action = DecisionAction.Wait, Reason = "no_action" };
    }

    /// <summary>
    /// 判断当前是否需要触发深度推理。
    /// 仅在"人生节点"触发：升级/死亡/稀有掉落/复杂社交。
    /// </summary>
    private bool ShouldDeepReason(WorldState state, PerceptContext context)
    {
        // 仅在 10% 的关键时刻触发深度推理
        // 预留：检查最近是否有死亡/升级/稀有掉落等事件
        return false; // 当前 Phase 尚不启用 LLM
    }

    /// <summary>
    /// 记录行为执行结果（用于惯性系统学习）。
    /// </summary>
    public void RecordActionResult(string actionName, bool success, WorldState state)
    {
        _inertia.Record(actionName, success, state);
    }
}

/// <summary>快速决策引擎 — 生存规则 + 行为惯性 + 效用。</summary>
public sealed class FastDecisionEngine
{
    /// <summary>
    /// 快速决策，延迟 <1ms。
    /// </summary>
    public DecisionResult? Decide(WorldState state, PersonalityEngine personality)
    {
        // 机制 1：生存规则引擎（绝对优先级）
        var survival = EvaluateSurvival(state, personality);
        if (survival is not null) return survival;

        // 其他快速规则...
        return null;
    }

    private static DecisionResult? EvaluateSurvival(WorldState state, PersonalityEngine personality)
    {
        var self = state.Self;
        if (self.MaxHP <= 0) return null;

        var hpPct = (float)self.HP / self.MaxHP;

        // 紧急逃生：HP < 5%
        if (hpPct < 0.05f)
        {
            return new DecisionResult
            {
                Action = DecisionAction.Flee,
                Priority = 1,
                Reason = $"HP危急({hpPct:P0})，紧急逃生",
            };
        }

        // 喝药：HP < 20% 且有药
        if (hpPct < 0.20f)
        {
            return new DecisionResult
            {
                Action = DecisionAction.UseHealthPotion,
                Priority = 2,
                Reason = $"HP低({hpPct:P0})，自动喝红",
            };
        }

        return null;
    }
}

/// <summary>深度决策引擎 — LLM 推理（预留接口）。</summary>
public sealed class DeepDecisionEngine
{
    /// <summary>
    /// LLM 深度推理决策。
    /// </summary>
    public ValueTask<DecisionResult?> Decide(WorldState state, PerceptContext context, PersonalityEngine personality)
    {
        // 预留：调用 LLM 进行深度推理
        // 当前 Phase 返回 null（降级到快速轨道）
        return ValueTask.FromResult<DecisionResult?>(null);
    }
}

/// <summary>行为惯性引擎 — 重复成功固化为习惯。</summary>
public sealed class BehaviorInertiaEngine
{
    private readonly Dictionary<string, HabitRecord> _habits = new();

    /// <summary>习惯成形所需成功次数。</summary>
    private const int HabitThreshold = 3;

    /// <summary>习惯强度衰减时间（分钟）。</summary>
    private const double HabitDecayMinutes = 60.0;

    /// <summary>
    /// 尝试获取习惯行为。如果当前环境匹配已有习惯，直接返回。
    /// </summary>
    public DecisionResult? TryHabit(WorldState state)
    {
        var now = DateTime.UtcNow;

        foreach (var kvp in _habits)
        {
            var habit = kvp.Value;

            // 习惯超时衰减
            if ((now - habit.LastPerformed).TotalMinutes > HabitDecayMinutes)
            {
                habit.Strength = Math.Max(0, habit.Strength - 0.2f);
                continue;
            }

            // 强习惯（强度 > 0.6）在匹配环境下直接执行
            if (habit.Strength > 0.6f && IsEnvironmentMatch(habit, state))
            {
                return new DecisionResult
                {
                    Action = habit.Action,
                    Priority = 3,
                    Reason = $"习惯: {kvp.Key}",
                    IsHabit = true,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 记录一次行为执行结果。连续成功累计习惯强度。
    /// </summary>
    public void Record(string actionName, bool success, WorldState state)
    {
        if (!_habits.TryGetValue(actionName, out var habit))
        {
            habit = new HabitRecord { Action = MapToAction(actionName) };
            _habits[actionName] = habit;
        }

        if (success)
        {
            habit.SuccessCount++;
            habit.Strength = Math.Min(1.0f, habit.SuccessCount / (float)HabitThreshold);

            // 记录环境上下文
            habit.LastMap = state.Self.X;
            habit.LastPosX = state.Self.X;
            habit.LastPosY = state.Self.Y;
        }
        else
        {
            habit.SuccessCount = Math.Max(0, habit.SuccessCount - 1);
            habit.Strength = Math.Max(0, habit.Strength - 0.1f);
        }

        habit.LastPerformed = DateTime.UtcNow;
    }

    private static bool IsEnvironmentMatch(HabitRecord habit, WorldState state)
    {
        if (habit.LastMap != state.Self.X) return false; // 简化：精确匹配地图
        return true;
    }

    private static DecisionAction MapToAction(string actionName) => actionName switch
    {
        "use_hp_potion" => DecisionAction.UseHealthPotion,
        "use_mp_potion" => DecisionAction.UseManaPotion,
        "attack_target" => DecisionAction.Attack,
        "pickup_item" => DecisionAction.Pickup,
        "flee" => DecisionAction.Flee,
        "move_to" => DecisionAction.Move,
        _ => DecisionAction.Wait,
    };
}

/// <summary>习惯记录。</summary>
public sealed class HabitRecord
{
    public DecisionAction Action { get; init; }
    public int SuccessCount { get; set; }
    public float Strength { get; set; }
    public int LastMap { get; set; }
    public byte LastPosX { get; set; }
    public byte LastPosY { get; set; }
    public DateTime LastPerformed { get; set; } = DateTime.UtcNow;
}

/// <summary>决策结果。</summary>
public record DecisionResult
{
    /// <summary>决策动作。</summary>
    public DecisionAction Action { get; init; }

    /// <summary>优先级（越小越优先）。</summary>
    public int Priority { get; init; } = 999;

    /// <summary>决策理由（用于调试/日志）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>是否由习惯引擎产生。</summary>
    public bool IsHabit { get; init; }
}

/// <summary>决策动作枚举。</summary>
public enum DecisionAction
{
    /// <summary>等待/无操作</summary>
    Wait,

    /// <summary>攻击目标</summary>
    Attack,

    /// <summary>使用技能攻击</summary>
    SkillAttack,

    /// <summary>逃跑</summary>
    Flee,

    /// <summary>使用HP药水</summary>
    UseHealthPotion,

    /// <summary>使用MP药水</summary>
    UseManaPotion,

    /// <summary>拾取物品</summary>
    Pickup,

    /// <summary>移动到坐标</summary>
    Move,

    /// <summary>与NPC交互</summary>
    Interact,

    /// <summary>巡逻</summary>
    Patrol,
}
