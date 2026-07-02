// <copyright file="SoftBucketStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OAPS.Mind;

/// <summary>
/// Soft bucket entry — each behavior segment now maps to a 7-dimensional probability
/// distribution instead of a single hard bucket label. This enables Fugu-style soft
/// distillation: richer training signals, multi-strategy fusion.
/// </summary>
public record SoftBucketEntry
{
    public string SegmentId { get; init; } = string.Empty;
    public float[] Distribution { get; init; } = Array.Empty<float>(); // length = BucketCount
    public StateVector Context { get; init; } = null!;
    public float Reward { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Fugu-style soft bucket store.
///
/// Replaces the v3.0 hard bucket (each segment → one of 7 buckets) with soft
/// probability distributions (each segment → 7-dim probability vector).
///
/// Training: offline KL-divergence minimization from behavior_events.json
///   L = Σ_i D_KL(p_i(·) ‖ π_θ(·|segment_i))
///   where p_i = softmax over reward-weighted capability scores
///
/// This produces SFT weights for the SoftRouter.
/// </summary>
public sealed class SoftBucketStore
{
    public const int BucketCount = 7;

    public static readonly string[] BucketNames =
    {
        "combat",
        "patrol",
        "pickup",
        "flee",
        "rest",
        "social",
        "explore",
    };

    private readonly ILogger _logger;
    private readonly List<SoftBucketEntry> _entries = new();
    private readonly object _lock = new();

    // Learned soft classifier weights: [StateVector.Dimension, BucketCount]
    // Initially random, trained via KL-divergence minimization.
    private float[,] _classifierWeights;

    public SoftBucketStore(ILogger logger)
    {
        _logger = logger;
        _classifierWeights = InitializeRandomWeights();
    }

    /// <summary>
    /// Gets the current classifier weights (for persistence or inspection).
    /// </summary>
    public float[,] ClassifierWeights => _classifierWeights;

    /// <summary>
    /// Adds a behavior segment to the store.
    /// </summary>
    public void AddEntry(SoftBucketEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > 100_000)
            {
                _entries.RemoveRange(0, 20_000); // Keep bounded
            }
        }
    }

    /// <summary>
    /// Computes the soft bucket distribution for a state vector using the learned classifier.
    /// Unlike v3.0 which returns a single bucket label, this returns a 7-dim probability vector.
    /// </summary>
    public float[] Classify(StateVector state)
    {
        var features = state.Encode();
        var logits = new float[BucketCount];

        for (int b = 0; b < BucketCount; b++)
        {
            float logit = 0f;
            for (int d = 0; d < StateVector.Dimension; d++)
            {
                logit += features[d] * _classifierWeights[d, b];
            }

            logits[b] = logit;
        }

        return Softmax(logits, 1.0f);
    }

    /// <summary>
    /// Trains the soft classifier using KL-divergence minimization (Fugu SFT stage).
    /// Iterates over stored entries and updates weights via gradient descent.
    /// </summary>
    /// <param name="learningRate">Learning rate for SGD.</param>
    /// <param name="epochs">Number of training epochs.</param>
    /// <param name="batchSize">Mini-batch size.</param>
    /// <returns>Final average loss.</returns>
    public float TrainFromEntries(float learningRate = 0.01f, int epochs = 10, int batchSize = 64)
    {
        SoftBucketEntry[] entries;
        lock (_lock)
        {
            entries = _entries.ToArray();
        }

        if (entries.Length == 0)
        {
            _logger.LogWarning("[SoftBucket] No training entries available.");
            return float.MaxValue;
        }

        float totalLoss = 0f;
        int totalBatches = 0;

        for (int epoch = 0; epoch < epochs; epoch++)
        {
            // Shuffle
            Random.Shared.Shuffle(entries);

            for (int batchStart = 0; batchStart < entries.Length; batchStart += batchSize)
            {
                var batch = entries.Skip(batchStart).Take(batchSize).ToArray();
                var batchLoss = TrainBatch(batch, learningRate);
                totalLoss += batchLoss;
                totalBatches++;
            }
        }

        var avgLoss = totalLoss / Math.Max(1, totalBatches);
        _logger.LogInformation(
            "[SoftBucket] Training complete: {Epochs} epochs, {Entries} entries, avg KL-loss={Loss:F4}",
            epochs, entries.Length, avgLoss);

        return avgLoss;
    }

    /// <summary>
    /// Loads the soft classifier weights from a binary file.
    /// </summary>
    public void LoadWeights(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("[SoftBucket] Weights file not found: {Path}", filePath);
            return;
        }

        var bytes = File.ReadAllBytes(filePath);
        var expectedSize = StateVector.Dimension * BucketCount * sizeof(float);
        if (bytes.Length != expectedSize)
        {
            _logger.LogWarning(
                "[SoftBucket] Weight file size mismatch: expected {Expected}, got {Actual}",
                expectedSize, bytes.Length);
            return;
        }

        Buffer.BlockCopy(bytes, 0, _classifierWeights, 0, bytes.Length);
        _logger.LogInformation("[SoftBucket] Loaded weights from {Path}", filePath);
    }

    /// <summary>
    /// Saves the soft classifier weights to a binary file.
    /// </summary>
    public void SaveWeights(string filePath)
    {
        var bytes = new byte[StateVector.Dimension * BucketCount * sizeof(float)];
        Buffer.BlockCopy(_classifierWeights, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(filePath, bytes);
        _logger.LogInformation("[SoftBucket] Saved weights to {Path}", filePath);
    }

    /// <summary>
    /// Exports all entries as a JSON Lines file for offline analysis.
    /// </summary>
    public void ExportEntries(string filePath)
    {
        SoftBucketEntry[] entries;
        lock (_lock)
        {
            entries = _entries.ToArray();
        }

        using var writer = new StreamWriter(filePath);
        foreach (var entry in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry));
        }

        _logger.LogInformation("[SoftBucket] Exported {Count} entries to {Path}", entries.Length, filePath);
    }

    /// <summary>
    /// Loads entries from a JSON Lines file.
    /// </summary>
    public void ImportEntries(string filePath)
    {
        if (!File.Exists(filePath)) return;

        lock (_lock)
        {
            foreach (var line in File.ReadLines(filePath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<SoftBucketEntry>(line);
                    if (entry is not null) _entries.Add(entry);
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }

        _logger.LogInformation("[SoftBucket] Imported {Count} entries from {Path}", _entries.Count, filePath);
    }

    // ═══ Private ═══

    private static float[,] InitializeRandomWeights()
    {
        var weights = new float[StateVector.Dimension, BucketCount];
        float scale = MathF.Sqrt(2f / StateVector.Dimension); // He initialization
        for (int d = 0; d < StateVector.Dimension; d++)
        {
            for (int b = 0; b < BucketCount; b++)
            {
                weights[d, b] = (float)(Random.Shared.NextDouble() * 2 - 1) * scale;
            }
        }

        return weights;
    }

    private float TrainBatch(SoftBucketEntry[] batch, float learningRate)
    {
        float batchLoss = 0f;

        foreach (var entry in batch)
        {
            // Forward: compute predicted distribution
            var predicted = Classify(entry.Context);

            // KL divergence loss: Σ p(i) * log(p(i) / q(i))
            float klLoss = 0f;
            for (int b = 0; b < BucketCount; b++)
            {
                if (entry.Distribution[b] > 1e-7f && predicted[b] > 1e-7f)
                {
                    klLoss += entry.Distribution[b] * MathF.Log(entry.Distribution[b] / predicted[b]);
                }
            }

            batchLoss += klLoss;

            // Backward: gradient of KL w.r.t. logits = -(target - predicted) * learningRate
            var features = entry.Context.Encode();
            for (int d = 0; d < StateVector.Dimension; d++)
            {
                for (int b = 0; b < BucketCount; b++)
                {
                    var gradient = (predicted[b] - entry.Distribution[b]) * features[d];
                    _classifierWeights[d, b] -= learningRate * gradient;
                }
            }
        }

        return batchLoss / batch.Length;
    }

    private static float[] Softmax(float[] logits, float temperature)
    {
        var result = new float[logits.Length];
        float maxLogit = float.MaxValue;
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] > maxLogit || maxLogit == float.MaxValue)
                maxLogit = logits[i];
        }

        float sum = 0f;
        for (int i = 0; i < logits.Length; i++)
        {
            result[i] = MathF.Exp((logits[i] - maxLogit) / temperature);
            sum += result[i];
        }

        for (int i = 0; i < result.Length; i++)
        {
            result[i] /= sum;
        }

        return result;
    }
}
