// <copyright file="CognitiveLoop.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using OAPS.AccessLayer;
using OAPS.World;

/// <summary>
/// 认知循环 — OAPS Layer 5 心智引擎主控器。
/// 整合感知 → 记忆 → 情感 → 个性 → 决策 的完整循环。
/// 每 400ms 由 HeartbeatService 调用一次。
/// 参考 OAPS v3.0 6.1。
/// </summary>
public sealed class CognitiveLoop
{
    private readonly IWorldSensor _sensor;
    private readonly MemorySystem _memory;
    private readonly PersonalityEngine _personality;
    private readonly DecisionCore _decision;

    /// <summary>上次决策的上下文，用于本次对比。</summary>
    private PerceptContext? _lastContext;

    /// <summary>
    /// 初始化认知循环。
    /// </summary>
    public CognitiveLoop(IWorldSensor sensor, MemorySystem memory,
        PersonalityEngine personality, DecisionCore decision)
    {
        _sensor = sensor;
        _memory = memory;
        _personality = personality;
        _decision = decision;
    }

    /// <summary>
    /// 获取当前个性配置（只读）。
    /// </summary>
    public PersonalityProfile PersonalityProfile => _personality.Profile;

    /// <summary>
    /// 获取当前情感状态（只读）。
    /// </summary>
    public EmotionState CurrentEmotion => _personality.Emotion;

    /// <summary>
    /// 执行一次认知循环：感知 → 记忆检索 → 情感着色 → 个性调制 → 决策。
    /// </summary>
    public async ValueTask<DecisionResult> ThinkAsync(CancellationToken ct = default)
    {
        // 1. 感知：获取世界状态
        var state = await _sensor.CaptureAsync(ct).ConfigureAwait(false);

        // 2. 构建感知上下文
        var context = BuildContext(state);

        // 3. 记忆检索（自动在 MemorySystem 内部完成三层检索）
        var memories = _memory.Retrieve(context);
        _lastContext = context;

        // 4. 记录感觉记忆（当前视野内的怪物密度等信息）
        RecordSensoryFromState(state);

        // 5. 情感着色 + 个性调制（在 DecisionCore 内部完成）
        // 6. 双轨决策
        var result = await _decision.Decide(state, context, _personality).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// 记录一次经历事件到记忆和个性系统。
    /// </summary>
    public void RecordExperience(ExperienceEvent experience)
    {
        // 个性演化
        _personality.Evolve(experience);

        // 长期记忆
        if (experience.IsDeath || experience.IsRareDrop || experience.IsLevelUp)
        {
            var importance = experience.IsDeath ? 0.9f
                : experience.IsRareDrop ? 0.8f
                : experience.IsLevelUp ? 0.6f : 0.3f;

            _memory.RecordLongTerm(new LongTermRecord
            {
                Summary = BuildExperienceSummary(experience),
                Importance = importance,
                Strength = importance,
                Tags = new HashSet<string> { BuildExperienceTag(experience) },
                Timestamp = experience.Timestamp,
            });
        }
    }

    /// <summary>
    /// 记录行为执行结果（用于惯性系统）。
    /// </summary>
    public void RecordAction(string actionName, bool success)
    {
        _decision.RecordActionResult(actionName, success,
            new WorldState
            {
                Self = new PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0),
                LastUpdate = DateTime.UtcNow,
            });
    }

    private static PerceptContext BuildContext(WorldState state)
    {
        var keywords = new HashSet<string>();

        // 从当前世界状态提取关键词
        keywords.Add($"map_{state.Self.Level}");

        if (state.Monsters.Count > 0) keywords.Add("has_monsters");
        if (state.Items.Count > 0) keywords.Add("has_items");

        return new PerceptContext
        {
            Keywords = keywords,
            CurrentMap = state.Self.X,
        };
    }

    private void RecordSensoryFromState(WorldState state)
    {
        // 记录视野内的怪物密度
        _memory.RecordSensory(new SensoryRecord
        {
            Type = SensoryType.MonsterDensity,
            Value = state.Monsters.Count,
            Timestamp = DateTime.UtcNow,
        });

        // 记录周围掉落
        foreach (var item in state.Items.Values)
        {
            _memory.RecordSensory(new SensoryRecord
            {
                Type = SensoryType.ItemDrop,
                EntityId = item.Id,
                X = item.X,
                Y = item.Y,
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    private static string BuildExperienceSummary(ExperienceEvent exp)
    {
        if (exp.IsDeath) return exp.IsDeathByPlayer ? "被玩家击杀" : "死亡";
        if (exp.IsRareDrop) return "获得稀有掉落";
        if (exp.IsLevelUp) return "升级";
        if (exp.IsOverlevelKill) return "击杀高等级怪物";
        return "未知事件";
    }

    private static string BuildExperienceTag(ExperienceEvent exp)
    {
        if (exp.IsDeath) return "death";
        if (exp.IsRareDrop) return "rare_drop";
        if (exp.IsLevelUp) return "level_up";
        return "event";
    }
}
