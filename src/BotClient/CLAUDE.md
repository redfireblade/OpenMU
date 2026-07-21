# BotClient 开发规范

**本文件是每次会话第一件事——了解 BotClient 项目状态、架构和设计文档索引。**

---

## 一、项目定位

**BotClient** = C# 端轻量脱机（Headless）AI 陪玩客户端，零渲染，纯 C# 网络封包库。

**核心系统：C++ HeadlessBot**（独立完整系统，见 `mu103/HeadlessBot/`，~9500 行）。

C# BotClient 与 C++ HeadlessBot 是**双轨并行**的两个实现：
- **C++ 轨道 (主)**：`mu103/HeadlessBot/` — 完整系统，含 Connection 加密、AiMuHelper（战斗/喝药/拾取/寻路）、AiDecisionSystem、Swarm 群控，**当前 S26 调试主力**
- **C# 轨道 (辅)**：`BotClient/` — 轻量 C# 实现，使用 OpenMU Network 封包库，备用/参考

---

## 二、系统架构（S26）

```
┌─ 轨道 A: C++ HeadlessBot (主) ──────────────────────────────────────────────┐
│  BotCluster (C++)                                                            │
│   └─ HeadlessBot[N]                                                         │
│        ├─ Connection (C++) → SimpleModulus + Xor32 → TCP                     │
│        ├─ AiMuHelper → 战斗/喝药/拾取/寻路                                    │
│        ├─ AiDecisionSystem → 规则引擎                                         │
│        └─ 13 种封包 Handler → 世界镜像同步                                     │
│   ~9500 行, 58 个 C++ 源文件                                                  │
└───────────────────────────────────────────────────────────────────────────────┘

┌─ 轨道 B: C# BotClient (辅) ─────────────────────────────────────────────────┐
│  BotClusterManager (C#)                                                      │
│   └─ MuHeadlessBot[N]                                                       │
│        ├─ Connection → OpenMU Network (C# 封包库)                            │
│        ├─ FSM 6状态 / BehaviorTree 行为树                                    │
│        └─ 小地图黄点坐标文件输出                                               │
│   ~1000 行 C#, 16 个源文件                                                    │
└───────────────────────────────────────────────────────────────────────────────┘
```

---

## 三、当前状态（S26 — 2026-07-11）

| 模块 | C++ HeadlessBot | C# BotClient |
|------|----------------|--------------|
| TCP 连接 | ✅ 完成 | ✅ 完成 |
| SimpleModulus 加密 | ❌ **解密失败(S26目标)** | ✅ 完成 |
| Xor32 加密 | ✅ 完成 | ✅ 完成 |
| 登录流程 | ✅ 完成 | ✅ 完成 |
| 世界镜像 | ✅ 13种封包 | ✅ 基础 |
| 行走 | ✅ A* + 贪心 | ✅ 方向行走 |
| 战斗 | ✅ AiMuHelper | ✅ Hit攻击 |
| 喝药 | ✅ AiMuHelper | ✅ 硬编码slot 12 |
| 拾取 | ✅ AiMuHelper + 地面物品跟踪 | ❌ 未实现 |
| 死亡检测 | ✅ | ✅ |
| 小地图 | ➖ GUI自带 | ✅ 文件写入 |
| 技能系统 | ✅ 技能列表解析 | ❌ |
| 寻路 | ✅ A* + 地形加载 | ❌ 无寻路 |
| 群控调度 | ✅ BotCluster | ✅ BotClusterManager |

---

## 四、C++ HeadlessBot 文件树

```
mu103/HeadlessBot/
├── Main.cpp                  — Win32 GUI 入口（ImGui + OpenGL）
├── HeadlessBot.cpp/h         — 单个 BOT（登录+封包处理+AI决策）
├── BotCluster.cpp/h          — 时间片调度器
├── HeadlessBotPath.cpp/h     — A* 寻径 + 地形(MAP)
├── stdafx.h                  — 预编译头
├── CMakeLists.txt            — CMake 构建
│
├── Net/                      — 网络层
│   ├── Connection.cpp/h      — TCP 连接 + SimpleModulus/Xor32 管线
│   ├── SimpleModulus.h       — SimpleModulus 加解密
│   ├── Xor32.h               — Xor32 加解密
│   └── Socket.cpp/h          — 原始 socket 封装
│
├── Client/                   — AI 客户端库（AiMuHelper 生态）
│   ├── AiMuHelper.cpp/h      — CMuHelper 克隆（核心 AI 决策）
│   ├── AiHelper.cpp/h        — AI 辅助逻辑
│   ├── AiInterface.cpp/h     — AI 接口层（寻路/目标/物品）
│   ├── AiCharacterUtil.cpp/h — 角色工具函数
│   ├── AiClassAttack.cpp/h   — 职业攻击逻辑
│   ├── AiCombatTarget.cpp/h  — 战斗目标选择
│   ├── AiInventory.cpp/h     — 背包管理
│   ├── AiSkillCast.cpp/h     — 技能施放
│   ├── AiSkillExecution.cpp/h — 技能执行
│   ├── AiSkillManager.cpp/h  — 技能管理
│   ├── AiBuff.cpp/h          — Buff 管理
│   └── (共 22 个源文件)
│
├── Decision/                 — 决策层
│   ├── AiDecisionSystem.cpp/h— 决策系统
│   ├── AiRuleEngine.cpp/h    — 规则引擎
│   ├── AiKanbanBoard.cpp/h   — 看板信息
│   └── AiMissionBoard.cpp/h  — 任务板
│
├── Learning/                 — 机器学习层
│   ├── AiBehaviorObserver.cpp/h — 行为观察器
│   ├── AiBehaviorEventStore.cpp/h — 行为事件存储
│   ├── AiBehaviorSegmenter.cpp/h — 行为切分
│   ├── AiBehaviorAnalyzer.cpp/h  — 行为分析
│   ├── AiRuleGenerator.cpp/h — 规则生成
│   ├── AiScriptGenerator.cpp/h — 脚本生成
│   └── AiKnowledgeBridge.cpp/h  — 知识桥接
│
├── Swarm/                    — 群控层
│   ├── AiSwarmOrchestrator.cpp/h — 群控编排器
│
├── Proto/                    — 协议定义
│   ├── AiDefines.h           — 常量定义
│   ├── AiStructures.h        — 数据结构
│   ├── AiCharacter.h         — 角色结构
│   └── AiObject.h            — 对象结构
│
└── ThirdParty/               — 第三方库
```

---

## 五、关键文件路径

### C# 端 (BotClient/)

| 文件 | 内容 |
|------|------|
| `Program.cs` | 入口 |
| `BotClusterManager.cs` | 时间片调度器 |
| `MuHeadlessBot.cs` | BOT 包装器（C# 直连实现） |
| `Core/BotEnums.cs` | ActionCommand / FsmStateType |
| `Core/BotContext.cs` | 决策黑板 |
| `Core/WorldMirror.cs` | 世界镜像 |
| `Core/AIConfig.cs` | 配置参数 |
| `FSM/BotStateMachine.cs` | FSM 引擎 |
| `FSM/PredefinedStates.cs` | 6 个状态 |
| `BehaviorTree/BTNodes.cs` | 行为树节点 |
| `Actions/BotActions.cs` | 动作分发 |

### C++ 端 (mu103/)

| 文件 | 内容 |
|------|------|
| `HeadlessBot/Net/SimpleModulus.h` | **← S26 调试核心文件** |
| `HeadlessBot/Net/Connection.h/.cpp` | 加密管线 |
| `HeadlessBot/HeadlessBot.cpp` | 完整 BOT 实现 |
| `HeadlessBot/Client/AiMuHelper.h/.cpp` | AI 决策核心 |

---

## 六、启动命令

```bash
# 1. 启动服务器
cd /d E:\mu_ai\MU_VER_1\SERVERS\OPENMU
dotnet run --project src/Startup/MUnique.OpenMU.Startup.csproj -p:ci=true -- --autostart -demo -resolveIP:local

# 2. 编译 + 运行 C++ HeadlessBot
cd /d E:\mu_ai\MU_VER_1\mu103\HeadlessBot\build
cmd.exe /c "clean_build.bat"
set BOT_COUNT=1
cmd.exe /c "HeadlessBot.exe"

# 3. (备用) 运行 C# BotClient
cd /d E:\mu_ai\MU_VER_1\SERVERS\OPENMU
BOT_COUNT=10 dotnet run --project src/BotClient/MUnique.OpenMU.BotClient.csproj
```

---

## 七、S26 调试流程

```
1. 启动服务器 → 确认 55901 监听
2. 运行 HeadlessBot (BOT_COUNT=1)
3. 从控制台输出观察 [Bot X] << RAW: 行——服务器发送的原始加密数据
4. 复制加密数据到测试程序，用 C# 解密对比
5. 修正 SimpleModulus::DecryptContent / DecodeBlockInput
6. 确认首包 GameServerEntered (0xF1 0x00) 解密成功
```

### C# 参考实现文件

| 文件 | 作用 |
|------|------|
| `src/Network/SimpleModulus/PipelinedSimpleModulusDecryptor.cs` | C# 解密参考 |
| `src/Network/SimpleModulus/PipelinedSimpleModulusEncryptor.cs` | C# 加密参考 |
| `src/Network/SimpleModulus/PipelinedSimpleModulusBase.cs` | C# 基类（位操作） |
| `src/Network/SimpleModulus/SimpleModulusKeys.cs` | C# 密钥结构 |

---

## 八、历史阶段

| 阶段 | 日期 | 内容 |
|------|------|------|
| S20 | 2026-07-06 | AiEntity(extends Player) → 废弃 |
| S21 | 2026-07-06 | AiEntity(extends Npc) → 废弃 |
| S22 | 2026-07-08 | C# BotClient + BotBridge 方案 → 废弃（BotBridge 未实现） |
| S23 | 2026-07-08 | C# MuHeadlessBot 直连登录完成 |
| S24 | 2026-07-09 | C# 战斗/行走闭环 |
| S25 | 2026-07-10 | C++ HeadlessBot 独立系统完成 (~9500行) |
| **S26** | **2026-07-11** | **← SimpleModulus 加密调试 (当前)** |
