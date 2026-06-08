// <copyright file="PersonalityProfile.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

public sealed class PersonalityProfile
{
    public double Greed { get; set; }
    public double RiskTolerance { get; set; }
    public double Caution { get; set; }
    public double Aggression { get; set; }
    public double Efficiency { get; set; }
    public static PersonalityProfile Balanced() => new();
}
