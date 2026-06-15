// <copyright file="AiViewPlugInContainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// AI 角色的视图插件容器。
/// 对所有 IViewPlugIn 返回默认值（null），除了 IChatViewPlugIn：
/// 收到聊天消息时存入消息缓存队列，供 GameAdapter.DrainChatMessages 排出。
/// </summary>
internal sealed class AiViewPlugInContainer : ICustomPlugInContainer<IViewPlugIn>
{
    /// <summary>
    /// 聊天消息缓存队列（线程安全，每次 Drain 批量取出）。
    /// </summary>
    internal readonly List<(string Sender, string Message, ChatMessageType Type)> ChatBuffer = new();

    public AiViewPlugInContainer(AiPlayer player)
    {
    }

    /// <inheritdoc />
    public T? GetPlugIn<T>()
        where T : class, IViewPlugIn
    {
        // 只处理 IChatViewPlugIn — 其他插件返回 null（无操作）
        if (typeof(T) == typeof(IChatViewPlugIn))
        {
            return (T)(object)new ChatBufferPlugIn(this);
        }

        return default;
    }

    /// <summary>
    /// 自定义 ChatViewPlugIn：收到聊天消息时存入缓存队列。
    /// 游戏引擎通过 <c>player.InvokeViewPlugInAsync&lt;IChatViewPlugIn&gt;(p => p.ChatMessageAsync(...))</c>
    /// 向所有在线玩家广播聊天消息时，该实现将消息缓存到 ChatBuffer 中。
    /// </summary>
    private sealed class ChatBufferPlugIn : IChatViewPlugIn
    {
        private readonly AiViewPlugInContainer _container;

        public ChatBufferPlugIn(AiViewPlugInContainer container)
        {
            this._container = container;
        }

        /// <inheritdoc />
        public ValueTask ChatMessageAsync(string message, string sender, ChatMessageType type)
        {
            lock (this._container.ChatBuffer)
            {
                this._container.ChatBuffer.Add((sender, message, type));
            }

            return ValueTask.CompletedTask;
        }
    }
}
