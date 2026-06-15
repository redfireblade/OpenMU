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

    /// <summary>合成操作引用的 NPC 窗口类型 → NPC 编号映射。</summary>
    private static readonly Dictionary<string, short> CraftingNpcMap = new()
    {
        { "ChaosMachine", 238 },        // Chaos Goblin
        { "DevilSquare", 237 },          // Charon (Devil Square)
        { "BloodCastle", 229 },          // Blood Castle entrance
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
            return StepResult.Completed;
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
    /// ID 格式: "craft_ticket_DevilSquare_2" → NPC=237(Charon)
    /// "craft_chaos_upgrade" → NPC=238(ChaosGoblin)
    /// </summary>
    private void ResolveCraftingNpc(MissionItem item)
    {
        if (item.Id.StartsWith("craft_ticket_DevilSquare", StringComparison.OrdinalIgnoreCase))
        {
            this._targetNpcNumber = 237; // Charon
        }
        else if (item.Id.StartsWith("craft_ticket_BloodCastle", StringComparison.OrdinalIgnoreCase))
        {
            this._targetNpcNumber = 229; // Blood Castle entrance NPC
        }
        else if (item.Id.StartsWith("craft_chaos_", StringComparison.OrdinalIgnoreCase)
              || item.Id.StartsWith("craft_equip_", StringComparison.OrdinalIgnoreCase)
              || item.Id.StartsWith("craft_wing_", StringComparison.OrdinalIgnoreCase))
        {
            this._targetNpcNumber = 238; // Chaos Goblin
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
    /// </summary>
    private byte GetMixTypeId(MissionItem item)
    {
        // Default mixType 0 for Chaos Goblin (chaos weapon, wing upgrade)
        if (item.Id.StartsWith("craft_ticket_", StringComparison.OrdinalIgnoreCase))
        {
            return 1; // Ticket crafting typically uses mixType 1
        }

        return 0;
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
    /// 针对混沌合成模式：找 Chaos 宝石(12,15) + 其他可能的材料。
    /// 针对门票合成模式：找对应的门票材料。
    /// </summary>
    private async ValueTask<bool> TryMoveCraftingMaterialsAsync()
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;

        // Check if materials already moved or if we need to move them
        var tmp = this._player.TemporaryStorage;
        if (tmp is not null && tmp.Items.Any())
        {
            return true; // Already have materials in tmp
        }

        // Primary crafting material: Jewel of Chaos (12,15)
        var chaosItem = inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == 12 && i.Definition?.Number == 15);
        if (chaosItem is null)
        {
            this._logger.LogDebug("[Crafting] 背包中无混沌宝石，尝试其他合成材料");
            // Fallback: try ANY stackable item that could be used in crafting
            var anyCraftable = inv.Items.FirstOrDefault(i =>
                i.Definition?.Group is >= 12 and <= 14);
            if (anyCraftable is null) return false;

            var fromSlot = anyCraftable.ItemSlot;
            await this._moveItemAction.MoveItemAsync(this._player, fromSlot, Storages.Inventory, 0, Storages.ChaosMachine)
                .ConfigureAwait(false);
            return true;
        }

        var chaosSlot = chaosItem.ItemSlot;
        await this._moveItemAction.MoveItemAsync(this._player, chaosSlot, Storages.Inventory, 0, Storages.ChaosMachine)
            .ConfigureAwait(false);
        return true;
    }
}
