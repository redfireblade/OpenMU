// <copyright file="AiCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public static class AiCommandPlugIn
{
    public static Func<IAiService?>? ServiceResolver { get; set; }
}
