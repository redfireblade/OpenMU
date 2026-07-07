# 玩家状态机与状态转换

## 状态转换图

```
Initial → LoginScreen → Authenticated → CharacterSelection → EnteredWorld
                                          ↓                     ↓
                                     FocusCharacter         Dead → EnteredWorld (重生)
                                      (保持状态)           ChangingMap → EnteredWorld (地图加载)
                                                          TradeRequested → TradeOpened → TradeButtonPressed
                                                          NpcDialogOpened
                                                          PartyRequest
                                       所有状态 → Disconnected → Finished
```

## 状态定义

文件: `src/GameLogic/PlayerState.cs`

```csharp
static PlayerState()
{
    // 静态构造函数建立所有合法转换路径
    Initial.PossibleTransitions = { LoginScreen };
    LoginScreen.PossibleTransitions = { Authenticated, Disconnected };
    // ...
    EnteredWorld.PossibleTransitions = { Dead, CharacterSelection, TradeRequested, 
                                          NpcDialogOpened, PartyRequest, ChangingMap, Disconnected };
}
```

## 进入游戏完整流程

```
Login封包 → LogInHandlerPlugIn → LoginAction.LoginAsync
  → TryEstablishSessionAsync → TryBeginAdvanceTo(Authenticated)
  → FinishLoginAsync → 状态: Authenticated

0xF3 0x00封包 → RequestCharacterListAction → 状态: CharacterSelection

0xF3 0x03封包 → SelectCharacterAction → SetSelectedCharacterAsync
  → OnPlayerEnteredWorldAsync: 初始化Inventory/Skills/MagicEffects
  → ClientReadyAfterMapChangeAsync: GetMapAsync → 状态: EnteredWorld
  → map.AddAsync(this): AOI注册 → 其他玩家看到你

0xF3 0x12封包 → ClientReadyAfterMapChangeAsync (地图加载确认)
```

## 关键文件

| 文件 | 行号 |
|------|------|
| `src/GameLogic/PlayerState.cs` | 全文件 |
| `src/GameLogic/Player.cs` 状态机字段 | 272 |
| `src/GameLogic/Player.cs` EnteredWorld | 2487 |
| `src/GameLogic/Player.cs` 死亡 | 2279 |
| `src/GameLogic/Player.cs` 重生 | 1101 |
| `src/GameLogic/Player.cs` 地图切换 | 1145 |
| `src/GameLogic/PlayerActions/LoginAction.cs` | 全文件 |
| `src/GameLogic/PlayerActions/Character/SelectCharacterAction.cs` | 全文件 |
