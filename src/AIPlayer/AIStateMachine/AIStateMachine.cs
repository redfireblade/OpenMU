// <copyright file="AIStateMachine.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.AIStateMachine;

public sealed class AIStateMachine : IDisposable
{
    public bool IsRunning { get; set; }
    public ScriptItem? CurrentItem { get; set; }
    public Heartbeat? LastHeartbeat { get; set; }
    public int PendingItemCount { get; set; }
    public int CompletedItemCount { get; set; }
    public int FailedItemCount { get; set; }
    public int EventQueueCount { get; set; }
    public long TotalEventsProcessed { get; set; }
    public event Action<object>? OnStuckDetected;

    public AIStateMachine(AiPlayer player, IGameAdapter adapter) { }
    public void Cancel() { }
    public void UpdateHeartbeatAsync(IGameAdapter adapter, int monsterCount) { }
    public void Dispose() { }
}

public sealed class ScriptItem
{
    public string? Label { get; set; }
    public object? Status { get; set; }
    public bool RequiresTickLoop { get; set; }
}

public sealed class Heartbeat
{
    public string? CurrentItemLabel { get; set; }
    public object? CurrentItemStatus { get; set; }
    public bool IsStuckDetected { get; set; }
}
