// <copyright file="ScriptReloadBridge.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Bridges <see cref="ScriptWatcherService"/> hot-reload events to running
/// <see cref="global::MUnique.OpenMU.AIPlayer.Scripting.ScriptExecutor"/> instances.
/// When a script file changes on disk, this hosted service:
///   1. Receives the script ID from <see cref="ScriptWatcherService.ScriptReloaded"/>
///   2. Fetches the updated <see cref="BehaviorScript"/> from the watcher's registry
///   3. Finds all active <see cref="AiPlayer"/> instances using that script (by ScriptPath)
///   4. Calls <see cref="AiPlayerLogic.ReloadScript"/> on each matching instance.
/// </summary>
public sealed class ScriptReloadBridge : IHostedService, IDisposable
{
    private readonly ScriptWatcherService _watcher;
    private readonly AiPlayerManager _playerManager;
    private readonly ILogger<ScriptReloadBridge> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptReloadBridge"/> class.
    /// </summary>
    /// <param name="watcher">The script watcher service.</param>
    /// <param name="playerManager">The AI player manager.</param>
    /// <param name="logger">The logger.</param>
    public ScriptReloadBridge(
        ScriptWatcherService watcher,
        AiPlayerManager playerManager,
        ILogger<ScriptReloadBridge> logger)
    {
        this._watcher = watcher;
        this._playerManager = playerManager;
        this._logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        this._watcher.ScriptReloaded += this.OnScriptReloaded;
        this._logger.LogInformation("ScriptReloadBridge started, listening for hot-reload events.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        this._watcher.ScriptReloaded -= this.OnScriptReloaded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!this._disposed)
        {
            this._disposed = true;
            this._watcher.ScriptReloaded -= this.OnScriptReloaded;
        }
    }

    private void OnScriptReloaded(string scriptId)
    {
        try
        {
            var newScript = this._watcher.GetScript(scriptId);
            if (newScript is null)
            {
                this._logger.LogWarning(
                    "ScriptReloadBridge: script '{ScriptId}' not found in watcher registry.",
                    scriptId);
                return;
            }

            var reloadCount = 0;
            foreach (var player in this._playerManager.GetActivePlayers())
            {
                if (player.Logic is null)
                {
                    continue;
                }

                if (!MatchesScript(player, scriptId))
                {
                    continue;
                }

                player.Logic.ReloadScript(newScript);
                reloadCount++;
                this._logger.LogInformation(
                    "ScriptReloadBridge: hot-reloaded script '{ScriptId}' for player {PlayerId}.",
                    scriptId,
                    player.AiPlayerId);
            }

            if (reloadCount == 0)
            {
                this._logger.LogDebug(
                    "ScriptReloadBridge: script '{ScriptId}' reloaded, but no active players use it.",
                    scriptId);
            }
            else
            {
                this._logger.LogInformation(
                    "ScriptReloadBridge: script '{ScriptId}' reloaded for {Count} active player(s).",
                    scriptId,
                    reloadCount);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(
                ex,
                "ScriptReloadBridge: error processing reload for script '{ScriptId}'.",
                scriptId);
        }
    }

    private static bool MatchesScript(AiPlayer player, string scriptId)
    {
        if (player.ScriptPath is null)
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(player.ScriptPath);
        var match = string.Equals(fileName, scriptId, StringComparison.OrdinalIgnoreCase);
        if (!match)
        {
            // Log non-match at Trace level to confirm hot-reload isolation
            System.Diagnostics.Debug.WriteLine(
                $"[ScriptReloadBridge] MatchesScript: player={player.SelectedCharacter?.Name} " +
                $"scriptPath={player.ScriptPath} fileName={fileName} scriptId={scriptId} => FALSE (isolated)");
        }

        return match;
    }
}
