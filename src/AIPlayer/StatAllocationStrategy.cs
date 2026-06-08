// <copyright file="StatAllocationStrategy.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public static class StatAllocationStrategy
{
    public static Dictionary<int, (int MinLevel, int MaxLevel, float Str, float Agi, float Vit, float Ene)[]> ClassBuilds { get; } = new();
    public static int GetBaseClass(int classNumber) => classNumber;
}
