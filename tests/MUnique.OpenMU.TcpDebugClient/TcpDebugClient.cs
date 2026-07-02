namespace MUnique.OpenMU.TcpDebugClient;

using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

/// <summary>
/// Configuration for the TCP debug client.
/// </summary>
public sealed record TcpDebugClientConfig
{
    public string Server { get; init; } = "localhost";
    public int Port { get; init; } = 55901;
    public string Username { get; init; } = "test1";
    public string Password { get; init; } = "test1";
    public string? CharacterName { get; init; }
    public bool Verbose { get; init; }
    public bool AiMode { get; init; }
}

/// <summary>
/// External TCP debug client that connects to the game server like a real game client.
/// Handles encryption, login sequence, packet dispatch, and optional AI loop.
/// </summary>
public sealed class TcpDebugClient : IAsyncDisposable
{
    private readonly TcpDebugClientConfig _config;
    private readonly ILogger<TcpDebugClient> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private TcpClient? _tcpClient;
    private IConnection? _connection;
    private LoginFlow? _loginFlow;
    private bool _disposed;

    // Track most recently parsed position for AI movements
    private byte _currentX;
    private byte _currentY;

    // Nearby entities and drops tracking
    private readonly Dictionary<ushort, EntityInfo> _nearbyEntities = new();
    private readonly Dictionary<ushort, DropInfo> _nearbyDrops = new();

    private record EntityInfo(ushort Id, ushort TypeNumber, byte X, byte Y);
    private record DropInfo(ushort Id, byte X, byte Y);

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpDebugClient"/> class.
    /// </summary>
    public TcpDebugClient(TcpDebugClientConfig config, ILoggerFactory? loggerFactory = null)
    {
        this._config = config;
        this._loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this._logger = this._loggerFactory.CreateLogger<TcpDebugClient>();
    }

    /// <summary>
    /// Gets the connection, once connected.
    /// </summary>
    public IConnection? Connection => this._connection;

    /// <summary>
    /// Gets the login flow handler, once login completes.
    /// </summary>
    public LoginFlow? LoginFlow => this._loginFlow;

    /// <summary>
    /// Connects to the game server and sets up the encryption pipeline.
    /// </summary>
    public async ValueTask ConnectAsync()
    {
        this._logger.LogInformation(
            "Connecting to {Server}:{Port} as '{Username}'...",
            this._config.Server, this._config.Port, this._config.Username);

        this._tcpClient = new TcpClient();
        await this._tcpClient.ConnectAsync(this._config.Server, this._config.Port).ConfigureAwait(false);

        var socketConnection = SocketConnection.Create(this._tcpClient.Client);

        // Client→Server encryption: Xor32 → SimpleModulus with ClientKey
        var encryptor = new PipelinedXor32Encryptor(
            new PipelinedSimpleModulusEncryptor(
                socketConnection.Output,
                PipelinedSimpleModulusEncryptor.DefaultClientKey).Writer);

        // Server→Client decryption: SimpleModulus with ClientKey (complements server's ServerKey)
        var decryptor = new PipelinedSimpleModulusDecryptor(
            socketConnection.Input,
            PipelinedSimpleModulusDecryptor.DefaultClientKey);

        var connectionLogger = this._loggerFactory.CreateLogger<Connection>();
        this._connection = new Connection(socketConnection, decryptor, encryptor, connectionLogger);
        this._connection.PacketReceived += this.OnPacketReceived;
        this._connection.Disconnected += this.OnDisconnected;

        _ = this._connection.BeginReceiveAsync();

        this._logger.LogInformation("Connected, encryption pipeline active.");
    }

    /// <summary>
    /// Runs the full login sequence.
    /// </summary>
    public async Task<bool> RunLoginSequenceAsync()
    {
        if (this._connection is null)
        {
            this._logger.LogError("Not connected. Call ConnectAsync first.");
            return false;
        }

        var clientVersion = new ClientVersion(6, 3, ClientLanguage.English);
        this._loginFlow = new LoginFlow(this._connection, clientVersion, this._config.Verbose, this._loggerFactory.CreateLogger<LoginFlow>());

        var success = await this._loginFlow.ExecuteAsync(
            this._config.Username,
            this._config.Password,
            this._config.CharacterName).ConfigureAwait(false);

        if (success)
        {
            this._logger.LogInformation(
                "Login complete! Character '{Name}' in game.",
                this._loginFlow.SelectedCharacterName);
        }

        return success;
    }

    /// <summary>
    /// Runs a REPL loop for interactive commands.
    /// </summary>
    public async Task RunReplAsync()
    {
        if (this._connection is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== TCP Debug Client REPL ===");
        Console.WriteLine("Commands: status, walk, say, attack, pickup, skill, move, targets, items, exit");
        Console.WriteLine();

        while (this._connection.Connected)
        {
            Console.Write("tcp> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();

            switch (command)
            {
                case "exit":
                case "quit":
                    return;

                case "status":
                    Console.WriteLine($"Connected: {this._connection.Connected}");
                    Console.WriteLine($"Character: {this._loginFlow?.SelectedCharacterName ?? "N/A"}");
                    Console.WriteLine($"Position: ({this._currentX}, {this._currentY})");
                    Console.WriteLine($"Nearby entities: {this._nearbyEntities.Count}, drops: {this._nearbyDrops.Count}");
                    break;

                case "walk":
                    if (parts.Length >= 3 &&
                        byte.TryParse(parts[1], out var wx) &&
                        byte.TryParse(parts[2], out var wy))
                    {
                        await this.SendWalkAsync(wx, wy).ConfigureAwait(false);
                    }
                    else
                    {
                        Console.WriteLine("Usage: walk <x> <y>");
                    }
                    break;

                case "say":
                    var msg = string.Join(" ", parts.Skip(1));
                    if (!string.IsNullOrEmpty(msg))
                    {
                        await this._connection.SendPublicChatMessageAsync("", msg).ConfigureAwait(false);
                        Console.WriteLine($"Sent: {msg}");
                    }
                    break;

                case "attack":
                    if (parts.Length >= 2 && ushort.TryParse(parts[1], out var targetId))
                    {
                        await this._connection.SendHitRequestAsync(targetId, 0, 0).ConfigureAwait(false);
                        Console.WriteLine($"Attacking target {targetId}.");
                    }
                    else
                    {
                        Console.WriteLine("Usage: attack <targetId>");
                    }
                    break;

                case "pickup":
                    if (parts.Length >= 2 && ushort.TryParse(parts[1], out var itemId))
                    {
                        await this._connection.SendPickupItemRequestAsync(itemId).ConfigureAwait(false);
                        Console.WriteLine($"Pickup request for item {itemId}.");
                    }
                    else
                    {
                        Console.WriteLine("Usage: pickup <itemId>");
                    }
                    break;

                case "targets":
                    Console.WriteLine($"\nNearby entities ({this._nearbyEntities.Count}):");
                    Console.WriteLine($"{"ID",5} {"Type",6} {"X",4} {"Y",4}");
                    foreach (var kv in this._nearbyEntities)
                    {
                        var e = kv.Value;
                        Console.WriteLine($"{e.Id,5} {e.TypeNumber,6} {e.X,4} {e.Y,4}");
                    }
                    break;

                case "items":
                    Console.WriteLine($"\nNearby drops ({this._nearbyDrops.Count}):");
                    Console.WriteLine($"{"ID",5} {"X",4} {"Y",4}");
                    foreach (var kv in this._nearbyDrops)
                    {
                        var d = kv.Value;
                        Console.WriteLine($"{d.Id,5} {d.X,4} {d.Y,4}");
                    }
                    break;

                case "skill":
                    if (parts.Length >= 3 &&
                        ushort.TryParse(parts[1], out var skillId) &&
                        ushort.TryParse(parts[2], out var skillTargetId))
                    {
                        await this._connection.SendTargetedSkillAsync(skillId, skillTargetId).ConfigureAwait(false);
                        Console.WriteLine($"Skill {skillId} -> target {skillTargetId}.");
                    }
                    else
                    {
                        Console.WriteLine("Usage: skill <skillId> <targetId>");
                    }
                    break;

                case "move":
                    if (parts.Length >= 3 &&
                        byte.TryParse(parts[1], out var mx) &&
                        byte.TryParse(parts[2], out var my))
                    {
                        await this._connection.SendInstantMoveRequestAsync(mx, my).ConfigureAwait(false);
                        this._currentX = mx;
                        this._currentY = my;
                        Console.WriteLine($"Moved to ({mx}, {my}).");
                    }
                    else
                    {
                        Console.WriteLine("Usage: move <x> <y>");
                    }
                    break;

                default:
                    Console.WriteLine("Commands: status, walk, say, attack, pickup, skill, move, targets, items, exit");
                    break;
            }
        }
    }

    /// <summary>
    /// Runs a simple AI loop that sends walk commands periodically.
    /// </summary>
    public async Task RunAiLoopAsync(CancellationToken ct)
    {
        if (this._connection is null)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== AI Walk Loop Started (Ctrl+C to stop) ===");

        var random = new Random();

        try
        {
            while (!ct.IsCancellationRequested && this._connection.Connected)
            {
                // Random walk: pick a direction (0-7) and step count (1-3)
                var direction = (byte)random.Next(8);
                var steps = (byte)random.Next(1, 4);

                // Build direction bytes for walk (max 3 steps)
                var directions = new byte[steps];
                Array.Fill(directions, direction);

                await this._connection.SendWalkRequestAsync(
                    this._currentX, this._currentY, steps, direction, directions).ConfigureAwait(false);

                Console.WriteLine(
                    "[AI] Walk: dir={0}, steps={1}, pos=({2},{3})",
                    direction, steps, this._currentX, this._currentY);

                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("AI loop stopped.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!this._disposed)
        {
            this._disposed = true;
            if (this._connection is not null)
            {
                this._connection.PacketReceived -= this.OnPacketReceived;
                this._connection.Disconnected -= this.OnDisconnected;
                await this._connection.DisconnectAsync().ConfigureAwait(false);
                this._connection.Dispose();
            }

            this._tcpClient?.Dispose();
            this._loggerFactory.Dispose();
        }
    }

    private async ValueTask OnPacketReceived(ReadOnlySequence<byte> packet)
    {
        if (packet.FirstSpan.Length < 3)
        {
            return;
        }

        var header = packet.FirstSpan[0];
        var length = packet.FirstSpan[1];
        var code = header switch
        {
            0xC2 or 0xC4 => packet.FirstSpan[3],
            _ => packet.FirstSpan[2],
        };

        if (this._config.Verbose)
        {
            PacketLogger.LogPacket(packet, hexDump: true);
        }

        // Route to login flow if active
        if (this._loginFlow is { IsActive: true })
        {
            await this._loginFlow.HandlePacketAsync(packet).ConfigureAwait(false);
            return;
        }

        // Handle in-game packets
        switch (code)
        {
            case 0x0E:
                // Ping from server (0x0E) - server sends ping, client responds
                break;

            case 0x13:
                // C2 AddNpcsToScope — nearby monsters/NPCs entering visible range
                this.HandleAddNpcsToScope(packet);
                break;

            case 0x14:
                // C1 MapObjectOutOfScope — objects leaving visible range
                this.HandleMapObjectOutOfScope(packet);
                break;

            case 0x20:
                // C2 ItemsDropped — nearby items on the ground
                this.HandleItemsDropped(packet);
                break;

            case 0x21:
                // C2 ItemDropRemoved — items removed from ground
                this.HandleItemDropRemoved(packet);
                break;

            case 0xA6:
            case 0xA9:
                // Walk response / coordinate updates
                this.TryUpdatePosition(packet);
                break;

            case 0x22:
                // C3 ItemPickUpRequestFailed
                if (packet.FirstSpan.Length >= 4)
                {
                    Console.WriteLine($"[Pickup] Failed: reason={packet.FirstSpan[3]}");
                }
                break;

            default:
                if (this._config.Verbose)
                {
                    PacketLogger.LogPacket(packet, hexDump: false);
                }
                break;
        }
    }

    private async ValueTask OnDisconnected()
    {
        this._logger.LogWarning("Server disconnected.");
        Console.WriteLine("Server disconnected.");
    }

    private async ValueTask SendWalkAsync(byte x, byte y)
    {
        if (this._connection is null)
        {
            return;
        }

        // Simple single-step walk
        byte targetRotation = 0;
        byte stepCount = 1;
        byte[] directions = [targetRotation];

        await this._connection.SendWalkRequestAsync(
            this._currentX, this._currentY, stepCount, targetRotation, directions).ConfigureAwait(false);

        this._currentX = x;
        this._currentY = y;
        Console.WriteLine($"Walking to ({x}, {y}).");
    }

    private void TryUpdatePosition(ReadOnlySequence<byte> packet)
    {
        if (packet.FirstSpan.Length >= 5)
        {
            this._currentX = packet.FirstSpan[3];
            this._currentY = packet.FirstSpan[4];
        }
    }

    private void HandleAddNpcsToScope(ReadOnlySequence<byte> packet)
    {
        var length = (int)packet.Length;
        if (length < 5)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        packet.CopyTo(rented);
        var span = rented.AsSpan(0, length);

        try
        {
            AddNpcsToScopeRef npcs = span;
            var count = npcs.NpcCount;
            for (int i = 0; i < count; i++)
            {
                var npc = npcs[i];
                var id = npc.Id;
                this._nearbyEntities[id] = new EntityInfo(id, npc.TypeNumber, npc.CurrentPositionX, npc.CurrentPositionY);
            }

            if (this._config.Verbose)
            {
                Console.WriteLine($"[Entities] +{count} NPCs/monsters added (total: {this._nearbyEntities.Count})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void HandleMapObjectOutOfScope(ReadOnlySequence<byte> packet)
    {
        var length = (int)packet.Length;
        if (length < 4)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        packet.CopyTo(rented);
        var span = rented.AsSpan(0, length);

        try
        {
            MapObjectOutOfScopeRef outOfScope = span;
            var count = outOfScope.ObjectCount;
            for (int i = 0; i < count; i++)
            {
                var objId = outOfScope[i];
                this._nearbyEntities.Remove(objId.Id);
            }

            if (this._config.Verbose)
            {
                Console.WriteLine($"[Entities] -{count} objects removed (total: {this._nearbyEntities.Count})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void HandleItemsDropped(ReadOnlySequence<byte> packet)
    {
        var length = (int)packet.Length;
        if (length < 5)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        packet.CopyTo(rented);
        var span = rented.AsSpan(0, length);

        try
        {
            ItemsDroppedRef items = span;
            var count = items.ItemCount;

            // Calculate DroppedItemRef entry size from packet length
            var headerSize = 5; // C2 header(4) + itemCount(1)
            var dataLength = length - headerSize;
            var entrySize = count > 0 ? dataLength / count : 0;

            if (entrySize < 4)
            {
                return; // malformed packet
            }

            for (int i = 0; i < count; i++)
            {
                var item = items[i, entrySize];
                var id = item.Id;
                this._nearbyDrops[id] = new DropInfo(id, item.PositionX, item.PositionY);
            }

            if (this._config.Verbose)
            {
                Console.WriteLine($"[Drops] +{count} items dropped (total: {this._nearbyDrops.Count})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void HandleItemDropRemoved(ReadOnlySequence<byte> packet)
    {
        var length = (int)packet.Length;
        if (length < 5)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        packet.CopyTo(rented);
        var span = rented.AsSpan(0, length);

        try
        {
            ItemDropRemovedRef removed = span;
            var count = removed.ItemCount;
            for (int i = 0; i < count; i++)
            {
                var itemId = removed[i];
                this._nearbyDrops.Remove(itemId.Id);
            }

            if (this._config.Verbose)
            {
                Console.WriteLine($"[Drops] -{count} items removed (total: {this._nearbyDrops.Count})");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
