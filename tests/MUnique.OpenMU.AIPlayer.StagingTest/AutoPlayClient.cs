// <copyright file="AutoPlayClient.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.StagingTest;

using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.TcpDebugClient;

/// <summary>
/// Automated end-to-end staging test client.
/// Connects to a running GameServer, logs in, enters world,
/// performs actions, and reports pass/fail verdict.
///
/// Usage:
///   dotnet run -- [host] [port] [username] [password] [character]
/// </summary>
internal static class AutoPlayClient
{
    private static readonly string[] VerdictNames = ["PASS", "PASS/COND", "HOLD", "BLOCK"];

    internal enum Verdict { Pass, PassCond, Hold, Block }

    internal sealed record StepResult(string Step, bool Success, string Detail, Verdict V);

    /// <summary>Entry point.</summary>
    internal static async Task<int> Main(string[] args)
    {
        var host = args.Length > 0 ? args[0] : "localhost";
        var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 55901;
        var username = args.Length > 2 ? args[2] : "test1";
        var password = args.Length > 3 ? args[3] : "test1";
        var character = args.Length > 4 ? args[4] : "test1Dk";

        Console.WriteLine("========================================");
        Console.WriteLine("  MU AI Player — Staging E2E Test");
        Console.WriteLine($"  Target: {host}:{port}");
        Console.WriteLine($"  User:   {username}");
        Console.WriteLine($"  Char:   {character}");
        Console.WriteLine("========================================");

        var results = new List<StepResult>();

        try
        {
            // ── Step 1: TCP Connect ──
            Console.Write("\n[1/5] Connecting... ");
            var config = new TcpDebugClientConfig
            {
                Server = host,
                Port = port,
                Username = username,
                Password = password,
                CharacterName = character,
            };

            await using var client = new TcpDebugClient(config);
            await client.ConnectAsync().ConfigureAwait(false);
            if (client.Connection?.Connected != true)
            {
                return RecordFailure(results, "TCP Connect", "Failed to establish TCP connection", Verdict.Block);
            }

            Console.WriteLine("OK (socket connected)");
            results.Add(new StepResult("TCP Connect", true, $"Connected to {host}:{port}", Verdict.Pass));

            // ── Step 2: Login Sequence ──
            Console.Write("[2/5] Login sequence... ");
            var loginOk = await client.RunLoginSequenceAsync().ConfigureAwait(false);
            if (!loginOk)
            {
                return RecordFailure(results, "Login Sequence", "Login failed (auth timeout or rejected)", Verdict.Block);
            }

            Console.WriteLine("OK (logged in)");
            results.Add(new StepResult("Login Sequence", true, $"User '{username}' authenticated, character '{character}' selected", Verdict.Pass));

            // ── Step 3: Enter World ──
            Console.Write("[3/5] Enter world... ");
            if (client.LoginFlow?.SelectedCharacterName != character)
            {
                return RecordFailure(results, "Enter World", $"Expected character '{character}', got '{client.LoginFlow?.SelectedCharacterName}'", Verdict.Hold);
            }

            Console.WriteLine("OK (in game)");
            results.Add(new StepResult("Enter World", true, $"Character '{character}' entered world", Verdict.Pass));

            // ── Step 4: Walk ──
            Console.Write("[4/5] Walking... ");
            var walkX = (byte)110;
            var walkY = (byte)110;
            await client.Connection!.SendWalkRequestAsync(walkX, walkY, 1, 0, new byte[] { 0 }).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);

            // Walk succeeded if no exception thrown
            Console.WriteLine($"OK (walk to {walkX},{walkY})");
            results.Add(new StepResult("Walk", true, $"Walk request sent to ({walkX},{walkY})", Verdict.Pass));

            // ── Step 5: Chat Message ──
            Console.Write("[5/5] Chat... ");
            await client.Connection!.SendPublicChatMessageAsync("", "Hello from AI Player!").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            Console.WriteLine("OK (chat sent)");
            results.Add(new StepResult("Chat", true, "Public chat message sent", Verdict.Pass));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFAILED: {ex.Message}");
            results.Add(new StepResult("Exception", false, ex.ToString(), Verdict.Block));
        }

        // ── Summary ──
        Console.WriteLine("\n========================================");
        Console.WriteLine("  E2E Test Results");
        Console.WriteLine("========================================");
        foreach (var r in results)
        {
            var icon = r.Success ? "[PASS]" : "[FAIL]";
            var color = r.V == Verdict.Block ? "[31m" : r.V == Verdict.Hold ? "[33m" : "[32m";
            Console.WriteLine($"  {color}{icon}[0m {r.Step,-20} {r.Detail}");
        }

        var finalVerdict = results.Any(r => r.V == Verdict.Block) ? "BLOCK" :
                           results.Any(r => r.V == Verdict.Hold) ? "HOLD" :
                           results.Any(r => r.V == Verdict.PassCond) ? "PASS/COND" : "PASS";

        Console.WriteLine($"\n  Final Verdict: {finalVerdict}");
        Console.WriteLine($"  {results.Count(r => r.Success)}/{results.Count} steps passed");
        Console.WriteLine("========================================");

        return finalVerdict switch
        {
            "PASS" => 0,
            "PASS/COND" => 1,
            "HOLD" => 2,
            _ => 3,
        };
    }

    private static int RecordFailure(List<StepResult> results, string step, string detail, Verdict verdict)
    {
        Console.WriteLine($"FAILED: {detail}");
        results.Add(new StepResult(step, false, detail, verdict));
        Console.WriteLine($"\n  Final Verdict: {VerdictNames[(int)verdict]}");
        return 3;
    }
}
