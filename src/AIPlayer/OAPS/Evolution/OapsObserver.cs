// <copyright file="OapsObserver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer;

/// <summary>
/// Layer 6 旁观者系统 — 真实玩家行为观察、记录和分析的统一入口。
/// 包装 <see cref="PlayerBehaviorObserver"/> 和 <see cref="BehaviorEventStore"/>，
/// 提供 OAPS 层级的玩家行为采样、行为记录导出、NPC 交互查询和模式摘要生成。
/// </summary>
/// <remarks>
/// 参考 OAPS v3.0 §7.1 ObserverSystem 和 §8.1 集成计划。
///
/// 数据流：
///   Observe() → PBO.Tick() (每 1.2s 全服扫描)
///     → BehaviorRecord (内存分析)
///       → ExportBehaviorRecords() (结构化导出)
///       → BehaviorEvent (离散事件)
///         → BehaviorEventStore (持久化)
///           → GetNpcInteractions() (NPC 交互过滤查询)
///
/// 使用示例：
/// <code>
/// var observer = new OapsObserver(pbo, eventStore, logger);
/// observer.Observe();
/// var exports = observer.ExportBehaviorRecords();
/// var npcLogs = observer.GetNpcInteractions("Player1");
/// var summary = observer.GetPatternSummary();
/// </code>
/// </remarks>
public sealed class OapsObserver
{
    private readonly PlayerBehaviorObserver _pbo;
    private readonly BehaviorEventStore _eventStore;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OapsObserver"/> class.
    /// </summary>
    /// <param name="pbo">玩家行为观察器实例，负责周期性扫描和分析真实玩家行为。</param>
    /// <param name="eventStore">行为事件存储实例，负责持久化和查询离散行为事件。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null 时抛出。</exception>
    public OapsObserver(PlayerBehaviorObserver pbo, BehaviorEventStore eventStore, ILogger logger)
    {
        _pbo = pbo ?? throw new ArgumentNullException(nameof(pbo));
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 执行一次全服玩家行为观察采样。
    /// 委托 <see cref="PlayerBehaviorObserver.Tick"/> 完成以下维度的数据采集：
    ///   ① 攻击距离直方图
    ///   ② 技能使用统计
    ///   ③ 药水使用阈值推断
    ///   ④ 行走路径记录
    ///   ⑤ 装备变更检测
    ///   ⑥ 经济行为分析
    ///   ⑦ 离散行为事件生成（存入 BehaviorEventStore）
    /// </summary>
    /// <remarks>
    /// 实际采样频率为 ~1.2s × 3 = ~3.6s（PBO 内部每 3 次 Tick() 真正采集一次）。
    /// 所有异常被捕获并记录日志，不会中断调用方。
    /// </remarks>
    public void Observe()
    {
        try
        {
            _pbo.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OapsObserver] Observe 采集全服玩家行为数据时出现异常");
        }
    }

    /// <summary>
    /// 导出所有被观察玩家的行为记录为可序列化的只读字典。
    /// 将 <see cref="PlayerBehaviorObserver.BehaviorRecords"/> 转换为
    /// <see cref="BehaviorRecordExport"/> 格式，包含攻击距离直方图、
    /// 技能使用统计、推断的药水阈值、经济数据和最近路径段。
    /// </summary>
    /// <returns>
    /// 玩家名称（不区分大小写）到行为记录导出的只读字典。
    /// 如果尚无行为数据或导出过程异常，返回空字典。
    /// </returns>
    /// <remarks>
    /// 导出的数据可用于：
    ///   - GlobalShadowMap 群体知识融合
    ///   - Web 管理面板行为展示
    ///   - AI 决策参考（学习真实玩家的高效策略）
    /// </remarks>
    public IReadOnlyDictionary<string, BehaviorRecordExport> ExportBehaviorRecords()
    {
        try
        {
            var result = new Dictionary<string, BehaviorRecordExport>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in _pbo.BehaviorRecords)
            {
                var record = kvp.Value;
                var export = new BehaviorRecordExport(
                    CharacterName: record.CharacterName,
                    Level: record.LastSeenLevel,
                    AttackDistanceHistogram: record.AttackDistanceHistogram.Count > 0
                        ? new Dictionary<int, int>(record.AttackDistanceHistogram)
                        : null,
                    SkillUsage: record.SkillUsage.Count > 0
                        ? new Dictionary<ushort, int>(record.SkillUsage)
                        : null,
                    InferredHpPotionThreshold: record.InferredHpPotionThreshold,
                    InferredMpPotionThreshold: record.InferredMpPotionThreshold,
                    TotalGoldPicked: record.TotalGoldPicked,
                    TotalGoldSpent: record.TotalGoldSpent,
                    RecentPath: record.RecentPath.Count > 0
                        ? record.RecentPath.Select(p => new PathSegmentExport(
                            FromX: p.From.X,
                            FromY: p.From.Y,
                            ToX: p.To.X,
                            ToY: p.To.Y,
                            MapNumber: p.MapNumber)).ToList()
                        : null);
                result[kvp.Key] = export;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OapsObserver] ExportBehaviorRecords 导出行为记录时出现异常");
            return new Dictionary<string, BehaviorRecordExport>(0);
        }
    }

    /// <summary>
    /// 获取指定玩家与 NPC 交互的记录列表。
    /// 从 <see cref="BehaviorEventStore"/> 中过滤出与 NPC 交互相关的事件类型
    /// （NpcTalk / NpcDialogChoice / PotionBought / BuffReceived），
    /// 转换为 <see cref="NpcInteractionRecord"/> 格式。
    /// </summary>
    /// <param name="playerName">要查询的玩家角色名称。</param>
    /// <returns>
    /// 该玩家 NPC 交互记录的列表，按事件时间升序排列。
    /// 如果玩家不存在、尚无交互记录或查询异常，返回空列表。
    /// </returns>
    /// <remarks>
    /// 交互类型映射规则：
    ///   NpcTalk       → NpcInteractionType.Talk  （打开对话框）
    ///   NpcDialogChoice → NpcInteractionType.Choice（选择选项）
    ///   PotionBought   → NpcInteractionType.Buy   （购买药水）
    ///   BuffReceived   → NpcInteractionType.Buff  （获得增益）
    /// </remarks>
    public List<NpcInteractionRecord> GetNpcInteractions(string playerName)
    {
        try
        {
            var events = _eventStore.GetEvents(playerName);
            if (events.Count == 0)
            {
                return new List<NpcInteractionRecord>(0);
            }

            return events
                .Where(e => e.EventType is BehaviorEventType.NpcTalk
                    or BehaviorEventType.NpcDialogChoice
                    or BehaviorEventType.PotionBought
                    or BehaviorEventType.BuffReceived)
                .Select(e => new NpcInteractionRecord(
                    NpcNumber: e.NpcNumber ?? 0,
                    NpcName: e.NpcName,
                    MapNumber: e.MapNumber,
                    X: e.X,
                    Y: e.Y,
                    InteractionType: MapInteractionType(e.EventType),
                    DialogChoice: e.DialogChoice,
                    DialogChoiceDescription: e.DialogChoiceDescription,
                    Timestamp: e.Timestamp,
                    Level: e.Level))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OapsObserver] GetNpcInteractions 查询玩家 {PlayerName} 的NPC交互记录时出现异常", playerName);
            return new List<NpcInteractionRecord>(0);
        }
    }

    /// <summary>
    /// 生成所有被观察玩家的行为模式摘要文本。
    /// 第 1 行显示观察玩家总数和存储的行为事件总数。
    /// 后续每个玩家输出 3-5 行，包含等级、攻击距离、常用技能、
    /// 喝药阈值和经济行为摘要。
    /// </summary>
    /// <returns>
    /// 格式化的多行摘要字符串。
    /// 每玩家之间用 "--- 角色名 ---" 分隔。
    /// 异常时返回含错误信息的单行字符串。
    /// </returns>
    /// <remarks>
    /// 输出示例：
    /// <code>
    /// 观察玩家数: 3 / 总事件数: 127
    /// --- DarkKnight1 ---
    ///   等级: Lv180
    ///   攻击距离: 1格×45次, 2格×12次
    ///   常用技能: #18(120次), #26(34次)
    ///   喝药阈值: HP 45.2% / MP 28.7%
    ///   经济: 拾取金币 1,234,567 / 花费金币 98,765
    /// </code>
    /// </remarks>
    public string GetPatternSummary()
    {
        try
        {
            var sb = new StringBuilder();

            // 第 1 行：观察玩家数 / 总事件数
            var playerCount = _pbo.BehaviorRecords.Count;
            var totalEvents = GetTotalEventCount();
            sb.AppendLine($"观察玩家数: {playerCount} / 总事件数: {totalEvents}");

            foreach (var kvp in _pbo.BehaviorRecords)
            {
                var record = kvp.Value;
                sb.AppendLine($"--- {record.CharacterName} ---");
                sb.AppendLine($"  等级: Lv{record.LastSeenLevel}");

                // ── 攻击距离 Top 3 ──
                if (record.AttackDistanceHistogram.Count > 0)
                {
                    var topDist = record.AttackDistanceHistogram
                        .OrderByDescending(kv => kv.Value)
                        .Take(3)
                        .Select(kv => $"{kv.Key}格×{kv.Value}次");
                    sb.AppendLine($"  攻击距离: {string.Join(", ", topDist)}");
                }
                else
                {
                    sb.AppendLine("  攻击距离: 暂无数据");
                }

                // ── 常用技能 Top 3 ──
                if (record.SkillUsage.Count > 0)
                {
                    var topSkills = record.SkillUsage
                        .OrderByDescending(kv => kv.Value)
                        .Take(3)
                        .Select(kv => $"#{kv.Key}({kv.Value}次)");
                    sb.AppendLine($"  常用技能: {string.Join(", ", topSkills)}");
                }
                else
                {
                    sb.AppendLine("  常用技能: 暂无数据");
                }

                // ── 喝药阈值 ──
                sb.AppendLine($"  喝药阈值: HP {record.InferredHpPotionThreshold:P1} / MP {record.InferredMpPotionThreshold:P1}");

                // ── 经济行为 ──
                sb.AppendLine($"  经济: 拾取金币 {record.TotalGoldPicked:N0} / 花费金币 {record.TotalGoldSpent:N0}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OapsObserver] GetPatternSummary 生成模式摘要时出现异常");
            return $"生成模式摘要失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 遍历行为事件存储中的所有玩家，累加其行为事件总数。
    /// </summary>
    /// <returns>所有玩家行为事件的总数。</returns>
    private int GetTotalEventCount()
    {
        var total = 0;
        foreach (var name in _eventStore.CharacterNames)
        {
            total += _eventStore.GetEvents(name).Count;
        }

        return total;
    }

    /// <summary>
    /// 将 <see cref="BehaviorEventType"/> 映射为 <see cref="NpcInteractionType"/>。
    /// </summary>
    /// <param name="eventType">行为事件类型。</param>
    /// <returns>对应的 NPC 交互类型。</returns>
    private static NpcInteractionType MapInteractionType(BehaviorEventType eventType) => eventType switch
    {
        BehaviorEventType.NpcTalk => NpcInteractionType.Talk,
        BehaviorEventType.NpcDialogChoice => NpcInteractionType.Choice,
        BehaviorEventType.PotionBought => NpcInteractionType.Buy,
        BehaviorEventType.BuffReceived => NpcInteractionType.Buff,
        _ => NpcInteractionType.Talk,
    };
}

/// <summary>
/// 行为记录导出格式 — 玩家行为模式的不可变快照。
/// 包含攻击距离直方图、技能使用统计、推断的药水消耗阈值、
/// 经济数据和最近路径段，供外部系统（如 GlobalShadowMap、Web 面板）消费。
/// </summary>
/// <param name="CharacterName">角色名称。</param>
/// <param name="Level">最近观察到的角色等级。</param>
/// <param name="AttackDistanceHistogram">攻击距离直方图：距离（格）→ 攻击次数。</param>
/// <param name="SkillUsage">技能使用统计：技能编号 → 使用次数。</param>
/// <param name="InferredHpPotionThreshold">推断的 HP 药水消耗阈值，范围 [0.0, 1.0] 表示血量百分比。</param>
/// <param name="InferredMpPotionThreshold">推断的 MP 药水消耗阈值，范围 [0.0, 1.0] 表示法力百分比。</param>
/// <param name="TotalGoldPicked">累计拾取的金币总数。</param>
/// <param name="TotalGoldSpent">累计花费的金币总数（购物、修理、交易等）。</param>
/// <param name="RecentPath">最近的路径段列表，记录玩家近期的移动轨迹。</param>
public record BehaviorRecordExport(
    string CharacterName,
    int Level,
    Dictionary<int, int>? AttackDistanceHistogram,
    Dictionary<ushort, int>? SkillUsage,
    double InferredHpPotionThreshold,
    double InferredMpPotionThreshold,
    long TotalGoldPicked,
    long TotalGoldSpent,
    List<PathSegmentExport>? RecentPath);

/// <summary>
/// 路径段导出格式 — 玩家移动轨迹上的一段显著位移。
/// 记录一次移动的起点、终点坐标和所在地图，用于路径重放和分析。
/// </summary>
/// <param name="FromX">起点 X 坐标。</param>
/// <param name="FromY">起点 Y 坐标。</param>
/// <param name="ToX">终点 X 坐标。</param>
/// <param name="ToY">终点 Y 坐标。</param>
/// <param name="MapNumber">所在地图编号。</param>
public record PathSegmentExport(byte FromX, byte FromY, byte ToX, byte ToY, int MapNumber);

/// <summary>
/// NPC 交互记录 — 玩家与 NPC 之间一次交互的完整信息。
/// 包含交互的 NPC、发生位置、交互类型（对话/选项/购买/增益）、
/// 对话框选项内容以及玩家状态（等级、时间戳）。
/// </summary>
/// <param name="NpcNumber">NPC 编号。</param>
/// <param name="NpcName">NPC 名称。可能为 null 如果名称不可用。</param>
/// <param name="MapNumber">交互发生的地图编号。</param>
/// <param name="X">交互位置的 X 坐标。</param>
/// <param name="Y">交互位置的 Y 坐标。</param>
/// <param name="InteractionType">交互类型，标识玩家执行的具体动作。</param>
/// <param name="DialogChoice">对话框选项编号（如 1 = 获得BUFF），仅当交互类型为 Choice 时有值。</param>
/// <param name="DialogChoiceDescription">对话框选项的文字描述，仅当交互类型为 Choice 时有值。</param>
/// <param name="Timestamp">事件发生的 UTC 时间戳。</param>
/// <param name="Level">事件发生时玩家的等级。</param>
public record NpcInteractionRecord(
    short NpcNumber,
    string? NpcName,
    int MapNumber,
    byte X,
    byte Y,
    NpcInteractionType InteractionType,
    int? DialogChoice,
    string? DialogChoiceDescription,
    DateTime Timestamp,
    int Level);

/// <summary>
/// NPC 交互类型枚举 — 描述玩家与 NPC 交互的具体动作分类。
/// </summary>
public enum NpcInteractionType
{
    /// <summary>与 NPC 对话（打开对话框）。</summary>
    Talk,

    /// <summary>选择了 NPC 对话框的某个选项（如 "选1获得BUFF"）。</summary>
    Choice,

    /// <summary>从 NPC 商店购买了物品（药水、装备等）。</summary>
    Buy,

    /// <summary>从 NPC 处接受了增益效果（Buff，如 Elf Soldier 攻防BUFF）。</summary>
    Buff,
}
