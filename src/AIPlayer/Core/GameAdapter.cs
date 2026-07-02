// <copyright file="GameAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.ItemConsumeActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.GameLogic.PlayerActions.Party;
using MUnique.OpenMU.GameLogic.PlayerActions.Skills;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.GameLogic.Views;

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
    private readonly SellItemToNpcAction _sellItemAction = new();

    /// <summary>
    /// 事件发布委托 — 由 HeartbeatService 在启动后设置。
    /// 用于发布怪物击杀等事件到 AiEventBus。
    /// 不在 <see cref="IGameAdapter"/> 接口中（接口不变约束）。
    /// </summary>
    public Action<Decision.AiEvent>? EventPublisher { get; set; }

    /// <summary>
    /// 聊天消息接收事件 — 由 <see cref="DrainChatMessages"/> 在每 tick 排出聊天缓存时触发。
    /// HeartbeatService 订阅此事件以处理交易喊话。
    /// </summary>
    public event Action<string, string, ChatMessageType>? ChatMessageReceived;

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

        // Broadcast attack animation to all nearby observers (including real players)
        // This is what the real AttackAction does — without this, clients see AI standing still.
        if (skillNumber > 0)
        {
            var skill = this._player.SkillList?.GetSkill(skillNumber)?.Skill;
            if (skill is not null && skill.SkillType != SkillType.Buff && skill.SkillType != SkillType.PassiveBoost)
            {
                await this._player.ForEachWorldObserverAsync<IShowSkillAnimationPlugIn>(
                    p => p.ShowSkillAnimationAsync(this._player, target, skill, true), false).ConfigureAwait(false);
            }
        }
        else
        {
            // Basic attack (no skill): broadcast animation to observers.
            // Animation 0 = default weapon swing. Without this broadcast,
            // real players never see the AI attack.
            await this._player.ForEachWorldObserverAsync<IShowAnimationPlugIn>(
                p => p.ShowAnimationAsync(this._player, 0, target, attackDir), false).ConfigureAwait(false);
        }

        var hitInfo = await target.AttackByAsync(this._player, null, false).ConfigureAwait(false);
        this._player.Logger.LogInformation("[P0D] HitAsync: target #{Target}({Name}) at ({X},{Y}), hpDmg={HpDmg}, shieldDmg={ShieldDmg}, targetAlive={Alive}",
            target is Monster m ? m.Definition?.Number : 0,
            target is Monster mon ? mon.Definition?.Designation ?? "?" : "?",
            target.Position.X, target.Position.Y,
            hitInfo?.HealthDamage ?? 0, hitInfo?.ShieldDamage ?? 0,
            target.IsAlive);
        await this.EnsureMinimumDamageAsync(target, hitInfo).ConfigureAwait(false);
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

        // Broadcast skill animation to observers
        if (skill.SkillType != SkillType.Buff && skill.SkillType != SkillType.PassiveBoost)
        {
            await this._player.ForEachWorldObserverAsync<IShowSkillAnimationPlugIn>(
                p => p.ShowSkillAnimationAsync(this._player, target, skill, true), true).ConfigureAwait(false);
        }

        var hitInfo = await target.AttackByAsync(this._player, skillEntry, false).ConfigureAwait(false);
        this._player.Logger.LogInformation("[P0D] HitWithSkillAsync: target #{Target}({Name}) at ({X},{Y}), skill={Skill}, hpDmg={HpDmg}, shieldDmg={ShieldDmg}, targetAlive={Alive}",
            target is Monster m ? m.Definition?.Number : 0,
            target is Monster mon ? mon.Definition?.Designation ?? "?" : "?",
            target.Position.X, target.Position.Y,
            skillEntry.Skill?.Number ?? 0,
            hitInfo?.HealthDamage ?? 0, hitInfo?.ShieldDamage ?? 0,
            target.IsAlive);
        await this.EnsureMinimumDamageAsync(target, hitInfo).ConfigureAwait(false);
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
            // 死亡/复活过渡期 CurrentMap 可能为 null。等最多 500ms 让地图就绪。
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                currentMap = this._player.CurrentMap;
                if (currentMap is not null) break;
            }

            if (currentMap is null)
            {
                var reason = "CurrentMap is null, cannot warp";
                this._player.Logger.LogWarning("[WarpToMapAsync] {Reason} to map {MapNumber}.", reason, mapNumber);
                return new WarpResult(WarpStatusCode.PlayerNullContext, mapNumber, null, reason, this._player.Position);
            }
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

        // Find a spawn gate on the target map (fallback to any exit gate)
        var spawnGate = targetDef.ExitGates.FirstOrDefault(g => g.IsSpawnGate)
                      ?? targetDef.ExitGates.FirstOrDefault();
        if (spawnGate is null)
        {
            var reason = "No exit gate found";
            this._player.Logger.LogWarning("[WarpToMapAsync] {Reason} for map {MapNumber}.", reason, mapNumber);
            return new WarpResult(WarpStatusCode.NoSpawnGate, mapNumber, (ushort)currentMap.Definition.Number, reason, this._player.Position);
        }

        try
        {
            await this._player.WarpToAsync(spawnGate).ConfigureAwait(false);

            // WarpToAsync 将 CurrentMap 设为 null，等客户端发 F3 12 确认。
            // AI 玩家没有真实客户端，直接确认地图变换完成。
            if (this._player.CurrentMap is null)
            {
                await this._player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            }

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

    // --- Wave Inventory: 背包管理接口实现 ---

    /// <inheritdoc />
    public async ValueTask SellItemToNpcAsync(byte inventorySlot)
    {
        await this._sellItemAction.SellItemAsync(this._player, inventorySlot).ConfigureAwait(false);
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
                    var currentCount = state.RequirementStates?
                        .FirstOrDefault(r => Equals(r.Requirement, killReq))
                        ?.KillCount ?? 0;

                    this._player.Logger.LogInformation("[P0D] GetActiveQuests: killReq #{Monster} cur={Cur}/{Req}, RequirementStates count={RS}",
                        killReq.Monster?.Number, currentCount, killReq.MinimumNumber,
                        state.RequirementStates?.Count ?? 0);

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

            // Fallback: StartQuestAsync failed (likely G0/QuestGiver=null because
            // GetQuest() depends on OpenedNpc). Try global config fallback.
            var found = false;
            if (this._player.GameContext?.Configuration is { } config)
            {
                foreach (var monster in config.Monsters)
                {
                    if (monster.Quests is null) continue;
                    var questDef = monster.Quests.FirstOrDefault(q =>
                        q.Group == group && q.Number == number
                        && (q.QualifiedCharacter is null || Equals(q.QualifiedCharacter, this._player.SelectedCharacter?.CharacterClass)));
                    if (questDef is null) continue;

                    this._player.Logger.LogInformation(
                        "[StartQuestAsync] Auto-activating quest G{Group}/N{Number} via config fallback (QuestDefinition found=True).",
                        group, number);

                    // Check level prerequisites
                    if (questDef.MinimumCharacterLevel > this._player.Level ||
                        (questDef.MaximumCharacterLevel > 0 && questDef.MaximumCharacterLevel < this._player.Level))
                    {
                        this._player.Logger.LogWarning(
                            "[StartQuestAsync] Fallback: level check failed for G{Group}/N{Number}.", group, number);
                        break;
                    }

                    // Get or create CharacterQuestState
                    var questState = this._player.SelectedCharacter!.QuestStates.FirstOrDefault(q => q.Group == group);
                    if (questState is null)
                    {
                        questState = this._player.PersistenceContext.CreateNew<CharacterQuestState>();
                        questState.Group = group;
                        this._player.SelectedCharacter.QuestStates.Add(questState);
                    }

                    // Check repeatability
                    if (Equals(questState.LastFinishedQuest, questDef) && !questDef.Repeatable)
                    {
                        this._player.Logger.LogWarning(
                            "[StartQuestAsync] Fallback: quest G{Group}/N{Number} is not repeatable.", group, number);
                        break;
                    }

                    // Check RequiredStartMoney
                    if (questDef.RequiredStartMoney > 0)
                    {
                        if (!this._player.TryRemoveMoney(questDef.RequiredStartMoney))
                        {
                            this._player.Logger.LogWarning(
                                "[StartQuestAsync] Fallback: insufficient money for G{Group}/N{Number} (need={Need}).",
                                group, number, questDef.RequiredStartMoney);
                            break;
                        }
                    }

                    // Clear existing state and set ActiveQuest (matching QuestStartAction pattern)
                    await questState.ClearAsync(this._player.PersistenceContext).ConfigureAwait(false);
                    questState.ActiveQuest = questDef;

                    this._player.Logger.LogInformation(
                        "[StartQuestAsync] Quest G{Group}/N{Number} activated via config fallback.", group, number);
                    found = true;
                    break;
                }
            }

            if (found)
            {
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

    /// <summary>
    /// 排出 AI 玩家的聊天消息缓存并触发 <see cref="ChatMessageReceived"/> 事件。
    /// 每 tick 由 HeartbeatService 调用，将 AiViewPlugInContainer 中缓存的聊天消息
    /// 批量取出并发布为事件，供 TransactionMonitor 处理。
    /// </summary>
    public void DrainChatMessages()
    {
        var container = this._player.ViewPlugIns as AiViewPlugInContainer;
        if (container is null)
        {
            return;
        }

        List<(string Sender, string Message, ChatMessageType Type)> batch;
        lock (container.ChatBuffer)
        {
            if (container.ChatBuffer.Count == 0)
            {
                return;
            }

            batch = new List<(string, string, ChatMessageType)>(container.ChatBuffer);
            container.ChatBuffer.Clear();
        }

        foreach (var (sender, message, type) in batch)
        {
            this.ChatMessageReceived?.Invoke(sender, message, type);
        }
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

    /// <summary>
    ///  AI 角色伤害兜底逻辑：当攻击怪物造成的基础伤害过低时（因为缺少装备/属性），
    ///  补充额外的直接伤害以确保 AI 角色至少能造成有意义的伤害。
    ///  这解决了 AI 角色没有武器、属性点未分配时的 8 点保底伤害问题。
    /// </summary>
    /// <remarks>
    ///  注意：不能修改 GameLogic 的 AttackByAsync（AR-20 约束）。
    ///  使用 IAttackable.ApplyBleedingDamageAsync 施加直接额外伤害，
    ///  该方法是 IAttackable 公开接口的一部分，不违反约束。
    /// </remarks>
    /// <param name="target">被攻击的目标。</param>
    /// <param name="hitInfo">原始攻击的伤害信息。</param>
    private async ValueTask EnsureMinimumDamageAsync(IAttackable target, HitInfo? hitInfo)
    {
        if (hitInfo is null || target is not Monster monster || !target.IsAlive)
        {
            return;
        }

        var level = this._player.Level;
        var minimumDamage = Math.Max(30, level / 2);

        // hitInfo is Nullable<HitInfo> (record struct), so use .Value to access members
        if (hitInfo.Value.HealthDamage < minimumDamage)
        {
            var bonusDamage = (uint)(minimumDamage - hitInfo.Value.HealthDamage);
            this._player.Logger.LogInformation(
                "[P0D] EnsureMinimumDamageAsync: boosting damage from {ActualDmg} to {MinDmg} (+{Bonus}) for target #{Target}",
                hitInfo.Value.HealthDamage, minimumDamage, bonusDamage,
                monster.Definition?.Number);
            await target.ApplyBleedingDamageAsync(this._player, bonusDamage).ConfigureAwait(false);
        }
    }
}
