// <copyright file="GameAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.ItemConsumeActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.GameLogic.PlayerActions.Party;
using MUnique.OpenMU.GameLogic.PlayerActions.Skills;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.GameLogic.Views.World;

/// <summary>
/// <see cref="IGameAdapter"/> 的默认实现。
/// 包装 <see cref="AiPlayer"/> 实例，将 AI 操作转换为游戏 API 调用。
/// </summary>
public sealed class GameAdapter : IGameAdapter
{
    private readonly AiPlayer _player;
    private readonly PickupItemAction _pickupAction = new();
    private readonly ItemConsumeAction _consumeAction = new();
    private readonly PartyRequestAction _partyRequestAction = new();
    private readonly PartyResponseAction _partyResponseAction = new();
    private readonly QuestStartAction _questStartAction = new();
    private readonly QuestCompletionAction _questCompletionAction = new();
    private readonly QuestClientAction _questClientAction = new();

    /// <summary>
    /// 事件发布委托 — 由 HeartbeatService 在启动后设置。
    /// 用于发布怪物击杀等事件到 AiEventBus。
    /// 不在 <see cref="IGameAdapter"/> 接口中（接口不变约束）。
    /// </summary>
    public Action<Decision.AiEvent>? EventPublisher { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GameAdapter"/> class.
    /// </summary>
    /// <param name="player">The AI player to wrap.</param>
    public GameAdapter(AiPlayer player)
    {
        this._player = player;
    }

    /// <inheritdoc />
    public async ValueTask<WalkResult> WalkToAsync(Point target, GameMap map)
    {
        var algorithm = this._player.AlgorithmSelector?.Select(SelectionMode.ByName, "AStar")
                        ?? this._player.AlgorithmSelector?.Select(SelectionMode.RoundRobin);
        if (algorithm is null)
        {
            return new WalkResult(WalkStatusCode.AlgorithmUnavailable, target, this._player.Position, "No algorithm selector available");
        }

        var path = algorithm.FindPath(this._player.Position, target, map.Terrain.AIgrid, true);
        if (path is null || path.Count == 0)
        {
            return new WalkResult(WalkStatusCode.PathNotFound, target, this._player.Position, "Path not found");
        }

        var maxSteps = Math.Min(path.Count, 16);
        var steps = new WalkingStep[maxSteps];
        for (int i = 0; i < maxSteps; i++)
        {
            var node = path[i];
            var prevPos = i == 0 ? this._player.Position : steps[i - 1].To;
            steps[i] = new WalkingStep(prevPos, node.Point, prevPos.GetDirectionTo(node.Point));
        }

        var targetNode = path[Math.Min(path.Count - 1, 16 - 1)];
        await this._player.WalkToAsync(new Point(targetNode.X, targetNode.Y), steps).ConfigureAwait(false);
        return new WalkResult(WalkStatusCode.Success, target, this._player.Position);
    }

    /// <inheritdoc />
    public async ValueTask HitAsync(IAttackable target, byte skillNumber, Direction direction)
    {
        var attackDir = direction == Direction.Undefined
            ? this._player.Position.GetDirectionTo(target.Position)
            : direction;
        this._player.Rotation = attackDir;
        await target.AttackByAsync(this._player, null, false).ConfigureAwait(false);
        this.TryPublishMonsterKilled(target);
    }

    /// <inheritdoc />
    public async ValueTask HitWithSkillAsync(IAttackable target, SkillEntry skillEntry)
    {
        if (skillEntry.Skill is not { } skill)
        {
            await this.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
            return;
        }

        // Check MP before skill attack
        var manaReq = skillEntry.Skill.ConsumeRequirements?.FirstOrDefault(r => r.Attribute == Stats.CurrentMana);
        if (manaReq is not null)
        {
            var currentMana = this._player.Attributes?[Stats.CurrentMana] ?? 0;
            if (currentMana < manaReq.MinimumValue)
            {
                await this.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
                return;
            }
        }

        this._player.Rotation = this._player.Position.GetDirectionTo(target.Position);
        await target.AttackByAsync(this._player, skillEntry, false).ConfigureAwait(false);
        this.TryPublishMonsterKilled(target);
    }

    /// <inheritdoc />
    public async ValueTask PerformSkillAsync(IAttackable? target, ushort skillNumber)
    {
        var strategy = this._player.GameContext.PlugInManager
            .GetStrategy<short, ITargetedSkillPlugin>((short)skillNumber)
            ?? new TargetedSkillDefaultPlugin();

        var actualTarget = target ?? this._player;
        await strategy.PerformSkillAsync(this._player, actualTarget, skillNumber).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PickupItemAsync(ushort itemId)
    {
        await this._pickupAction.PickupItemAsync(this._player, itemId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WalkDirectAsync(Point target, WalkingStep[] steps)
    {
        await this._player.WalkToAsync(target, steps).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ConsumeItemAsync(byte inventorySlot)
    {
        await this._consumeAction.HandleConsumeRequestAsync(
            this._player, inventorySlot, inventorySlot, FruitUsage.Undefined).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Point GetPlayerPosition() => this._player.Position;

    /// <inheritdoc />
    public bool IsPlayerWalking() => this._player.IsWalking;

    /// <inheritdoc />
    public int GetCurrentHp() => (int)(this._player.Attributes?[Stats.CurrentHealth] ?? 0f);

    /// <inheritdoc />
    public int GetMaxHp() => (int)(this._player.Attributes?[Stats.MaximumHealth] ?? 1f);

    /// <inheritdoc />
    public int GetCurrentMp() => (int)(this._player.Attributes?[Stats.CurrentMana] ?? 0f);

    /// <inheritdoc />
    public int GetMaxMp() => (int)(this._player.Attributes?[Stats.MaximumMana] ?? 1f);

    /// <inheritdoc />
    public int GetPlayerLevel() => this._player.Level;

    /// <inheritdoc />
    public GameMap? GetCurrentMap() => this._player.CurrentMap;

    /// <inheritdoc />
    public async ValueTask<WarpResult> WarpToMapAsync(ushort mapNumber)
    {
        var currentMap = this._player.CurrentMap;
        if (currentMap is null)
        {
            var reason = "CurrentMap is null, cannot warp";
            this._player.Logger.LogWarning("[WarpToMapAsync] {Reason} to map {MapNumber}.", reason, mapNumber);
            return new WarpResult(WarpStatusCode.PlayerNullContext, mapNumber, null, reason, this._player.Position);
        }

        if (currentMap.Definition.Number == mapNumber)
        {
            var reason = "Already on target map";
            this._player.Logger.LogInformation("[WarpToMapAsync] {Reason} {MapNumber}, no warp needed.", reason, mapNumber);
            return new WarpResult(WarpStatusCode.AlreadyOnTarget, mapNumber, mapNumber, reason, this._player.Position);
        }

        // Find the target map definition
        var targetDef = this._player.GameContext.Configuration.Maps.FirstOrDefault(m => m.Number == mapNumber);
        if (targetDef is null)
        {
            var reason = $"Map #{mapNumber} not found in configuration";
            this._player.Logger.LogWarning("[WarpToMapAsync] {Reason}.", reason);
            return new WarpResult(WarpStatusCode.MapNotFound, mapNumber, (ushort)currentMap.Definition.Number, reason, this._player.Position);
        }

        // Find a spawn gate on the target map
        var spawnGate = targetDef.ExitGates.FirstOrDefault(g => g.IsSpawnGate);
        if (spawnGate is null)
        {
            var reason = "No spawn gate found";
            this._player.Logger.LogWarning("[WarpToMapAsync] {Reason} for map {MapNumber}.", reason, mapNumber);
            return new WarpResult(WarpStatusCode.NoSpawnGate, mapNumber, (ushort)currentMap.Definition.Number, reason, this._player.Position);
        }

        try
        {
            await this._player.WarpToAsync(spawnGate).ConfigureAwait(false);
            this._player.Logger.LogInformation("[WarpToMapAsync] WarpToAsync 完成: " +
                "player.CurrentMap={Map} pos=({X},{Y}) gate.Map={GateMap}",
                this._player.CurrentMap?.Definition.Number,
                this._player.Position.X, this._player.Position.Y,
                spawnGate.Map?.Number);
            return new WarpResult(WarpStatusCode.Success, mapNumber, mapNumber, null, this._player.Position);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[WarpToMapAsync] Failed to warp to map {MapNumber}.", mapNumber);
            return new WarpResult(WarpStatusCode.WarpFailed, mapNumber, (ushort)currentMap.Definition.Number, ex.Message, this._player.Position);
        }
    }

    /// <inheritdoc />
    public async ValueTask<PartyJoinResult> PartyJoinAsync(string targetPlayerName)
    {
        var targetPlayer = this._player.GameContext.GetPlayerByCharacterName(targetPlayerName);
        if (targetPlayer is null)
        {
            return new PartyJoinResult(PartyJoinStatusCode.TargetNotFound, targetPlayerName, "Target player not found on server");
        }

        try
        {
            await this._partyRequestAction.HandlePartyRequestAsync(this._player, targetPlayer).ConfigureAwait(false);

            // If the target is also an AI player, auto-accept the party request
            if (targetPlayer is AiPlayer)
            {
                await this._partyResponseAction.HandleResponseAsync(targetPlayer, true).ConfigureAwait(false);
            }

            return new PartyJoinResult(PartyJoinStatusCode.Success, targetPlayerName);
        }
        catch (Exception ex)
        {
            return new PartyJoinResult(PartyJoinStatusCode.InternalError, targetPlayerName, ex.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask PartyLeaveAsync()
    {
        if (this._player.Party is not { } party)
        {
            return;
        }

        try
        {
            await party.KickMySelfAsync(this._player).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[PartyLeaveAsync] Failed to leave party.");
        }
    }

    /// <inheritdoc />
    public async ValueTask PartyFollowAsync(float followDistance = 3f)
    {
        var party = this._player.Party;
        if (party is null || party.PartyList.Count == 0)
        {
            return;
        }

        var leader = party.PartyMaster;
        if (leader is null)
        {
            return;
        }

        // Check if leader is on the same map
        if (leader.CurrentMap != this._player.CurrentMap || this._player.CurrentMap is null)
        {
            return;
        }

        var leaderPos = leader.Position;
        var myPos = this._player.Position;
        var dist = myPos.EuclideanDistanceTo(leaderPos);

        if (dist <= followDistance)
        {
            // Already close to leader
            return;
        }

        // Walk toward leader
        await this.WalkToAsync(leaderPos, this._player.CurrentMap).ConfigureAwait(false);
    }

    // --- Wave Quest: 任务接口实现 ---

    /// <inheritdoc />
    public IReadOnlyList<ActiveQuestInfo> GetActiveQuests()
    {
        var questStates = this._player.SelectedCharacter?.QuestStates;
        if (questStates is null || questStates.Count == 0)
        {
            this._player.Logger.LogInformation("[GameAdapter] GetActiveQuests: no QuestStates (null or empty) — character={Char}, questStates loaded={HasQS}",
                this._player.SelectedCharacter?.Name ?? "null",
                this._player.SelectedCharacter?.QuestStates?.Count.ToString() ?? "null");
            return Array.Empty<ActiveQuestInfo>();
        }

        var result = new List<ActiveQuestInfo>(questStates.Count);
        foreach (var state in questStates)
        {
            var aqStatus = state.ActiveQuest is null ? "null" : $"#{state.ActiveQuest.Number} name={state.ActiveQuest.Name}";
            this._player.Logger.LogInformation("[GameAdapter] GetActiveQuests: processing QuestState G{Group} ActiveQuest={AQ}",
                state.Group, aqStatus);

            if (state.ActiveQuest is not { } activeQuest)
            {
                continue;
            }

            var questInfo = new ActiveQuestInfo
            {
                Group = state.Group,
                Number = activeQuest.Number,
                Name = activeQuest.Name.ToString() ?? string.Empty,
                RequiresClientAction = activeQuest.RequiresClientAction,
                QuestGiverNumber = activeQuest.QuestGiver?.Number,
            };

            // Extract monster kill progress
            if (activeQuest.RequiredMonsterKills is { Count: > 0 })
            {
                var killInfos = new List<QuestMonsterKillInfo>(activeQuest.RequiredMonsterKills.Count);
                foreach (var killReq in activeQuest.RequiredMonsterKills)
                {
                    var currentCount = state.RequirementStates
                        .FirstOrDefault(r => Equals(r.Requirement, killReq))
                        ?.KillCount ?? 0;

                    killInfos.Add(new QuestMonsterKillInfo
                    {
                        MonsterName = killReq.Monster?.Designation ?? string.Empty,
                        MonsterNumber = (short)(killReq.Monster?.Number ?? 0),
                        Current = currentCount,
                        Required = killReq.MinimumNumber,
                    });
                }

                questInfo = BuildQuestInfoWithKills(questInfo, killInfos.AsReadOnly());
            }

            // Extract item requirement progress
            if (activeQuest.RequiredItems is { Count: > 0 })
            {
                var itemInfos = new List<QuestItemRequirementInfo>(activeQuest.RequiredItems.Count);
                foreach (var itemReq in activeQuest.RequiredItems)
                {
                    var itemName = itemReq.Item?.GetNameForLevel(itemReq.DropItemGroup?.ItemLevel ?? 0) ?? string.Empty;
                    itemInfos.Add(new QuestItemRequirementInfo
                    {
                        ItemName = itemName,
                        Required = itemReq.MinimumNumber,
                    });
                }

                questInfo = BuildQuestInfoWithItems(questInfo, itemInfos.AsReadOnly());
            }

            result.Add(questInfo);
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<QuestDefinitionInfo> GetAvailableQuests()
    {
        if (this._player.OpenedNpc is null)
        {
            return Array.Empty<QuestDefinitionInfo>();
        }

        var availableQuests = this._player.GetAvailableQuestsOfOpenedNpc();
        return availableQuests.Select(q => new QuestDefinitionInfo
        {
            Group = q.Group,
            Number = q.Number,
            Name = q.Name.ToString() ?? string.Empty,
            MinimumCharacterLevel = q.MinimumCharacterLevel,
            Repeatable = q.Repeatable,
            RequiresClientAction = q.RequiresClientAction,
            QuestGiverNumber = q.QuestGiver?.Number,
        }).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async ValueTask<QuestActionResult> StartQuestAsync(short group, short number)
    {
        try
        {
            await this._questStartAction.StartQuestAsync(this._player, group, number).ConfigureAwait(false);

            // 验证 ActiveQuest 是否真被设置
            var questStates = this._player.SelectedCharacter?.QuestStates;
            var activeQuestSet = questStates?.Any(qs => qs.Group == group && qs.ActiveQuest is not null) ?? false;

            if (activeQuestSet)
            {
                this._player.Logger.LogInformation(
                    "[StartQuestAsync] Quest accepted: group={Group}, number={Number}.", group, number);
                return new QuestActionResult(
                    QuestStatusCode.Accepted, group, number,
                    MoneyBalance: this._player.SelectedCharacter?.Inventory?.Money,
                    CharacterLevel: this._player.Level);
            }

            // 任务未激活 — 可能是钱不够、等级不够或不可重复
            // 尝试判断具体原因
            var money = this._player.SelectedCharacter?.Inventory?.Money ?? 0;
            var level = this._player.Level;
            var reason = money < 10000 ? "Insufficient money" : "Quest not repeatable or prerequisites not met";
            this._player.Logger.LogWarning(
                "[StartQuestAsync] Quest not accepted for group={Group}, money={Money}, level={Level}.",
                group, money, level);
            return new QuestActionResult(
                QuestStatusCode.InsufficientMoney, group, number, reason,
                MoneyBalance: money, CharacterLevel: level);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[StartQuestAsync] Failed to start quest group={Group}, number={Number}.", group, number);
            return new QuestActionResult(QuestStatusCode.InternalError, group, number, ex.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask<QuestActionResult> CompleteQuestAsync(short group, short number)
    {
        try
        {
            await this._questCompletionAction.CompleteQuestAsync(this._player, group, number).ConfigureAwait(false);
            this._player.Logger.LogInformation("[CompleteQuestAsync] Quest completed: group={Group}, number={Number}.", group, number);
            return new QuestActionResult(
                QuestStatusCode.Accepted, group, number,
                MoneyBalance: this._player.SelectedCharacter?.Inventory?.Money,
                CharacterLevel: this._player.Level);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[CompleteQuestAsync] Failed to complete quest group={Group}, number={Number}.", group, number);
            return new QuestActionResult(QuestStatusCode.InternalError, group, number, ex.Message);
        }
    }

    /// <inheritdoc />
    public ValueTask PerformQuestClientActionAsync(short group, short number)
    {
        this._questClientAction.ClientAction(this._player, group, number);
        this._player.Logger.LogInformation("[QuestClientAction] ClientAction performed: group={Group}, number={Number}.", group, number);
        return ValueTask.CompletedTask;
    }

    private static ActiveQuestInfo BuildQuestInfoWithKills(ActiveQuestInfo info, IReadOnlyList<QuestMonsterKillInfo> kills)
    {
        return new ActiveQuestInfo
        {
            Group = info.Group,
            Number = info.Number,
            Name = info.Name,
            RequiresClientAction = info.RequiresClientAction,
            QuestGiverNumber = info.QuestGiverNumber,
            RequiredKills = kills,
            RequiredItems = info.RequiredItems,
        };
    }

    private static ActiveQuestInfo BuildQuestInfoWithItems(ActiveQuestInfo info, IReadOnlyList<QuestItemRequirementInfo> items)
    {
        return new ActiveQuestInfo
        {
            Group = info.Group,
            Number = info.Number,
            Name = info.Name,
            RequiresClientAction = info.RequiresClientAction,
            QuestGiverNumber = info.QuestGiverNumber,
            RequiredKills = info.RequiredKills,
            RequiredItems = items,
        };
    }

    /// <summary>
    /// 检测目标是否已死亡（怪物被击杀），如果是则发布 <see cref="Decision.MonsterKilledEvent"/>。
    /// </summary>
    /// <param name="target">被攻击的目标。</param>
    private void TryPublishMonsterKilled(IAttackable target)
    {
        if (this.EventPublisher is null)
        {
            return;
        }

        // 只关心怪物死亡
        if (target is not Monster monster || monster.IsAlive)
        {
            return;
        }

        var monsterNumber = (short)(monster.Definition?.Number ?? 0);
        var monsterName = monster.Definition?.Designation ?? "?";
        this.EventPublisher(new Decision.MonsterKilledEvent(monsterNumber, monsterName));
    }
}
