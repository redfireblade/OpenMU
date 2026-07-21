namespace MUnique.OpenMU.BotClient.Core;

/// <summary>AI 系统配置参数。所有数值可调，支持 JSON 反序列化。</summary>
public sealed class AIConfig
{
    // ─── 时间参数 ───
    /// <summary>调度器 tick 间隔（毫秒）。越小调度越精细，但 CPU 开销越大。</summary>
    public int SchedulerTickMs { get; init; } = 10;

    /// <summary>每个 BOT 的决策周期（毫秒）。每个 BOT 每隔此时间执行一次 AI 决策。</summary>
    public int DecisionIntervalMs { get; init; } = 200;

    /// <summary>心跳周期（毫秒），用于维持连接的 keep-alive。</summary>
    public int HeartbeatIntervalMs { get; init; } = 400;

    /// <summary>网络操作超时（秒），登录等待/包超时。</summary>
    public int NetworkTimeoutSec { get; init; } = 10;

    // ─── 战斗参数 ───
    /// <summary>攻击距离（切比雪夫距离）。</summary>
    public int AttackRange { get; init; } = 3;

    /// <summary>视野范围（切比雪夫距离）。</summary>
    public int VisionRange { get; init; } = 15;

    /// <summary>喝药 HP 阈值（百分比）。</summary>
    public int HealThresholdPercent { get; init; } = 30;

    /// <summary>喝药间隔（tick 数）。</summary>
    public int HealCooldownTicks { get; init; } = 5;

    /// <summary>低血触发逃跑的阈值（百分比）。</summary>
    public int EvadeThresholdPercent { get; init; } = 20;

    /// <summary>逃跑后恢复 Patrol 的 HP 阈值（百分比）。</summary>
    public int EvadeRecoverPercent { get; init; } = 50;

    // ─── BOT 数量 ───
    /// <summary>BOT 实例总数。</summary>
    public int BotCount { get; init; } = 100;

    /// <summary>AI 决策模式。</summary>
    public BotDecisionMode DecisionMode { get; init; } = BotDecisionMode.FiniteStateMachine;

    /// <summary>启用高精度定时器（timeBeginPeriod(1)）。</summary>
    public bool HighPrecisionTimer { get; init; } = true;

    // ─── 行为参数 ───
    /// <summary>最小随机行走步数。</summary>
    public int WalkStepsMin { get; init; } = 1;

    /// <summary>最大随机行走步数。</summary>
    public int WalkStepsMax { get; init; } = 3;

    // ─── 派生属性 ───
    /// <summary>每个 tick 处理的 BOT 数量。</summary>
    public int BotsPerTick => Math.Max(1, BotCount * SchedulerTickMs / DecisionIntervalMs);
}
