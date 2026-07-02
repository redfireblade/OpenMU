// <copyright file="FuguSftTrainer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer;
using OAPS.Mind;

/// <summary>
/// P3+P4: Unified SFT training pipeline.
///
/// Reads behavior events from BehaviorEventStore (both human players via PBO
/// and AI characters via AiBehaviorCollector), segments them, converts to
/// SoftBucketEntries, trains the SoftBucketStore classifier, and exports
/// SFT weights that the SoftRouter can load.
///
/// Pipeline:
///   BehaviorEventStore → Segmenter → Bucket → SoftBucketEntries
///     → Train SoftBucketStore classifier
///     → Export SFT weights (binary)
///     → SoftRouter.LoadSftWeights()
/// </summary>
public sealed class FuguSftTrainer
{
    private readonly BehaviorEventStore _eventStore;
    private readonly SoftBucketStore _bucketStore;
    private readonly BehaviorSegmenter _segmenter;
    private readonly ILogger _logger;
    private DateTime _lastTraining = DateTime.MinValue;
    private static readonly TimeSpan TrainingInterval = TimeSpan.FromMinutes(10);

    /// <summary>Path where SFT weights are saved.</summary>
    public string SftWeightsPath { get; }

    public FuguSftTrainer(
        BehaviorEventStore eventStore,
        SoftBucketStore bucketStore,
        ILogger<FuguSftTrainer> logger,
        string? weightsDir = null)
    {
        _eventStore = eventStore;
        _bucketStore = bucketStore;
        _logger = logger;
        _segmenter = new BehaviorSegmenter();

        var dir = weightsDir ?? Path.Combine(AppContext.BaseDirectory, "scripts", "learned");
        Directory.CreateDirectory(dir);
        SftWeightsPath = Path.Combine(dir, "fugu_sft_weights.bin");
    }

    /// <summary>
    /// Runs the full SFT training pipeline.
    /// Returns true if training was performed, false if skipped (not enough data or too soon).
    /// </summary>
    public bool TrainIfNeeded()
    {
        if (DateTime.UtcNow - _lastTraining < TrainingInterval)
        {
            return false;
        }

        _lastTraining = DateTime.UtcNow;

        try
        {
            // 1. Collect all events from the store (all characters)
            var allCharacters = _eventStore.GetAllCharacterNames();
            var allSegments = new List<BehaviorSegment>();

            foreach (var charName in allCharacters)
            {
                var events = _eventStore.GetEvents(charName);
                if (events.Count < 5) continue;

                var segments = _segmenter.Segmentate(events);
                allSegments.AddRange(segments);
            }

            if (allSegments.Count < 10)
            {
                _logger.LogInformation("[FuguSFT] Not enough data: {Seg} segments from {Chars} chars",
                    allSegments.Count, allCharacters.Count);
                return false;
            }

            // 2. Convert segments to SoftBucketEntries
            var entries = new List<SoftBucketEntry>();
            foreach (var seg in allSegments)
            {
                // Rough state estimation from segment context
                var state = EstimateStateFromSegment(seg);
                var distribution = EstimateDistributionFromSegment(seg);

                entries.Add(new SoftBucketEntry
                {
                    SegmentId = $"{seg.MapNumber}_{seg.SegmentType}_{seg.StartTime.Ticks}",
                    Distribution = distribution,
                    Context = state,
                    Reward = seg.KillCount * 10f,
                    Timestamp = seg.EndTime,
                });
            }

            // 3. Train SoftBucketStore
            foreach (var entry in entries)
            {
                _bucketStore.AddEntry(entry);
            }

            var loss = _bucketStore.TrainFromEntries(learningRate: 0.01f, epochs: 3, batchSize: 32);

            // 4. Export SFT weights
            _bucketStore.SaveWeights(SftWeightsPath);
            _logger.LogInformation(
                "[FuguSFT] Training complete: {Chars} chars, {Seg} segments, {Ent} entries, KL-loss={Loss:F4}",
                allCharacters.Count, allSegments.Count, entries.Count, loss);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FuguSFT] Training failed");
            return false;
        }
    }

    /// <summary>
    /// Reloads the latest SFT weights into all registered SoftRouters.
    /// </summary>
    public bool LoadIntoRouter(SoftRouter router)
    {
        if (!File.Exists(SftWeightsPath))
        {
            _logger.LogWarning("[FuguSFT] No SFT weights file at {Path}", SftWeightsPath);
            return false;
        }

        try
        {
            var weights = SoftRouter.LoadSftWeights(SftWeightsPath);
            var biases = router.OnlineBiases;
            for (int i = 0; i < biases.Length; i++)
            {
                biases[i] = weights[0, i] * 0.1f; // Start with small bias
            }

            _logger.LogInformation("[FuguSFT] Loaded SFT weights into router");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FuguSFT] Failed to load weights");
            return false;
        }
    }

    // ═══ Private helpers ═══

    private static StateVector EstimateStateFromSegment(BehaviorSegment seg)
    {
        return new StateVector
        {
            HpPercent = 0.8f,
            MpPercent = 0.5f,
            Level = Math.Clamp(seg.KillCount * 2, 1, 400),
            IsAtSafeZone = false,
            InventorySlotsFree = 32,
            IsSurrounded = false,
            TargetCount = seg.KillCount > 0 ? 3 : 0,
            NearestMonsterLevel = seg.MonsterNumber.HasValue ? Math.Min(seg.MonsterNumber.Value / 3, 150) : 10,
            NearbyItemCount = 0,
            HotspotDistance = 50f,
            TimeSinceLastKill = seg.KillCount > 0 ? 10f : 300f,
            IsDebuffed = false,
            NearbyAiCount = 1,
        };
    }

    private static float[] EstimateDistributionFromSegment(BehaviorSegment seg)
    {
        // Map segment type to soft distribution over 7 buckets
        var dist = new float[SoftBucketStore.BucketCount];

        switch (seg.SegmentType)
        {
            case SegmentType.Hunting:
                dist[0] = 0.7f; dist[1] = 0.15f; dist[2] = 0.05f; // combat + patrol + pickup
                dist[3] = 0.02f; dist[4] = 0.03f; dist[5] = 0.02f; dist[6] = 0.03f;
                break;
            case SegmentType.Navigation:
                dist[1] = 0.5f; dist[6] = 0.3f; dist[2] = 0.1f; // patrol + explore
                dist[0] = 0.05f; dist[3] = 0.02f; dist[4] = 0.02f; dist[5] = 0.01f;
                break;
            case SegmentType.Restock:
                dist[5] = 0.6f; dist[2] = 0.2f;  // social + pickup
                dist[0] = 0.05f; dist[1] = 0.05f; dist[3] = 0.03f; dist[4] = 0.05f; dist[6] = 0.02f;
                break;
            default:
                dist[1] = 0.4f; dist[6] = 0.3f; dist[2] = 0.1f; dist[4] = 0.1f; dist[3] = 0.05f; dist[0] = 0.03f; dist[5] = 0.02f;
                break;
        }

        // Normalize
        float sum = dist.Sum();
        if (sum > 0) for (int i = 0; i < dist.Length; i++) dist[i] /= sum;

        return dist;
    }
}

/// <summary>
/// Extension methods for BehaviorEventStore to support multi-character queries.
/// </summary>
public static class BehaviorEventStoreExtensions
{
    private static readonly HashSet<string> _knownCharacters = new();

    public static List<string> GetAllCharacterNames(this BehaviorEventStore store)
    {
        // Scan the events directory for character files
        var dataDir = Path.Combine(AppContext.BaseDirectory, "behavior_events");
        if (!Directory.Exists(dataDir))
        {
            return _knownCharacters.ToList();
        }

        var chars = new HashSet<string>();
        foreach (var file in Directory.GetFiles(dataDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name != "index")
            {
                chars.Add(name);
                _knownCharacters.Add(name);
            }
        }

        return chars.ToList();
    }
}
