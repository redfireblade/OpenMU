// <copyright file="PersonalityEngine.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

/// <summary>
/// 8 维个性引擎 — OAPS Layer 5 心智引擎核心组件。
/// 经验驱动个性演化：频繁死亡增加谨慎、连续击杀增加攻击性。
/// 结合 OCC 情感模型（喜悦/恐惧/愤怒/悲伤）影响决策权重。
/// 参考 OAPS v3.0 6.4。
/// </summary>
public sealed class PersonalityEngine
{
    /// <summary>8 维个性配置。</summary>
    public PersonalityProfile Profile { get; private set; } = new();

    /// <summary>当前情感状态。</summary>
    public EmotionState Emotion { get; private set; } = new();

    /// <summary>
    /// 根据经历更新个性与情感。
    /// </summary>
    public void Evolve(ExperienceEvent experience)
    {
        // 个性演化
        Profile.Evolve(experience);

        // 情感更新（OCC 模型）
        Emotion.Update(experience);

        // 情感自然衰减
        Emotion.Decay();
    }

    /// <summary>
    /// 用当前个性和情感调制决策候选的权重。
    /// 个性提供基础偏好，情感提供短期偏移。
    /// </summary>
    public float Modulate(float baseWeight, DecisionType decisionType)
    {
        var weight = baseWeight;

        // 个性调制
        weight = decisionType switch
        {
            DecisionType.Fight => weight * (1.0f + Profile.Aggression * 0.3f - Profile.Caution * 0.2f),
            DecisionType.Flee => weight * (1.0f + Profile.Caution * 0.4f - Profile.Aggression * 0.2f),
            DecisionType.Pickup => weight * (1.0f + Profile.Greed * 0.3f),
            DecisionType.Social => weight * (1.0f + Profile.Sociability * 0.3f),
            DecisionType.Explore => weight * (1.0f + Profile.Curiosity * 0.3f - Profile.Caution * 0.2f),
            DecisionType.Risk => weight * (1.0f + Profile.RiskTolerance * 0.3f),
            DecisionType.SkillUse => weight * (1.0f + Profile.Efficiency * 0.2f),
            DecisionType.Retreat => weight * (1.0f + Profile.Loyalty * 0.2f + Profile.Caution * 0.3f),
            _ => weight,
        };

        // 情感调制（短期偏移）
        weight = Emotion.EmotionalState switch
        {
            EmotionalState.Anger => weight * 1.2f,   // 愤怒 → 更激进
            EmotionalState.Fear => weight * 0.7f,     // 恐惧 → 更保守
            EmotionalState.Joy => weight * 1.1f,      // 喜悦 → 略积极
            EmotionalState.Sadness => weight * 0.8f,  // 悲伤 → 消沉
            _ => weight,
        };

        return weight;
    }
}

/// <summary>8 维个性配置。</summary>
public sealed class PersonalityProfile
{
    /// <summary>攻击性 — 倾向于主动攻击 vs 被动防御。</summary>
    public float Aggression { get; set; } = 0.5f;

    /// <summary>谨慎性 — 避免危险 vs 冒险。</summary>
    public float Caution { get; set; } = 0.5f;

    /// <summary>贪婪 — 拾取优先级 vs 安全第一。</summary>
    public float Greed { get; set; } = 0.5f;

    /// <summary>社交性 — 倾向于组队/帮助 vs 独行。</summary>
    public float Sociability { get; set; } = 0.3f;

    /// <summary>效率倾向 — 追求最优路线 vs 随意探索。</summary>
    public float Efficiency { get; set; } = 0.6f;

    /// <summary>风险承受 — 面对强敌时的战斗/逃跑倾向。</summary>
    public float RiskTolerance { get; set; } = 0.4f;

    /// <summary>好奇心 — 探索未知区域的欲望。</summary>
    public float Curiosity { get; set; } = 0.5f;

    /// <summary>忠诚度 — 组队后的稳定性，不离队。</summary>
    public float Loyalty { get; set; } = 0.5f;

    /// <summary>
    /// 根据经历演化个性。
    /// </summary>
    public void Evolve(ExperienceEvent experience)
    {
        if (experience.IsDeath)
        {
            Caution = Lerp(Caution, 1.0f, 0.08f);
            Aggression = Lerp(Aggression, 0.0f, 0.04f);
            RiskTolerance = Lerp(RiskTolerance, 0.0f, 0.06f);
        }

        if (experience.IsOverlevelKill && experience.IsSuccess)
        {
            Aggression = Lerp(Aggression, 1.0f, 0.06f);
            RiskTolerance = Lerp(RiskTolerance, 1.0f, 0.04f);
        }

        if (experience.IsRareDrop)
        {
            Greed = Lerp(Greed, 1.0f, 0.05f);
            Curiosity = Lerp(Curiosity, 1.0f, 0.03f);
        }

        if (experience.IsGroupSuccess)
        {
            Sociability = Lerp(Sociability, 1.0f, 0.06f);
            Loyalty = Lerp(Loyalty, 1.0f, 0.04f);
        }

        if (experience.IsDeathByPlayer)
        {
            Caution = Lerp(Caution, 1.0f, 0.12f);
            Sociability = Lerp(Sociability, 0.0f, 0.05f);
        }
    }

    private static float Lerp(float from, float to, float t) =>
        from + (to - from) * Math.Clamp(t, 0, 1);
}

/// <summary>OCC 情感状态。</summary>
public sealed class EmotionState
{
    /// <summary>当前主导情感。</summary>
    public EmotionalState EmotionalState { get; private set; } = EmotionalState.Neutral;

    /// <summary>喜悦度 0~1。</summary>
    public float Joy { get; set; }

    /// <summary>恐惧度 0~1。</summary>
    public float Fear { get; set; }

    /// <summary>愤怒度 0~1。</summary>
    public float Anger { get; set; }

    /// <summary>悲伤度 0~1。</summary>
    public float Sadness { get; set; }

    /// <summary>
    /// 根据经历更新情感（OCC 模型）。
    /// </summary>
    public void Update(ExperienceEvent exp)
    {
        if (exp.IsDeath)
        {
            Sadness = Clamp(Sadness + 0.3f);
            Fear = Clamp(Fear + 0.25f);
        }

        if (exp.IsRareDrop) Joy = Clamp(Joy + 0.4f);

        if (exp.IsOverlevelKill && exp.IsSuccess) Joy = Clamp(Joy + 0.2f);

        if (exp.IsDeathByPlayer) Anger = Clamp(Anger + 0.35f);

        if (exp.IsLevelUp) Joy = Clamp(Joy + 0.3f);

        // 更新主导情感
        EmotionalState = UpdateDominantEmotion();
    }

    /// <summary>
    /// 自然衰减。
    /// </summary>
    public void Decay()
    {
        Joy *= 0.97f;
        Fear *= 0.95f;
        Anger *= 0.95f;
        Sadness *= 0.97f;
    }

    private EmotionalState UpdateDominantEmotion()
    {
        if (Fear > 0.6f) return EmotionalState.Fear;
        if (Anger > 0.6f) return EmotionalState.Anger;
        if (Joy > 0.6f) return EmotionalState.Joy;
        if (Sadness > 0.6f) return EmotionalState.Sadness;
        return EmotionalState.Neutral;
    }

    private static float Clamp(float v) => Math.Clamp(v, 0, 1);
}

/// <summary>情感状态枚举（OCC 模型）。</summary>
public enum EmotionalState
{
    /// <summary>中立。</summary>
    Neutral,

    /// <summary>喜悦。</summary>
    Joy,

    /// <summary>恐惧。</summary>
    Fear,

    /// <summary>愤怒。</summary>
    Anger,

    /// <summary>悲伤。</summary>
    Sadness,
}

/// <summary>经历事件 — 驱动个性与情感演化的输入。</summary>
public record ExperienceEvent
{
    /// <summary>是否死亡。</summary>
    public bool IsDeath { get; init; }

    /// <summary>是否被玩家杀死。</summary>
    public bool IsDeathByPlayer { get; init; }

    /// <summary>是否击杀高等级怪物。</summary>
    public bool IsOverlevelKill { get; init; }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>是否稀有掉落。</summary>
    public bool IsRareDrop { get; init; }

    /// <summary>是否组队成功。</summary>
    public bool IsGroupSuccess { get; init; }

    /// <summary>是否升级。</summary>
    public bool IsLevelUp { get; init; }

    /// <summary>事件时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>决策类型。</summary>
public enum DecisionType
{
    /// <summary>战斗</summary>
    Fight,

    /// <summary>逃跑</summary>
    Flee,

    /// <summary>拾取</summary>
    Pickup,

    /// <summary>社交</summary>
    Social,

    /// <summary>探索</summary>
    Explore,

    /// <summary>冒险决策</summary>
    Risk,

    /// <summary>使用技能</summary>
    SkillUse,

    /// <summary>撤退/回城</summary>
    Retreat,
}
