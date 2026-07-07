# 玩家行为处理完整工作流总览

## 五阶段工作流

```
[客户端] → [1. 网络接收] → [2. 封包路由] → [3. 状态机门控] → [4. 游戏逻辑执行] → [5. 结果广播] → [其他客户端]
```

## 完整链路流程图

详见 WIKI 系列文档：

| Phase | 文档 | 内容 |
|-------|------|------|
| 1 | 32-packet-communication.md | TCP→解密→组包→PacketReceived→路由 |
| 2 | 33-player-state-machine.md | 状态定义→转换→门控 |
| 3 | 34-movement-system.md | WalkRequest→WalkHandler→WalkToAsync→Walker |
| 4 | 35-combat-system.md | HitHandler→HitAction→CalculateDamageAsync→广播 |
| 5 | 36-aoi-broadcast.md | 桶矩阵→ObserverAdapter→ViewPlugIn→客户端 |

## 关键盲区（S20→S21）

S20 遗留盲区已通过源码分析填补：
- ✅ 封包加密/解密管道链
- ✅ Player 状态机完整转换图
- ✅ WalkToAsync 6 道门禁
- ✅ CalculateDamageAsync 12 步公式链
- ✅ AOI 32×32 桶矩阵
- ❌ IViewPlugIn 具体实现（40+文件未逐项审查）
- ❌ PlayerActions 具体动作（30+文件未逐项审查）
