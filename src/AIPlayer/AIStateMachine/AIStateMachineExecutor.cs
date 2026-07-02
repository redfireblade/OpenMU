// <copyright file="AIStateMachineExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.AIStateMachine;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Decision;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.AIPlayer.Warp;

/// <summary>
/// 状态机执行器 — 负责任务阶段的执行、中断处理、跨图移动、生存补给等。
/// 每 tick 被 <see cref="HeartbeatService"/> 调用一次，处理一个任务步骤。
/// 内部维护脚本执行器、药水冷却和 NPC 对话状态。
/// </summary>
public sealed class AIStateMachineExecutor
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly BehaviorContext _context;
    private readonly IReadOnlyDictionary<string, IBehaviorSubModule> _modules;
    private readonly IBehaviorSubModule _survival;
    private readonly NpcInteractionService _npcService;
    private readonly WarpPlanner _warpPlanner;
    private readonly ILogger _logger;

    /// <summary>看板脚本执行器 — 当前活跃任务的 ScriptExecutor。</summary>
    private ScriptExecutor? _scriptExecutorField;

    /// <summary>中断上下文。</summary>
    private InterruptContext? _pendingInterrupt;

    private int _npcDialogTicks;

    /// <summary>血量药水定义 (Group, Number, Name)。</summary>
    private static readonly (byte Group, short Number)[] HpPotions =
    {
        (14, 3),   // Large Healing
        (14, 2),   // Medium Healing
        (14, 1),   // Small Healing
    };

    /// <summary>法力药水定义 (Group, Number, Name)。</summary>
    private static readonly (byte Group, short Number)[] MpPotions =
    {
        (14, 6),   // Large Mana
        (14, 5),   // Medium Mana
        (14, 4),   // Small Mana
    };

    /// <summary>药水冷却跟踪。</summary>
    private DateTime _lastHpPotionTime = DateTime.MinValue;

    /// <summary>法力药水冷却跟踪。</summary>
    private DateTime _lastMpPotionTime = DateTime.MinValue;

    /// <summary>药水冷却 (2秒)。</summary>
    private static readonly TimeSpan PotionCooldown = TimeSpan.FromSeconds(2);

    /// <summary>低血量阈值 — 提升到 60% 以便在危险地图有足够反应时间。</summary>
    private const float LowHpThreshold = 0.6f;

    /// <summary>低法力阈值。</summary>
    private const float LowMpThreshold = 0.25f;

    /// <summary>
    /// Gets the currently active behavior submodule, if any.
    /// </summary>
    public IBehaviorSubModule? ActiveModule { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a script executor is currently loaded and active.
    /// Used by <see cref="HeartbeatService"/> to determine NPC dialog handling.
    /// </summary>
    internal bool HasActiveScript => this._scriptExecutorField is not null;

    /// <summary>
    /// Gets or sets the pending interrupt context. Set by <see cref="HeartbeatService"/> when an interrupt is triggered.
    /// </summary>
    public InterruptContext? PendingInterrupt
    {
        get => this._pendingInterrupt;
        set => this._pendingInterrupt = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AIStateMachineExecutor"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="adapter">The game adapter.</param>
    /// <param name="context">The behavior context.</param>
    /// <param name="modules">The loaded behavior submodules.</param>
    /// <param name="npcService">The NPC interaction service.</param>
    /// <param name="warpPlanner">The warp planner for cross-map routing.</param>
    /// <param name="logger">The logger.</param>
    public AIStateMachineExecutor(
        AiPlayer player,
        IGameAdapter adapter,
        BehaviorContext context,
        IReadOnlyDictionary<string, IBehaviorSubModule> modules,
        NpcInteractionService npcService,
        WarpPlanner warpPlanner,
        ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._context = context;
        this._modules = modules;
        this._survival = modules["survival"];
        this._npcService = npcService;
        this._warpPlanner = warpPlanner;
        this._logger = logger;
    }

    /// <summary>状态机执行一步。返回执行结果。</summary>
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem task)
    {
        var stageResult = await this.ExecuteCurrentStageAsync(task).ConfigureAwait(false);
        return stageResult switch
        {
            StageResult.Completed => StepResult.Completed,
            StageResult.InProgress => StepResult.InProgress,
            StageResult.Failed => StepResult.Failed,
            StageResult.NoTarget => StepResult.NoTarget,
            _ => StepResult.InProgress,
        };
    }

    /// <summary>处理中断（死亡/卡住）。</summary>
    public async ValueTask HandleInterruptAsync()
    {
        var ctx = this._pendingInterrupt!;
        this._logger.LogInformation("[SM] 中断: {Reason}", ctx.Reason);

        var temp = new MissionItem
        {
            Id = $"intr_{ctx.Reason}",
            Title = ctx.Reason,
            Priority = 0,
            Type = MissionType.Emergency,
            Module = "survival",
        };

        var result = await this._survival.ExecuteStepAsync(temp).ConfigureAwait(false);
        if (result != StepResult.InProgress)
        {
            this._pendingInterrupt = null;
            this._logger.LogInformation("[SM] 中断处理完毕");
        }
    }

    /// <summary>跨地图移动。返回是否到达目标。</summary>
    public async ValueTask<bool> ExecuteWarpAsync(ushort targetMap)
    {
        var currentMapNum = (ushort)(this._adapter.GetCurrentMap()?.Definition.Number ?? 0);
        if (currentMapNum == targetMap)
        {
            this._context.TargetMapNumber = null;
            return true;
        }

        // 如果当前有活跃路由，先推进
        if (this._context.ActiveWarpRoute is not null)
        {
            var stepIdx = this._context.ActiveWarpStepIndex;
            var steps = this._context.ActiveWarpRoute.Steps;

            if (stepIdx >= steps.Count)
            {
                // 路由完成
                this._context.ActiveWarpRoute = null;
                this._context.ActiveWarpStepIndex = 0;
                this._context.TargetMapNumber = null;
                this._logger.LogInformation("[WarpRoute] ✅ 跨图传送完成（目标地图 #{Map}）", targetMap);
                return true;
            }

            // 检查当前是否在地图传送等待中
            if (this._context.WarpInProgress)
            {
                // 等待地图切换完成
                if (this._adapter.GetCurrentMap() is not null)
                {
                    this._context.WarpInProgress = false;
                    this._context.ActiveWarpStepIndex++;
                }

                return false;
            }

            // 执行下一步
            await this.ExecuteWarpStepAsync(steps[stepIdx]).ConfigureAwait(false);
            return false;
        }

        // 没有活跃路由 → 计算新路由
        var route = this._warpPlanner.ComputeRoute(
            (short)currentMapNum,
            (short)targetMap,
            this._adapter.GetPlayerLevel(),
            this._player.Money);

        if (route is null || !route.IsFeasible)
        {
            this._logger.LogWarning("[WarpRoute] 无法找到从地图 #{From} 到 #{To} 的可行路线",
                currentMapNum, targetMap);
            return false;
        }

        this._context.ActiveWarpRoute = route;
        this._context.ActiveWarpStepIndex = 0;
        this._context.TargetMapNumber = targetMap;
        this._logger.LogInformation("[WarpRoute] 开始跨图传送: {From}→{To} ({Steps}步, 金币={Gold})",
            currentMapNum, targetMap, route.Steps.Count, route.TotalGoldCost);
        return false;
    }

    /// <summary>生存检查：嗑药。</summary>
    public async ValueTask TryConsumePotionsAsync()
    {
        try
        {
            var inv = this._player.Inventory;
            if (inv is null) return;

            var maxHp = this._adapter.GetMaxHp();
            var hp = this._adapter.GetCurrentHp();
            var maxMp = this._adapter.GetMaxMp();
            var mp = this._adapter.GetCurrentMp();
            var now = DateTime.UtcNow;

            // HP 药水
            if (maxHp > 0 && (float)hp / maxHp < LowHpThreshold
                && now - this._lastHpPotionTime >= PotionCooldown)
            {
                foreach (var (group, number) in HpPotions)
                {
                    var potion = inv.Items.FirstOrDefault(i =>
                        i.Definition?.Group == group && i.Definition?.Number == number && i.Durability > 0);
                    if (potion is null) continue;

                    await this._adapter.ConsumeItemAsync(potion.ItemSlot).ConfigureAwait(false);
                    this._lastHpPotionTime = now;
                    this._logger.LogDebug("[SM] ❤️ 使用 HP 药水 (G{Group}N{Number})", group, number);
                    break;
                }
            }

            // MP 药水
            if (maxMp > 0 && (float)mp / maxMp < LowMpThreshold
                && now - this._lastMpPotionTime >= PotionCooldown)
            {
                foreach (var (group, number) in MpPotions)
                {
                    var potion = inv.Items.FirstOrDefault(i =>
                        i.Definition?.Group == group && i.Definition?.Number == number && i.Durability > 0);
                    if (potion is null) continue;

                    await this._adapter.ConsumeItemAsync(potion.ItemSlot).ConfigureAwait(false);
                    this._lastMpPotionTime = now;
                    this._logger.LogDebug("[SM] 💙 使用 MP 药水 (G{Group}N{Number})", group, number);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[SM] 嗑药失败: {Msg}", ex.Message);
        }
    }

    /// <summary>复活后补给。</summary>
    public async ValueTask PostRespawnSupplyAsync()
    {
        try
        {
            var map = this._adapter.GetCurrentMap();
            if (map is null) return;

            var npc = map.GetNpcsInRange(this._player.Position, 50)
                .FirstOrDefault(n => n.Definition?.Number == 226
                                  || n.Definition?.Number == 240
                                  || n.Definition?.Number == 233);
            if (npc is null)
            {
                this._logger.LogDebug("[SM] 复活补给: 附近无商店NPC");
                return;
            }

            if (this._player.Position.EuclideanDistanceTo(npc.Position) > 3f)
            {
                await this._adapter.WalkToAsync(
                    new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
                return;
            }

            if (this._player.OpenedNpc != npc)
            {
                var talkAction = new TalkNpcAction();
                await talkAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);
                return;
            }

            await this._npcService.RepairAllEquipmentAsync(this._player).ConfigureAwait(false);
            await this._npcService.BuyPotionsAsync(this._player, 10).ConfigureAwait(false);

            await this.CloseNpcDialogAsync().ConfigureAwait(false);
            this._logger.LogInformation("[SM] ✅ 复活补给完成 (修理+买药)");
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[SM] 复活补给失败: {Msg}", ex.Message);
        }
    }

    /// <summary>手动复活。</summary>
    public async ValueTask RespawnPlayerAsync()
    {
        try
        {
            this._logger.LogWarning("[SM] 💀 执行手动复活...");
            await this._player.WarpToSafezoneAsync().ConfigureAwait(false);
            this._logger.LogInformation("[SM] ✅ WarpToSafezoneAsync 完成");

            // WarpToSafezoneAsync 只传送不恢复HP — 手动恢复满属性
            foreach (var regen in Stats.IntervalRegenerationAttributes)
            {
                this._player.Attributes![regen.CurrentAttribute] = this._player.Attributes[regen.MaximumAttribute];
            }

            this._player.IsAlive = true;

            // 重置 ScriptExecutor 状态，确保复活后能正常处理任务
            this._scriptExecutorField = null;

            // 如果传送后 CurrentMap 为 null（等待客户端确认），直接确认地图变换
            if (this._player.CurrentMap is null)
            {
                await this._player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                this._logger.LogInformation("[SM] ✅ ClientReadyAfterMapChangeAsync 完成（手动复活）");
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[SM] 💀 手动复活异常");
        }
    }

    /// <summary>关闭 NPC 对话框。</summary>
    internal async ValueTask CloseNpcDialogAsync()
    {
        var closeAction = new CloseNpcDialogAction();
        await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
    }

    /// <summary>执行当前任务的当前阶段。</summary>
    private async ValueTask<StageResult> ExecuteCurrentStageAsync(MissionItem task)
    {
        // 没有阶段树或所有阶段完成 → 整任务完成
        if (task.Stages.Count == 0 || task.CurrentStageIndex >= task.Stages.Count)
        {
            // 脚本模式
            if (task.Script is not null)
            {
                var scriptResult = await this.ExecuteScriptAsync(task).ConfigureAwait(false);
                return scriptResult;
            }

            // 无阶段树也无脚本 → 模块模式
            if (!this._modules.TryGetValue(task.Module, out var module))
            {
                this._logger.LogWarning("[SM] 未知模块: {Mod}", task.Module);
                return StageResult.Failed;
            }

            this.ActiveModule = module;
            return await module.ExecuteStepAsync(task).ConfigureAwait(false) switch
            {
                StepResult.Completed => StageResult.Completed,
                StepResult.InProgress => StageResult.InProgress,
                StepResult.Failed => StageResult.Failed,
                StepResult.NoTarget => StageResult.NoTarget,
                _ => StageResult.InProgress,
            };
        }

        // 有阶段树 → 按阶段执行
        var stage = task.Stages[task.CurrentStageIndex];
        this._logger.LogDebug("[SM] ▶ {Title} stage={StageLabel}({StageIdx})", task.Title, stage.Label, task.CurrentStageIndex);

        // 脚本优先：有脚本时不管哪个阶段都用 ScriptExecutor
        // ScriptExecutor 的段落脚本会处理 accept/hunt/submit 的全自动流转
        if (task.Script is not null)
        {
            return await this.ExecuteScriptAsync(task).ConfigureAwait(false);
        }

        // 无脚本时的狩猎阶段：用模块执行
        if (stage.Id == "hunt" && this._modules.TryGetValue(task.Module, out var huntModule))
        {
            this.ActiveModule = huntModule;
            var result = await huntModule.ExecuteStepAsync(task).ConfigureAwait(false);
            if (result == StepResult.Completed)
            {
                stage.Status = TaskStageStatus.Completed;
                task.CurrentStageIndex++;
                this._logger.LogInformation("[SM] ✅ stage={Stage} 完成 → 进入下一阶段", stage.Label);
                return StageResult.InProgress; // 继续下一阶段
            }

            return StageResult.InProgress;
        }

        // fallback: 模块模式
        if (!this._modules.TryGetValue(task.Module, out var mod))
            return StageResult.Failed;
        this.ActiveModule = mod;
        return await mod.ExecuteStepAsync(task).ConfigureAwait(false) switch
        {
            StepResult.Completed => StageResult.Completed,
            StepResult.InProgress => StageResult.InProgress,
            StepResult.Failed => StageResult.Failed,
            StepResult.NoTarget => StageResult.NoTarget,
            _ => StageResult.InProgress,
        };
    }

    /// <summary>用 ScriptExecutor 执行脚本一步。</summary>
    private async ValueTask<StageResult> ExecuteScriptAsync(MissionItem item)
    {
        if (item.Script is null) return StageResult.Failed;

        var scriptId = item.Script.Id;
        var lastScriptId = this._scriptExecutorField?.Script?.Id;

        if (this._scriptExecutorField is null && item.Script is not null)
        {
            // 使用共享的 BehaviorContext — WorldState 每 tick 被刷新
            this._scriptExecutorField = new ScriptExecutor(this._player, this._context, item.Script!);
        }
        else if (item.Script is not null && scriptId != lastScriptId)
        {
            this._scriptExecutorField!.ReloadScript(item.Script);
        }

        var tickResult = await this._scriptExecutorField!.TickAsync().ConfigureAwait(false);
        if (tickResult.State == ScriptTaskState.Completed)
        {
            // 脚本完成 → 标记当前阶段完成并推进
            if (item.CurrentStageIndex < item.Stages.Count)
            {
                item.Stages[item.CurrentStageIndex].Status = TaskStageStatus.Completed;
                item.CurrentStageIndex++;
            }

            // 没有更多阶段 → 整个任务完成
            if (item.CurrentStageIndex >= item.Stages.Count)
                return StageResult.Completed;
            return StageResult.InProgress; // 继续下阶段
        }

        if (tickResult.State == ScriptTaskState.Stuck)
            return StageResult.Failed;
        return StageResult.InProgress;
    }

    /// <summary>执行路由单步（门传送或传送菜单）。</summary>
    private async ValueTask ExecuteWarpStepAsync(WarpStep step)
    {
        if (step.Method == WarpEdgeType.Gate)
        {
            // 门传送：走到门中心 → 进门
            var currentMap = this._adapter.GetCurrentMap();
            if (currentMap is null) return;

            var pos = this._adapter.GetPlayerPosition();
            var atGate = Math.Abs(pos.X - step.GateCenter.X) <= 3
                      && Math.Abs(pos.Y - step.GateCenter.Y) <= 3;

            if (!atGate)
            {
                await this._adapter.WalkToAsync(step.GateCenter, currentMap).ConfigureAwait(false);
                return;
            }

            if (step.EnterGate is not null)
            {
                var warpAction = new WarpGateAction();
                await warpAction.EnterGateAsync(this._player, step.EnterGate).ConfigureAwait(false);
                this._context.WarpInProgress = true;
            }
        }
        else if (step.WarpInfo is not null)
        {
            // 传送菜单：直接使用 WarpAction
            var warpAction = new WarpAction();
            await warpAction.WarpToAsync(this._player, step.WarpInfo).ConfigureAwait(false);
            this._context.WarpInProgress = true;
        }
    }
}
