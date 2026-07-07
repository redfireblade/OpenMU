# 移动系统（行走完整链路）

## 处理链

```
客户端发 WalkRequest(C1 0xD4) 
→ CharacterWalkHandlerPlugIn(Key=0xD4)
  → CharacterWalkBaseHandlerPlugIn.WalkAsync
    → DecodePayload: 解析步进方向和步数
    → GetSteps: 计算 WalkingStep[] (From→To)
    → player.WalkToAsync(target, steps)
      → 6 道门禁检查
      → _walker.InitializeWalkToAsync(target, steps)
      → map.MoveAsync → AOI广播 (MoveType.Walk)
      → _walker.StartWalkAsync → Task.Run(WalkLoopAsync)
```

## WalkToAsync 6 道门禁

```
门禁1: currentMap == null → return
门禁2: Attributes == null → return
门禁3: 被冰冻/眩晕/睡眠 → return
门禁4: steps.IsEmpty → return
门禁5: 起点偏移距离 > 3 → 发 IObjectMovedPlugIn(Instant) 同步客户端
门禁6: !WalkMap[target.X, target.Y] → 发 IObjectMovedPlugIn(Instant) 同步客户端
```

## Walker 步进循环 (异步后台)

```
WalkLoopAsync(CancellationToken):
  while (not cancelled):
    WalkStepAsync:
      读锁检查: ShouldWalkerStop? (队列空/不活跃)
      写锁: WalkNextStepIfStepAvailable → Position = nextStep.To
    nextDelay = StepDelay - 50ms - lastOffset
    await Task.Delay(nextDelay)
```

## IsWalking 生命周期

- **true**: `InitializeWalkToAsync` 设置 `CurrentTarget`
- **false**: `StopAsync` (队列空/死亡/传送/断线)

## WalkMap / AIgrid / SafezoneMap 对比

| 属性 | WalkMap | SafezoneMap | AIgrid |
|------|---------|-------------|--------|
| 类型 | bool[,] | bool[,] | byte[,] |
| 判断逻辑 | `value!=0xFF && value!=5 && value<10` | `value==1` | `(WalkMap?1:0) \| (SafezoneMap?0x80:0)` |
| 用途 | 玩家/怪物能否站在这格 | 安全区判定 | 寻路算法输入 |
| 更新方式 | 一次性加载，只读 | 加载+硬编码 | 随WalkMap/SafezoneMap更新 |

## 地形文件格式

```
.att 文件: [3字节头] + [65536字节服务端地形] + [65536字节客户端纹理]
```

## 关键文件

| 文件 | 行号 |
|------|------|
| `src/GameServer/MessageHandler/CharacterWalkBaseHandlerPlugIn.cs` | 全文件 |
| `src/GameLogic/Player.cs WalkToAsync` | 1351 |
| `src/GameLogic/Walker.cs` | 全文件 |
| `src/GameLogic/GameMapTerrain.cs` | 全文件 |
| `src/GameLogic/BucketAreaOfInterestManager.cs` | 93-99 |
