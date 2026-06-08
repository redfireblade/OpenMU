// <copyright file="AiEventBus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// AI 事件总线 — 每个 AI 角色私有实例。
/// 提供 Subscribe/Publish/Drain 机制，事件按优先级处理。
/// </summary>
public sealed class AiEventBus
{
    private readonly Dictionary<Type, Action<AiEvent>> _handlers = new();
    private readonly PriorityQueue<AiEvent, int> _queue = new();
    private readonly object _lock = new();

    /// <summary>
    /// 注册指定事件类型的处理器。
    /// 同一事件类型只允许注册一个处理器（后注册覆盖前者）。
    /// </summary>
    /// <typeparam name="TEvent">事件类型，必须继承 <see cref="AiEvent"/>。</typeparam>
    /// <param name="handler">事件处理委托。</param>
    public void Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : AiEvent
    {
        this._handlers[typeof(TEvent)] = evt => handler((TEvent)evt);
    }

    /// <summary>
    /// 发布事件到总线队列。
    /// 事件会根据优先级排序，高优先级（数值小）先处理。
    /// </summary>
    /// <param name="evt">事件实例。</param>
    public void Publish(AiEvent evt)
    {
        lock (this._lock)
        {
            this._queue.Enqueue(evt, evt.Priority);
        }
    }

    /// <summary>
    /// 清空事件队列，按优先级依次处理所有已注册的事件。
    /// 处理过程中新发布的事件也会在本次 drain 中处理（入队顺序）。
    /// </summary>
    public void DrainEvents()
    {
        while (true)
        {
            AiEvent evt;
            int priority;
            lock (this._lock)
            {
                if (this._queue.Count == 0)
                {
                    break;
                }

                evt = this._queue.Dequeue();
                priority = evt.Priority; // capture before possible re-enter
            }

            this.DispatchEvent(evt);
        }
    }

    /// <summary>
    /// 清空事件队列，不处理（丢弃所有事件）。
    /// </summary>
    public void Clear()
    {
        lock (this._lock)
        {
            this._queue.Clear();
        }
    }

    /// <summary>
    /// 当前队列中的事件数。
    /// </summary>
    public int Count
    {
        get
        {
            lock (this._lock)
            {
                return this._queue.Count;
            }
        }
    }

    private void DispatchEvent(AiEvent evt)
    {
        if (this._handlers.TryGetValue(evt.GetType(), out var handler))
        {
            handler(evt);
        }
    }
}
