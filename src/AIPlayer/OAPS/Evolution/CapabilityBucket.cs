// <copyright file="CapabilityBucket.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

/// <summary>
/// 能力桶系统 — 将同类型行为 Segment 归桶存储，积累足够证据后蒸馏为规则/脚本。
/// 参考 Browser-BC (Journey Forge Local) 的 Bucket + Consolidate 设计。
///
/// 桶键 = "地图号::能力标签"
/// 桶包含多个 Segment 证据，当证据数 ≥ 蒸馏阈值时触发蒸馏。
/// </summary>
public sealed class CapabilityBucketStore
{
    private readonly Dictionary<string, CapabilityBucket> _buckets = new();
    private readonly object _lock = new();

    /// <summary>蒸馏所需的最小 Segment 数（证据驱动）。</summary>
    private const int DistillThreshold = 3;

    /// <summary>合并所需的最小桶数。</summary>
    private const int ConsolidateThreshold = 3;

    /// <summary>
    /// 将所有 Segment 归入对应的能力桶。
    /// 返回新创建的桶列表。
    /// </summary>
    public List<CapabilityBucket> BucketSegments(IEnumerable<BehaviorSegment> segments)
    {
        var newBuckets = new List<CapabilityBucket>();
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var seg in segments)
            {
                var capacity = seg.CapacityLabel ?? InferCapacity(seg);
                var key = $"{seg.MapNumber}::{capacity}";

                if (!_buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new CapabilityBucket
                    {
                        BucketId = key,
                        MapNumber = seg.MapNumber,
                        Capacity = capacity,
                        CreatedAt = now,
                    };
                    _buckets[key] = bucket;
                    newBuckets.Add(bucket);
                }

                if (!bucket.SegmentIds.Contains(seg.SegmentId))
                {
                    bucket.SegmentIds.Add(seg.SegmentId);
                    bucket.TotalKills += seg.KillCount;
                    bucket.LastSegmentAt = now;
                    bucket.EvidenceCount = bucket.SegmentIds.Count;
                }
            }
        }

        return newBuckets;
    }

    /// <summary>
    /// 获取所有达到蒸馏阈值的桶。
    /// </summary>
    public List<CapabilityBucket> GetBucketsReadyForDistill()
    {
        lock (_lock)
        {
            return _buckets.Values
                .Where(b => b.EvidenceCount >= DistillThreshold && !b.IsDistilled)
                .ToList();
        }
    }

    /// <summary>
    /// 标记桶已蒸馏。
    /// </summary>
    public void MarkDistilled(string bucketId)
    {
        lock (_lock)
        {
            if (_buckets.TryGetValue(bucketId, out var bucket))
            {
                bucket.IsDistilled = true;
                bucket.LastDistilledAt = DateTime.UtcNow;
                bucket.DistillVersion++;
            }
        }
    }

    /// <summary>
    /// 获取所有桶。
    /// </summary>
    public IReadOnlyList<CapabilityBucket> GetAllBuckets()
    {
        lock (_lock) return _buckets.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 对指定地图的桶执行合并。
    /// 合并相似容量的桶（如 "打蜘蛛" + "打蜘蛛Lv2" → "打蜘蛛"）。
    /// </summary>
    public List<BucketMerge> Consolidate(int mapNumber)
    {
        var merges = new List<BucketMerge>();

        lock (_lock)
        {
            var mapBuckets = _buckets.Values
                .Where(b => b.MapNumber == mapNumber)
                .ToList();

            if (mapBuckets.Count < ConsolidateThreshold) return merges;

            // 简单合并：按容量名称前缀分组（如 "打蜘蛛Lv2" → "打蜘蛛"）
            var groups = mapBuckets
                .GroupBy(b => NormalizeCapacity(b.Capacity))
                .Where(g => g.Count() >= 2)
                .ToList();

            foreach (var group in groups)
            {
                var canonical = group.Key;
                var sources = group.ToList();
                var targetKey = $"{mapNumber}::{canonical}";

                // 如果目标桶不存在，创建
                if (!_buckets.TryGetValue(targetKey, out var target))
                {
                    target = new CapabilityBucket
                    {
                        BucketId = targetKey,
                        MapNumber = mapNumber,
                        Capacity = canonical,
                        CreatedAt = sources.Min(s => s.CreatedAt),
                    };
                    _buckets[targetKey] = target;
                }

                // 合并来源桶到目标桶
                foreach (var src in sources.Where(s => s.BucketId != targetKey))
                {
                    foreach (var sid in src.SegmentIds)
                    {
                        if (!target.SegmentIds.Contains(sid))
                            target.SegmentIds.Add(sid);
                    }
                    target.TotalKills += src.TotalKills;
                    target.EvidenceCount = target.SegmentIds.Count;

                    merges.Add(new BucketMerge
                    {
                        SourceBucketId = src.BucketId,
                        TargetBucketId = targetKey,
                        MergedAt = DateTime.UtcNow,
                    });

                    _buckets.Remove(src.BucketId);
                }
            }
        }

        return merges;
    }

    /// <summary>
    /// 从 Segment 内容推断能力标签（当分类器未设置时）。
    /// </summary>
    private static string InferCapacity(BehaviorSegment seg)
    {
        if (seg.SegmentType == SegmentType.Hunting)
        {
            return seg.MonsterNumber.HasValue
                ? $"hunt-mob-{seg.MonsterNumber}"
                : "hunt-generic";
        }

        return seg.SegmentType switch
        {
            SegmentType.Restock => "restock",
            SegmentType.Quest => "quest",
            SegmentType.LevelUp => "level-up",
            SegmentType.Navigation => "navigation",
            _ => "unknown",
        };
    }

    /// <summary>
    /// 归并能力名称（去掉版本号/位置后缀）。
    /// </summary>
    private static string NormalizeCapacity(string capacity)
    {
        // "hunt-mob-67-Lv5" → "hunt-mob-67"
        // "打蜘蛛Lv2" → "打蜘蛛"
        var idx = capacity.LastIndexOfAny(new[] { '-', 'L', 'l' });
        if (idx > 0 && capacity.Length > idx + 1 && char.IsDigit(capacity[idx + 1]))
            return capacity[..idx];
        return capacity;
    }
}

/// <summary>能力桶 — 同类行为的证据集合。</summary>
public sealed class CapabilityBucket
{
    /// <summary>桶唯一标识（"地图号::能力标签"）。</summary>
    public string BucketId { get; init; } = string.Empty;

    /// <summary>地图号。</summary>
    public int MapNumber { get; init; }

    /// <summary>能力标签。</summary>
    public string Capacity { get; init; } = string.Empty;

    /// <summary>包含的 Segment ID 列表。</summary>
    public List<string> SegmentIds { get; init; } = new();

    /// <summary>总击杀数。</summary>
    public int TotalKills { get; set; }

    /// <summary>证据数（Segment 数量）。</summary>
    public int EvidenceCount { get; set; }

    /// <summary>是否已蒸馏。</summary>
    public bool IsDistilled { get; set; }

    /// <summary>蒸馏版本号。</summary>
    public int DistillVersion { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>最后 Segment 添加时间。</summary>
    public DateTime? LastSegmentAt { get; set; }

    /// <summary>最后蒸馏时间。</summary>
    public DateTime? LastDistilledAt { get; set; }
}

/// <summary>桶合并记录。</summary>
public sealed class BucketMerge
{
    /// <summary>来源桶 ID。</summary>
    public string SourceBucketId { get; init; } = string.Empty;

    /// <summary>目标桶 ID。</summary>
    public string TargetBucketId { get; init; } = string.Empty;

    /// <summary>合并时间。</summary>
    public DateTime MergedAt { get; init; } = DateTime.UtcNow;
}
