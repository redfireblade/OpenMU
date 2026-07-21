using MUnique.OpenMU.BotClient.Core;

namespace MUnique.OpenMU.BotClient.FSM;

/// <summary>有限状态机状态接口。</summary>
public interface IState
{
    /// <summary>状态类型标识。</summary>
    FsmStateType Type { get; }

    /// <summary>进入状态时调用。</summary>
    void OnEnter(BotContext context, WorldMirror mirror);

    /// <summary>每 tick 调用，输出动作指令。</summary>
    void OnUpdate(BotContext context, WorldMirror mirror, out ActionCommand cmd);

    /// <summary>离开状态时调用。</summary>
    void OnExit(BotContext context, WorldMirror mirror);

    /// <summary>检测是否需要切换到其他状态。返回自身类型表示不切换。</summary>
    FsmStateType CheckTransition(BotContext context, WorldMirror mirror);
}
