namespace MUnique.OpenMU.TcpDebugClient;

/// <summary>
/// Entry point for the MU TCP debug client.
/// Connects to an OpenMU game server, logs in, and optionally runs an AI walk loop.
/// </summary>
internal static class Program
{
    /// <summary>Entry point.</summary>
    internal static async Task Main(string[] args)
    {
        var config = ParseArgs(args);

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?")
        {
            PrintHelp();
            return;
        }

        Console.WriteLine($"MU TCP Debug Client");
        Console.WriteLine($"Target: {config.Server}:{config.Port}");
        Console.WriteLine($"Account: {config.Username}");
        Console.WriteLine();

        var client = new TcpDebugClient(config);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                await client.ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to connect: {ex.Message}");
                return;
            }

            var loggedIn = await client.RunLoginSequenceAsync().ConfigureAwait(false);
            if (!loggedIn)
            {
                Console.Error.WriteLine("Login failed. Check credentials and server state.");
                return;
            }

            if (config.AiMode)
            {
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                await client.RunAiLoopAsync(cts.Token).ConfigureAwait(false);
            }
            else
            {
                await client.RunReplAsync().ConfigureAwait(false);
            }
        }

        Console.WriteLine("Disconnected. Goodbye!");
    }

    private static TcpDebugClientConfig ParseArgs(string[] args)
    {
        var config = new TcpDebugClientConfig();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--server":
                    config = config with { Server = args[++i] };
                    break;
                case "--port":
                    config = config with { Port = int.Parse(args[++i]) };
                    break;
                case "--username":
                    config = config with { Username = args[++i] };
                    break;
                case "--password":
                    config = config with { Password = args[++i] };
                    break;
                case "--character":
                    config = config with { CharacterName = args[++i] };
                    break;
                case "--verbose":
                    config = config with { Verbose = true };
                    break;
                case "--ai":
                    config = config with { AiMode = true };
                    break;
            }
        }

        return config;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MU TCP Debug Client - External TCP connection debugger

            Usage:
              MUnique.OpenMU.TcpDebugClient --server <host> [options]

            Options:
              --server <host>       Game server host (default: localhost)
              --port <port>         Game server port (default: 55901)
              --username <name>     Account login name (default: test1)
              --password <pass>     Account password (default: test1)
              --character <name>    Character to select (default: first in list)
              --verbose             Show raw packet hex dumps
              --ai                  Run AI walk loop after login (default: REPL mode)
              --help                Show this help

            Examples:
              MUnique.OpenMU.TcpDebugClient --server localhost --port 55901 --verbose
              MUnique.OpenMU.TcpDebugClient --server localhost --ai
              MUnique.OpenMU.TcpDebugClient --username admin --password s3cret --verbose
            """);
    }
}
