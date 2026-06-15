// <copyright file="MapController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Map.Map;

using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Web.Map.ViewPlugIns;

/// <summary>
/// Controller which contains the logic for the shown game map.
/// </summary>
public sealed class MapController : IMapController, IWorldObserver, ILocateable, IBucketMapObserver, ISupportIdUpdate
{
    private readonly string _identifier;
    private readonly IObservableGameServer _gameServer;
    private readonly Guid _mapId;
    private readonly IJSRuntime _jsRuntime;
    private readonly ObserverToWorldViewAdapter _adapterToWorldView;
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MapController" /> class.
    /// </summary>
    /// <param name="jsRuntime">The js runtime.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="mapIdentifier">The map identifier.</param>
    /// <param name="gameServer">The game server.</param>
    /// <param name="mapId">The map id.</param>
    public MapController(IJSRuntime jsRuntime, ILoggerFactory loggerFactory, string mapIdentifier, IObservableGameServer gameServer, Guid mapId)
    {
        this._jsRuntime = jsRuntime;

        this._identifier = mapIdentifier;
        this._gameServer = gameServer;
        this._mapId = mapId;
        this.Logger = loggerFactory.CreateLogger(this.GetType());
        this._adapterToWorldView = new ObserverToWorldViewAdapter(this, byte.MaxValue);

        var viewPlugIns = new CustomPlugInContainer<IViewPlugIn>();
        var worldAccessor = $"{mapIdentifier}.world";
        viewPlugIns.RegisterPlugIn<IObjectGotKilledPlugIn>(new ObjectGotKilledPlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));
        viewPlugIns.RegisterPlugIn<IObjectMovedPlugIn>(new ObjectMovedPlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));
        viewPlugIns.RegisterPlugIn<IShowAnimationPlugIn>(new ShowAnimationPlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));
        viewPlugIns.RegisterPlugIn<IObjectsOutOfScopePlugIn>(new ObjectsOutOfScopePlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token, this.Players, () => this.PlayersChanged?.Invoke(this, EventArgs.Empty)));
        viewPlugIns.RegisterPlugIn<INewPlayersInScopePlugIn>(new NewPlayersInScopePlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token, this.Players, () => this.PlayersChanged?.Invoke(this, EventArgs.Empty)));
        viewPlugIns.RegisterPlugIn<INewNpcsInScopePlugIn>(new NewNpcsInScopePlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));
        viewPlugIns.RegisterPlugIn<IShowSkillAnimationPlugIn>(new ShowSkillAnimationPlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));
        viewPlugIns.RegisterPlugIn<IShowAreaSkillAnimationPlugIn>(new ShowAreaSkillAnimationPlugIn(this._jsRuntime, loggerFactory, worldAccessor, this._disposeCts.Token));

        this.ViewPlugIns = viewPlugIns;
        this.Logger.LogDebug("[MapController] Created for map {MapId} on server {ServerId}, worldAccessor={WorldAccessor}, position=({X},{Y}), infoRange={Range}",
            mapId, gameServer.Id, worldAccessor, this.Position.X, this.Position.Y, this.InfoRange);
    }

    /// <inheritdoc />
    public event EventHandler? PlayersChanged;

    /// <inheritdoc />
    public ILogger Logger { get; }

    /// <inheritdoc/>
    public int InfoRange => byte.MaxValue;

    /// <inheritdoc/>
    public GameMap? CurrentMap => null;

    /// <inheritdoc/>
    public Point Position { get; set; }

    /// <inheritdoc cref="ISupportIdUpdate" />
    public ushort Id { get; set; }

    /// <inheritdoc/>
    public IList<Bucket<ILocateable>> ObservingBuckets => this._adapterToWorldView.ObservingBuckets;

    /// <inheritdoc/>
    public ICustomPlugInContainer<IViewPlugIn> ViewPlugIns { get; }

    /// <inheritdoc />
    public ConcurrentDictionary<int, Player> Players { get; } = new();

    /// <inheritdoc/>
    public ValueTask LocateableAddedAsync(ILocateable item)
    {
        if (item is Player p)
        {
            this.Logger.LogDebug("[MapController] LocateableAddedAsync: Player \"{Name}\" (id={Id}) at ({X},{Y}), IsActive={IsActive}, IsInvisible={IsInvisible}",
                p.SelectedCharacter?.Name ?? "?", p.Id, p.Position.X, p.Position.Y, item.IsActive(), (p as Player)?.IsInvisible);
        }
        else if (item is NonPlayerCharacter)
        {
            this.Logger.LogDebug("[MapController] LocateableAddedAsync: NPC at ({X},{Y})", item.Position.X, item.Position.Y);
        }
        return this._adapterToWorldView.LocateableAddedAsync(item);
    }

    /// <inheritdoc/>
    public ValueTask LocateableRemovedAsync(ILocateable item)
    {
        return this._adapterToWorldView.LocateableRemovedAsync(item);
    }

    /// <inheritdoc/>
    public ValueTask LocateablesOutOfScopeAsync(IEnumerable<ILocateable> oldObjects)
    {
        return this._adapterToWorldView.LocateablesOutOfScopeAsync(oldObjects);
    }

    /// <inheritdoc/>
    public ValueTask NewLocateablesInScopeAsync(IEnumerable<ILocateable> newObjects)
    {
        var playerCount = newObjects.OfType<Player>().Count();
        var npcCount = newObjects.OfType<NonPlayerCharacter>().Count();
        var itemCount = newObjects.Count() - playerCount - npcCount;
        if (playerCount > 0 || npcCount > 0)
        {
            this.Logger.LogDebug("[MapController] NewLocateablesInScopeAsync: {TotalCount} total, {PlayerCount} players, {NpcCount} NPCs, {ItemCount} other",
                newObjects.Count(), playerCount, npcCount, itemCount);
        }
        return this._adapterToWorldView.NewLocateablesInScopeAsync(newObjects);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this._disposeCts.CancelAsync().ConfigureAwait(false);
        await this._gameServer.UnregisterMapObserverAsync(this._mapId, this.Id).ConfigureAwait(false);
        this._adapterToWorldView.Dispose();
        try
        {
            await this._jsRuntime.InvokeVoidAsync("DisposeMap", this._identifier).ConfigureAwait(false);
        }
        finally
        {
            this._disposeCts.Dispose();
        }
    }
}