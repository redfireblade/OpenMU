// <copyright file="VaultModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Globalization;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 仓库执行模块 — 从仓库取出物品到背包。
///
/// 状态机:
///   Phase 1: 当前地图找仓库 NPC（NpcWindow == VaultStorage）
///   Phase 2: 找不到 → 巡逻搜索
///   Phase 3: 找到但不在旁边 → 走过去
///   Phase 4: 在旁边但对话未打开 → TalkNpcAction 打开对话
///   Phase 5: 对话已打开 → 读任务上下文 → MoveItemAction 取物品 → 关闭对话
///
/// 任务 Id 格式: "vault_ticket_{Type}_{Level}" 或 "vault_material_{Type}_{Level}"
/// 例如: vault_ticket_BloodCastle_2
///         vault_material_BloodCastle_2
/// </summary>
public sealed class VaultModule : IBehaviorSubModule
{
    /// <summary>NPC 交互的最小距离。</summary>
    private const float NpcInteractionDistance = 3f;

    /// <summary>感知范围内搜索 NPC 的最大半径。</summary>
    private const int NpcSearchRange = 150;

    /// <summary>巡逻搜索的随机步长范围。</summary>
    private const int PatrolStepRange = 15;

    /// <summary>事件ID最小段数（格式: vault_ticket_Type_Level 或 vault_material_Type_Level）。</summary>
    private const int VaultIdMinParts = 4;

    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly TalkNpcAction _talkNpcAction = new();
    private readonly MoveItemAction _moveItemAction = new();
    private readonly CloseNpcDialogAction _closeAction = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="VaultModule"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="adapter">The game adapter.</param>
    /// <param name="logger">The logger.</param>
    public VaultModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <inheritdoc />
    public string ModuleId => "vault_executor";

    /// <inheritdoc />
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null)
        {
            this._logger.LogWarning("[VaultModule] 当前地图为 null");
            return StepResult.Failed;
        }

        // 在感知范围内查找仓库 NPC（通过 NpcWindow.VaultStorage 标识）
        var vaultNpc = map.GetNpcsInRange(this._player.Position, NpcSearchRange)
            .FirstOrDefault(n => n.Definition?.NpcWindow == NpcWindow.VaultStorage);

        // Phase 1: 找不到仓库 NPC → 巡逻搜索
        if (vaultNpc is null)
        {
            this._logger.LogDebug("[VaultModule] 仓库NPC不在视野，巡逻中");
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-PatrolStepRange, PatrolStepRange), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-PatrolStepRange, PatrolStepRange), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 2: 找到 NPC 但不在旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(vaultNpc.Position);
        if (dist > NpcInteractionDistance)
        {
            this._logger.LogDebug(
                "[VaultModule] 走向仓库NPC #{Num} ({X},{Y})",
                vaultNpc.Definition?.Number,
                vaultNpc.Position.X,
                vaultNpc.Position.Y);
            await this._adapter.WalkToAsync(
                new Point((byte)vaultNpc.Position.X, (byte)vaultNpc.Position.Y),
                map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 3: 在 NPC 旁边但未打开对话 → 对话打开
        if (this._player.OpenedNpc != vaultNpc)
        {
            this._logger.LogInformation(
                "[VaultModule] 打开仓库NPC #{Num} 对话",
                vaultNpc.Definition?.Number);
            await this._talkNpcAction.TalkToNpcAsync(this._player, vaultNpc).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // Phase 4: 对话已打开，等待 vault 存储初始化
        if (this._player.Vault is null)
        {
            this._logger.LogDebug("[VaultModule] 等待仓库存储初始化");
            return StepResult.InProgress;
        }

        // Phase 5: 仓库已就绪 → 根据任务类型取物
        var vaultResult = await this.ExecuteVaultRetrieval(item).ConfigureAwait(false);
        if (vaultResult == StepResult.Completed)
        {
            // 取物完成 → 关闭对话
            await this._closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            this._logger.LogInformation("[VaultModule] ✅ 仓库任务完成");
        }

        return vaultResult;
    }

    /// <summary>
    /// 根据任务条目 Id 解析要取的物品并执行。
    /// </summary>
    private async ValueTask<StepResult> ExecuteVaultRetrieval(MissionItem item)
    {
        var id = item.Id;

        // 格式 1: vault_ticket_{Type}_{Level}
        if (TryParseVaultForMiniGame(id, out var miniGameType, out var gameLevel))
        {
            return await this.RetrieveTicketAsync(miniGameType, gameLevel).ConfigureAwait(false);
        }

        this._logger.LogWarning("[VaultModule] 无法解析仓库任务ID格式: {Id}", id);
        return StepResult.Failed;
    }

    /// <summary>
    /// 从仓库中取门票——先尝试取门票本身，如果没有门票则取合成材料。
    /// </summary>
    private async ValueTask<StepResult> RetrieveTicketAsync(MiniGameType miniGameType, int gameLevel)
    {
        // 查找 MiniGameDefinition
        var miniGameDef = this.FindMiniGameDefinition(miniGameType, gameLevel);
        if (miniGameDef is null)
        {
            return StepResult.Failed;
        }

        var ticketDef = miniGameDef.TicketItem;
        if (ticketDef is null)
        {
            // 不需要门票 → 取物完成
            return StepResult.Completed;
        }

        // Step 1: 尝试取门票
        if (this._player.Vault is not null)
        {
            var ticketInVault = this._player.Vault.Items
                .FirstOrDefault(i =>
                    i.Definition == ticketDef
                    && i.Durability > 0
                    && i.Level == miniGameDef.TicketItemLevel);

            if (ticketInVault is not null)
            {
                // 取门票到背包
                await this.MoveVaultItemToInventoryAsync(ticketInVault).ConfigureAwait(false);
                return StepResult.Completed;
            }
        }

        // Step 2: 没有门票 → 取合成材料（混沌宝石 + 事件特定材料）
        var materialResult = await this.RetrieveCraftingMaterialsAsync(miniGameDef).ConfigureAwait(false);
        if (materialResult == StepResult.Completed)
        {
            this._logger.LogInformation(
                "[VaultModule] ✅ 从仓库取出合成材料 {Type} Lv.{Level}",
                miniGameType,
                gameLevel);
            return StepResult.Completed;
        }

        // Step 3: 没有门票也没有材料 → 失败
        this._logger.LogWarning(
            "[VaultModule] 仓库中没有 {Type} Lv.{Level} 的门票或合成材料",
            miniGameType,
            gameLevel);
        return StepResult.Failed;
    }

    /// <summary>
    /// 从仓库中取出门票合成材料到背包。
    /// </summary>
    private async ValueTask<StepResult> RetrieveCraftingMaterialsAsync(MiniGameDefinition miniGameDef)
    {
        if (this._player.Vault is null)
        {
            return StepResult.Failed;
        }

        var ticketLevel = miniGameDef.TicketItemLevel;
        bool anyMaterialMoved = false;

        // 混沌宝石（所有合成必需）
        var chaosGem = this._player.Vault.Items
            .FirstOrDefault(i => i.Definition?.Group == 12 && i.Definition?.Number == 15 && i.Durability > 0);
        if (chaosGem is not null)
        {
            await this.MoveVaultItemToInventoryAsync(chaosGem).ConfigureAwait(false);
            anyMaterialMoved = true;
        }

        // 事件特定材料
        switch (miniGameDef.Type)
        {
            case MiniGameType.BloodCastle:
                var scroll = this._player.Vault.Items
                    .FirstOrDefault(i => i.Definition?.Group == 13 && i.Definition?.Number == 16 && i.Level == ticketLevel && i.Durability > 0);
                if (scroll is not null)
                {
                    await this.MoveVaultItemToInventoryAsync(scroll).ConfigureAwait(false);
                    anyMaterialMoved = true;
                }

                var bone = this._player.Vault.Items
                    .FirstOrDefault(i => i.Definition?.Group == 13 && i.Definition?.Number == 17 && i.Level == ticketLevel && i.Durability > 0);
                if (bone is not null)
                {
                    await this.MoveVaultItemToInventoryAsync(bone).ConfigureAwait(false);
                    anyMaterialMoved = true;
                }

                break;

            case MiniGameType.DevilSquare:
                var eye = this._player.Vault.Items
                    .FirstOrDefault(i => i.Definition?.Group == 14 && i.Definition?.Number == 17 && i.Level == ticketLevel && i.Durability > 0);
                if (eye is not null)
                {
                    await this.MoveVaultItemToInventoryAsync(eye).ConfigureAwait(false);
                    anyMaterialMoved = true;
                }

                var key = this._player.Vault.Items
                    .FirstOrDefault(i => i.Definition?.Group == 14 && i.Definition?.Number == 18 && i.Level == ticketLevel && i.Durability > 0);
                if (key is not null)
                {
                    await this.MoveVaultItemToInventoryAsync(key).ConfigureAwait(false);
                    anyMaterialMoved = true;
                }

                break;
        }

        return anyMaterialMoved ? StepResult.Completed : StepResult.Failed;
    }

    /// <summary>
    /// 将仓库中的一个物品移动到背包第一个空位。
    /// </summary>
    private async ValueTask MoveVaultItemToInventoryAsync(DataModel.Entities.Item vaultItem)
    {
        var fromSlot = vaultItem.ItemSlot;
        var inventory = this._player.Inventory;

        // 找背包空位，FreeSlots 为空(FirstOrDefault返回0)且背包有物品 → 已满
        var freeSlots = inventory?.FreeSlots.ToList();
        if (freeSlots is null || freeSlots.Count == 0)
        {
            this._logger.LogWarning("[VaultModule] 背包已满，无法取物");
            return;
        }

        var targetSlot = freeSlots[0];

        await this._moveItemAction.MoveItemAsync(
                this._player,
                fromSlot,
                Storages.Vault,
                targetSlot,
                Storages.Inventory)
            .ConfigureAwait(false);

        this._logger.LogDebug(
            "[VaultModule] 仓库 ({Group}/{Number} Lv.{Level}) slot={FromSlot} → 背包 slot={ToSlot}",
            vaultItem.Definition?.Group,
            vaultItem.Definition?.Number,
            vaultItem.Level,
            fromSlot,
            targetSlot);
    }

    /// <summary>
    /// 从配置文件查找匹配的 MiniGameDefinition。
    /// </summary>
    private MiniGameDefinition? FindMiniGameDefinition(MiniGameType miniGameType, int gameLevel)
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null)
        {
            this._logger.LogWarning("[VaultModule] GameConfiguration 为空");
            return null;
        }

        return config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);
    }

    /// <summary>
    /// 解析仓库任务 ID 格式 "vault_ticket_{Type}_{Level}" 或 "vault_material_{Type}_{Level}"。
    /// </summary>
    private static bool TryParseVaultForMiniGame(string id, out MiniGameType miniGameType, out int gameLevel)
    {
        miniGameType = MiniGameType.Undefined;
        gameLevel = 0;

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var parts = id.Split('_');
        if (parts.Length < VaultIdMinParts)
        {
            return false;
        }

        // 检查前两个段: vault_ticket 或 vault_material
        if (!string.Equals(parts[0], "vault", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 从第2段开始拼 MiniGameType，最后一段是 Level
        var typeStr = string.Join("_", parts.Skip(2).Take(parts.Length - 3));
        if (!Enum.TryParse<MiniGameType>(typeStr, ignoreCase: true, out miniGameType))
        {
            return false;
        }

        return int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out gameLevel);
    }
}
