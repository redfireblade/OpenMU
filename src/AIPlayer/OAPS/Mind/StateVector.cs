// <copyright file="StateVector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

/// <summary>
/// Fugu-style unified state representation — the single input vector to the SoftRouter.
/// Encodes self-state, environmental context, and swarm context from the SharedMemoryLayer.
/// Maps to Fugu's "hidden state h" that feeds into the SelectionHead for per-step routing.
/// </summary>
public record StateVector
{
    // ═══ Self State ═══
    /// <summary>HP percentage (0.0 = dead, 1.0 = full).</summary>
    public float HpPercent { get; init; }

    /// <summary>MP percentage (0.0 = empty, 1.0 = full).</summary>
    public float MpPercent { get; init; }

    /// <summary>Current character level.</summary>
    public int Level { get; init; }

    /// <summary>Whether the character is at a safe zone.</summary>
    public bool IsAtSafeZone { get; init; }

    /// <summary>Number of free inventory slots.</summary>
    public int InventorySlotsFree { get; init; }

    /// <summary>Whether the character is currently surrounded by monsters (3+ in melee range).</summary>
    public bool IsSurrounded { get; init; }

    // ═══ Environmental Context ═══

    /// <summary>Number of attackable targets within view range.</summary>
    public int TargetCount { get; init; }

    /// <summary>Level of the nearest monster, or 0 if none.</summary>
    public int NearestMonsterLevel { get; init; }

    /// <summary>Number of dropped items within pickup range.</summary>
    public int NearbyItemCount { get; init; }

    /// <summary>Distance in tiles to the nearest hotspot, or 255 if none known.</summary>
    public float HotspotDistance { get; init; }

    /// <summary>Time in seconds since last monster killed.</summary>
    public float TimeSinceLastKill { get; init; }

    /// <summary>Whether the character is currently poisoned or taking elemental damage.</summary>
    public bool IsDebuffed { get; init; }

    // ═══ Swarm Context (from SharedMemoryLayer) ═══

    /// <summary>Number of other AI characters within 30 tiles (crowding).</summary>
    public int NearbyAiCount { get; init; }

    /// <summary>
    /// Pheromone intensity at current position (aggregate of Shared layer).
    /// High = many other AI found this area productive.
    /// </summary>
    public float PheromoneIntensity { get; init; }

    /// <summary>
    /// Best known route score from current position to any hunting ground,
    /// from the Shared layer's route database.
    /// </summary>
    public float BestRouteScore { get; init; }

    /// <summary>
    /// Drop rate estimate at current hotspot (from Shared layer).
    /// </summary>
    public float EstimatedDropRate { get; init; }

    // ═══ Serialization ═══

    /// <summary>
    /// Encode into a fixed-size float array for the SoftRouter matrix multiplication.
    /// </summary>
    public float[] Encode()
    {
        return new[]
        {
            HpPercent,
            MpPercent,
            Level / 400f,                // normalize to [0, 1]
            IsAtSafeZone ? 1f : 0f,
            InventorySlotsFree / 64f,    // normalize
            IsSurrounded ? 1f : 0f,
            TargetCount / 20f,           // normalize
            NearestMonsterLevel / 150f,  // normalize
            NearbyItemCount / 10f,       // normalize
            Math.Min(HotspotDistance / 255f, 1f),
            Math.Min(TimeSinceLastKill / 300f, 1f),  // 5 minutes max
            IsDebuffed ? 1f : 0f,
            NearbyAiCount / 10f,
            Math.Min(PheromoneIntensity, 1f),
            Math.Min(BestRouteScore, 1f),
            Math.Min(EstimatedDropRate, 1f),
        };
    }

    /// <summary>Dimension of the encoded state vector.</summary>
    public const int Dimension = 16;
}
