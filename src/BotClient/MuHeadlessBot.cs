using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;
using MUnique.OpenMU.BotClient.Actions;
using MUnique.OpenMU.BotClient.BehaviorTree;
using MUnique.OpenMU.BotClient.Core;
using MUnique.OpenMU.BotClient.FSM;

namespace MUnique.OpenMU.BotClient;

/// <summary>
/// State of a headless bot connection.
/// </summary>
public enum BotState
{
    /// <summary>Not connected.</summary>
    Disconnected,

    /// <summary>Connecting to server.</summary>
    Connecting,

    /// <summary>Connected, waiting for GameServerEntered.</summary>
    Connected,

    /// <summary>Login sent, waiting for response.</summary>
    LoginSent,

    /// <summary>Logged in, at character selection.</summary>
    LoggedIn,

    /// <summary>Character list received.</summary>
    CharListReceived,

    /// <summary>In game world, character is playing.</summary>
    InGame,

    /// <summary>Character is dead.</summary>
    Dead,
}

/// <summary>
/// Info about a monster, NPC, or other player in visible range.
/// </summary>
public sealed class MonsterInfo
{
    /// <summary>Gets the object ID.</summary>
    public ushort Id { get; init; }

    /// <summary>Gets or sets the current X position.</summary>
    public byte PosX { get; set; }

    /// <summary>Gets or sets the current Y position.</summary>
    public byte PosY { get; set; }

    /// <summary>Gets the name (empty for NPCs).</summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// A headless bot client that connects, logs in, wanders, and fights monsters.
/// Uses the same connection/encryption pattern proven in Program.cs.
/// </summary>
public sealed class MuHeadlessBot : IDisposable
{
    // ─── Fields ───

    private readonly int _id;
    private readonly string _host;
    private readonly int _port;
    private readonly string _account;
    private readonly string _password;
    private readonly string _posFilePath;
    private readonly ILogger _logger;
    private readonly Random _rng = new();

    private Connection? _conn;
    private Socket? _sock;

    // State
    private BotState _state = BotState.Disconnected;

    // World mirror
    private byte _posX;
    private byte _posY;
    private ushort _mapId;
    private ushort _currentHp;
    private ushort _maximumHp;

    // Monster tracking (thread-safe)
    private readonly ConcurrentDictionary<ushort, MonsterInfo> _monsters = new();

    // Identity
    private string? _characterName;
    private ushort? _playerId;

    // One-shot TCS for login handshake steps
    private TaskCompletionSource? _gseTcs;
    private TaskCompletionSource? _loginResultTcs;
    private TaskCompletionSource? _charListTcs;
    private TaskCompletionSource? _charInfoTcs;

    // Tick counter for interval timing
    private int _tickCount;

    // Intervals in ticks (TickAsync called every ~200ms)
    private const int WalkIntervalTicks = 10;   // every ~2s
    private const int CombatIntervalTicks = 5;  // every ~1s

    // Direction-offset map: index 1..8 maps to MU screen directions.
    private static readonly (sbyte DX, sbyte DY)[] DirectionOffsets =
    {
        (0, 0),    // 0: unused
        (-1, -1),  // 1
        (0, -1),   // 2
        (1, -1),   // 3
        (1, 0),    // 4
        (1, 1),    // 5
        (0, 1),    // 6
        (-1, 1),   // 7
        (-1, 0),   // 8
    };

    // ─── 世界镜像（由封包处理器写入，AI 引擎读取） ───
    public WorldMirror Mirror { get; } = new();

    // ─── BOT 上下文（黑板） ───
    public BotContext BotCtx { get; private set; } = default!;

    // ─── 决策引擎 ───
    private BotStateMachine? _fsmEngine;
    private BehaviorTreeEngine? _btEngine;
    private BotDecisionMode _decisionMode = BotDecisionMode.FiniteStateMachine;

    // ─── 动作分发委托 ───
    public BotActionDelegates? ActionDelegates { get; private set; }

    // ─── Public Properties ───

    /// <summary>Gets the current bot state.</summary>
    public BotState State => _state;

    /// <summary>Gets the current X position.</summary>
    public byte PosX => _posX;

    /// <summary>Gets the current Y position.</summary>
    public byte PosY => _posY;

    /// <summary>Gets the current map ID.</summary>
    public ushort MapId => _mapId;

    /// <summary>Gets the current health points.</summary>
    public ushort CurrentHP => _currentHp;

    /// <summary>Gets the maximum health points.</summary>
    public ushort MaximumHP => _maximumHp;

    /// <summary>Gets the number of monsters in visible range.</summary>
    public int MonsterCount => _monsters.Count;

    // ─── Constructor ───

    /// <summary>
    /// Initializes a new instance of the <see cref="MuHeadlessBot"/> class.
    /// </summary>
    public MuHeadlessBot(int id, string host, int port, string account, string password, string posFilePath, ILogger logger)
    {
        _id = id;
        _host = host;
        _port = port;
        _account = account;
        _password = password;
        _posFilePath = posFilePath;
        _logger = logger;

        BotCtx = new BotContext
        {
            BotId = id,
            AccountName = account,
            Password = password,
            CharacterName = "",
            Host = host,
            Port = port,
        };
    }

    // ─── LoginAsync ───

    /// <summary>
    /// Connects to the game server, performs the full login sequence,
    /// selects the first character, and enters the game world.
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        _state = BotState.Connecting;
        _logger.LogInformation("[{Id}] Connecting to {Host}:{Port}...", _id, _host, _port);

        try
        {
            // --- Connection: Socket → SocketConnection → SimpleModulus + Xor32 → Connection ---
            // This mirrors the working pattern proven in Program.cs exactly.
            _sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await _sock.ConnectAsync(_host, _port);
            var sc = SocketConnection.Create(_sock);

            var dec = new PipelinedSimpleModulusDecryptor(sc.Input, PipelinedSimpleModulusDecryptor.DefaultClientKey);
            var sme = new PipelinedSimpleModulusEncryptor(sc.Output, PipelinedSimpleModulusEncryptor.DefaultClientKey);
            var x32 = new PipelinedXor32Encryptor(sme.Writer, DefaultKeys.Xor32Key);

            _conn = new Connection(sc, dec, x32, new LogCon<Connection>());

            // Create the TCS before starting the receive loop to avoid a race:
            // the server may send GameServerEntered (0xF1,0x00) immediately upon
            // connection, and OnPacketReceived must find _gseTcs already initialized.
            _gseTcs = new TaskCompletionSource();

            // Subscribe single central packet handler and start receive loop
            _conn.PacketReceived += OnPacketReceived;
            _ = _conn.BeginReceiveAsync();

            // 1. Wait for GameServerEntered (0xF1, 0x00)
            await _gseTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _logger.LogInformation("[{Id}] GameServerEntered received", _id);
            _state = BotState.Connected;

            await Task.Delay(500);

            // 2. Send login with XOR3-obfuscated credentials (same as Program.cs)
            var ub = new byte[10];
            var pb = new byte[10];
            Encoding.UTF8.GetBytes(_account, 0, Math.Min(_account.Length, 10), ub, 0);
            Encoding.UTF8.GetBytes(_password, 0, Math.Min(_password.Length, 10), pb, 0);
            var x3 = DefaultKeys.Xor3Keys;
            for (int i = 0; i < 10; i++)
            {
                ub[i] ^= x3[i % 3];
                pb[i] ^= x3[i % 3];
            }

            _loginResultTcs = new TaskCompletionSource();
            await _conn.SendLoginShortPasswordAsync(
                ub.AsMemory(),
                pb.AsMemory(),
                0,
                new byte[] { 0x30, 0x32, 0x5F, 0x00, 0x00 }.AsMemory(),
                new byte[16].AsMemory());
            await _loginResultTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _logger.LogInformation("[{Id}] Login successful", _id);
            _state = BotState.LoggedIn;

            // 3. Request character list
            _charListTcs = new TaskCompletionSource();
            await _conn.SendRequestCharacterListAsync(0);
            await _charListTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

            if (string.IsNullOrEmpty(_characterName))
            {
                _logger.LogError("[{Id}] No character found to select", _id);
                return false;
            }

            _logger.LogInformation("[{Id}] Selected character: {Name}", _id, _characterName);
            _state = BotState.CharListReceived;

            // 4. Select the character and wait for CharacterInformation
            _charInfoTcs = new TaskCompletionSource();
            await _conn.SendSelectCharacterAsync(_characterName);
            await _charInfoTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _logger.LogInformation(
                "[{Id}] Entered world at ({X},{Y}) map={Map}",
                _id,
                _posX,
                _posY,
                _mapId);

            // 5. Focus and ready
            await _conn.SendFocusCharacterAsync(_characterName);
            await _conn.SendClientReadyAfterMapChangeAsync();

            _state = BotState.InGame;
            _logger.LogInformation("[{Id}] Bot is now in game", _id);

            // 设置动作分发委托（供 Actions 层调用）
            ActionDelegates = new BotActionDelegates
            {
                SendWalk = (fromX, fromY, steps, dir, newX, newY) =>
                {
                    var directions = new byte[steps];
                    Array.Fill(directions, dir);
                    _ = _conn.SendWalkRequestAsync(fromX, fromY, steps, dir, directions.AsMemory());
                },
                SendAttack = (targetId) =>
                {
                    _ = _conn.SendHitRequestAsync(targetId, 0x78, 0);
                },
                SendUseHealthPotion = () =>
                {
                    // 喝药：背包第 12 格（创建角色时 HP 药水放在 slot 12）
                    _ = _conn.SendConsumeItemRequestAsync(12, 0, 0);
                },
                SendPickup = (itemId) =>
                {
                    // TODO: 发送 C3 05 22 <idHi> <idLo>
                },
            };

            // 坐标文件由 BotClusterManager 聚合写入共享文件，此处不再写单个文件
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Id}] Login failed", _id);
            _state = BotState.Disconnected;
            return false;
        }
    }

    // ─── TickAsync ───

    /// <summary>
    /// Called periodically (every ~200ms) to drive walking and combat.
    /// </summary>
    public async ValueTask TickAsync()
    {
        if (_state != BotState.InGame || _conn == null)
        {
            return;
        }

        _tickCount++;

        // Combat: every ~1 second
        if (_tickCount % CombatIntervalTicks == 0)
        {
            await DoCombatTickAsync().ConfigureAwait(false);
        }

        // Walk: every ~2 seconds
        if (_tickCount % WalkIntervalTicks == 0)
        {
            await DoWalkTickAsync().ConfigureAwait(false);
        }
    }

    // ════════════════════════════════════════════════════════
    //  时间片驱动 AI 决策入口（由 BotClusterManager 调度器调用）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 时间片驱动 AI 决策入口。
    /// 由 BotClusterManager 的调度器线程每决策周期调用一次。
    /// 纯状态判断，微秒级返回，不阻塞。
    /// </summary>
    public void TickAI()
    {
        if (_state != BotState.InGame)
            return;

        // 1. 同步 WorldMirror 标量字段
        Mirror.PosX = _posX;
        Mirror.PosY = _posY;
        Mirror.MapId = _mapId;
        Mirror.CurrentHp = _currentHp;
        Mirror.MaximumHp = _maximumHp;

        // 2. 检查死亡状态
        if (_currentHp <= 0)
        {
            BotCtx.IsDead = true;
            BotCtx.DeathCount++;
            return;
        }
        BotCtx.IsDead = false;

        // 3. 递增全局动作计时器（所有状态共享，防止发包过密）
        BotCtx.TicksSinceLastAction++;

        // 4. 紧急喝药：HP < 50% 且距离上次喝药足够久
        if (Mirror.MaximumHp > 0 &&
            (float)Mirror.CurrentHp / Mirror.MaximumHp < 0.5f &&
            BotCtx.TicksSinceLastHeal > 20 &&
            ActionDelegates != null)
        {
            BotCtx.TicksSinceLastHeal = 0;
            ActionDelegates.SendUseHealthPotion();
        }
        BotCtx.TicksSinceLastHeal++;

        // 5. 执行 AI 决策（FSM 或 Behavior Tree）
        ActionCommand cmd;

        if (_fsmEngine != null)
        {
            cmd = _fsmEngine.Tick(BotCtx, Mirror);
        }
        else if (_btEngine != null)
        {
            cmd = _btEngine.Tick(BotCtx, Mirror);
        }
        else
        {
            cmd = ActionCommand.None;
        }

        // 6. 执行动作
        if (cmd != ActionCommand.None && ActionDelegates != null)
        {
            BotCtx.TotalActions++;
            Actions.BotActions.Execute(cmd, BotCtx, Mirror, ActionDelegates);
        }
    }

    /// <summary>设置 FSM 引擎。</summary>
    public void SetFsmEngine(BotStateMachine fsm)
    {
        _fsmEngine = fsm;
        _btEngine = null;
        _decisionMode = BotDecisionMode.FiniteStateMachine;
        fsm.Start(BotCtx, Mirror);
    }

    /// <summary>设置行为树引擎。</summary>
    public void SetBehaviorTree(BehaviorTreeEngine bt)
    {
        _btEngine = bt;
        _fsmEngine = null;
        _decisionMode = BotDecisionMode.BehaviorTree;
    }

    /// <summary>获取当前 AI 决策模式。</summary>
    public BotDecisionMode DecisionMode => _decisionMode;

    // ─── WritePositionFile ───

    /// <summary>
    /// Writes the current position to a binary file.
    /// Format: 1 byte count + 1 byte X + 1 byte Y + 10 byte name.
    /// </summary>
    /// <param name="path">Output file path.</param>
    public void WritePositionFile(string path)
    {
        try
        {
            var tmp = path + ".tmp";
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);
            bw.Write((byte)1); // count
            bw.Write(_posX);   // X
            bw.Write(_posY);   // Y
            var nameBytes = new byte[10];
            if (_characterName != null)
            {
                Encoding.UTF8.GetBytes(_characterName, 0, Math.Min(_characterName.Length, 10), nameBytes, 0);
            }

            bw.Write(nameBytes);
            bw.Flush();
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Id}] Failed to write position file", _id);
        }
    }

    // ─── Dispose ───

    /// <inheritdoc />
    public void Dispose()
    {
        _conn?.Dispose();
        _sock?.Close();
        _state = BotState.Disconnected;
    }

    // ════════════════════════════════════════════════════════
    //  Packet handler — central routing (single subscription)
    // ════════════════════════════════════════════════════════

    private ValueTask OnPacketReceived(ReadOnlySequence<byte> seq)
    {
        // Convert ReadOnlySequence to byte[] using seq.CopyTo(arr) — same pattern as Program.cs
        var p = new byte[seq.Length];
        seq.CopyTo(p);

        if (p.Length < 3)
        {
            System.Diagnostics.Debug.WriteLine($"[{_id}] OnPacketReceived called with {p.Length} bytes");
            return ValueTask.CompletedTask;
        }

        System.Diagnostics.Debug.WriteLine($"[{_id}] OnPacketReceived type={p[0]:X2} code={p[2]:X2} sc={(p.Length>3?p[3]:0):X2}");

        var type = p[0];
        byte code;
        byte subCode;

        switch (type)
        {
            case 0xC1:
            case 0xC3:
                code = p[2];
                subCode = p.Length > 3 ? p[3] : (byte)0;
                break;
            case 0xC2:
                code = p[3];
                subCode = p.Length > 4 ? p[4] : (byte)0;
                break;
            default:
                return ValueTask.CompletedTask;
        }

        // ── Login / connection handshake ──

        if (code == 0xF1 && subCode == 0x00)
        {
            _gseTcs?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        if (code == 0xF1 && subCode == 0x01 && p.Length >= 5)
        {
            var resultOffset = type == 0xC2 ? 5 : 4;
            _logger.LogInformation("[{Id}] Login result: {Result}", _id, p[resultOffset]);
            _loginResultTcs?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        // ── Character List (0xF3, 0x00) ──

        if (code == 0xF3 && subCode == 0x00)
        {
            HandleCharacterList(p);
            _charListTcs?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        // ── Character Information (0xF3, 0x03) ──

        if (code == 0xF3 && subCode == 0x03)
        {
            HandleCharacterInformation(p);
            _charInfoTcs?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        // ── World mirror updates (only after entering the game world) ──

        if (_state < BotState.InGame)
        {
            return ValueTask.CompletedTask;
        }

        switch (code)
        {
            case 0x12 when type == 0xC2:
                HandleAddCharactersToScope(p);
                break;
            case 0x13 when type == 0xC2:
                HandleAddNpcsToScope(p);
                break;
            case 0x14:
                HandleMapObjectOutOfScope(p);
                break;
            case 0x26:
                HandleCurrentHealthAndShield(p);
                break;
            case 0xD4:
                HandleObjectWalked(p);
                break;
        }

        return ValueTask.CompletedTask;
    }

    // ─── Character list (0xF3, 0x00) ───

    private void HandleCharacterList(byte[] p)
    {
        if (p.Length < 8)
        {
            return;
        }

        var charCount = p[6];
        if (charCount == 0)
        {
            _logger.LogWarning("[{Id}] Character list is empty", _id);
            return;
        }

        // CharacterData entries start at offset 8.
        // Each entry: Byte 0 = SlotIndex, Bytes 1..10 = Name (UTF8, 10 bytes)
        // Total per entry: 11 bytes
        // Select the character whose name starts with "Bot_", otherwise take first
        int selectedIndex = 0;
        for (int i = 0; i < charCount && i < 20; i++)
        {
            var offset = 8 + i * 11;
            if (offset + 11 > p.Length) break;
            var name = Encoding.UTF8.GetString(p.AsSpan(offset + 1, 10)).TrimEnd('\0');
            if (name.StartsWith("Bot_"))
            {
                selectedIndex = i;
                break;
            }
        }

        var nameBytes = p.AsSpan(8 + selectedIndex * 11 + 1, 10);
        _characterName = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
        BotCtx.CharacterName = _characterName;
        _logger.LogInformation("[{Id}] Character list: {Count} chars, selected '{Name}' (idx={Idx})",
            _id, charCount, _characterName, selectedIndex);
    }

    // ─── Character information (0xF3, 0x03) ───

    private void HandleCharacterInformation(byte[] p)
    {
        if (p.Length < 72)
        {
            _logger.LogWarning("[{Id}] CharacterInformation packet too short: {Len}", _id, p.Length);
            return;
        }

        _posX = p[4];
        _posY = p[5];
        _mapId = (ushort)(p[6] | (p[7] << 8));
        _currentHp = (ushort)((p[36] << 8) | p[37]);
        _maximumHp = (ushort)((p[38] << 8) | p[39]);

        Mirror.PosX = _posX;
        Mirror.PosY = _posY;
        Mirror.MapId = _mapId;
        Mirror.CurrentHp = _currentHp;
        Mirror.MaximumHp = _maximumHp;
    }

    // ─── AddCharactersToScope (0xC2, 0x12) ───

    private void HandleAddCharactersToScope(byte[] p)
    {
        if (p.Length < 5)
        {
            return;
        }

        var count = p[4];
        var idx = 5;

        for (int i = 0; i < count && idx + 36 <= p.Length; i++)
        {
            // CharacterData: Id(2) + X(1) + Y(1) + Appearance(18) + Name(10) + ... + EffectCount(1) = 36 base
            var id = (ushort)((p[idx] << 8) | p[idx + 1]);
            var x = p[idx + 2];
            var y = p[idx + 3];

            var nameBytes = p.AsSpan(idx + 22, 10);
            var name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

            if (!string.IsNullOrEmpty(name) && name == _characterName)
            {
                _playerId = id;
                Mirror.PlayerId = id;
                _posX = x;
                _posY = y;
                _logger.LogDebug("[{Id}] Self in scope: id={Id} pos=({X},{Y})", _id, id, x, y);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                // Other players tracked as monster-like objects
                _monsters[id] = new MonsterInfo { Id = id, PosX = x, PosY = y, Name = name };
            }

            // Advance past this entry: 36 bytes + effect count at offset 35
            var effectCount = idx + 35 < p.Length ? p[idx + 35] : 0;
            idx += 36 + effectCount;

            if (idx > p.Length)
            {
                break;
            }
        }
    }

    // ─── AddNpcsToScope (0xC2, 0x13) ───

    private void HandleAddNpcsToScope(byte[] p)
    {
        if (p.Length < 5)
        {
            return;
        }

        var count = p[4];
        const int entrySize = 10;

        for (int i = 0; i < count; i++)
        {
            var offset = 5 + (i * entrySize);
            if (offset + entrySize > p.Length)
            {
                break;
            }

            var id = (ushort)((p[offset] << 8) | p[offset + 1]);
            var typeNumber = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
            var x = p[offset + 4];
            var y = p[offset + 5];

            _monsters[id] = new MonsterInfo { Id = id, PosX = x, PosY = y, Name = $"NPC_{typeNumber}" };
            Mirror.Monsters[id] = new MonsterEntry { Id = id, PosX = x, PosY = y, TypeNumber = typeNumber };
        }
    }

    // ─── MapObjectOutOfScope (0xC1, 0x14) ───

    private void HandleMapObjectOutOfScope(byte[] p)
    {
        if (p.Length < 4)
        {
            return;
        }

        var count = p[3];

        for (int i = 0; i < count; i++)
        {
            var offset = 4 + (i * 2);
            if (offset + 2 > p.Length)
            {
                break;
            }

            var id = (ushort)((p[offset] << 8) | p[offset + 1]);
            _monsters.TryRemove(id, out _);

            if (id == _playerId)
            {
                _playerId = null;
            }

            Mirror.Monsters.TryRemove(id, out _);
            Mirror.Players.TryRemove(id, out _);
        }
    }

    // ─── CurrentHealthAndShield (0xC1, 0x26) ───

    private void HandleCurrentHealthAndShield(byte[] p)
    {
        // 9-byte format: bytes[4..5] = Health (ushort BE)
        if (p.Length >= 9)
        {
            _currentHp = (ushort)((p[4] << 8) | p[5]);
            Mirror.CurrentHp = _currentHp;
        }
    }

    // ─── ObjectWalked (0xC1, 0xD4) ───

    private void HandleObjectWalked(byte[] p)
    {
        if (p.Length < 8)
        {
            return;
        }

        var objectId = (ushort)((p[3] << 8) | p[4]);
        var targetX = p[5];
        var targetY = p[6];

        if (objectId == _playerId)
        {
            _posX = targetX;
            _posY = targetY;
            Mirror.PosX = _posX;
            Mirror.PosY = _posY;
            _logger.LogDebug("[{Id}] Position confirmed: ({X},{Y})", _id, _posX, _posY);
        }
        else if (_monsters.TryGetValue(objectId, out var monster))
        {
            monster.PosX = targetX;
            monster.PosY = targetY;
            if (Mirror.Monsters.TryGetValue(objectId, out var me))
            {
                me.PosX = targetX;
                me.PosY = targetY;
            }
        }
    }

    // ════════════════════════════════════════════════════════
    //  Combat
    // ════════════════════════════════════════════════════════

    private async ValueTask DoCombatTickAsync()
    {
        if (_conn == null)
        {
            return;
        }

        var snapshot = _monsters.Values.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        // Nearest by Chebyshev distance
        MonsterInfo? nearest = null;
        var nearestDist = int.MaxValue;

        foreach (var m in snapshot)
        {
            var dist = Math.Max(Math.Abs(m.PosX - _posX), Math.Abs(m.PosY - _posY));
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = m;
            }
        }

        if (nearest == null)
        {
            return;
        }

        if (nearestDist <= 3)
        {
            await _conn.SendHitRequestAsync(nearest.Id, 0x78, 0).ConfigureAwait(false);
            _logger.LogInformation("[{Id}] Attack monster id={Id} dist={Dist}", _id, nearest.Id, nearestDist);
        }
        else
        {
            // Walk toward it
            WalkToward(nearest.PosX, nearest.PosY);
        }
    }

    // ════════════════════════════════════════════════════════
    //  Walking
    // ════════════════════════════════════════════════════════

    private async ValueTask DoWalkTickAsync()
    {
        if (_conn == null)
        {
            return;
        }

        var dir = _rng.Next(1, 9);   // 1..8
        var steps = _rng.Next(1, 4); // 1..3

        var offset = DirectionOffsets[dir];
        var newX = (int)_posX + (offset.DX * steps);
        var newY = (int)_posY + (offset.DY * steps);

        if (newX < 0 || newX > 255 || newY < 0 || newY > 255)
        {
            return;
        }

        var directions = new byte[steps];
        Array.Fill(directions, (byte)dir);

        await _conn.SendWalkRequestAsync(_posX, _posY, (byte)steps, (byte)dir, directions.AsMemory()).ConfigureAwait(false);

        // Optimistic position update; corrected by ObjectWalked (0xD4)
        _posX = (byte)newX;
        _posY = (byte)newY;
    }

    private void WalkToward(byte targetX, byte targetY)
    {
        var dir = GetDirectionTo(_posX, _posY, targetX, targetY);
        if (dir == 0)
        {
            return;
        }

        var steps = 1;
        var offset = DirectionOffsets[dir];
        var newX = (int)_posX + (offset.DX * steps);
        var newY = (int)_posY + (offset.DY * steps);

        if (newX < 0 || newX > 255 || newY < 0 || newY > 255)
        {
            return;
        }

        // Fire-and-forget to avoid blocking the combat tick
        _ = SendWalkAsync((byte)dir, steps, (byte)newX, (byte)newY);
    }

    private async Task SendWalkAsync(byte dir, int steps, byte newX, byte newY)
    {
        if (_conn == null)
        {
            return;
        }

        try
        {
            await _conn.SendWalkRequestAsync(_posX, _posY, (byte)steps, dir, new[] { dir }.AsMemory()).ConfigureAwait(false);
            _posX = newX;
            _posY = newY;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Id}] Walk send failed", _id);
        }
    }

    /// <summary>
    /// Returns the MU direction (1..8) from one tile to another.
    /// 0 means no movement.
    /// </summary>
    private static byte GetDirectionTo(byte fromX, byte fromY, byte toX, byte toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;

        if (dx == 0 && dy == 0)
        {
            return 0;
        }

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            if (dx > 0)
            {
                return dy >= 0 ? (byte)5 : (byte)3;
            }
            else
            {
                return dy >= 0 ? (byte)7 : (byte)1;
            }
        }
        else
        {
            if (dy > 0)
            {
                return dx >= 0 ? (byte)6 : (byte)7;
            }
            else
            {
                return dx >= 0 ? (byte)4 : (byte)2;
            }
        }
    }
}

/// <summary>
/// Minimal ILogger that discards all messages, for the Connection constructor.
/// </summary>
internal sealed class LogCon<T> : ILogger<T>
{
    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;
}
