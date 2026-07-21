using System.Diagnostics;
using MUnique.OpenMU.BotClient.Core;
using MUnique.OpenMU.BotClient;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════
// 快速登录测试（临时）
// ═══════════════════════════════════════════════════════════
await QuickLoginTest.RunAsync();
return;

// ═══════════════════════════════════════════════════════════
// OAPS BotClient v2.0 — 头戴式群控客户端
// ═══════════════════════════════════════════════════════════
Console.OutputEncoding = System.Text.Encoding.UTF8;

// ─── 解析参数 ───

var botCount = 100;
var modeStr = "fsm";

if (args.Length >= 1 && int.TryParse(args[0], out var parsedCount))
    botCount = Math.Clamp(parsedCount, 1, 5000);

botCount = int.TryParse(Environment.GetEnvironmentVariable("BOT_COUNT"), out var envCount)
    ? Math.Clamp(envCount, 1, 5000)
    : botCount;

modeStr = Environment.GetEnvironmentVariable("AI_MODE")?.ToLower() switch
{
    "bt" or "behaviortree" or "behavior_tree" => "bt",
    _ => "fsm",
};

// ─── 构建配置 ───

var config = new AIConfig
{
    BotCount = botCount,
    DecisionMode = modeStr == "bt" ? BotDecisionMode.BehaviorTree : BotDecisionMode.FiniteStateMachine,
    SchedulerTickMs = 10,
    DecisionIntervalMs = 200,
    AttackRange = 3,
    HealThresholdPercent = 30,
    HighPrecisionTimer = true,
};

Console.WriteLine(@$"
╔═══════════════════════════════════════════════════════════╗
║         OAPS BotClient v2.0 — 时间片群控调度器              ║
╠═══════════════════════════════════════════════════════════╣
║  BOT 数量:      {config.BotCount,6}                            ║
║  AI 模式:       {config.DecisionMode,6}                           ║
║  调度器 Tick:   {config.SchedulerTickMs,6}ms                           ║
║  决策周期:      {config.DecisionIntervalMs,6}ms                           ║
║  每 Tick 处理:  {config.BotsPerTick,6} 个 BOT                      ║
╚═══════════════════════════════════════════════════════════╝");

// ─── 启动 ───

// 使用项目自带的 SimpleLoggerFactory（无需额外 NuGet 包）
var loggerFactory = new SimpleLoggerFactory();
var clusterLogger = loggerFactory.CreateLogger("Main");
var cluster = new BotClusterManager(config, loggerFactory);

// 注册 Ctrl+C 优雅退出
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    clusterLogger.LogInformation("收到退出信号，关闭中...");
    cluster.Dispose();
    Environment.Exit(0);
};

// ─── 初始化 + 登录 + 启动调度器 ───

var initSw = Stopwatch.StartNew();
await cluster.InitializeAsync();
initSw.Stop();

clusterLogger.LogInformation("初始化耗时: {Time}s", initSw.Elapsed.TotalSeconds.ToString("F1"));
clusterLogger.LogInformation("{Stats}", cluster.GetStats());

// ─── 主线程：每隔 5 秒打印统计 ───

clusterLogger.LogInformation("调度器已启动，每 5 秒打印统计...");
clusterLogger.LogInformation("按 Ctrl+C 停止");

var statsTimer = Stopwatch.StartNew();
var lastStats = statsTimer.Elapsed;

while (true)
{
    await Task.Delay(1000);

    if (statsTimer.Elapsed - lastStats >= TimeSpan.FromSeconds(5))
    {
        lastStats = statsTimer.Elapsed;
        clusterLogger.LogInformation("{Stats}", cluster.GetStats());
    }
}
