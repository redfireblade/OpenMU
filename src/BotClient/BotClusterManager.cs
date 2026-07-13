using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.BotClient.Actions;
using MUnique.OpenMU.BotClient.BehaviorTree;
using MUnique.OpenMU.BotClient.Core;
using MUnique.OpenMU.BotClient.FSM;

namespace MUnique.OpenMU.BotClient;

/// <summary>
/// ★ 时间片轮转调度器 — 单线程管理 N 个 BOT 的 AI 决策引擎。
///
/// 设计原理：
/// ───────────────────────────────────────────────────────────
/// 100 个 BOT 不需要每帧都决策。将决策周期设为 200ms，
/// 每 10ms 只唤醒 BatchSize 个 BOT 做决策，CPU 峰值被完美抹平。
///
/// 时间轴 (10ms 为一个 tick)：
///   T+0ms:   bots[ 0.. 4] 执行 AI 决策
///   T+10ms:  bots[ 5.. 9] 执行 AI 决策
///   T+20ms:  bots[10..14] 执行 AI 决策
///   ...
///   T+190ms: bots[95..99] 执行 AI 决策
///   T+200ms: bots[ 0.. 4] 重新开始（循环）
///
/// 每个 BOT 的决策周期 = 200ms（100 bots / 5 × 10ms）
/// 每个 tick 处理的 BOT 数 = 总 BOT 数 × 10ms / 200ms
///
/// 线程模型：
///   ┌──────────────────────────────────────────────┐
///   │  调度器线程 (1个)                              │
///   │  └─ 时间片轮转 → Bot[i].TickAI() → 非阻塞返回  │
///   ├──────────────────────────────────────────────┤
///   │  IOCP 网络线程池 (由 Connection 内部管理)       │
///   │  └─ 异步收包 → 更新 WorldMirror               │
///   └──────────────────────────────────────────────┘
/// </summary>
public sealed class BotClusterManager : IDisposable
{
    // ─── P/Invoke: 高精度定时器 ───
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint NativeTimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint NativeTimeEndPeriod(uint uPeriod);

    // ─── 字段 ───
    private readonly MuHeadlessBot[] _bots;
    private readonly AIConfig _config;
    private readonly ILogger _logger;
    private readonly Thread _schedulerThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly BotDecisionMode _decisionMode;

    private int _currentBatchStart;
    private long _tickCount;
    private readonly string _sharedPosFilePath;

    // ─── 统计 ───
    private int _peakOnline;
    private int _totalDeaths;
    private long _totalTicks;

    // ─── 构造 ───

    /// <summary>初始化 BotClusterManager。</summary>
    public BotClusterManager(AIConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _logger = loggerFactory.CreateLogger("Cluster");
        _decisionMode = config.DecisionMode;

        _bots = new MuHeadlessBot[config.BotCount];
        _schedulerThread = new Thread(RunSchedulerLoop)
        {
            Name = "BotScheduler",
            IsBackground = true,
        };
        _sharedPosFilePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            @"..\..\runtime\oaps_ai_pos.dat"));
        Directory.CreateDirectory(Path.GetDirectoryName(_sharedPosFilePath)!);
    }

    // ─── 属性 ───

    /// <summary>获取所有 BOT 实例（只读视图）。</summary>
    public IReadOnlyList<MuHeadlessBot> Bots => _bots;

    /// <summary>获取配置。</summary>
    public AIConfig Config => _config;

    /// <summary>通过服务器 API 创建 BOT 账号。</summary>
    private async Task CreateAccountsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (int i = 0; i < _config.BotCount; i++)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    loginName = $"bot{i}",
                    password = $"bot{i}",
                    characterName = $"Bot_{i}",
                    characterClass = 0,    // 全黑暗巫师=洛伦西亚
                });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await http.PostAsync("http://localhost/api/account/create", content).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        _logger.LogInformation("[Cluster] 账号创建完成");
    }

    // ─── 初始化 ───

    /// <summary>创建所有 BOT 实例并执行登录，完成后启动调度器。</summary>
    public async Task InitializeAsync()
    {
        _logger.LogInformation(
            "[Cluster] 初始化 {Count} 个 BOT, 模式={Mode}, 每 tick 处理 {Bpt} 个",
            _config.BotCount, _decisionMode, _config.BotsPerTick);

        // 1. 通过 API 创建游戏账号
        await CreateAccountsAsync();

        // 2. 创建 BOT 实例（并行创建网络对象）
        var createTasks = new Task[_config.BotCount];
        for (int i = 0; i < _config.BotCount; i++)
        {
            var idx = i; // 捕获
            createTasks[i] = Task.Run(() => CreateBot(idx));
        }
        await Task.WhenAll(createTasks);

        _logger.LogInformation("[Cluster] 全部 BOT 实例创建完成，开始分批登录...");

        // 2. 分批登录（每批 10 个，间隔 500ms 防服务器过载）
        const int batchSize = 10;
        var totalLoggedIn = 0;

        for (int start = 0; start < _config.BotCount; start += batchSize)
        {
            var end = Math.Min(start + batchSize, _config.BotCount);
            var batch = new List<Task>(end - start);

            for (int i = start; i < end; i++)
            {
                batch.Add(_bots[i].LoginAsync());
            }

            await Task.WhenAll(batch);
            totalLoggedIn += batch.Count;

            var inGame = _bots.Count(b => b.State == BotState.InGame);
            _peakOnline = Math.Max(_peakOnline, inGame);
            _logger.LogInformation(
                "[Cluster] 登录进度: {Total}/{Count} 完成, {InGame} 进入游戏",
                totalLoggedIn, _config.BotCount, inGame);

            if (end < _config.BotCount)
            {
                await Task.Delay(500);
            }
        }

        _logger.LogInformation(
            "[Cluster] 初始化完成! {InGame}/{Count} 进入游戏",
            _bots.Count(b => b.State == BotState.InGame), _config.BotCount);

        // 3. 设置 FSM / BT（每个 BOT 独立的引擎）
        SetupDecisionEngines();

        // 4. 立即写入第一次位置文件
        WriteSharedPositionsFile();

        // 5. 启动调度器线程
        _schedulerThread.Start();
    }

    /// <summary>获取集群统计信息摘要。</summary>
    public string GetStats()
    {
        var inGame = _bots.Count(b => b.State == BotState.InGame);
        var online = _bots.Count(b => b.State >= BotState.Connected);
        var dead = _bots.Count(b => b.State == BotState.Dead);
        var disconnected = _bots.Count(b => b.State == BotState.Disconnected);

        // 平均 HP
        var avgHp = 0.0;
        var hpCount = 0;
        foreach (var b in _bots)
        {
            if (b.State == BotState.InGame && b.Mirror.MaximumHp > 0)
            {
                avgHp += (double)b.Mirror.CurrentHp / b.Mirror.MaximumHp;
                hpCount++;
            }
        }
        if (hpCount > 0) avgHp /= hpCount;

        return $"[Cluster] 游戏中={inGame} 在线={online} 死亡={dead} 断开={disconnected} | " +
               $"平均HP={avgHp:P1} | 峰值在线={_peakOnline} 总死亡={_totalDeaths} Tick={_totalTicks}";
    }

    // ─── 创建 BOT ───

    private void CreateBot(int index)
    {
        var logger = new BotLogger(index, _logger);
        var posFile = Path.Combine(Path.GetTempPath(), $"oaps_bot_{index}_pos.dat");

        _bots[index] = new MuHeadlessBot(
            id: index,
            host: "127.0.0.1",
            port: 55901,
            account: $"bot{index}",
            password: $"bot{index}",
            posFilePath: posFile,
            logger: logger);
    }

    // ─── 设置决策引擎 ───

    private void SetupDecisionEngines()
    {
        if (_decisionMode == BotDecisionMode.FiniteStateMachine)
        {
            foreach (var bot in _bots)
            {
                if (bot.State == BotState.InGame)
                {
                    bot.SetFsmEngine(new BotStateMachine());
                }
            }
            _logger.LogInformation("[Cluster] FSM 引擎已设置");
        }
        else
        {
            var tree = TreeDefinitions.CreateHuntingTree();
            foreach (var bot in _bots)
            {
                if (bot.State == BotState.InGame)
                {
                    bot.SetBehaviorTree(tree);
                }
            }
            _logger.LogInformation("[Cluster] BehaviorTree 引擎已设置");
        }
    }

    // ════════════════════════════════════════════════════════
    //  调度器核心 — 单线程时间片轮转
    // ════════════════════════════════════════════════════════

    private void RunSchedulerLoop()
    {
        // 请求 1ms 精度（Windows 多媒体定时器）
        if (_config.HighPrecisionTimer)
        {
            NativeTimeBeginPeriod(1);
        }

        var sw = Stopwatch.StartNew();
        var tickInterval = TimeSpan.FromMilliseconds(_config.SchedulerTickMs);
        var nextTick = tickInterval;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // 1. 执行当前时间片的 BOT 批处理
                ProcessTick();

                // 2. 高精度等待到下一个 tick
                var now = sw.Elapsed;
                var delay = nextTick - now;
                if (delay > TimeSpan.Zero)
                {
                    Thread.Sleep(delay);
                }
                nextTick += tickInterval;
            }
        }
        catch (ThreadInterruptedException)
        {
            // 正常关闭
        }
        finally
        {
            if (_config.HighPrecisionTimer)
            {
                NativeTimeEndPeriod(1);
            }
        }
    }

    /// <summary>处理一个时间片的 BOT 批。非阻塞，< 5ms。</summary>
    private void ProcessTick()
    {
        _tickCount++;
        _totalTicks++;
        var count = _config.BotsPerTick;

        for (int i = 0; i < count; i++)
        {
            var idx = (_currentBatchStart + i) % _bots.Length;
            var bot = _bots[idx];

            if (bot == null || bot.State != BotState.InGame)
                continue;

            try
            {
                // ★ 执行一次 AI 决策（纯状态判断，微秒级返回）
                bot.TickAI();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Bot {Id}] TickAI 异常", idx);
            }
        }

        // 时间片指针前进
        _currentBatchStart = (_currentBatchStart + count) % _bots.Length;

        // 每 100 tick (~1 秒) 写入一次位置文件
        if (_tickCount % 100 == 0)
        {
            WriteSharedPositionsFile();
        }

        // 每 500 tick (~5 秒) 采集一次统计
        if (_tickCount % 500 == 0)
        {
            RefreshStats();
        }
    }

    private void RefreshStats()
    {
        var dead = 0;
        foreach (var bot in _bots)
        {
            if (bot.State == BotState.Dead)
            {
                dead++;
                _totalDeaths++;
            }
        }

        var inGame = _bots.Count(b => b.State == BotState.InGame);
        if (inGame > _peakOnline)
            _peakOnline = inGame;

        WriteSharedPositionsFile();
    }

    /// <summary>
    /// 将所有 BOT 坐标写入共享文件，供游戏客户端的小地图读取渲染。
    /// 格式: [BYTE count][BYTE posX][BYTE posY][char[10] name] × count
    /// posX=PositionX(row)→Location[0](地图Y), posY=PositionY(col)→Location[1](地图X)
    /// </summary>
    private void WriteSharedPositionsFile()
    {
        var inGameBots = _bots.Where(b => b.State == BotState.InGame).ToArray();
        if (inGameBots.Length == 0) return;

        try
        {
            var count = (byte)Math.Min(inGameBots.Length, 100);
            var buf = new byte[1 + count * 12];
            buf[0] = count;
            for (int i = 0; i < count; i++)
            {
                var bot = inGameBots[i];
                var off = 1 + i * 12;
                buf[off] = bot.PosY;      // posY 在前 (配合旧客户端黄点代码)
                buf[off + 1] = bot.PosX;  // posX 在后
                var name = bot.BotCtx.CharacterName;
                var rawName = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(name) ? "Bot" : name);
                for (int j = 0; j < 10 && j < rawName.Length; j++)
                    buf[off + 2 + j] = rawName[j];
            }
            WriteFileAtomic(_sharedPosFilePath, buf);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("位置文件写入失败({Count}): {Msg}", inGameBots.Length, ex.Message);
        }
    }

    /// <summary>
    /// 写入位置文件。先写 .new 再改名，避免与客户端读文件冲突。
    /// 如果改名失败（客户端正在读），下次 tick 重试。
    /// </summary>
    private static void WriteFileAtomic(string path, byte[] data)
    {
        var tmpPath = path + ".new";
        File.WriteAllBytes(tmpPath, data);
        try
        {
            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }

    // ─── 生命周期 ───

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();

        foreach (var bot in _bots)
        {
            bot?.Dispose();
        }

        _logger.LogInformation("[Cluster] 已关闭");
    }

    // ─── 辅助日志适配器 ───

    /// <summary>为每个 BOT 创建带 ID 前缀的日志器。</summary>
    private sealed class BotLogger : ILogger
    {
        private readonly int _id;
        private readonly ILogger _parent;

        public BotLogger(int id, ILogger parent)
        {
            _id = id;
            _parent = parent;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // 只转发 Information 及以上级别的日志，避免刷屏
            if (logLevel >= LogLevel.Information)
            {
                _parent.Log(logLevel, eventId, state, exception,
                    (s, e) => $"[Bot {_id}] {formatter(s, e)}");
            }
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
