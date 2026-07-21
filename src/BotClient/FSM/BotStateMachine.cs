using MUnique.OpenMU.BotClient.Core;
using MUnique.OpenMU.BotClient.FSM.States;

namespace MUnique.OpenMU.BotClient.FSM;

/// <summary>有限状态机引擎。管理状态注册、切换和 tick 驱动。</summary>
public sealed class BotStateMachine
{
    private readonly Dictionary<FsmStateType, IState> _states;
    private IState? _currentState;

    /// <summary>初始化状态机并注册所有状态。</summary>
    public BotStateMachine()
    {
        _states = new Dictionary<FsmStateType, IState>
        {
            [FsmStateType.Idle] = new IdleState(),
            [FsmStateType.Patrol] = new PatrolState(),
            [FsmStateType.Chase] = new ChaseState(),
            [FsmStateType.Attack] = new AttackState(),
            [FsmStateType.Loot] = new LootState(),
            [FsmStateType.Evade] = new EvadeState(),
        };
    }

    /// <summary>获取当前状态类型。</summary>
    public FsmStateType CurrentType => _currentState?.Type ?? FsmStateType.Idle;

    /// <summary>启动状态机，从 Idle 开始。</summary>
    public void Start(BotContext context, WorldMirror mirror)
    {
        _currentState = _states[FsmStateType.Idle];
        context.CurrentFsmState = FsmStateType.Idle;
        _currentState.OnEnter(context, mirror);
    }

    /// <summary>执行一次 AI tick：先检查转换条件，再执行当前状态。</summary>
    /// <param name="context">BOT 上下文（黑板）。</param>
    /// <param name="mirror">世界镜像。</param>
    /// <returns>动作指令。</returns>
    public ActionCommand Tick(BotContext context, WorldMirror mirror)
    {
        if (_currentState == null)
        {
            Start(context, mirror);
            return ActionCommand.None;
        }

        // 1. 检测状态转换
        var nextType = _currentState.CheckTransition(context, mirror);
        if (nextType != _currentState.Type)
        {
            _currentState.OnExit(context, mirror);
            _currentState = _states[nextType];
            context.CurrentFsmState = nextType;
            _currentState.OnEnter(context, mirror);
        }

        // 2. 执行当前状态
        _currentState.OnUpdate(context, mirror, out var cmd);
        return cmd;
    }
}
