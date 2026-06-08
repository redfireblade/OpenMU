// <copyright file="ExecutionContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class ExecutionContext
{
    public Goal? ActiveGoal { get; set; }
    public static ExecutionContext? LoadAsync(string dir, string name) => null;
    public Task SaveAsync(string dir, string name) => Task.CompletedTask;
}

public sealed class Goal
{
    public ushort? TargetMap { get; set; }
}
