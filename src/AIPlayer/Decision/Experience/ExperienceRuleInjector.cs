// <copyright file="ExperienceRuleInjector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Experience;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 经验→规则注入器。将高置信度的规则候选写入规则表文件。
/// 经验=未成熟的规则，这里就是"成熟"的那一步。
/// </summary>
public sealed class ExperienceRuleInjector
{
    private readonly string _rulesDir;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ExperienceRuleInjector(string baseDir, ILogger logger)
    {
        this._rulesDir = Path.Combine(baseDir, "aiplayer_data", "scripts_rules");
        this._logger = logger;
        Directory.CreateDirectory(this._rulesDir);
    }

    /// <summary>
    /// 将规则候选注入到规则表文件。
    /// 置信度≥0.9 → 写入 rules_experience.json（高置信度规则）
    /// 置信度≥0.6 → 写入 rules_candidates.json（待使用）
    /// 置信度<0.6 → 跳过
    /// </summary>
    /// <returns>本次新注入的规则数。</returns>
    public async ValueTask<int> InjectAsync(List<RuleCandidate> candidates)
    {
        var count = 0;

        // 分离可用和待提升
        var usable = candidates.Where(c => c.IsUsable && !c.PromotedToRule).ToList();
        var promote = usable.Where(c => c.ShouldPromote).ToList();

        // 高置信度 → 写入经验规则表
        if (promote.Count > 0)
        {
            count += await this.WriteExperienceRulesAsync(promote).ConfigureAwait(false);
        }

        // 可用但不够高 → 写入候选表
        var keep = usable.Where(c => !c.ShouldPromote).ToList();
        if (keep.Count > 0)
        {
            count += await this.WriteCandidatesAsync(keep).ConfigureAwait(false);
        }

        return count;
    }

    private async ValueTask<int> WriteExperienceRulesAsync(List<RuleCandidate> candidates)
    {
        try
        {
            var filePath = Path.Combine(this._rulesDir, "rules_experience.json");
            var existing = new List<RuleDef>();
            if (File.Exists(filePath))
            {
                var prev = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    existing = JsonSerializer.Deserialize<List<RuleDef>>(prev, options) ?? new();
                }
                catch { }
            }

            var newCount = 0;
            foreach (var candidate in candidates)
            {
                var ruleDef = candidate.ToRuleDef();
                var idx = existing.FindIndex(r => r.RuleId == candidate.RuleId);
                if (idx >= 0)
                {
                    existing[idx] = ruleDef;
                }
                else
                {
                    existing.Add(ruleDef);
                    newCount++;
                }
            }

            var json = JsonSerializer.Serialize(existing, JsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            if (newCount > 0)
            {
                this._logger.LogInformation("[ExpRule] ✅ 注入 {NewCount} 条经验规则到 {File} (总计{Total}条)",
                    newCount, filePath, existing.Count);
            }

            return newCount;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ExpRule] 写入经验规则失败");
            return 0;
        }
    }

    private async ValueTask<int> WriteCandidatesAsync(List<RuleCandidate> candidates)
    {
        try
        {
            var filePath = Path.Combine(this._rulesDir, "rules_candidates.json");
            var existing = new List<RuleDef>();
            if (File.Exists(filePath))
            {
                var prev = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                try
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    existing = JsonSerializer.Deserialize<List<RuleDef>>(prev, options) ?? new();
                }
                catch { }
            }

            var newCount = 0;
            foreach (var candidate in candidates)
            {
                var ruleDef = candidate.ToRuleDef();
                if (existing.All(r => r.RuleId != candidate.RuleId))
                {
                    existing.Add(ruleDef);
                    newCount++;
                }
            }

            var json = JsonSerializer.Serialize(existing, JsonOptions);
            await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            if (newCount > 0)
            {
                this._logger.LogInformation("[ExpRule] 📋 新增 {NewCount} 条规则候选到 {File} (置信度{Conf:F1})",
                    newCount, filePath, candidates.First().Confidence);
            }

            return newCount;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 从经验规则表加载规则，供 RuleEngine 使用。
    /// </summary>
    public async ValueTask<List<RuleDef>> LoadExperienceRulesAsync()
    {
        var filePath = Path.Combine(this._rulesDir, "rules_experience.json");
        if (!File.Exists(filePath)) return new();

        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<RuleDef>>(json, options) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
