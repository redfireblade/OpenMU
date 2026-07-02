// <copyright file="MapConnectionInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// Information about a gate connection between two maps.
/// </summary>
public record MapConnectionInfo(
    int FromMapNumber,
    int ToMapNumber,
    int GateNumber,
    int MinLevel,
    string GateName);
