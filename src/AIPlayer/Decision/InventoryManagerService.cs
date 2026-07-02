// <copyright file="InventoryManagerService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 背包管理服务 — 自动检测背包满仓，回城清理。
///
/// 清理流程：
///   1. 找贩卖 NPC（商店）→ 卖垃圾道具（D/E/Junk 级）
///   2. 找仓库 NPC → 存入好物品（S/A/B 级、卓越、古代、宝石等）
/// </summary>
public sealed class InventoryManagerService
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly ValueAssessmentService? _valueAssessment;
    private readonly MoveItemAction _moveItemAction = new();
    private readonly DropItemAction _dropItemAction = new();
    private readonly CloseNpcDialogAction _closeAction = new();

    private const int RegularInventorySlots = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="InventoryManagerService"/> class.
    /// </summary>
    /// <param name="player">AI 玩家实例。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="logger">日志。</param>
    public InventoryManagerService(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
        this._valueAssessment = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InventoryManagerService"/> class with value assessment.
    /// </summary>
    public InventoryManagerService(AiPlayer player, IGameAdapter adapter, ILogger logger, ValueAssessmentService valueAssessment)
        : this(player, adapter, logger)
    {
        this._valueAssessment = valueAssessment;
    }

    /// <summary>
    /// 背包占用率 — 占据背包储物格（非装备格）的比例，范围 [0.0~1.0]。
    /// </summary>
    public float Fullness { get; private set; }

    /// <summary>
    /// 判断背包是否已满，需要清理。
    /// </summary>
    /// <param name="threshold">阈值，默认 0.8（80%）。</param>
    /// <returns>如果占用率 ≥ 阈值则返回 true。</returns>
    public bool NeedsInventoryCleanup(float threshold = 0.8f)
    {
        this.CalculateFullness();
        return this.Fullness >= threshold;
    }

    /// <summary>
    /// 执行背包清理流程：
    ///   1. 找仓库 NPC → 存入好物品（S/A/B 级、卓越、古代、宝石、技能书）
    ///   2. 找贩卖 NPC → 卖垃圾道具（D/E/Junk 级）
    ///   3. 最后计算背包空间，如果还不够则重复
    /// </summary>
    /// <returns>执行结果。</returns>
    public async ValueTask<StepResult> CleanupInventoryAsync()
    {
        // Step 1: 先检查背包，把好物品存仓库
        var hasVaultItem = this.HasVaultableItem();
        if (hasVaultItem)
        {
            return await this.StoreVaultItemsAsync().ConfigureAwait(false);
        }

        // Step 2: 好物品都存完了，再卖垃圾
        return await this.SellJunkItemsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 背包清理后再次检查空格数，如果还不够就扔垃圾。
    /// 卖不掉也存不进的东西直接扔掉。
    /// </summary>
    public async ValueTask<StepResult> ForceCleanupAsync()
    {
        // 先尝试正常清理（存仓库+卖）
        var result = await this.CleanupInventoryAsync().ConfigureAwait(false);
        if (result != StepResult.Completed)
            return result;

        // 正常清理完了，如果空格还是不够，扔掉D/E/Junk垃圾
        if (this.GetFreeSlotCount() >= 4)
            return StepResult.Completed;

        return await this.DropTrashAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 扔掉卖不掉的垃圾。卖不掉= NPC不收（如任务道具、绑定物）。
    /// </summary>
    private async ValueTask<StepResult> DropTrashAsync()
    {
        var inv = this._player.Inventory;
        if (inv is null) return StepResult.Failed;

        var pos = this._adapter.GetPlayerPosition();
        var dropTarget = new Point(pos.X, pos.Y);

        var trashItems = inv.Items
            .Where(i => i.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            .Where(i =>
            {
                // 宝石、羽毛、技能书一律不扔（应该已经存仓库了）
                if (i.Definition is null) return true;
                var group = i.Definition.Group;
                if (group == 12) return false; // 宝石（Bless/Soul/Chaos/Creation/Life等）
                if (group == 13 && (i.Definition.Number == 11 || i.Definition.Number == 16 || i.Definition.Number == 31))
                    return false; // 羽毛（洛克之羽/神鹰羽毛/大天使之羽）
                if (i.Definition.Skill is not null) return false; // 技能书

                // 只能扔 NPC 不收，且 ValueAssessment 认为是垃圾的东西
                if (this._valueAssessment is not null)
                {
                    var asm = this._valueAssessment.Evaluate(i);
                    if (asm.Tier <= ValueTier.C) return false; // S/A/B/C 保留
                }

                return true;
            })
            .OrderBy(i => i.ItemSlot)
            .ToList();

        if (trashItems.Count == 0)
        {
            this._logger.LogInformation("[InvMgr] 无垃圾可扔");
            return StepResult.Completed;
        }

        var toDrop = trashItems[0];
        this._logger.LogInformation(
            "[InvMgr] 🗑️ 扔掉: {Name} (Slot={Slot}), 还剩 {Remaining} 件",
            toDrop.Definition?.Name ?? "?",
            toDrop.ItemSlot,
            trashItems.Count - 1);

        await this._dropItemAction.DropItemAsync(this._player, toDrop.ItemSlot, dropTarget).ConfigureAwait(false);

        if (trashItems.Count == 1)
            return StepResult.Completed;

        return StepResult.InProgress;
    }

    /// <summary>
    /// 检查背包中是否有值得存入仓库的物品。
    /// 值得存的物品：S/A/B 级、卓越、古代、宝石、技能书
    /// </summary>
    private bool HasVaultableItem()
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;

        foreach (var item in inv.Items)
        {
            // 跳过装备格
            if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex)
                continue;

            if (this.IsVaultableItem(item))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 判断物品是否值得存入仓库。
    /// 标准：S/A/B级、卓越、古代、宝石、技能书
    /// </summary>
    private bool IsVaultableItem(Item item)
    {
        if (item.Definition is null) return false;

        // 有 ValueAssessment 就用它的评级
        if (this._valueAssessment is not null)
        {
            var assessment = this._valueAssessment.Evaluate(item);
            // S/A/B 级物品值得存
            if (assessment.Tier <= ValueTier.B)
                return true;

            // C 级但对合成有价值也存（任务道具、合成材料等）
            if (assessment.Tier == ValueTier.C && item.Definition.Skill is not null)
                return true;
        }

        // 卓越、幸运、古代装备值得存
        var hasExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);
        if (hasExcellent || isAncient)
            return true;

        // 宝石、技能书值得存
        var group = item.Definition.Group;
        if (group == 12) return true; // 宝石类
        if (item.Definition.Skill is not null) return true; // 技能书

        return false;
    }

    /// <summary>
    /// 存入仓库流程：找到仓库 NPC → 走过去 → 打开对话 → 依次存入好物品 → 关闭对话。
    /// </summary>
    private async ValueTask<StepResult> StoreVaultItemsAsync()
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null)
        {
            this._logger.LogWarning("[InvMgr] 当前地图为 null，无法存仓库");
            return StepResult.Failed;
        }

        // 1) 找到仓库 NPC
        var vaultNpc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.NpcWindow == NpcWindow.VaultStorage
                              || n.Definition?.NpcWindow == NpcWindow.Storage);

        if (vaultNpc is null)
        {
            this._logger.LogInformation("[InvMgr] 当前地图未发现仓库NPC，先卖垃圾");
            // 没有仓库NPC时直接跳到贩卖
            return await this.SellJunkItemsAsync().ConfigureAwait(false);
        }

        // 2) 不在 NPC 旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(vaultNpc.Position);
        if (dist > 3f)
        {
            this._logger.LogDebug("[InvMgr] 走向仓库NPC ({X},{Y})", vaultNpc.Position.X, vaultNpc.Position.Y);
            await this._adapter.WalkToAsync(
                new Point((byte)vaultNpc.Position.X, (byte)vaultNpc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 3) 打开 NPC 对话
        if (this._player.OpenedNpc != vaultNpc)
        {
            this._logger.LogInformation("[InvMgr] 打开仓库NPC对话");
            var talkAction = new TalkNpcAction();
            await talkAction.TalkToNpcAsync(this._player, vaultNpc).ConfigureAwait(false);
            if (this._player.OpenedNpc != vaultNpc)
            {
                this._logger.LogWarning("[InvMgr] 打开仓库NPC对话失败");
                return StepResult.Failed;
            }

            return StepResult.InProgress;
        }

        // 4) 对话已打开 → 存入好物品
        var inv = this._player.Inventory;
        if (inv is null) return StepResult.Failed;

        var vaultableItems = inv.Items
            .Where(i => i.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex && this.IsVaultableItem(i))
            .OrderBy(i => this._valueAssessment?.Evaluate(i).Tier ?? ValueTier.E) // 先存低价值的
            .ThenBy(i => i.ItemSlot)
            .ToList();

        if (vaultableItems.Count == 0)
        {
            this._logger.LogInformation("[InvMgr] 无物品需要存仓库");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            // 接着去卖垃圾
            return await this.SellJunkItemsAsync().ConfigureAwait(false);
        }

        // 找仓库空格
        var vault = this._player.Account?.Vault;
        if (vault is null)
        {
            this._logger.LogWarning("[InvMgr] 仓库为空，无法存入");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Failed;
        }

        // 遍历仓库格子找第一个空格
        var vaultItems = vault.Items ?? Enumerable.Empty<Item>();
        var vaultOccupied = new HashSet<int>(vaultItems.Select(i => (int)i.ItemSlot));
        byte? freeVaultSlot = null;
        for (byte slot = 0; slot < InventoryConstants.WarehouseSize; slot++)
        {
            if (!vaultOccupied.Contains(slot))
            {
                freeVaultSlot = slot;
                break;
            }
        }

        if (freeVaultSlot is null)
        {
            this._logger.LogWarning("[InvMgr] 仓库已满，无法存入");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Failed;
        }

        // 每 tick 存一件
        var itemToStore = vaultableItems[0];

        this._logger.LogInformation(
            "[InvMgr] 📦 存入仓库: {Name} (Slot={FromSlot}) → 仓库(Slot={VaultSlot}), 还剩 {Remaining} 件",
            itemToStore.Definition?.Name ?? "?",
            itemToStore.ItemSlot,
            freeVaultSlot,
            vaultableItems.Count - 1);

        await this._moveItemAction.MoveItemAsync(
            this._player,
            itemToStore.ItemSlot,
            Storages.Inventory,
            freeVaultSlot!.Value,
            Storages.Vault).ConfigureAwait(false);

        if (vaultableItems.Count == 1)
        {
            // 最后一件存完了，关闭对话
            this._logger.LogInformation("[InvMgr] 最后一件已存仓库，关闭对话");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        return StepResult.InProgress;
    }

    /// <summary>
    /// 判断道具是否为可贩卖的垃圾。
    /// 委托给 ValueAssessmentService.ShouldSellToNpc 进行评估；若未注入则回退到旧逻辑。
    /// </summary>
    /// <param name="item">道具实例。</param>
    /// <returns>是否为垃圾道具。</returns>
    private bool IsJunkItem(Item item)
    {
        // 非装备格（在背包储物格中）
        if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex)
        {
            return false;
        }

        if (this._valueAssessment is not null)
        {
            // 强化保护：ShouldKeep 的道具绝不放进出售列表
            if (this._valueAssessment.ShouldKeep(item))
            {
                return false;
            }

            return this._valueAssessment.ShouldSellToNpc(item);
        }

        // Legacy fallback: old hardcoded logic
        return LegacyIsJunkItem(item);
    }

    /// <summary>
    /// Legacy hardcoded junk detection used when ValueAssessmentService is not available.
    /// </summary>
    private static bool LegacyIsJunkItem(Item item)
    {

        // 绑定道具不可卖
        if (item.Definition?.IsBoundToCharacter == true)
        {
            return false;
        }

        // Excellent 物品保留
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent))
        {
            return false;
        }

        // Luck 物品保留
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck))
        {
            return false;
        }

        // Socket 物品保留（有 SocketCount 表示可镶嵌）
        if (item.SocketCount > 0)
        {
            return false;
        }

        if (item.Definition is null)
        {
            return true; // 无定义 → 可卖
        }

        // 保留贵重物品类别
        var group = item.Definition.Group;
        switch (group)
        {
            case 12: // 宝石 (Bless=14, Soul=13, Chaos=15, Creation=16, Life=17)
                return false;
            case 14: // 药水
                return false;
            case 15: // 卷轴
                return false;
            default:
                return true; // 普通白装/绿装/武器 → 可卖
        }
    }

    /// <summary>
    /// 计算背包可用空格数（只计储物格，装备格 0~11 不计入）。
    /// 供换装/学技能逻辑调用，确保操作前有足够空格。
    /// </summary>
    /// <returns>背包可用储物格数量。</returns>
    public int GetFreeSlotCount()
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            return 0;
        }

        var occupied = 0;
        foreach (var item in inv.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            {
                occupied++;
            }
        }

        return RegularInventorySlots - occupied;
    }

    /// <summary>
    /// 计算背包占用率。
    /// 只计储物格（非装备格），装备格 0~11 不计入。
    /// </summary>
    private void CalculateFullness()
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            this.Fullness = 0f;
            return;
        }

        var occupied = 0;
        foreach (var item in inv.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            {
                occupied++;
            }
        }

        this.Fullness = (float)occupied / RegularInventorySlots;
    }

    /// <summary>
    /// 贩卖垃圾道具流程：找到贩卖 NPC → 走过去 → 打开对话 → 贩卖 → 关闭对话。
    /// </summary>
    private async ValueTask<StepResult> SellJunkItemsAsync()
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null)
        {
            this._logger.LogWarning("[InvMgr] 当前地图为 null，无法贩卖");
            return StepResult.Failed;
        }

        // 1) 在当前地图查找贩卖 NPC
        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.MerchantStore is not null);

        if (npc is null)
        {
            this._logger.LogInformation("[InvMgr] 当前地图未发现贩卖NPC，巡逻搜索");
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 2) 不在 NPC 旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 3) 打开 NPC 对话
        if (this._player.OpenedNpc != npc)
        {
            this._logger.LogInformation("[InvMgr] 打开贩卖NPC对话");
            var talkAction = new TalkNpcAction();
            await talkAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);
            if (this._player.OpenedNpc != npc)
            {
                this._logger.LogWarning("[InvMgr] 打开NPC对话失败");
                return StepResult.Failed;
            }

            return StepResult.InProgress;
        }

        // 4) 对话已打开 → 依次贩卖垃圾道具
        var inv = this._player.Inventory;
        if (inv is null) return StepResult.Failed;

        var junkItems = inv.Items
            .Where(i => this.IsJunkItem(i))
            .ToList();

        if (this._valueAssessment is not null)
        {
            junkItems = junkItems
                .Where(i => !this._valueAssessment.ShouldKeep(i))
                .OrderBy(i => this._valueAssessment.Evaluate(i).Tier)
                .ThenBy(i => i.ItemSlot)
                .ToList();
        }
        else
        {
            junkItems = junkItems
                .OrderBy(i => i.ItemSlot)
                .ToList();
        }

        if (junkItems.Count == 0)
        {
            this._logger.LogInformation("[InvMgr] 无垃圾道具可卖");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        var itemToSell = junkItems[0];
        this._logger.LogInformation(
            "[InvMgr] 🏪 贩卖垃圾: {Name} (Slot={Slot}), 还剩 {Remaining} 件",
            itemToSell.Definition?.Name ?? "?",
            itemToSell.ItemSlot,
            junkItems.Count - 1);

        await this._adapter.SellItemToNpcAsync(itemToSell.ItemSlot).ConfigureAwait(false);

        if (junkItems.Count == 1)
        {
            this._logger.LogInformation("[InvMgr] 最后一件垃圾已卖");
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        return StepResult.InProgress;
    }
}
