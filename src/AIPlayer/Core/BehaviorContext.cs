// <copyright file="BehaviorContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.AIPlayer.Warp;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 模块间共享上下文。
/// Contains game-specific world state, targets, services, and Tick recording.
/// </summary>
public sealed class BehaviorContext
{
    /// <summary>当前玩家引用（所有模块共享）。</summary>
    public AiPlayer Player { get; }

    /// <summary>当前世界状态快照（每 Tick 刷新一次，避免重复查询 GameMap）。</summary>
    public WorldState WorldState { get; set; } = new();

    /// <summary>当前目标（由 CombatModule 设置，NavigationModule 读取）。</summary>
    public IAttackable? CurrentTarget { get; set; }

    /// <summary>紧急撤退标志（SurvivalModule → NavigationModule）。</summary>
    public bool EmergencyRetreat { get; set; }

    /// <summary>
    /// 当设置为 true 时，NavigationModule 应跳过自动寻路（走向 CurrentTarget 或巡逻）。
    /// 由 InteractionModule 在 NPC 交互进行中时设置，避免 NavigationModule 覆盖交互行走。
    /// </summary>
    public bool SuppressAutoNavigation { get; set; }

    /// <summary>目标地图编号。到达后置 null。</summary>
    public ushort? TargetMapNumber { get; set; }

    /// <summary>当前多跳传送路线（不为 null 时表示正在路由中）。</summary>
    public WarpRoute? ActiveWarpRoute { get; set; }

    /// <summary>当前路由步骤索引。</summary>
    public int ActiveWarpStepIndex { get; set; }

    /// <summary>Warp 进行中，等待地图切换完成。</summary>
    public bool WarpInProgress { get; set; }

    /// <summary>经验记忆（跨模块共享）。</summary>
    public ExperienceMemory? ExperienceMemory { get; set; }

    /// <summary>最后加点时间戳。</summary>
    public DateTime LastStatTick { get; set; } = DateTime.UtcNow;

    /// <summary>最后装备检查时间戳。</summary>
    public DateTime LastEquipCheck { get; set; } = DateTime.UtcNow;

    /// <summary>最后记忆更新时间戳。</summary>
    public DateTime LastMemoryUpdate { get; set; } = DateTime.UtcNow;

    /// <summary>最后经验合并时间戳。</summary>
    public DateTime LastConsolidationTick { get; set; } = DateTime.UtcNow;

    /// <summary>游戏操作适配器（模块通过此接口操作游戏，不直接依赖 Player/GameContext）。</summary>
    public IGameAdapter GameAdapter { get; set; } = null!;

    /// <summary>当前 Tick 各阶段耗时 (μs)。</summary>
    public TickTiming Timing { get; set; }

    // ===== Tick 决策录制（用于测试客户端调试） =====

    private readonly List<ModuleDecision> _currentTickDecisions = new();
    private readonly List<TickSnapshot> _tickHistory = new(capacity: 1024);
    private int _recordingTickNumber;
    private Point _recordingPosition;

    /// <summary>获取 Tick 历史记录（测试客户端使用）。</summary>
    public IReadOnlyList<TickSnapshot> TickHistory => this._tickHistory;

    /// <summary>开始录制当前 Tick 的模块决策。</summary>
    public void BeginTickRecording(int tickNumber, Point position)
    {
        this._recordingTickNumber = tickNumber;
        this._recordingPosition = position;
        this._currentTickDecisions.Clear();
    }

    /// <summary>录制单个模块的决策结果。</summary>
    public void RecordDecision(string moduleName, TimeSpan duration)
    {
        this._currentTickDecisions.Add(new ModuleDecision(moduleName, duration));
    }

    /// <summary>结束录制并生成 TickSnapshot，存入历史缓冲区。</summary>
    public void EndTickRecording(uint hp, uint maxHp, uint mp, uint maxMp, int level, ushort mapId)
    {
        var snapshot = new TickSnapshot(
            this._recordingTickNumber,
            this._recordingPosition,
            hp,
            maxHp,
            mp,
            maxMp,
            level,
            mapId,
            SurvivalLevel.Normal,
            this.CurrentTarget is not null,
            this.EmergencyRetreat,
            this._currentTickDecisions.ToArray(),
            this.Timing);

        if (this._tickHistory.Count >= 1024)
        {
            this._tickHistory.RemoveAt(0);
        }

        this._tickHistory.Add(snapshot);
    }

    /// <summary>获取最新的 TickSnapshot（测试客户端快捷方法）。</summary>
    public TickSnapshot? GetLatestSnapshot()
        => this._tickHistory.Count > 0 ? this._tickHistory[^1] : null;

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorContext"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    public BehaviorContext(AiPlayer player)
    {
        this.Player = player;
    }
}

/// <summary>Tick 各阶段耗时 (微秒)。</summary>
public struct TickTiming
{
    public long WorldRefreshUs;
    public long ScriptExecutionUs;
    public long TotalUs;
}

/// <summary>世界状态快照（每 Tick 采集一次）。</summary>
public sealed class WorldState
{
    /// <summary>范围内的可攻击对象（怪物 + 可 PVP 玩家）。</summary>
    public IList<IAttackable> AttackablesInRange { get; init; } = new List<IAttackable>();

    /// <summary>范围内的掉落物品。</summary>
    public IList<DroppedItem> DropsInRange { get; init; } = new List<DroppedItem>();

    /// <summary>范围内的 NPC。</summary>
    public IList<NonPlayerCharacter> NpcsInRange { get; init; } = new List<NonPlayerCharacter>();

    /// <summary>当前地图。</summary>
    public GameMap? CurrentMap { get; init; }

    /// <summary>当前玩家位置。</summary>
    public Point PlayerPosition { get; init; }

    /// <summary>是否在安全区。</summary>
    public bool IsAtSafezone { get; init; }

    /// <summary>范围内其他非队友玩家/AI（用于防堆叠和热点占用检测）。</summary>
    public IList<Player> OtherPlayersInRange { get; init; } = new List<Player>();
}
