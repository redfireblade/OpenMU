// <copyright file="ServerConfigHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel;

using System.IO;

/// <summary>
/// Helper to read server_config.ini.
/// </summary>
internal static class ServerConfigHelper
{
    /// <summary>
    /// Reads an integer value from server_config.ini.
    /// Returns <paramref name="defaultValue"/> if the file or key is not found.
    /// </summary>
    public static int ReadInt(string section, string key, int defaultValue)
    {
        try
        {
            var configPath = Path.Combine(
                Path.GetDirectoryName(typeof(ServerConfigHelper).Assembly.Location) ?? ".",
                "server_config.ini");
            if (!File.Exists(configPath))
                return defaultValue;

            var lines = File.ReadAllLines(configPath);
            var currentSection = string.Empty;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                    continue;
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    currentSection = trimmed[1..^1].Trim();
                    continue;
                }

                var eqPos = trimmed.IndexOf('=');
                if (eqPos > 0 && string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                {
                    var k = trimmed[..eqPos].Trim();
                    if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(trimmed[(eqPos + 1)..].Trim(), out var val))
                    {
                        return val;
                    }
                }
            }
        }
        catch
        {
        }

        return defaultValue;
    }
}
