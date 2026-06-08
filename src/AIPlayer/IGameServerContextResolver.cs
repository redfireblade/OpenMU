// <copyright file="IGameServerContextResolver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;

public interface IGameServerContextResolver
{
    IGameContext? ResolveContext();
}
