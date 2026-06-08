// <copyright file="GameVersion.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class GameVersion
{
    public int Season { get; }
    public int Episode { get; }

    public GameVersion(int season, int episode)
    {
        Season = season;
        Episode = episode;
    }
}
