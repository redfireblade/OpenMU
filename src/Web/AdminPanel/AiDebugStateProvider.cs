// <copyright file="AiDebugStateProvider.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using MUnique.OpenMU.AIPlayer;

/// <summary>
/// Singleton service that polls AI player state on a 1-second interval.
/// Provides cached <see cref="AiPlayerDebugData"/> snapshots to the
/// Blazor debug dashboard via <see cref="INotifyPropertyChanged"/>.
/// </summary>
public sealed class AiDebugStateProvider : INotifyPropertyChanged, IDisposable
{
    private readonly IAiService _aiService;
    private readonly IAiDebugService _debugService;
    private readonly Timer _timer;
    private IReadOnlyCollection<AiPlayerDebugData> _players = Array.Empty<AiPlayerDebugData>();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiDebugStateProvider"/> class.
    /// </summary>
    public AiDebugStateProvider(IAiService aiService, IAiDebugService debugService)
    {
        this._aiService = aiService;
        this._debugService = debugService;
        this._timer = new Timer(this.Poll, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the cached collection of all active AI player debug data.
    /// </summary>
    public IReadOnlyCollection<AiPlayerDebugData> Players => this._players;

    /// <inheritdoc />
    public void Dispose()
    {
        if (!this._disposed)
        {
            this._disposed = true;
            this._timer.Dispose();
        }
    }

    /// <summary>
    /// Triggers an immediate poll and notifies listeners.
    /// </summary>
    public void Refresh()
    {
        this.Poll(null);
    }

    private void Poll(object? state)
    {
        IReadOnlyCollection<AiPlayerDebugData> snapshot;
        try
        {
            snapshot = this._debugService.GetAllDebugData();
        }
        catch
        {
            return;
        }

        this._players = snapshot;
        this.OnPropertyChanged(nameof(this.Players));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
