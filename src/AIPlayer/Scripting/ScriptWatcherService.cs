// <copyright file="ScriptWatcherService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Represents a watched script with its metadata and current runtime state.
/// </summary>
public sealed class WatchedScript
{
    /// <summary>
    /// Gets or sets the full file path of the script.
    /// </summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last successfully loaded script, or null if load failed.
    /// </summary>
    public BehaviorScript? LoadedScript { get; set; }

    /// <summary>
    /// Gets or sets the last load time (UTC).
    /// </summary>
    public DateTime LastLoadTime { get; set; }

    /// <summary>
    /// Gets or sets the last error message, if any.
    /// </summary>
    public string? LoadError { get; set; }

    /// <summary>
    /// Gets or sets the version string from the last successful load.
    /// </summary>
    public string? Version { get; set; }
}

/// <summary>
/// FileSystemWatcher-based hot-reload service for JSON script files.
/// Watches the Scripts/ directory for *.json changes and provides methods
/// to reload scripts in-place.
/// </summary>
public sealed class ScriptWatcherService : IDisposable
{
    private readonly string _scriptsDirectoryPath;
    private readonly ILogger<ScriptWatcherService> _logger;
    private readonly Action<string>? _onScriptReloaded;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, WatchedScript> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _debounceLock = new();
    private bool _disposed;

    /// <summary>
    /// Fired after a script has been hot-reloaded from disk.
    /// The argument is the script ID.
    /// </summary>
    public event Action<string>? ScriptReloaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptWatcherService"/> class.
    /// </summary>
    /// <param name="scriptsDirectoryPath">Absolute or relative path to the Scripts directory containing *.json files.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="onScriptReloaded">Optional callback invoked with the script ID when a script is successfully reloaded.</param>
    public ScriptWatcherService(string scriptsDirectoryPath, ILogger<ScriptWatcherService> logger, Action<string>? onScriptReloaded = null)
    {
        this._scriptsDirectoryPath = Path.GetFullPath(scriptsDirectoryPath);
        this._logger = logger;
        this._onScriptReloaded = onScriptReloaded;

        // Discover existing scripts on startup
        if (Directory.Exists(this._scriptsDirectoryPath))
        {
            foreach (var file in Directory.EnumerateFiles(this._scriptsDirectoryPath, "*.json", SearchOption.AllDirectories))
            {
                // 排除学习脚本 — 学习脚本是旁观者系统的输出记录，
                // 应通过 RuleEngine → DecisionCore 路径执行，而非 1D ScriptExecutor
                if (Path.GetFileName(file).StartsWith("learned_", StringComparison.OrdinalIgnoreCase)) continue;
                this.TryRegisterScript(file);
            }

            // Initialize .mu files: compile to JSON, register from JSON
            foreach (var file in Directory.EnumerateFiles(this._scriptsDirectoryPath, "*.mu", SearchOption.AllDirectories))
            {
                this.TryCompileAndRegisterMu(file);
            }
        }
        else
        {
            this._logger.LogWarning("Scripts directory does not exist: {Path}", this._scriptsDirectoryPath);
        }

        // Set up FileSystemWatcher for both .json and .mu
        this._watcher = new FileSystemWatcher(this._scriptsDirectoryPath, "*.*")
        {
            NotifyFilter = NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };

        this._watcher.Changed += this.OnScriptFileChanged;
        this._watcher.Created += this.OnScriptFileChanged;

        // Debounce timer: 500ms delay before processing batched changes
        this._debounceTimer = new Timer(this.DebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        this._logger.LogInformation("ScriptWatcherService started. Watching: {Path}", this._scriptsDirectoryPath);
    }

    /// <summary>
    /// Gets the registry of all watched scripts, keyed by script ID.
    /// </summary>
    public IReadOnlyDictionary<string, WatchedScript> Registry => this._registry;

    /// <summary>
    /// Gets the loaded BehaviorScript by script ID, or null if not found.
    /// </summary>
    public BehaviorScript? GetScript(string scriptId)
    {
        if (this._registry.TryGetValue(scriptId, out var watched))
        {
            return watched.LoadedScript;
        }

        return null;
    }

    /// <summary>
    /// Gets the count of registered scripts.
    /// </summary>
    public int Count => this._registry.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._watcher.EnableRaisingEvents = false;
        this._watcher.Changed -= this.OnScriptFileChanged;
        this._watcher.Created -= this.OnScriptFileChanged;
        this._watcher.Dispose();
        this._debounceTimer.Dispose();
    }

    /// <summary>
    /// Attempts to reload a specific script by its ID.
    /// </summary>
    /// <param name="scriptId">The script ID to reload.</param>
    /// <returns><c>true</c> if the script was found and reloaded; <c>false</c> if not found.</returns>
    public bool TryReloadScript(string scriptId)
    {
        if (this._registry.TryGetValue(scriptId, out var watched))
        {
            return this.ReloadSingleScript(watched);
        }

        this._logger.LogWarning("ScriptReload: script ID '{ScriptId}' not found in registry.", scriptId);
        return false;
    }

    /// <summary>
    /// Reloads all known scripts from their file paths.
    /// </summary>
    /// <returns>The number of scripts successfully reloaded.</returns>
    public int ReloadAllScripts()
    {
        var successCount = 0;
        foreach (var kvp in this._registry)
        {
            if (this.ReloadSingleScript(kvp.Value))
            {
                successCount++;
            }
        }

        this._logger.LogInformation("ScriptReload: reloaded {Count}/{Total} scripts.", successCount, this._registry.Count);
        return successCount;
    }

    /// <summary>
    /// Gets a snapshot of script status information for all registered scripts.
    /// </summary>
    /// <returns>A list of status records.</returns>
    public List<ScriptStatusInfo> GetScriptStatus()
    {
        var now = DateTime.UtcNow;
        return this._registry.Values.Select(w => new ScriptStatusInfo
        {
            Id = w.LoadedScript?.Id ?? ExtractIdFromPath(w.ScriptPath),
            FilePath = w.ScriptPath,
            Version = w.Version ?? "unknown",
            LastLoadTime = w.LastLoadTime,
            Status = w.LoadError is null ? "loaded" : $"error: {w.LoadError}",
            HasErrors = w.LoadError is not null,
        }).ToList();
    }

    private static string ExtractIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name ?? "unknown";
    }

    private void OnScriptFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (this._debounceLock)
        {
            this._pendingChanges.Add(e.FullPath);
        }

        // Reset debounce timer to 500ms from last change
        this._debounceTimer.Change(500, Timeout.Infinite);
    }

    private void DebounceElapsed(object? state)
    {
        string[] paths;
        lock (this._debounceLock)
        {
            paths = this._pendingChanges.ToArray();
            this._pendingChanges.Clear();
        }

        foreach (var path in paths)
        {
            this.ProcessFileChange(path);
        }
    }

    private void ProcessFileChange(string filePath)
    {
        try
        {
            // 排除学习脚本 — learned_*.json 由 RuleEngine/DecisionCore 路径执行
            if (Path.GetFileName(filePath).StartsWith("learned_", StringComparison.OrdinalIgnoreCase)) return;

            if (!File.Exists(filePath))
            {
                return;
            }

            // Handle .mu files: compile to BehaviorScript and register
            if (filePath.EndsWith(".mu", StringComparison.OrdinalIgnoreCase))
            {
                this.TryCompileAndRegisterMu(filePath);
                return;
            }

            // Try to parse the JSON to validate format
            var json = File.ReadAllText(filePath);
            var script = JsonSerializer.Deserialize<BehaviorScript>(json);

            if (script is null)
            {
                this._logger.LogWarning("ScriptWatcher: failed to parse script at {Path} — keeping old version.", filePath);
                return;
            }

            // Register or update in the registry
            if (this._registry.TryGetValue(script.Id, out var existing))
            {
                existing.LoadedScript = script;
                existing.LastLoadTime = DateTime.UtcNow;
                existing.Version = script.Version;
                existing.LoadError = null;
                existing.ScriptPath = filePath;
                this._logger.LogInformation("ScriptWatcher: hot-reloaded '{ScriptId}' v{Version} from {Path}", script.Id, script.Version, filePath);
            }
            else
            {
                var watched = new WatchedScript
                {
                    ScriptPath = filePath,
                    LoadedScript = script,
                    LastLoadTime = DateTime.UtcNow,
                    Version = script.Version,
                    LoadError = null,
                };

                this._registry[script.Id] = watched;
                this._logger.LogInformation("ScriptWatcher: registered new script '{ScriptId}' v{Version} from {Path}", script.Id, script.Version, filePath);
            }

            // Notify callback so ScriptExecutors can hot-switch
            this._onScriptReloaded?.Invoke(script.Id);
            this.ScriptReloaded?.Invoke(script.Id);
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "ScriptWatcher: JSON parse error for {Path} — keeping old version.", filePath);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "ScriptWatcher: unexpected error processing {Path}.", filePath);
        }
    }

    /// <summary>
    /// Reads a .mu file, compiles it via MuScriptCompiler, and registers the resulting JSON.
    /// Also writes the compiled JSON alongside the .mu file (as .mu.json) for engine loading.
    /// </summary>
    private void TryCompileAndRegisterMu(string filePath)
    {
        try
        {
            var dslSource = File.ReadAllText(filePath);
            var script = MuScriptCompiler.CompileFromDsl(dslSource, Path.GetFileNameWithoutExtension(filePath));

            if (script is null)
            {
                this._logger.LogWarning("ScriptWatcher: failed to compile .mu script at {Path}", filePath);
                return;
            }

            // Store DSL source
            script.DslSource = dslSource;

            // Write compiled JSON alongside .mu file
            var jsonPath = filePath + ".json";
            var json = JsonSerializer.Serialize(script, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json);

            // Register the compiled script
            if (this._registry.TryGetValue(script.Id, out var existing))
            {
                existing.LoadedScript = script;
                existing.LastLoadTime = DateTime.UtcNow;
                existing.Version = script.Version;
                existing.LoadError = null;
                existing.ScriptPath = jsonPath;
                this._logger.LogInformation("ScriptWatcher: hot-reloaded .mu script '{ScriptId}' v{Version}", script.Id, script.Version);
            }
            else
            {
                var watched = new WatchedScript
                {
                    ScriptPath = jsonPath,
                    LoadedScript = script,
                    LastLoadTime = DateTime.UtcNow,
                    Version = script.Version,
                    LoadError = null,
                };

                this._registry[script.Id] = watched;
                this._logger.LogInformation("ScriptWatcher: registered .mu script '{ScriptId}' v{Version} from {Path}", script.Id, script.Version, filePath);
            }

            // Notify hot-reload
            this._onScriptReloaded?.Invoke(script.Id);
            this.ScriptReloaded?.Invoke(script.Id);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "ScriptWatcher: error compiling .mu script {Path}", filePath);
        }
    }

    private bool ReloadSingleScript(WatchedScript watched)
    {
        try
        {
            if (!File.Exists(watched.ScriptPath))
            {
                this._logger.LogWarning("ScriptReload: file not found: {Path}", watched.ScriptPath);
                watched.LoadError = "File not found";
                return false;
            }

            var json = File.ReadAllText(watched.ScriptPath);
            var script = JsonSerializer.Deserialize<BehaviorScript>(json);

            if (script is null)
            {
                watched.LoadError = "Deserialization returned null";
                this._logger.LogWarning("ScriptReload: JSON deserialization failed for {Path}", watched.ScriptPath);
                return false;
            }

            watched.LoadedScript = script;
            watched.LastLoadTime = DateTime.UtcNow;
            watched.Version = script.Version;
            watched.LoadError = null;

            this._onScriptReloaded?.Invoke(script.Id);
            this.ScriptReloaded?.Invoke(script.Id);
            return true;
        }
        catch (JsonException ex)
        {
            watched.LoadError = $"JSON error: {ex.Message}";
            this._logger.LogWarning(ex, "ScriptReload: JSON error for {Path}", watched.ScriptPath);
            return false;
        }
        catch (Exception ex)
        {
            watched.LoadError = $"Error: {ex.Message}";
            this._logger.LogError(ex, "ScriptReload: error reloading {Path}", watched.ScriptPath);
            return false;
        }
    }

    private void TryRegisterScript(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var script = JsonSerializer.Deserialize<BehaviorScript>(json);

            if (script is null)
            {
                this._logger.LogWarning("ScriptWatcher: skipping unparseable script at {Path}", filePath);
                return;
            }

            var watched = new WatchedScript
            {
                ScriptPath = filePath,
                LoadedScript = script,
                LastLoadTime = DateTime.UtcNow,
                Version = script.Version,
                LoadError = null,
            };

            if (this._registry.TryAdd(script.Id, watched))
            {
                this._logger.LogDebug("ScriptWatcher: registered '{ScriptId}' v{Version}", script.Id, script.Version);
            }
            else
            {
                // Update existing
                this._registry[script.Id] = watched;
            }
        }
        catch (JsonException)
        {
            // Silently skip invalid files at startup
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "ScriptWatcher: error registering {Path}", filePath);
        }
    }
}

/// <summary>
/// Status information for a script, returned by <see cref="ScriptWatcherService.GetScriptStatus"/>.
/// </summary>
public sealed class ScriptStatusInfo
{
    /// <summary>Script identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Full file path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Script version string.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Last load time (UTC).</summary>
    public DateTime LastLoadTime { get; set; }

    /// <summary>Status description: "loaded" or "error: {message}".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Whether the script has errors.</summary>
    public bool HasErrors { get; set; }
}
