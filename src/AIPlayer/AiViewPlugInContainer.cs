// <copyright file="AiViewPlugInContainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.PlugIns;

internal sealed class AiViewPlugInContainer : ICustomPlugInContainer<IViewPlugIn>
{
    public AiViewPlugInContainer(AiPlayer player)
    {
    }

    public T? GetPlugIn<T>()
        where T : class, IViewPlugIn => default;
}
