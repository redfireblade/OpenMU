// <copyright file="BuildDirection.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// AI角色发展方向 — 由AI群体决策系统在创建时设定，AI角色也可基于经验自主调整。
/// 记录在 AI_BuildDirection 表中，供其他AI查询了解。
/// </summary>
public enum BuildDirection
{
    // ═══════ 黑暗巫师 DW (0) / 神导师 SM (2) / 大导师 GM (3) ═══════
    /// <summary>智力法师(刷怪型)。力量够穿装备，敏捷600-800，体力少量，智力其余全加。</summary>
    IntWizard,

    /// <summary>血法(PK型)。力量够穿装备，敏捷300，体力剩余全加，智力尾数1最佳(1601/1801)。</summary>
    ManaWizard,

    /// <summary>智法(高攻速型)。力量300，敏捷1000+，体力不加，智力其余。</summary>
    SpeedWizard,

    // ═══════ 黑暗骑士 DK (4) / 骑士 BK (6) / 骑士 BM (7) ═══════
    /// <summary>平衡型战士(主流)。力量1200-1400，敏捷250-350，体力500-700，智力20-200。</summary>
    BalancedKnight,

    /// <summary>力血战士(PK)。力量1200-1600，敏捷不加，体力600-900，智力20-200。</summary>
    ForceKnight,

    /// <summary>血牛战士。力量300-600，敏捷不加，体力800-1200，智力10-200。</summary>
    BloodKnight,

    /// <summary>高智战士(连击型)。力量300-600，敏捷不加，体力300-500，智力600-1200。</summary>
    SmartKnight,

    /// <summary>敏捷战士。力量600-1000，敏捷600-1200，体力400-600，智力20-120。</summary>
    SpeedKnight,

    // ═══════ 精灵 FE (8) / 圣射手 ME (10) / 射手 HE (11) ═══════
    /// <summary>敏弓(物理输出)。力量够穿装备，敏捷其余全加，智力体力不加。</summary>
    AgilityElf,

    /// <summary>智弓(纯辅助)。力量够穿装备，敏捷够穿装备，智力其余全加。</summary>
    IntElf,

    /// <summary>敏战弓(混合型)。力量300，敏捷1500，体力300，智力800。</summary>
    HybridElf,

    // ═══════ 魔剑士 MG (12) / 双倍大师 DM (13) ═══════
    /// <summary>力魔(物理输出)。力量主加，敏捷800-2000，体力少量，智力不加。</summary>
    ForceMG,

    /// <summary>法魔(魔法输出)。力量够穿装备，敏捷视情况，体力不加，智力其余全加。</summary>
    IntMG,

    // ═══════ 圣导师 DL (16) / 君主 LE (17) ═══════
    /// <summary>力智双加流(平民首选)。力30% 智40% 敏20% 体10%。</summary>
    BalancedDL,

    /// <summary>智力主导流(团队辅助)。智60% 敏20% 体15% 力5%。</summary>
    IntDL,

    /// <summary>敏捷输出流(后期爆发)。敏50% 智25% 力20% 体5%。</summary>
    AgilityDL,

    /// <summary>力敏圣(打手型)。力量1000-1500，敏捷其余全加。</summary>
    ForceDL,

    // ═══════ 召唤术士 SUM (20) / 血召唤 BS (22) / 次元大师 DM (23) ═══════
    /// <summary>打手型召唤。力量够穿装备，敏捷200-1000，体力少量，智力其余全加。</summary>
    IntSummoner,

    /// <summary>辅助型召唤。力量够穿装备，敏捷够穿装备，智力其余全加。</summary>
    SupportSummoner,

    // ═══════ 格斗家 RF (24) / 拳王 FM (25) ═══════
    /// <summary>敏格(输出)。力量够穿装备，体力够穿装备，敏捷其余全加。</summary>
    AgilityFighter,
}
