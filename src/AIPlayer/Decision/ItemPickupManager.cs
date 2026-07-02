// <copyright file="ItemPickupManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Pathfinding;
using Microsoft.Extensions.Logging;

/// <summary>
/// 物品拾取模块 — 处理地面掉落物品的拾取逻辑。
///
/// 执行流程：
///   1. 通过 WorldState.DropsInRange 获取范围内掉落物品
///   2. 按白名单/黑名单过滤（宝石、卓越、幸运、任务道具；排除药水、箭矢）
///   3. 筛选最近的有价值的掉落物
///   4. 寻路走过去 → 拾取
///
/// 白名单（值得拾取）：
///   - 宝石类：祝福(Group=12,Num=14)、灵魂(Group=12,Num=13)、混沌(Group=12,Num=15)、创造(Group=12,Num=16)
///   - 卓越装备（有 Excellent 选项）
///   - 幸运装备（有 Luck 选项）
///   - 任务道具（由 QuestItemDroppedEvent 机制标记）
/// 黑名单（不拾取）：
///   - 药水(Group=14)
///   - 箭矢/弩箭(Group=15)
///   - 低等级白板无选项装备
/// </summary>
public sealed class ItemPickupManager : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly BehaviorContext _context;
    private readonly ILogger _logger;
    private readonly ValueAssessmentService? _valueAssessment;

    /// <summary>拾取最大行走距离（超出此距离不值得走过去）。</summary>
    private const float MaxPickupDistance = 30f;

    /// <summary>最小背包空位数（空位不足时不执行拾取）。</summary>
    private const int MinFreeSlotsForPickup = 2;

    // 宝石定义：Group=12
    private const byte GemGroup = 12;
    private const byte JewelOfBless = 14;
    private const byte JewelOfSoul = 13;
    private const byte JewelOfChaos = 15;
    private const byte JewelOfCreation = 16;

    // 黑名单：药水 Group=14
    private const byte PotionGroup = 14;
    // 黑名单：箭矢/弩箭 Group=15
    private const byte AmmoGroup = 15;

    public string ModuleId => "item_pickup_manager";

    public ItemPickupManager(AiPlayer player, IGameAdapter adapter, BehaviorContext context, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._context = context;
        this._logger = logger;
        this._valueAssessment = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemPickupManager"/> class with value assessment.
    /// </summary>
    public ItemPickupManager(AiPlayer player, IGameAdapter adapter, BehaviorContext context, ILogger logger, ValueAssessmentService valueAssessment)
        : this(player, adapter, context, logger)
    {
        this._valueAssessment = valueAssessment;
    }

    /// <summary>
    /// 执行拾取步骤。
    /// 每 tick 扫描周围掉落物 → 筛选有价值的 → 走过去 → 拾取。
    /// </summary>
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        // Step 0: 检查背包空位
        var inv = this._player.Inventory;
        if (inv is null) return StepResult.Failed;

        var freeSlots = CountFreeInventorySlots(inv);
        if (freeSlots < MinFreeSlotsForPickup)
        {
            this._logger.LogDebug("[ItemPickup] 背包已满({FreeSlots}空位)，停止拾取", freeSlots);
            return StepResult.Completed;
        }

        // Step 1: 获取当前掉落物（包括物品和金钱）
        var dropLocateables = this._context.WorldState.DropsInRange;
        if (dropLocateables is null || dropLocateables.Count == 0)
        {
            this._logger.LogDebug("[ItemPickup] 附近无掉落物");
            return StepResult.Completed;
        }

        // 分离物品和金钱，分别处理
        var moneyDrops = dropLocateables.OfType<DroppedMoney>().ToList();
        var itemDrops = dropLocateables.OfType<DroppedItem>().ToList();

        // 金钱：直接走过去捡（金钱不占背包空间，永远值得捡）
        if (moneyDrops.Count > 0)
        {
            var nearestMoney = moneyDrops
                .OrderBy(m => this._player.Position.EuclideanDistanceTo(m.Position))
                .First();
            var moneyDist = this._player.Position.EuclideanDistanceTo(nearestMoney.Position);

            if (moneyDist <= 2.0f)
            {
                await this._adapter.PickupItemAsync(nearestMoney.Id).ConfigureAwait(false);
                this._logger.LogInformation("[ItemPickup] 拾取金钱: {Amount} at ({X},{Y})",
                    nearestMoney.Amount, nearestMoney.Position.X, nearestMoney.Position.Y);
                return StepResult.InProgress;
            }

            // 金钱在可走范围内 → 走过去
            if (moneyDist <= MaxPickupDistance)
            {
                var map = this._adapter.GetCurrentMap();
                if (map is not null)
                {
                    await this._adapter.WalkToAsync(
                        new Point((byte)nearestMoney.Position.X, (byte)nearestMoney.Position.Y), map)
                        .ConfigureAwait(false);
                    return StepResult.InProgress;
                }
            }
        }

        // 物品：按价值过滤
        var itemDropList = itemDrops;
        var valuableDrops = itemDropList
            .Where(d => this.IsWorthPickingUp(d))
            .ToList();

        if (this._valueAssessment is not null)
        {
            valuableDrops = valuableDrops
                .OrderByDescending(d => this._valueAssessment.GetPickupPriority(
                    this._valueAssessment.Evaluate(d.Item)))
                .ThenBy(d => this._player.Position.EuclideanDistanceTo(d.Position))
                .ToList();
        }
        else
        {
            // Legacy: 纯按距离排序
            valuableDrops = valuableDrops
                .OrderBy(d => this._player.Position.EuclideanDistanceTo(d.Position))
                .ToList();
        }

        if (valuableDrops.Count == 0)
        {
            this._logger.LogDebug("[ItemPickup] 范围掉落物 {Total} 个(物品 {ItemCount}+金钱 {MoneyCount}) 无值得拾取的",
                itemDrops.Count, moneyDrops.Count);
            return StepResult.Completed;
        }

        // Step 3: 选最高优先级 + 最近的掉落物
        var target = valuableDrops[0];
        var dist = this._player.Position.EuclideanDistanceTo(target.Position);

        // 日志：记录价值层级分布
        if (this._valueAssessment is not null && this._logger.IsEnabled(LogLevel.Debug))
        {
            var tiers = valuableDrops
                .Select(d => this._valueAssessment.Evaluate(d.Item))
                .GroupBy(a => a.Tier)
                .OrderByDescending(g => g.Key)
                .Select(g => $"{g.Key}×{g.Count()}");
            this._logger.LogDebug(
                "[ItemPickup] 价值分布: {Tiers}",
                string.Join(" ", tiers));
        }

        this._logger.LogInformation(
            "[ItemPickup] 目标: {Item}(Id={Id}) 距离={Dist:F1}, 可选={Count}个{Tier}",
            target.Item?.Definition?.Name ?? $"G{target.Item?.Definition?.Group}N{target.Item?.Definition?.Number}",
            target.Id,
            dist,
            valuableDrops.Count,
            this._valueAssessment is not null && target.Item is not null
                ? $" [层级={this._valueAssessment.Evaluate(target.Item).Tier}]"
                : string.Empty);

        // Step 4: 如果超出最大拾取距离，且还有别的选择，选个值得走过去的
        if (dist > MaxPickupDistance)
        {
            this._logger.LogDebug("[ItemPickup] 目标距离 {Dist:F1} 超出最大拾取距离 {MaxDist}", dist, MaxPickupDistance);
            // 尝试走另一方向找更近的掉落
            var pos = this._adapter.GetPlayerPosition();
            var map = this._adapter.GetCurrentMap();
            if (map is not null)
            {
                var walkTarget = new Point(
                    (byte)Math.Clamp((int)pos.X + Random.Shared.Next(-10, 10), 0, 255),
                    (byte)Math.Clamp((int)pos.Y + Random.Shared.Next(-10, 10), 0, 255));
                await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
                return StepResult.InProgress;
            }

            return StepResult.Completed;
        }

        // Step 5: 目标在可拾取范围内 → 走过去
        if (dist > 2.0f)
        {
            var map = this._adapter.GetCurrentMap();
            if (map is not null)
            {
                // 走到掉落物旁边
                var walkTarget = new Point(
                    (byte)Math.Clamp((int)target.Position.X, 0, 255),
                    (byte)Math.Clamp((int)target.Position.Y, 0, 255));
                await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
                return StepResult.InProgress;
            }

            return StepResult.Failed;
        }

        // Step 6: 在拾取范围内 → 拾取
        this._logger.LogInformation(
            "[ItemPickup] 拾取: {Item}(Id={Id}) at ({X},{Y})",
            target.Item?.Definition?.Name ?? $"G{target.Item?.Definition?.Group}N{target.Item?.Definition?.Number}",
            target.Id,
            target.Position.X,
            target.Position.Y);

        await this._adapter.PickupItemAsync(target.Id).ConfigureAwait(false);

        // 拾取后继续检查是否有更多值得拾取的物品
        return StepResult.InProgress;
    }

    /// <summary>
    /// 判断掉落物是否值得拾取。
    /// 委托给 ValueAssessmentService 进行评估；若未注入则回退到旧逻辑。
    /// </summary>
    private bool IsWorthPickingUp(DroppedItem drop)
    {
        var item = drop.Item;
        if (item is null || item.Definition is null)
        {
            return false;
        }

        if (this._valueAssessment is not null)
        {
            return this._valueAssessment.ShouldPickup(item);
        }

        // Legacy fallback: old hardcoded logic
        return LegacyIsWorthPickingUp(item);
    }

    /// <summary>
    /// Legacy hardcoded pickup logic used when ValueAssessmentService is not available.
    /// </summary>
    private static bool LegacyIsWorthPickingUp(Item item)
    {
        if (item.Definition is null)
        {
            return false;
        }

        // ===== 白名单：宝石类 =====
        if (item.Definition.Group == GemGroup)
        {
            if (item.Definition.Number == JewelOfBless ||
                item.Definition.Number == JewelOfSoul ||
                item.Definition.Number == JewelOfChaos ||
                item.Definition.Number == JewelOfCreation)
            {
                return true;
            }
        }

        // ===== 白名单：卓越装备 =====
        if (item.ItemOptions is not null &&
            item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent))
        {
            return true;
        }

        // ===== 白名单：幸运装备 =====
        if (item.ItemOptions is not null &&
            item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck))
        {
            return true;
        }

        // ===== 白名单：任务道具（有 Quest 相关标记） =====
        // 后续可通过 QuestItemDroppedEvent 机制标记的任务道具扩展
        // 目前简单检查：装备类物品（非药水非箭矢）都值得拾取

        // ===== 黑名单：药水 =====
        if (item.Definition.Group == PotionGroup)
        {
            return false;
        }

        // ===== 黑名单：箭矢/弩箭 =====
        if (item.Definition.Group == AmmoGroup)
        {
            return false;
        }

        // ===== 装备类物品：有等级或选项的值得拾取 =====
        if (item.Level > 0)
        {
            // 已强化过的装备
            return true;
        }

        // 正常装备（非药水、非箭矢）：Group 在 0-11 之间的为普通装备
        // 普通装备有选项（ItemOptions 非空）时拾取
        if (item.ItemOptions is not null && item.ItemOptions.Count > 0)
        {
            return true;
        }

        // 卷轴类（Group=13）：可能是有用的卷轴或门票
        if (item.Definition.Group == 13)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 计算背包空位数。
    /// 基础背包：ItemSlot 0-11 为装备位，12-75(8行8列)为背包格。
    /// </summary>
    private static int CountFreeInventorySlots(IInventoryStorage inv)
    {
        const int equipSlots = 12; // ItemSlot 0-11 装备位
        const int baseRows = 8;
        const int rowSize = 8;

        var totalInventorySlots = equipSlots + (baseRows * rowSize);
        var occupiedCount = inv.Items.Count(i => i.ItemSlot >= equipSlots && i.ItemSlot < totalInventorySlots);
        var freeSlots = (baseRows * rowSize) - occupiedCount;

        return Math.Max(0, freeSlots);
    }
}
