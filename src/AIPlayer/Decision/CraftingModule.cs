// <copyright file="CraftingModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Pathfinding;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// 合成执行模块 — 驱动混沌合成流程。
/// 支持多配方：为不同合成类型查找对应的 NPC。
/// 支持跨地图：不在 NPC 所在图时触发 WarpPlanner 传送。
/// 状态机: WarpToMap → WalkToNpc → OpenDialog → MoveItemsToTmp → DoMix → MoveResultBack。
/// </summary>
public sealed class CraftingModule : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly TalkNpcAction _talkNpcAction = new();
    private readonly ItemCraftAction _craftAction = new();
    private readonly MoveItemAction _moveItemAction = new();

    /// <summary>混沌合成 NPC 编号 (Chaos Goblin = 238 / 混沌大师 = 256)。</summary>
    private const short ChaosGoblinNumber = 238;

    /// <summary>
    /// 合成操作引用的 NPC 窗口类型 → NPC 编号映射。
    /// 门票合成统一由 Chaos Goblin(238) 处理。
    /// 入场 NPC 和合成 NPC 不同。
    /// </summary>
    private static readonly Dictionary<string, short> CraftingNpcMap = new()
    {
        { "ChaosMachine", 238 },        // Chaos Goblin (所有合成)
        { "DevilSquare", 237 },          // Charon (Devil Square 入场, 非合成)
        { "BloodCastle", 229 },          // Blood Castle entrance 入场
        { "Warehouse", 232 },            // Warehouse Keeper
    };

    /// <summary>当前任务指定的合成类型标识（从 MissionItem 解析）。</summary>
    private short _targetNpcNumber = ChaosGoblinNumber;

    public string ModuleId => "crafting_executor";

    public CraftingModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <summary>
    /// 执行合成任务的一步。
    /// 状态机: 跨地图传送(如果需要) → 搜索NPC → 走向NPC → 打开对话 → 放材料 → 合成 → 关闭。
    /// </summary>
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        // 从任务 ID 解析目标 NPC 和合成类型
        this.ResolveCraftingNpc(item);

        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        // Phase 0: Check if we are on the right map for the NPC
        var npcMap = this.FindNpcMap();
        if (npcMap.HasValue && npcMap.Value != map.Definition.Number)
        {
            this._logger.LogInformation("[Crafting] NPC #{Npc} 不在当前地图 #{CurMap}，需要传送到 #{TargetMap}",
                this._targetNpcNumber, map.Definition.Number, npcMap.Value);
            // Set TargetMapNumber so HeartbeatService's ExecuteCrossMapWarp picks it up
            // Access BehaviorContext through the shared context reference
            if (this._adapter is IGameAdapter && this._adapter.GetCurrentMap() is not null)
            {
                var logic = this._player.Logic;
                if (logic is not null && npcMap.HasValue)
                {
                    logic.TargetMapNumber = npcMap.Value;
                }
            }
            return StepResult.NoTarget;
        }

        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.Number == this._targetNpcNumber);

        // Phase 1: 找不到 NPC → 巡逻搜索
        if (npc is null)
        {
            this._logger.LogDebug("[Crafting] NPC #{Num} 不在视野，巡逻中", this._targetNpcNumber);
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 2: 找到 NPC 但不在旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            this._logger.LogDebug("[Crafting] 走向 NPC #{Num} ({X},{Y})", this._targetNpcNumber, npc.Position.X, npc.Position.Y);
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 3: 在 NPC 旁边但未打开对话 → 打开对话
        if (this._player.OpenedNpc != npc)
        {
            this._logger.LogInformation("[Crafting] 打开 NPC #{Num} 对话", this._targetNpcNumber);
            await this._talkNpcAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 4: 对话已打开，合成窗口就绪
        if (this._player.TemporaryStorage is { } tmp && tmp.Items.Any())
        {
            // 已有材料 → 执行合成（尝试 mixTypeId=0，如果不对则从 NPC 的 ItemCraftings 推导）
            var mixTypeId = this.GetMixTypeId(item);
            this._logger.LogInformation("[Crafting] 执行合成 (mixType={MixType})", mixTypeId);
            await this._craftAction.MixItemsAsync(this._player, mixTypeId, 0).ConfigureAwait(false);
            var closeAction = new CloseNpcDialogAction();
            await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;

            // 合成后验证：检查背包是否出现了目标物品（门票）
            if (this.IsCraftingResultPresent(item))
            {
                this._logger.LogInformation("[Crafting] ✅ 合成成功！目标物品已出现在背包");
                return StepResult.Completed;
            }

            this._logger.LogWarning("[Crafting] ❌ 合成失败（材料消耗但目标物品未出现）");
            return StepResult.Failed;
        }

        // 还没放材料 → 从背包中找到合成材料移到 TemporaryStorage
        var itemsMoved = await this.TryMoveCraftingMaterialsAsync().ConfigureAwait(false);
        if (itemsMoved)
        {
            this._logger.LogInformation("[Crafting] 已移动合成材料到 TemporaryStorage");
            return StepResult.InProgress;
        }

        // 没有材料 → 无法合成
        this._logger.LogWarning("[Crafting] 背包中无合成材料，合成任务失败");
        var closeAction2 = new CloseNpcDialogAction();
        await closeAction2.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
        this._player.OpenedNpc = null;
        return StepResult.Failed;
    }

    /// <summary>
    /// 从 MissionItem ID 或 Category 解析目标 NPC 和合成类型。
    /// ID 格式: "craft_ticket_DevilSquare_2" → NPC=238(ChaosGoblin, 门票合成)
    /// "craft_ticket_BloodCastle_2" → NPC=238(ChaosGoblin)
    /// "craft_chaos_upgrade" → NPC=238(ChaosGoblin)
    /// 所有合成都在混沌哥布林(238)处理。入场NPC(Charon 237/BloodCastle 229)由 EventExecutorModule 处理。
    /// </summary>
    private void ResolveCraftingNpc(MissionItem item)
    {
        if (item.Id.StartsWith("craft_ticket_", StringComparison.OrdinalIgnoreCase)
            || item.Id.StartsWith("craft_chaos_", StringComparison.OrdinalIgnoreCase)
            || item.Id.StartsWith("craft_equip_", StringComparison.OrdinalIgnoreCase)
            || item.Id.StartsWith("craft_wing_", StringComparison.OrdinalIgnoreCase))
        {
            this._targetNpcNumber = 238; // Chaos Goblin (所有合成)
        }
        else
        {
            this._targetNpcNumber = ChaosGoblinNumber; // Default
        }

        this._logger.LogDebug("[Crafting] 任务 {Id} → NPC #{Npc}", item.Id, this._targetNpcNumber);
    }

    /// <summary>
    /// 从 MissionItem 推导合成类型 ID（mixTypeId）。
    /// 对于混沌合成使用 0（默认），门票合成使用对应的 mixType。
    /// 门票合成在 Chaos Goblin 的 ItemCraftings 中一般是第一个(Number=0)。
    /// 混沌武器合成通常是 Number=2。
    /// </summary>
    private byte GetMixTypeId(MissionItem item)
    {
        if (item.Id.StartsWith("craft_ticket_", StringComparison.OrdinalIgnoreCase))
        {
            return 0; // 门票合成通常是 Chaos Goblin 的第一个合成类型
        }

        if (item.Id.StartsWith("craft_chaos_", StringComparison.OrdinalIgnoreCase))
        {
            return 2; // 混沌武器合成
        }

        if (item.Id.StartsWith("craft_wing_", StringComparison.OrdinalIgnoreCase))
        {
            return 3; // 翅膀合成
        }

        return 0; // Default
    }

    /// <summary>
    /// 查找目标 NPC 所在的地图编号。通过 MonsterSpawns 配置定位。
    /// </summary>
    private ushort? FindNpcMap()
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null) return null;

        foreach (var mapDef in config.Maps)
        {
            if (mapDef.MonsterSpawns?.Any(s => s.MonsterDefinition?.Number == this._targetNpcNumber) == true)
                return (ushort)mapDef.Number;
        }

        return null;
    }

    /// <summary>
    /// 从背包中找到合成材料并移到 TemporaryStorage。
    /// 针对不同合成类型移动对应的材料到混沌合成机。
    /// 门票合成: 恶魔眼(14,17) + 恶魔钥匙(14,18) + 混沌宝石(12,15)
    /// 翅膀合成: 洛克之羽(13,11) + 混沌宝石(12,15) + +4以上装备
    /// 混沌武器: +4以上装备 + 混沌宝石(12,15) 等
    /// 装备升级: 装备 + 祝福(12,14)/灵魂(12,13)/混沌(12,15)
    /// </summary>
    private async ValueTask<bool> TryMoveCraftingMaterialsAsync()
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;

        // Check if materials already moved
        var tmp = this._player.TemporaryStorage;
        if (tmp is not null && tmp.Items.Any())
        {
            return true;
        }

        // 门票合成: 恶魔眼 + 恶魔钥匙 + 混沌宝石
        if (this._targetNpcNumber != ChaosGoblinNumber)
        {
            // 门票合成: 混沌宝石(12,15) 是最重要的
            var moved = await this.TryMoveItemsByGroupAsync(inv, 12, 15).ConfigureAwait(false);
            if (!moved)
            {
                this._logger.LogWarning("[Crafting] 背包中无混沌宝石，合成可能失败");
                return false;
            }

            // 如果有其他材料也移过去
            await this.TryMoveItemsByGroupAsync(inv, 14, 17).ConfigureAwait(false); // 恶魔眼
            await this.TryMoveItemsByGroupAsync(inv, 14, 18).ConfigureAwait(false); // 恶魔钥匙
            return true;
        }

        // 混沌合成 (默认): 混沌宝石(12,15)
        if (await this.TryMoveItemsByGroupAsync(inv, 12, 15).ConfigureAwait(false))
        {
            this._logger.LogInformation("[Crafting] 已移动混沌宝石到 TemporaryStorage");
            return true;
        }

        this._logger.LogDebug("[Crafting] 背包中无混沌宝石，尝试其他合成材料");
        var anyCraftable = inv.Items.FirstOrDefault(i =>
            i.Definition?.Group is >= 12 and <= 14);
        if (anyCraftable is null) return false;

        var fromSlot = anyCraftable.ItemSlot;
        await this._moveItemAction.MoveItemAsync(this._player, fromSlot, Storages.Inventory, 0, Storages.ChaosMachine)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 按 Group/Number 将指定物品从背包移到混沌合成机的 TemporaryStorage。
    /// </summary>
    private async ValueTask<bool> TryMoveItemsByGroupAsync(IInventoryStorage inv, int group, int number)
    {
        var item = inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == group && i.Definition?.Number == number);
        if (item is null) return false;

        await this._moveItemAction.MoveItemAsync(this._player, item.ItemSlot, Storages.Inventory, 0, Storages.ChaosMachine)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 合成后验证：检查背包是否有目标物品。
    /// 从 MissionItem.Context["TicketItemGroup"] 和 ["TicketItemNumber"] 读取。
    /// 如果 Context 中无定义，则不验证（返回 true 兼容旧版逻辑）。
    /// </summary>
    private bool IsCraftingResultPresent(MissionItem item)
    {
        if (!item.Context.TryGetValue("TicketItemGroup", out var groupObj) || groupObj is not int ticketGroup)
        {
            // 无目标物品定义 → 信任混和结果（旧版行为）
            this._logger.LogDebug("[Crafting] 任务 {Id} Context 无 TicketItemGroup 定义，跳过验证", item.Id);
            return true;
        }

        if (!item.Context.TryGetValue("TicketItemNumber", out var numObj) || numObj is not int ticketNumber)
        {
            return true;
        }

        var inv = this._player.Inventory;
        if (inv is null) return false;

        var hasTicket = inv.Items.Any(i =>
            i.Definition?.Group == ticketGroup && i.Definition?.Number == ticketNumber && i.Durability > 0);

        this._logger.LogInformation(
            "[Crafting] 合成验证: 目标物品 G{Group}N{Number} {Status}",
            ticketGroup, ticketNumber, hasTicket ? "✅ 存在" : "❌ 不存在");

        return hasTicket;
    }

    /// <summary>记录实际合成类型，用于材料检查决策。</summary>
    private int _actualMixTypeId = -1;
}
