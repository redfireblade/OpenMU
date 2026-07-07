// <copyright file="AiViewPlugInContainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

// OAPS001 suppressed: AI角色需要 IAppearanceSerializer 才能被客户端识别为玩家
#pragma warning disable OAPS001
namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// AI 角色的视图插件容器。
/// 返回 IChatViewPlugIn 和 IAppearanceSerializer，其他返回 null。
/// </summary>
internal sealed class AiViewPlugInContainer : ICustomPlugInContainer<IViewPlugIn>
{
    internal readonly List<(string Sender, string Message, ChatMessageType Type)> ChatBuffer = new();
    private readonly AppearanceSerializer _appearanceSerializer;

    public AiViewPlugInContainer(AiPlayer player)
    {
        this._appearanceSerializer = new AppearanceSerializer();
    }

    public T? GetPlugIn<T>()
        where T : class, IViewPlugIn
    {
        if (typeof(T) == typeof(IChatViewPlugIn))
            return (T)(object)new ChatBufferPlugIn(this);
        if (typeof(T) == typeof(IAppearanceSerializer))
            return (T)(object)this._appearanceSerializer;
        return default;
    }

    private sealed class ChatBufferPlugIn : IChatViewPlugIn
    {
        private readonly AiViewPlugInContainer _container;
        public ChatBufferPlugIn(AiViewPlugInContainer container) => this._container = container;
        public ValueTask ChatMessageAsync(string message, string sender, ChatMessageType type)
        {
            lock (this._container.ChatBuffer)
                this._container.ChatBuffer.Add((sender, message, type));
            return ValueTask.CompletedTask;
        }
    }
}
