# 封包通信完整链路

## 链路总图

```
客户端 → TCP Socket → SimpleModulus解密 → Xor32解密 → PacketPipeReaderBase 组包 → PacketReceived事件
→ RemotePlayer.PacketReceivedAsync → MainPacketRouter.HandlePacketAsync → IPacketHandlerPlugIn 或 GroupPacketHandler
```

## 加密管道链

### 接收方向
```
[SocketConnection.Input] → [PipelinedSimpleModulusDecryptor] → [PipelinedXor32Decryptor] → [Connection.Source]
```

### 发送方向
```
[Connection.Output: ExtendedPipeWriter] → [PipelinedXor32Encryptor] → [PipelinedSimpleModulusEncryptor] → [Socket]
```

### SimpleModulus 解密规则
- `packet[0] < 0xC3` (C1/C2): 直通，不加密
- `packet[0] >= 0xC3` (C3/C4): DecryptAndWrite（XOR + 模运算 + 反序 ReverseEndianness）
- Season 6+ 第一字节是计数器，防重放攻击

### Xor32 解密规则
- 从尾部向头部反向遍历（跳过 header）
- `target[i] ^= target[i-1] ^ xor32Key[i % 32]`
- 32 字节密钥

## 封包路由

```csharp
var typeIndex = packet.Span[0] % 2 == 1 ? 2 : 3;  // C1/C3→索引2, C2/C4→索引3
var packetType = packet.Span[typeIndex];
var handler = this[packetType];
```

| 类型 | 头大小 | 长度字段 | 加密 | Code位置 | SubCode位置 |
|------|--------|---------|------|---------|------------|
| C1 | 2字节 | 1字节(max255) | Xor32 | packet[2] | packet[3] |
| C2 | 3字节 | 2字节BE(max65535) | Xor32 | packet[3] | packet[4] |
| C3 | 2字节 | 1字节 | Xor32+SimpleModulus | packet[2] | packet[3] |
| C4 | 3字节 | 2字节BE | Xor32+SimpleModulus | packet[3] | packet[4] |

## 关键文件

| 组件 | 文件 | 行号 |
|------|------|------|
| Connection | `src/Network/Connection.cs` | 25-218 |
| SimpleModulus解密 | `src/Network/SimpleModulus/PipelinedSimpleModulusDecryptor.cs` | 84-155 |
| Xor32解密 | `src/Network/Xor/PipelinedXor32Decryptor.cs` | 60-83 |
| 封包组包 | `src/Network/PacketPipeReaderBase.cs` | 98-155 |
| 路由容器 | `src/GameServer/MessageHandler/PacketHandlerPlugInContainer.cs` | 68-74 |
| 组路由 | `src/GameServer/MessageHandler/GroupPacketHandlerPlugIn.cs` | 36-41 |
| RemotePlayer入口 | `src/GameServer/RemoteView/RemotePlayer.cs` | 115-157 |
