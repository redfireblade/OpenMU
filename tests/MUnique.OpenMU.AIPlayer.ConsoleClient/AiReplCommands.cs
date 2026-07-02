// <copyright file="AiReplCommands.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.ConsoleClient;

using MUnique.OpenMU.AIPlayer.Testing;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.TcpDebugClient;
using Spectre.Console;

/// <summary>
/// Command handlers for the AI player debug REPL.
/// Wraps <see cref="AiPlayerTestHarness"/> and provides Spectre.Console formatted output.
/// Supports two modes: InProcess (harness-based) and Tcp (external server connection).
/// </summary>
internal sealed class AiReplCommands : IAsyncDisposable
{
    private AiPlayerTestHarness? _harness;
    private TcpDebugClient? _tcpClient;
    private RunMode _mode = RunMode.InProcess;
    private bool _disposed;

    private enum RunMode { InProcess, Tcp }

    /// <summary>
    /// Ensures a default harness exists. Called at REPL startup.
    /// </summary>
    internal async Task EnsureHarnessAsync()
    {
        try
        {
            this._harness = await AiPlayerTestHarness.CreateAsync("DebugBot").ConfigureAwait(false);
            AnsiConsole.MarkupLine("[green]AI player 'DebugBot' created[/] (map 0, level 1, step mode).");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to create default player:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[yellow]Use 'create' to create one manually.[/]");
        }
    }

    internal void Help()
    {
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Command");
        table.AddColumn(new TableColumn("Description").LeftAligned());

        table.AddRow("[green]create[/] [name] [class] [map]", "Create a new AI player (in-process)");
        table.AddRow("[green]status[/]", "Show player status (HP/MP/level/position)");
        table.AddRow("[green]step[/] [n]", "Execute N ticks (default 1)");
        table.AddRow("[green]modules[/]", "Show last tick's module decisions");
        table.AddRow("[green]hp[/] <value>", "Set current HP");
        table.AddRow("[green]mp[/] <value>", "Set current MP");
        table.AddRow("[green]level[/] <n>", "Set character level");
        table.AddRow("[green]pos[/] <x> <y>", "Set position");
        table.AddRow("[green]inventory[/]", "Show inventory contents");
        table.AddRow("[green]additem[/] <group> <number>", "Add an item to inventory");
        table.AddRow("[green]spawn[/] <id> [x] [y]", "Spawn a monster near the player");
        table.AddRow("[green]clearmonsters[/]", "Remove all monsters from the map");
        table.AddRow("[green]watch[/]", "Live dashboard (auto-refresh every 400ms)");
        table.AddRow("[green]cont[/]", "Toggle continuous 400ms auto-step mode");
        table.AddRow("[green]scenario[/] <name>", "Load a preset scenario (lowhp, empty, combat)");
        table.AddRow(string.Empty, string.Empty);
        table.AddRow("[bold]TCP Commands[/]", string.Empty);
        table.AddRow("[green]connect[/] [host] [port]", "Connect to a game server via TCP (default: localhost:55901)");
        table.AddRow("[green]disconnect[/]", "Disconnect TCP and return to in-process mode");
        table.AddRow("[green]walk[/] <x> <y>", "Send walk request to the server");
        table.AddRow("[green]say[/] <message>", "Send chat message");
        table.AddRow(string.Empty, string.Empty);
        table.AddRow("[green]help[/]", "Show this help");
        table.AddRow("[red]exit[/]", "Exit");

        AnsiConsole.Write(table);
    }

    internal async Task CreateAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;

        var name = args.Length > 0 ? args[0] : "DebugBot";
        var classNum = args.Length > 1 && int.TryParse(args[1], out var cn) ? cn : 0;
        var mapId = args.Length > 2 && ushort.TryParse(args[2], out var mn) ? mn : (ushort)0;

        if (this._harness is not null)
        {
            await this._harness.DisposeAsync().ConfigureAwait(false);
        }

        this._harness = await AiPlayerTestHarness.CreateAsync(name, classNum, mapId).ConfigureAwait(false);
        AnsiConsole.MarkupLineInterpolated($"[green]Player '{name}' created[/] (class={classNum}, map={mapId}).");
    }

    internal async Task StatusAsync()
    {
        if (this._mode == RunMode.Tcp)
        {
            this.ShowTcpStatus();
            return;
        }

        if (this._harness is null)
        {
            AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
            return;
        }

        var ctx = this._harness.Context;
        var snap = ctx.GetLatestSnapshot();
        var player = this._harness.Player;

        var emergencyText = snap?.EmergencyRetreat == true ? "[red]RETREAT[/]" : "no";
        var survivalText = snap?.SurvivalLevel.ToString() ?? "(no data)";
        var targetText = snap?.HasTarget == true ? "yes" : "no";

        string statusText;
        if (snap is not null)
        {
            statusText = $"HP: {RenderBar(snap.Hp, snap.MaxHp, 20)} [bold]{snap.Hp}/{snap.MaxHp}[/]   " +
                $"MP: {RenderBar(snap.Mp, snap.MaxMp, 20)} [bold]{snap.Mp}/{snap.MaxMp}[/]\n" +
                $"[bold]Survival:[/] {survivalText}   " +
                $"[bold]Target:[/] {targetText}   " +
                $"[bold]Emergency:[/] {emergencyText}";
        }
        else
        {
            statusText = "(no tick data)";
        }

        var panel = new Panel(
            Align.Center(new Markup(
                $"[bold]Level:[/] {player.Level}   " +
                $"[bold]Map:[/] {player.CurrentMap?.Definition.Number ?? 0}   " +
                $"[bold]Pos:[/] ({player.Position.X}, {player.Position.Y})   " +
                $"[bold]Tick:[/] {player.Logic?.TickCounter ?? 0}\n" +
                statusText)))
        {
            Header = new PanelHeader($"[bold]{player.SelectedCharacter?.Name ?? "Unknown"}[/]"),
        };

        AnsiConsole.Write(panel);
    }

    internal async Task StepAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;

        if (this._harness is null)
        {
            AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
            return;
        }

        var count = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 1;
        if (count < 1) count = 1;

        var snaps = await this._harness.StepAsync(count).ConfigureAwait(false);
        var last = snaps[^1];

        var survivalText = last.SurvivalLevel.ToString();
        var targetText = last.HasTarget ? "[green]yes[/]" : "no";
        var emergencyText = last.EmergencyRetreat ? "[red]RETREAT[/]" : "no";

        AnsiConsole.MarkupLine(
            $"[green]{count}[/] tick(s) executed. " +
            $"Tick #[yellow]{last.TickNumber}[/], " +
            $"HP=[green]{last.Hp}/{last.MaxHp}[/], " +
            $"MP=[blue]{last.Mp}/{last.MaxMp}[/], " +
            $"survival=[yellow]{survivalText}[/], " +
            $"target={targetText}, " +
            $"emergency={emergencyText}");
    }

    internal async Task ModulesAsync()
    {
        if (!this.EnsureInProcess()) return;

        if (this._harness is null)
        {
            AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
            return;
        }

        var snap = this._harness.Context.GetLatestSnapshot();
        if (snap is null)
        {
            AnsiConsole.MarkupLine("[yellow]No tick data. Run 'step' first.[/]");
            return;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Module");
        table.AddColumn("Duration");
        table.AddColumn("Tick #");

        foreach (var decision in snap.Decisions)
        {
            var color = decision.Duration.TotalMilliseconds < 5 ? "green"
                : decision.Duration.TotalMilliseconds < 20 ? "yellow"
                : "red";
            table.AddRow(
                $"[bold]{decision.ModuleName}[/]",
                $"[{color}]{decision.Duration.TotalMilliseconds,7:F1} ms[/]",
                snap.TickNumber.ToString());
        }

        AnsiConsole.Write(table);
    }

    internal async Task SetHpAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 1 || !uint.TryParse(args[0], out var hp))
        {
            AnsiConsole.MarkupLine("[red]Usage: hp <value>[/]");
            return;
        }

        this._harness.SetHp(hp);
        AnsiConsole.MarkupLineInterpolated($"[green]HP set to {hp}.[/]");
    }

    internal async Task SetMpAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 1 || !uint.TryParse(args[0], out var mp))
        {
            AnsiConsole.MarkupLine("[red]Usage: mp <value>[/]");
            return;
        }

        this._harness.SetMp(mp);
        AnsiConsole.MarkupLineInterpolated($"[green]MP set to {mp}.[/]");
    }

    internal async Task SetLevelAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 1 || !int.TryParse(args[0], out var level))
        {
            AnsiConsole.MarkupLine("[red]Usage: level <n>[/]");
            return;
        }

        this._harness.SetLevel(level);
        AnsiConsole.MarkupLineInterpolated($"[green]Level set to {level}.[/]");
    }

    internal async Task SetPositionAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 2 || !byte.TryParse(args[0], out var x) || !byte.TryParse(args[1], out var y))
        {
            AnsiConsole.MarkupLine("[red]Usage: pos <x> <y>[/]");
            return;
        }

        this._harness.SetPosition(x, y);
        AnsiConsole.MarkupLineInterpolated($"[green]Position set to ({x}, {y}).[/]");
    }

    internal async Task InventoryAsync()
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        var items = this._harness.Player.Inventory?.Items;
        if (items is null)
        {
            AnsiConsole.MarkupLine("[yellow]Inventory is empty.[/]");
            return;
        }

        var itemList = items.ToList();
        if (itemList.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Inventory is empty.[/]");
            return;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Slot");
        table.AddColumn("Item");
        table.AddColumn("Group");
        table.AddColumn("Number");
        table.AddColumn("Durability");

        foreach (var item in itemList.OrderBy(i => i.ItemSlot))
        {
            table.AddRow(
                item.ItemSlot.ToString(),
                item.Definition?.Name ?? "Unknown",
                item.Definition?.Group.ToString() ?? "-",
                item.Definition?.Number.ToString() ?? "-",
                item.Durability.ToString("F1"));
        }

        AnsiConsole.Write(table);
    }

    internal async Task AddItemAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 2 || !int.TryParse(args[0], out var group) || !int.TryParse(args[1], out var number))
        {
            AnsiConsole.MarkupLine("[red]Usage: additem <group> <number>[/]");
            return;
        }

        var ctx = this._harness.PersistenceContext;
        var def = ctx.CreateNew<ItemDefinition>();
        def.Group = (byte)group;
        def.Number = (short)number;
        def.Width = (byte)1;
        def.Height = (byte)1;
        def.Durability = 1;

        var item = ctx.CreateNew<Item>();
        item.Definition = def;

        // Find next available slot starting from 12 (inventory)
        var existingSlots = this._harness.Player.Inventory?.Items
            .Select(i => (int)i.ItemSlot)
            .ToHashSet() ?? new HashSet<int>();

        byte slot = 12;
        while (existingSlots.Contains(slot) && slot < 64) slot++;

        item.ItemSlot = slot;
        item.Durability = 1;

        await this._harness.AddInventoryItemAsync(item).ConfigureAwait(false);
        AnsiConsole.MarkupLineInterpolated($"[green]Added item ({group}, {number}) to slot {slot}.[/]");
    }

    internal async Task SpawnAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        if (args.Length < 1 || !int.TryParse(args[0], out var monsterNumber))
        {
            AnsiConsole.MarkupLine("[red]Usage: spawn <id> [x] [y][/]");
            return;
        }

        var player = this._harness.Player;
        var map = player.CurrentMap;
        if (map is null)
        {
            AnsiConsole.MarkupLine("[red]Player not on a map.[/]");
            return;
        }

        var x = args.Length > 1 && byte.TryParse(args[1], out var px) ? px : (byte)(player.Position.X + 3);
        var y = args.Length > 2 && byte.TryParse(args[2], out var py) ? py : player.Position.Y;

        // Look up monster definition from game config
        var monsterDef = player.GameContext.Configuration.Monsters.FirstOrDefault(m => m.Number == monsterNumber);
        if (monsterDef is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]Monster #{monsterNumber} not found in config.[/]");
            return;
        }

        var area = new MonsterSpawnArea
        {
            MonsterDefinition = monsterDef,
            SpawnTrigger = SpawnTrigger.Automatic,
            Quantity = 1,
            X1 = x,
            X2 = x,
            Y1 = y,
            Y2 = y,
        };

        try
        {
            var intelligence = new BasicMonsterIntelligence();
            var monster = new Monster(
                area,
                monsterDef,
                map,
                player.GameContext.DropGenerator,
                intelligence,
                player.GameContext.PlugInManager,
                player.GameContext.PathFinderPool);
            intelligence.Npc = monster;

            monster.Initialize();
            monster.Position = new Pathfinding.Point(x, y);
            await map.AddAsync(monster).ConfigureAwait(false);
            monster.OnSpawn();
            AnsiConsole.MarkupLineInterpolated($"[green]Spawned monster #{monsterNumber} at ({x}, {y}).[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Failed to spawn: {ex.Message}[/]");
        }
    }

    internal async Task ClearMonstersAsync()
    {
        if (!this.EnsureInProcess()) return;
        if (this._harness is null) { MissingPlayer(); return; }

        var map = this._harness.Player.CurrentMap;
        if (map is null)
        {
            AnsiConsole.MarkupLine("[red]Player not on a map.[/]");
            return;
        }

        var monsters = map.GetAttackablesInRange(
                new Pathfinding.Point(0, 0),
                256)
            .OfType<Monster>()
            .ToList();

        foreach (var m in monsters)
        {
            await map.RemoveAsync(m).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLineInterpolated($"[green]Removed {monsters.Count} monsters from the map.[/]");
    }

    internal async Task WatchAsync()
    {
        if (!this.EnsureInProcess()) return;

        if (this._harness is null)
        {
            AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[yellow]Entering watch mode. Press [bold]CTRL+C[/] to stop.[/]");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await AnsiConsole.Live(ReplRenderer.CreateDashboard(this._harness))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(400, cts.Token).ConfigureAwait(false);

                        // Step and update display
                        if (this._harness.Player.Logic is { } logic)
                        {
                            await logic.TickOnceAsync().ConfigureAwait(false);
                        }

                        ctx.UpdateTarget(ReplRenderer.CreateDashboard(this._harness));
                    }
                });
        }
        catch (OperationCanceledException)
        {
        }

        AnsiConsole.MarkupLine("[yellow]Watch mode stopped.[/]");
    }

    internal void ContinuousMode()
    {
        AnsiConsole.MarkupLine("[yellow]Continuous mode toggle is only available in the server context.[/]");
        AnsiConsole.MarkupLine("Use [green]step[/] to execute ticks manually, or [green]watch[/] for auto-refresh display.");
    }

    internal async Task ScenarioAsync(string[] args)
    {
        if (!this.EnsureInProcess()) return;

        if (this._harness is null)
        {
            AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
            return;
        }

        var scenario = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        switch (scenario)
        {
            case "lowhp":
                var maxHp = this._harness.Player.Attributes?[Stats.MaximumHealth] ?? 100;
                this._harness.SetHp((uint)(maxHp * 0.25));
                AnsiConsole.MarkupLineInterpolated($"[yellow]Scenario 'lowhp': HP set to 25% ({(uint)(maxHp * 0.25)}/{maxHp}). Step to observe potion use.[/]");
                break;

            case "empty":
                this._harness.SetHp(1);
                this._harness.SetMp(1);
                AnsiConsole.MarkupLine("[yellow]Scenario 'empty': HP=1, MP=1. Emergency state.[/]");
                break;

            case "combat":
                await this.SpawnAsync(["0"]).ConfigureAwait(false);
                await this.SpawnAsync(["0"]).ConfigureAwait(false);
                await this.SpawnAsync(["0"]).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[yellow]Scenario 'combat': 3 monsters spawned nearby. Step to engage.[/]");
                break;

            default:
                AnsiConsole.MarkupLine("[yellow]Available scenarios: lowhp, empty, combat[/]");
                break;
        }
    }

    /// <summary>Connects to a game server via TCP and logs in.</summary>
    internal async Task ConnectTcpAsync(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "localhost";
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 55901;

        if (this._tcpClient is not null)
        {
            AnsiConsole.MarkupLine("[yellow]Already connected. Use 'disconnect' first.[/]");
            return;
        }

        var config = new TcpDebugClientConfig
        {
            Server = host,
            Port = port,
            Username = "test1",
            Password = "test1",
        };

        AnsiConsole.MarkupLineInterpolated($"[green]Connecting to {host}:{port}...[/]");
        this._tcpClient = new TcpDebugClient(config);
        try
        {
            await this._tcpClient.ConnectAsync().ConfigureAwait(false);
            var ok = await this._tcpClient.RunLoginSequenceAsync().ConfigureAwait(false);
            if (!ok)
            {
                AnsiConsole.MarkupLine("[red]Login failed. Disconnecting.[/]");
                await this._tcpClient.DisposeAsync().ConfigureAwait(false);
                this._tcpClient = null;
                return;
            }

            this._mode = RunMode.Tcp;
            AnsiConsole.MarkupLine("[green]TCP mode active[/]. Use 'disconnect' to return to in-process mode.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]TCP connect failed:[/] {ex.Message}");
            if (this._tcpClient is not null)
            {
                await this._tcpClient.DisposeAsync().ConfigureAwait(false);
                this._tcpClient = null;
            }
        }
    }

    /// <summary>Disconnects from the game server and returns to in-process mode.</summary>
    internal async Task DisconnectTcpAsync()
    {
        if (this._tcpClient is null)
        {
            AnsiConsole.MarkupLine("[yellow]Not connected.[/]");
            return;
        }

        await this._tcpClient.DisposeAsync().ConfigureAwait(false);
        this._tcpClient = null;
        this._mode = RunMode.InProcess;
        AnsiConsole.MarkupLine("[green]Disconnected, back to in-process mode.[/]");
    }

    /// <summary>Sends a walk request via TCP connection.</summary>
    internal async Task TcpWalkAsync(string[] args)
    {
        if (this._tcpClient?.Connection is null)
        {
            AnsiConsole.MarkupLine("[red]Not connected. Use 'connect' first.[/]");
            return;
        }

        if (args.Length < 2 || !byte.TryParse(args[0], out var x) || !byte.TryParse(args[1], out var y))
        {
            AnsiConsole.MarkupLine("[red]Usage: walk <x> <y>[/]");
            return;
        }

        await this._tcpClient.Connection.SendWalkRequestAsync(x, y, 1, 0, new byte[] { 0 }).ConfigureAwait(false);
        AnsiConsole.MarkupLineInterpolated($"[green]Walk request sent to ({x}, {y}).[/]");
    }

    /// <summary>Sends a chat message via TCP connection.</summary>
    internal async Task TcpSayAsync(string[] args)
    {
        if (this._tcpClient?.Connection is null)
        {
            AnsiConsole.MarkupLine("[red]Not connected. Use 'connect' first.[/]");
            return;
        }

        var msg = string.Join(" ", args);
        if (string.IsNullOrEmpty(msg))
        {
            AnsiConsole.MarkupLine("[red]Usage: say <message>[/]");
            return;
        }

        await this._tcpClient.Connection.SendPublicChatMessageAsync("", msg).ConfigureAwait(false);
        AnsiConsole.MarkupLineInterpolated($"[green]Sent: {msg}[/]");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!this._disposed)
        {
            this._disposed = true;
            if (this._harness is not null)
            {
                await this._harness.DisposeAsync().ConfigureAwait(false);
            }

            if (this._tcpClient is not null)
            {
                await this._tcpClient.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void ShowTcpStatus()
    {
        if (this._tcpClient is null)
        {
            AnsiConsole.MarkupLine("[red]Not connected.[/]");
            return;
        }

        var connected = this._tcpClient.Connection?.Connected ?? false;
        var characterName = this._tcpClient.LoginFlow?.SelectedCharacterName ?? "N/A";

        var panel = new Panel(
            Align.Center(new Markup(
                $"[bold]Mode:[/] [green]TCP[/]\n" +
                $"[bold]Connected:[/] {(connected ? "[green]yes[/]" : "[red]no[/]")}\n" +
                $"[bold]Character:[/] {characterName}")))
        {
            Header = new PanelHeader("[bold]TCP Connection[/]"),
        };

        AnsiConsole.Write(panel);
    }

    private bool EnsureInProcess()
    {
        if (this._mode == RunMode.Tcp)
        {
            AnsiConsole.MarkupLine("[yellow]Command only available in in-process mode. Use 'disconnect' first.[/]");
            return false;
        }

        return true;
    }

    private static void MissingPlayer()
    {
        AnsiConsole.MarkupLine("[red]No AI player. Use 'create' first.[/]");
    }

    private static string RenderBar(uint value, uint max, int width)
    {
        if (max == 0) return new string('░', width);

        var filled = (int)((float)value / max * width);
        filled = Math.Clamp(filled, 0, width);

        var bar = new char[width];
        for (int i = 0; i < width; i++)
        {
            bar[i] = i < filled ? '█' : '░';
        }

        return $"[green]{new string(bar.Take(filled).ToArray())}[/][gray]{new string(bar.Skip(filled).ToArray())}[/]";
    }
}
