// <copyright file="StatAllocationStrategy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Generic;
using System.Linq;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Strategy for allocating stat points when AI characters level up.
/// Provides build data organized by class and development direction.
/// Supports per-character build direction selection for flexible stat allocation.
/// </summary>
public static class StatAllocationStrategy
{
    /// <summary>
    /// All build definitions (single source of truth). Must be declared FIRST
    /// because BuildsByClass and ClassBuilds reference it in their initializers.
    /// </summary>
    private static readonly BuildDirectionInfo[] AllBuilds = GetAllBuilds();

    /// <summary>
    /// Gets all defined builds organized by base class number.
    /// </summary>
    public static Dictionary<int, List<BuildDirectionInfo>> BuildsByClass { get; }
        = InitializeBuilds();

    /// <summary>
    /// Gets class builds as phase tuples (backward-compatible with existing AiPlayerLogic).
    /// Uses the default (first) build direction for each class.
    /// </summary>
    public static Dictionary<int, (int MinLevel, int MaxLevel, float Str, float Agi, float Vit, float Ene)[]> ClassBuilds { get; }
        = InitializeClassBuilds();

    /// <summary>
    /// Gets the base class number from a specific class number, ignoring advancement/transcendence.
    /// </summary>
    public static int GetBaseClass(int classNumber) => classNumber switch
    {
        >= 0 and < 4 => 0,   // DW/SM/GM → DarkWizard
        >= 4 and < 8 => 4,   // DK/BK/BM → DarkKnight
        >= 8 and < 12 => 8,  // FE/ME/HE → FairyElf
        >= 12 and < 16 => 12, // MG/DM → MagicGladiator
        >= 16 and < 20 => 16, // DL/LE → DarkLord
        >= 20 and < 24 => 20, // SUM/BS/DM → Summoner
        >= 24 => 24,          // RF/FM → RageFighter
        _ => 0,
    };

    /// <summary>
    /// Gets the default build direction for a given character class number.
    /// </summary>
    public static BuildDirection GetDefaultDirection(int classNumber)
    {
        var baseClass = GetBaseClass(classNumber);
        return BuildsByClass.TryGetValue(baseClass, out var builds) && builds.Count > 0
            ? builds[0].Direction
            : BuildDirection.IntWizard;
    }

    /// <summary>
    /// Gets the appropriate build phase for a given direction and character level.
    /// Falls back to nearest phase if exact match not found.
    /// </summary>
    /// <returns>The matching phase, or null if no builds found for the direction.</returns>
    public static BuildPhase? GetPhase(BuildDirection direction, int level)
    {
        var info = AllBuilds.FirstOrDefault(b => b.Direction == direction);
        if (info is null)
        {
            return null;
        }

        // Exact phase match
        var exact = info.Phases.FirstOrDefault(p => level >= p.MinLevel && level <= p.MaxLevel);
        if (exact is not null)
        {
            return exact;
        }

        // If below any phase range, return the first (lowest) phase
        var below = info.Phases.FirstOrDefault(p => level <= p.MinLevel);
        if (below is not null)
        {
            return below;
        }

        // If above all phases, return the last (highest) phase
        return info.Phases.LastOrDefault();
    }

    /// <summary>
    /// Gets all build definitions with full phase data.
    /// </summary>
    private static BuildDirectionInfo[] GetAllBuilds()
    {
        return new BuildDirectionInfo[]
        {
            // ════════════════════════════════════════════════════════════════
            // 黑暗巫师 (0) / 神导师 (2) / 大导师 (3) — 基础职业0
            // 敏智均衡型(单刷/生存主流): 玩家经验目标 Lv10敏25智40 → Lv50敏75智200
            // 初始: 力18 敏18 体15 能30 | 每级5点
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.IntWizard,
                "敏智法师(单刷主流)",
                0,
                "敏智均衡型(单刷/生存) — 敏捷够拿武器，智力其余全加，不加体力",
                new BuildPhase[]
                {
                    new BuildPhase(1, 10, 0.05f, 0.14f, 0.05f, 0.76f, "Lv10目标敏25智40"),
                    new BuildPhase(11, 20, 0.03f, 0.20f, 0.03f, 0.74f, "Lv20目标敏35智70"),
                    new BuildPhase(21, 30, 0.03f, 0.20f, 0.02f, 0.75f, "Lv30目标敏45智110"),
                    new BuildPhase(31, 40, 0.03f, 0.30f, 0.02f, 0.65f, "Lv40目标敏60智150"),
                    new BuildPhase(41, 50, 0.03f, 0.30f, 0.02f, 0.65f, "Lv50目标敏75智200"),
                    new BuildPhase(51, 80, 0.03f, 0.20f, 0.02f, 0.75f, "继续敏捷+智力"),
                    new BuildPhase(81, 200, 0.02f, 0.15f, 0.03f, 0.80f, "主力加智力"),
                    new BuildPhase(201, 400, 0.02f, 0.10f, 0.03f, 0.85f, "极限智力"),
                    new BuildPhase(401, int.MaxValue, 0.02f, 0.10f, 0.05f, 0.83f, "少许体力防秒杀"),
                }),
            new BuildDirectionInfo(
                BuildDirection.ManaWizard,
                "血法",
                0,
                "血法(PK型) — 力量够穿装备，敏捷300，体力剩余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.10f, 0.20f, 0.30f, 0.40f, "初期均衡"),
                    new BuildPhase(51, 80, 0.05f, 0.15f, 0.35f, 0.45f, "开始堆血"),
                    new BuildPhase(81, 200, 0.03f, 0.10f, 0.42f, 0.45f, "体力智力均衡"),
                    new BuildPhase(201, 400, 0.02f, 0.08f, 0.45f, 0.45f, "更多体力"),
                    new BuildPhase(401, int.MaxValue, 0.02f, 0.05f, 0.48f, 0.45f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.SpeedWizard,
                "智法",
                0,
                "智法(高攻速型) — 力量300，敏捷1000+，体力不加，智力其余",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.10f, 0.30f, 0.02f, 0.58f, "先补敏捷"),
                    new BuildPhase(51, 80, 0.05f, 0.30f, 0.02f, 0.63f, "继续敏捷"),
                    new BuildPhase(81, 200, 0.03f, 0.28f, 0.02f, 0.67f, "敏捷800+后主智力"),
                    new BuildPhase(201, 400, 0.02f, 0.20f, 0.02f, 0.76f, "高攻速输出"),
                    new BuildPhase(401, int.MaxValue, 0.02f, 0.18f, 0.02f, 0.78f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 黑暗骑士 (4) / 骑士 (6) / 骑士 (7) — 基础职业4
            // 力敏均衡型(90%玩家主流): 玩家经验 Lv10力45敏25 → Lv50力210敏70
            // 初始: 力28 敏20 体25 能10 | 每级5点
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.BalancedKnight,
                "力敏均衡战士(主流)",
                4,
                "力敏均衡型(90%玩家主流) — 力量够武器，敏捷够防具，剩余全加体力",
                new BuildPhase[]
                {
                    new BuildPhase(1, 10, 0.34f, 0.10f, 0.56f, 0.00f, "Lv10目标力45敏25"),
                    new BuildPhase(11, 20, 0.70f, 0.30f, 0.00f, 0.00f, "Lv20目标力80敏40"),
                    new BuildPhase(21, 30, 1.00f, 0.00f, 0.00f, 0.00f, "Lv30目标力130敏50(+任务点)"),
                    new BuildPhase(31, 40, 0.70f, 0.20f, 0.10f, 0.00f, "Lv40目标力165敏60"),
                    new BuildPhase(41, 50, 0.90f, 0.20f, 0.00f, 0.00f, "Lv50目标力210敏70(+任务点)"),
                    new BuildPhase(51, 80, 0.50f, 0.20f, 0.30f, 0.00f, "开始加体"),
                    new BuildPhase(81, 200, 0.35f, 0.15f, 0.40f, 0.10f, "均衡力敏体"),
                    new BuildPhase(201, 400, 0.30f, 0.15f, 0.40f, 0.15f, "加智力支持技能"),
                    new BuildPhase(401, int.MaxValue, 0.28f, 0.15f, 0.40f, 0.17f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.ForceKnight,
                "力血战士",
                4,
                "力血战士(PK) — 力量1200-1600，敏捷不加，体力600-900",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.80f, 0.10f, 0.10f, 0.00f, "全力穿装备"),
                    new BuildPhase(51, 80, 0.60f, 0.05f, 0.35f, 0.00f, "开始加体"),
                    new BuildPhase(81, 200, 0.45f, 0.03f, 0.47f, 0.05f, "力体均衡"),
                    new BuildPhase(201, 400, 0.40f, 0.03f, 0.50f, 0.07f, "继续堆体力"),
                    new BuildPhase(401, int.MaxValue, 0.35f, 0.03f, 0.55f, 0.07f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.BloodKnight,
                "血牛战士",
                4,
                "血牛战士 — 力量300-600，敏捷不加，体力800-1200",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.50f, 0.05f, 0.45f, 0.00f, "初期均衡"),
                    new BuildPhase(51, 80, 0.30f, 0.03f, 0.67f, 0.00f, "主加体力"),
                    new BuildPhase(81, 200, 0.20f, 0.03f, 0.72f, 0.05f, "极限体力"),
                    new BuildPhase(201, 400, 0.15f, 0.02f, 0.78f, 0.05f, string.Empty),
                    new BuildPhase(401, int.MaxValue, 0.12f, 0.02f, 0.81f, 0.05f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.SmartKnight,
                "高智战士(连击型)",
                4,
                "高智战士(连击型) — 力量300-600，体力300-500，智力600-1200",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.55f, 0.05f, 0.20f, 0.20f, "初期力智均衡"),
                    new BuildPhase(51, 80, 0.35f, 0.03f, 0.20f, 0.42f, "开始堆智力"),
                    new BuildPhase(81, 200, 0.20f, 0.03f, 0.20f, 0.57f, "主加智力"),
                    new BuildPhase(201, 400, 0.15f, 0.02f, 0.18f, 0.65f, string.Empty),
                    new BuildPhase(401, int.MaxValue, 0.12f, 0.02f, 0.21f, 0.65f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.SpeedKnight,
                "敏捷战士",
                4,
                "敏捷战士 — 力量600-1000，敏捷600-1200，体力400-600",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.40f, 0.40f, 0.20f, 0.00f, "初期力敏均衡"),
                    new BuildPhase(51, 80, 0.30f, 0.45f, 0.25f, 0.00f, "主力敏"),
                    new BuildPhase(81, 200, 0.25f, 0.45f, 0.28f, 0.02f, "继续堆敏捷"),
                    new BuildPhase(201, 400, 0.22f, 0.48f, 0.28f, 0.02f, string.Empty),
                    new BuildPhase(401, int.MaxValue, 0.20f, 0.50f, 0.28f, 0.02f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 精灵 (8) / 圣射手 (10) / 射手 (11) — 基础职业8
            // 敏弓(绝对主流): 玩家经验 Lv10力25敏40 → Lv50力65敏240
            // 初始: 力22 敏25 体20 能15 | 每级5点
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.AgilityElf,
                "敏弓(绝对主流)",
                8,
                "敏弓(物理输出) — 力量够拿弓，敏捷其余全加，智力体力不加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 10, 0.06f, 0.30f, 0.32f, 0.32f, "Lv10目标力25敏40(+任务点)"),
                    new BuildPhase(11, 20, 0.20f, 0.80f, 0.00f, 0.00f, "Lv20目标力35敏80"),
                    new BuildPhase(21, 30, 0.20f, 0.80f, 0.00f, 0.00f, "Lv30目标力45敏130"),
                    new BuildPhase(31, 40, 0.20f, 0.80f, 0.00f, 0.00f, "Lv40目标力55敏180"),
                    new BuildPhase(41, 50, 0.20f, 0.80f, 0.00f, 0.00f, "Lv50目标力65敏240(+任务点)"),
                    new BuildPhase(51, 80, 0.08f, 0.90f, 0.02f, 0.00f, "主加敏捷"),
                    new BuildPhase(81, 200, 0.05f, 0.92f, 0.03f, 0.00f, "极限敏捷"),
                    new BuildPhase(201, 400, 0.05f, 0.92f, 0.02f, 0.01f, "全敏"),
                    new BuildPhase(401, int.MaxValue, 0.05f, 0.88f, 0.05f, 0.02f, "少许体力"),
                }),
            new BuildDirectionInfo(
                BuildDirection.IntElf,
                "智弓(辅助)",
                8,
                "智弓(纯辅助) — 力量够穿装备，敏捷够穿装备，智力其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.15f, 0.15f, 0.05f, 0.65f, "Lv50目标智力240"),
                    new BuildPhase(51, 80, 0.08f, 0.12f, 0.03f, 0.77f, "专注智力"),
                    new BuildPhase(81, 200, 0.05f, 0.10f, 0.03f, 0.82f, "极限智力"),
                    new BuildPhase(201, 400, 0.05f, 0.08f, 0.03f, 0.84f, string.Empty),
                    new BuildPhase(401, int.MaxValue, 0.05f, 0.08f, 0.05f, 0.82f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.HybridElf,
                "敏战弓(混合型)",
                8,
                "敏战弓(混合型) — 力量300，敏捷1500，体力300，智力800",
                new BuildPhase[]
                {
                    new BuildPhase(1, 50, 0.20f, 0.55f, 0.05f, 0.20f, "初期敏为主"),
                    new BuildPhase(51, 80, 0.12f, 0.55f, 0.08f, 0.25f, "提升敏捷和智力"),
                    new BuildPhase(81, 200, 0.08f, 0.48f, 0.08f, 0.36f, "混合发展"),
                    new BuildPhase(201, 400, 0.08f, 0.42f, 0.08f, 0.42f, string.Empty),
                    new BuildPhase(401, int.MaxValue, 0.08f, 0.40f, 0.10f, 0.42f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 魔剑士 (12) / 双倍大师 (13) — 基础职业12
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.ForceMG,
                "力魔",
                12,
                "力魔(物理输出) — 力量主加，敏捷800-2000，体力少量，智力不加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.50f, 0.40f, 0.08f, 0.02f, "力量和敏捷均衡"),
                    new BuildPhase(81, 200, 0.55f, 0.35f, 0.08f, 0.02f, "力量为主"),
                    new BuildPhase(201, 400, 0.58f, 0.30f, 0.10f, 0.02f, "继续堆力量"),
                    new BuildPhase(401, int.MaxValue, 0.55f, 0.30f, 0.13f, 0.02f, "稍加体力"),
                }),
            new BuildDirectionInfo(
                BuildDirection.IntMG,
                "法魔",
                12,
                "法魔(魔法输出) — 力量够穿装备，敏捷视情况，体力不加，智力其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.25f, 0.25f, 0.05f, 0.45f, "先满足装备"),
                    new BuildPhase(81, 200, 0.10f, 0.15f, 0.03f, 0.72f, "主加智力"),
                    new BuildPhase(201, 400, 0.05f, 0.15f, 0.03f, 0.77f, "极限智力"),
                    new BuildPhase(401, int.MaxValue, 0.05f, 0.13f, 0.05f, 0.77f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 圣导师 (16) / 君主 (17) — 基础职业16
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.BalancedDL,
                "力智圣",
                16,
                "力智双加流(平民首选) — 力30% 智40% 敏20% 体10%",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.30f, 0.20f, 0.10f, 0.40f, "力智均衡"),
                    new BuildPhase(81, 200, 0.30f, 0.20f, 0.10f, 0.40f, "保持一致比例"),
                    new BuildPhase(201, 400, 0.28f, 0.20f, 0.12f, 0.40f, "稍微加体"),
                    new BuildPhase(401, int.MaxValue, 0.25f, 0.20f, 0.15f, 0.40f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.IntDL,
                "智力圣导师",
                16,
                "智力主导流(团队辅助) — 智60% 敏20% 体15% 力5%",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.15f, 0.25f, 0.10f, 0.50f, "初期加些敏捷"),
                    new BuildPhase(81, 200, 0.05f, 0.20f, 0.10f, 0.65f, "主智力"),
                    new BuildPhase(201, 400, 0.05f, 0.15f, 0.10f, 0.70f, "极限智力"),
                    new BuildPhase(401, int.MaxValue, 0.05f, 0.15f, 0.10f, 0.70f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.AgilityDL,
                "敏圣",
                16,
                "敏捷输出流(后期爆发) — 敏50% 智25% 力20% 体5%",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.30f, 0.40f, 0.05f, 0.25f, "初期均衡"),
                    new BuildPhase(81, 200, 0.20f, 0.48f, 0.05f, 0.27f, "提升敏捷"),
                    new BuildPhase(201, 400, 0.15f, 0.53f, 0.05f, 0.27f, "极限敏捷"),
                    new BuildPhase(401, int.MaxValue, 0.15f, 0.55f, 0.05f, 0.25f, "全敏爆发"),
                }),
            new BuildDirectionInfo(
                BuildDirection.ForceDL,
                "力敏圣",
                16,
                "力敏圣(打手型) — 力量1000-1500，敏捷其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.40f, 0.45f, 0.05f, 0.10f, "力敏均衡"),
                    new BuildPhase(81, 200, 0.35f, 0.50f, 0.05f, 0.10f, "主力敏"),
                    new BuildPhase(201, 400, 0.30f, 0.55f, 0.05f, 0.10f, "更多敏捷"),
                    new BuildPhase(401, int.MaxValue, 0.28f, 0.57f, 0.05f, 0.10f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 召唤术士 (20) / 血召唤 (22) / 次元大师 (23) — 基础职业20
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.IntSummoner,
                "打手型召唤",
                20,
                "打手型召唤 — 力量够穿装备，敏捷200-1000，体力少量，智力其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.15f, 0.25f, 0.10f, 0.50f, "初期均衡"),
                    new BuildPhase(81, 200, 0.05f, 0.15f, 0.05f, 0.75f, "主智力"),
                    new BuildPhase(201, 400, 0.03f, 0.12f, 0.05f, 0.80f, "极限智力"),
                    new BuildPhase(401, int.MaxValue, 0.03f, 0.10f, 0.05f, 0.82f, string.Empty),
                }),
            new BuildDirectionInfo(
                BuildDirection.SupportSummoner,
                "辅助型召唤",
                20,
                "辅助型召唤 — 力量够穿装备，敏捷够穿装备，智力其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.15f, 0.15f, 0.15f, 0.55f, "初期均衡"),
                    new BuildPhase(81, 200, 0.05f, 0.10f, 0.10f, 0.75f, "主智力"),
                    new BuildPhase(201, 400, 0.05f, 0.08f, 0.08f, 0.79f, "极限智力"),
                    new BuildPhase(401, int.MaxValue, 0.05f, 0.08f, 0.08f, 0.79f, string.Empty),
                }),

            // ════════════════════════════════════════════════════════════════
            // 格斗家 (24) / 拳王 (25) — 基础职业24
            // ════════════════════════════════════════════════════════════════
            new BuildDirectionInfo(
                BuildDirection.AgilityFighter,
                "敏格",
                24,
                "敏格(输出) — 力量够穿装备，体力够穿装备，敏捷其余全加",
                new BuildPhase[]
                {
                    new BuildPhase(1, 80, 0.25f, 0.55f, 0.15f, 0.05f, "初期均衡"),
                    new BuildPhase(81, 200, 0.10f, 0.70f, 0.15f, 0.05f, "主力敏"),
                    new BuildPhase(201, 400, 0.08f, 0.75f, 0.12f, 0.05f, "极限敏捷"),
                    new BuildPhase(401, int.MaxValue, 0.08f, 0.77f, 0.12f, 0.03f, "全敏输出"),
                }),
        };
    }

    /// <summary>
    /// Initializes the builds-by-class dictionary from all build definitions.
    /// </summary>
    private static Dictionary<int, List<BuildDirectionInfo>> InitializeBuilds()
    {
        var dict = new Dictionary<int, List<BuildDirectionInfo>>();
        foreach (var build in AllBuilds)
        {
            var baseClass = build.BaseClassNumber;
            if (!dict.TryGetValue(baseClass, out var list))
            {
                list = new List<BuildDirectionInfo>();
                dict[baseClass] = list;
            }

            list.Add(build);
        }

        return dict;
    }

    /// <summary>
    /// Initializes backward-compatible class builds dictionary using the default build for each class.
    /// </summary>
    private static Dictionary<int, (int MinLevel, int MaxLevel, float Str, float Agi, float Vit, float Ene)[]> InitializeClassBuilds()
    {
        var dict = new Dictionary<int, (int, int, float, float, float, float)[]>();
        foreach (var kvp in BuildsByClass)
        {
            var builds = kvp.Value;
            if (builds.Count > 0)
            {
                var defaultBuild = builds[0];
                dict[kvp.Key] = defaultBuild.Phases
                    .Select(p => (p.MinLevel, p.MaxLevel, p.StrWeight, p.AgiWeight, p.VitWeight, p.EneWeight))
                    .ToArray();
            }
        }

        return dict;
    }
}
