// <copyright file="ItemPickupManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;

public sealed class ItemPickupManager
{
    public ItemPickupManager(AiPlayer player, ILogger logger) { }
    public Task<bool> TryPickupNearbyItemsAsync() => Task.FromResult(false);
    public Task<bool> TryPickupNearbyFilteredAsync(Func<ILocateable, bool> filter) => Task.FromResult(false);
}
