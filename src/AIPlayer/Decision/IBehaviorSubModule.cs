// <copyright file="IBehaviorSubModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 子模块接口 — 可插拔任务执行器。
/// 每次心跳调一次 ExecuteStepAsync，返回是否完成。
/// </summary>
public interface IBehaviorSubModule
{
    string ModuleId { get; }
    ValueTask<StepResult> ExecuteStepAsync(MissionItem item);
}

/// <summary>
/// 任务执行器 — 接任务→做条件→交任务。
/// 不依赖 JSON 脚本，直接从 QuestDefinition 读取任务需求。
/// </summary>
public sealed class QuestExecutor : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    public string ModuleId => "quest_executor";

    public QuestExecutor(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        var quest = item.QuestDef;
        if (quest is null) return StepResult.Failed;

        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        var activeQuests = this._adapter.GetActiveQuests();
        var activeQuest = activeQuests.FirstOrDefault(q => q.Group == quest.Group);

        // === 阶段1: 没接任务 → 去接 ===
        if (activeQuest is null)
        {
            this._logger.LogDebug("[QuestExec] 📋 阶段=接 G{Group}N{Num}「{Name}」", quest.Group, quest.Number, quest.Name);
            return await this.TryAcceptQuestAsync(quest, map).ConfigureAwait(false);
        }

        // === 阶段2: 条件满足 → 去交 ===
        var allKillsDone = activeQuest.RequiredKills.Count == 0 ||
                           activeQuest.RequiredKills.All(k => k.Current >= k.Required);
        if (allKillsDone)
        {
            this._logger.LogDebug("[QuestExec] ✅ 阶段=交 G{Group}N{Num} kill={Kill}/{Req}", activeQuest.Group, activeQuest.Number,
                activeQuest.RequiredKills.Sum(k => k.Current), activeQuest.RequiredKills.Sum(k => k.Required));
            return await this.TrySubmitQuestAsync(quest, map).ConfigureAwait(false);
        }

        // === 阶段3: 条件未满足 → 去打 ===
        this._logger.LogDebug("[QuestExec] ⚔️ 阶段=打 G{Group}N{Num} need kill #{Monster}({Cur}/{Req})",
            activeQuest.Group, activeQuest.Number,
            activeQuest.RequiredKills.FirstOrDefault(k => k.Current < k.Required)?.MonsterNumber,
            activeQuest.RequiredKills.FirstOrDefault(k => k.Current < k.Required)?.Current,
            activeQuest.RequiredKills.FirstOrDefault(k => k.Current < k.Required)?.Required);
        return await this.TryHuntAsync(activeQuest).ConfigureAwait(false);
    }

    private async ValueTask<StepResult> TryAcceptQuestAsync(QuestDefinition quest, GameMap map)
    {
        var npcDef = quest.QuestGiver;
        if (npcDef is null) return StepResult.Failed;

        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.Number == npcDef.Number);
        if (npc is null)
        {
            this._logger.LogDebug("[QuestExec] NPC #{Num} not in range, will try walking toward expected location", npcDef.Number);
            // NPC not spawned or not in range — try walking around to find it
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 已到达NPC → 打开对话 + 接任务
        this._player.OpenedNpc = npc;
        var result = await this._adapter.StartQuestAsync(quest.Group, quest.Number).ConfigureAwait(false);
        if (result.Status == QuestStatusCode.Accepted)
        {
            this._logger.LogInformation("[QuestExec] 接任务 OK: G{Group}N{Num}", quest.Group, quest.Number);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        if (result.Status == QuestStatusCode.AlreadyCompleted)
        {
            this._logger.LogInformation("[QuestExec] 任务已做完: G{Group}N{Num}", quest.Group, quest.Number);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        this._logger.LogWarning("[QuestExec] 接任务失败: G{Group}N{Num} status={Status}",
            quest.Group, quest.Number, result.Status);
        return StepResult.InProgress;
    }

    private async ValueTask<StepResult> TrySubmitQuestAsync(QuestDefinition quest, GameMap map)
    {
        var npcDef = quest.QuestGiver;
        if (npcDef is null) return StepResult.Failed;

        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.Number == npcDef.Number);
        if (npc is null)
        {
            this._logger.LogDebug("[QuestExec] 交任务NPC #{Num} 不在视野，巡逻中", npcDef.Number);
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // === 走到 NPC 面前后：打开对话 → 提交任务 → 关闭对话 ===

        // 1) 打开 NPC 对话（如果尚未打开）
        if (this._player.OpenedNpc != npc)
        {
            this._logger.LogDebug("[QuestExec] 打开 NPC #{Num} 对话交任务", npcDef.Number);
            var talkAction = new MUnique.OpenMU.GameLogic.PlayerActions.TalkNpcAction();
            await talkAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);
            if (this._player.OpenedNpc != npc)
            {
                this._logger.LogWarning("[QuestExec] 打开 NPC #{Num} 对话失败", npcDef.Number);
                return StepResult.InProgress;
            }
        }

        // 2) 检查是否需要 ClientAction
        var activeQuests = this._adapter.GetActiveQuests();
        var activeQuest = activeQuests.FirstOrDefault(q => q.Group == quest.Group);
        if (activeQuest?.RequiresClientAction == true)
        {
            this._logger.LogInformation("[QuestExec] 执行 ClientAction G{Group}N{Num}", quest.Group, quest.Number);
            await this._adapter.PerformQuestClientActionAsync(quest.Group, quest.Number).ConfigureAwait(false);
        }

        // 3) 提交任务
        this._player.OpenedNpc = npc;
        var result = await this._adapter.CompleteQuestAsync(quest.Group, quest.Number).ConfigureAwait(false);
        if (result.Status == QuestStatusCode.Accepted)
        {
            this._logger.LogInformation("[QuestExec] ✅ 交任务 OK: G{Group}N{Num}", quest.Group, quest.Number);

            // 4) 关闭 NPC 对话
            var closeAction = new MUnique.OpenMU.GameLogic.PlayerActions.CloseNpcDialogAction();
            await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            this._player.OpenedNpc = null;
            return StepResult.Completed;
        }

        this._logger.LogWarning("[QuestExec] 交任务失败: {Status}", result.Status);
        return StepResult.InProgress;
    }

    private async ValueTask<StepResult> TryHuntAsync(ActiveQuestInfo quest)
    {
        var killReq = quest.RequiredKills.FirstOrDefault(k => k.Current < k.Required);
        if (killReq is null) return StepResult.Completed;

        var map = this._adapter.GetCurrentMap();
        if (map is null) return StepResult.Failed;

        var pos = this._adapter.GetPlayerPosition();

        this._logger.LogInformation("[P0D] TryHuntAsync: quest G{Group}, need monster #{Monster} ({Name}), cur={Cur}/{Req}, pos=({X},{Y}), map={Map}",
            quest.Group, killReq.MonsterNumber, killReq.MonsterName,
            killReq.Current, killReq.Required,
            pos.X, pos.Y, map.Definition.Number);

        // 扩大搜索范围: 先近后远扫描 (20→60)
        const int nearRange = 20;
        const int farRange = 60;

        var target = map.GetAttackablesInRange(pos, nearRange)
            .OfType<Monster>()
            .FirstOrDefault(m => m.IsAlive && m.Definition?.Number == killReq.MonsterNumber)
                     ?? map.GetAttackablesInRange(pos, farRange)
            .OfType<Monster>()
            .FirstOrDefault(m => m.IsAlive && m.Definition?.Number == killReq.MonsterNumber);

        if (target is null)
        {
            // 远距离也找不到 → 按模式巡逻，逐步扩大搜索面
            var patrolTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-30, 30), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-30, 30), 0, 255));
            await this._adapter.WalkToAsync(patrolTarget, map).ConfigureAwait(false);
            this._logger.LogDebug("[QuestExec] 任务目标 #{Monster}({MonsterName}) 不在视野({Near}+{Far})，巡逻中 to=({Tx},{Ty})",
                killReq.MonsterNumber, killReq.MonsterName, nearRange, farRange, patrolTarget.X, patrolTarget.Y);
            return StepResult.NoTarget;
        }

        this._logger.LogInformation("[P0D] TryHuntAsync: found monster #{Monster} at ({X},{Y}), dist={Dist:F1}",
            target.Definition?.Number, target.Position.X, target.Position.Y,
            pos.EuclideanDistanceTo(target.Position));

        var dist = pos.EuclideanDistanceTo(target.Position);
        if (dist > 2.5f)
        {
            await this._adapter.WalkToAsync(
                new Point((byte)target.Position.X, (byte)target.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 最佳技能或普通攻击 — 每 tick 连续攻击最多 10 次或直到目标死亡
        // 确保低伤害角色也能在合理时间内杀死怪物
        for (int attempt = 0; attempt < 10 && target.IsAlive; attempt++)
        {
            var skill = this._player.SkillList?.Skills
                .OrderByDescending(s => s.Skill?.AttackDamage ?? 0)
                .FirstOrDefault(s => s.Skill?.SkillType == SkillType.DirectHit);
            if (skill is not null && skill.Skill is not null)
            {
                await this._adapter.HitWithSkillAsync(target, skill).ConfigureAwait(false);
            }
            else
            {
                await this._adapter.HitAsync(target, 0, Direction.Undefined).ConfigureAwait(false);
            }

            // 攻击后如果目标死亡，发布事件并跳出循环
            if (!target.IsAlive)
            {
                this._logger.LogInformation("[P0D] multi-hit killed monster #{Monster} after {Attempts} hits",
                    target is Monster m ? m.Definition?.Number : 0, attempt + 1);
                break;
            }
        }

        return StepResult.InProgress;
    }
}

/// <summary>
/// 子模块执行结果。
/// </summary>
public enum StepResult
{
    InProgress,
    Completed,
    Failed,
    NoTarget,
}
