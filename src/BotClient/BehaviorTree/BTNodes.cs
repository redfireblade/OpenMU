using MUnique.OpenMU.BotClient.Core;

namespace MUnique.OpenMU.BotClient.BehaviorTree;

/// <summary>行为树节点执行状态。</summary>
public enum NodeStatus
{
    /// <summary>执行成功。</summary>
    Success,

    /// <summary>执行失败。</summary>
    Failure,

    /// <summary>正在执行（未完成）。</summary>
    Running,
}

/// <summary>行为树节点基类。所有节点类型继承自此类。</summary>
/// <remarks>
/// 节点本身不存储实例状态（Flyweight 模式），
/// 所有运行时数据在 Blackboard（BotContext）中。
/// 100 个 BOT 共享同一棵节点树。
/// </remarks>
public abstract class BTNode
{
    /// <summary>节点标识，用于调试和 Running 状态恢复。</summary>
    public int NodeId { get; set; }

    /// <summary>节点名称，用于调试输出。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>执行一次节点逻辑。</summary>
    public abstract NodeStatus Tick(BotContext ctx, WorldMirror wm);
}

/// <summary>
/// 选择节点（Selector/Fallback）。
/// 从左到右依次执行子节点，遇到第一个 SUCCESS 即返回 SUCCESS。
/// 所有子节点 FAILURE 则返回 FAILURE。
/// 相当于逻辑 OR。
/// </summary>
public sealed class SelectorNode : BTNode
{
    private readonly List<BTNode> _children = new();

    public SelectorNode() { }

    public SelectorNode(params BTNode[] children) => _children.AddRange(children);

    /// <summary>添加子节点。</summary>
    public SelectorNode Add(BTNode child) { _children.Add(child); return this; }

    /// <summary>子节点列表（用于 JSON 反序列化）。</summary>
    public IReadOnlyList<BTNode> Children => _children;

    public override NodeStatus Tick(BotContext ctx, WorldMirror wm)
    {
        foreach (var child in _children)
        {
            var status = child.Tick(ctx, wm);
            if (status != NodeStatus.Failure)
                return status;
        }
        return NodeStatus.Failure;
    }
}

/// <summary>
/// 顺序节点（Sequence）。
/// 从左到右依次执行子节点，遇到第一个 FAILURE 即返回 FAILURE。
/// 全部子节点 SUCCESS 则返回 SUCCESS。
/// 相当于逻辑 AND。
/// </summary>
public sealed class SequenceNode : BTNode
{
    private readonly List<BTNode> _children = new();

    public SequenceNode() { }

    public SequenceNode(params BTNode[] children) => _children.AddRange(children);

    /// <summary>添加子节点。</summary>
    public SequenceNode Add(BTNode child) { _children.Add(child); return this; }

    /// <summary>子节点列表（用于 JSON 反序列化）。</summary>
    public IReadOnlyList<BTNode> Children => _children;

    public override NodeStatus Tick(BotContext ctx, WorldMirror wm)
    {
        foreach (var child in _children)
        {
            var status = child.Tick(ctx, wm);
            if (status != NodeStatus.Success)
                return status;
        }
        return NodeStatus.Success;
    }
}

/// <summary>条件节点。仅判断条件是否成立，不产生动作。</summary>
public sealed class ConditionNode : BTNode
{
    private readonly Func<BotContext, WorldMirror, bool> _predicate;

    public ConditionNode(Func<BotContext, WorldMirror, bool> predicate, string name = "")
    {
        _predicate = predicate;
        Name = name;
    }

    public override NodeStatus Tick(BotContext ctx, WorldMirror wm)
    {
        return _predicate(ctx, wm) ? NodeStatus.Success : NodeStatus.Failure;
    }
}

/// <summary>
/// 动作节点。产生一个 ActionCommand 写入 Blackboard，
/// 由外层调度器读取并执行。
/// </summary>
public sealed class ActionNode : BTNode
{
    private readonly ActionCommand _command;

    public ActionNode(ActionCommand command, string name = "")
    {
        _command = command;
        Name = name;
    }

    /// <summary>获取此节点产生的动作指令。</summary>
    public ActionCommand Command => _command;

    public override NodeStatus Tick(BotContext ctx, WorldMirror wm)
    {
        ctx.LastActionCommand = _command;
        return NodeStatus.Success;
    }
}

/// <summary>反转节点（Inverter）。将子节点的结果反转：SUCCESS↔FAILURE。</summary>
public sealed class InverterNode : BTNode
{
    private readonly BTNode _child;

    public InverterNode(BTNode child) => _child = child;

    public override NodeStatus Tick(BotContext ctx, WorldMirror wm)
    {
        var status = _child.Tick(ctx, wm);
        return status switch
        {
            NodeStatus.Success => NodeStatus.Failure,
            NodeStatus.Failure => NodeStatus.Success,
            _ => NodeStatus.Running,
        };
    }
}

/// <summary>行为树引擎。包装根节点，提供 Tick 入口。</summary>
public sealed class BehaviorTreeEngine
{
    private readonly BTNode _root;

    public BehaviorTreeEngine(BTNode root) => _root = root;

    /// <summary>执行一次完整的树遍历，产生动作指令。</summary>
    public ActionCommand Tick(BotContext ctx, WorldMirror wm)
    {
        ctx.LastActionCommand = ActionCommand.None;
        _root.Tick(ctx, wm);
        return ctx.LastActionCommand;
    }
}
