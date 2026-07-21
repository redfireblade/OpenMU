namespace MUnique.OpenMU.BotClient.Core;

/// <summary>每个 BOT 的运行期上下文（行为树的黑板 Blackboard）。</summary>
/// <remarks>
/// 存储 AI 决策过程中产生的中间状态：
/// - FSM 当前状态
/// - 战斗/拾取/NPC 目标
/// - 计时器
/// - 行为树运行状态
/// 所有字段由调度器线程独占读写，无需加锁。
/// </remarks>
public sealed class BotContext
{
    // ─── 身份信息 ───
    public int BotId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 55901;

    // ─── FSM 当前状态 ───
    public FsmStateType CurrentFsmState { get; set; } = FsmStateType.Idle;

    // ─── 行为树输出 ───
    public ActionCommand LastActionCommand { get; set; } = ActionCommand.None;

    // ─── 战斗目标 ───
    public ushort? TargetMonsterId { get; set; }
    public int TargetMonsterX { get; set; }
    public int TargetMonsterY { get; set; }

    // ─── 拾取目标 ───
    public ushort? TargetItemId { get; set; }

    // ─── NPC 交互 ───
    public ushort? TargetNpcId { get; set; }

    // ─── 计时器（基于 tick 计数） ───
    public int TicksSinceLastHeal { get; set; }
    public int TicksSinceLastAction { get; set; }

    // ─── 巡逻路径 ───
    public byte PatrolTargetX { get; set; }
    public byte PatrolTargetY { get; set; }
    public int PatrolStepsRemaining { get; set; }

    // ─── 行为树运行状态 ───
    public int RunningNodeId { get; set; } = -1;

    // ─── 死亡/复活 ───
    public bool IsDead { get; set; }
    public int DeathCount { get; set; }
    public int RespawnTimerTicks { get; set; }

    // ─── 调试统计 ───
    public int TotalActions { get; set; }
    public int TotalKills { get; set; }
    public int TotalPickups { get; set; }
}
