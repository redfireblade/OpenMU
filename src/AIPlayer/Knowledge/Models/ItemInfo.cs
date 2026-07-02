// <copyright file="ItemInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about an item definition, pre-loaded from <see cref="DataModel.Configuration.Items.ItemDefinition"/>.
/// </summary>
public record ItemInfo(
    int Group,
    int Number,
    string Name,
    int DropLevel,
    string? ItemSlot,
    int Width,
    int Height);
