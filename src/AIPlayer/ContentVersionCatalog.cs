// <copyright file="ContentVersionCatalog.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public static class ContentVersionCatalog
{
    public static GameVersion? ServerVersion { get; set; }
    public static bool IsSkillAvailable(ushort skillNumber) => true;
    public static bool IsMapAvailable(ushort mapNumber) => true;
    public static bool IsItemAvailable(MUnique.OpenMU.DataModel.Configuration.Items.ItemDefinition itemDef) => true;
}
