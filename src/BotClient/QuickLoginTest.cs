// QuickLoginTest.cs — Season 6 登录+行走测试
using System.Net.Sockets;
using System.Buffers;
using System.Text;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.SimpleModulus;
using MUnique.OpenMU.Network.Xor;
using Pipelines.Sockets.Unofficial;

namespace MUnique.OpenMU.BotClient;

public static class QuickLoginTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Quick Login Test (Season 6) ===");

        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await sock.ConnectAsync("127.0.0.1", 55901);
        var sc = SocketConnection.Create(sock);

        // 加密管线：入站 SM→Xor32, 出站 Xor32→SM
        var dec = new PipelinedSimpleModulusDecryptor(sc.Input, PipelinedSimpleModulusDecryptor.DefaultClientKey);
        var sme = new PipelinedSimpleModulusEncryptor(sc.Output, PipelinedSimpleModulusEncryptor.DefaultClientKey);
        var x32 = new PipelinedXor32Encryptor(sme.Writer, DefaultKeys.Xor32Key);

        var gseTcs = new TaskCompletionSource();
        var loginResultTcs = new TaskCompletionSource<byte>();
        var charListTcs = new TaskCompletionSource();
        var charInfoTcs = new TaskCompletionSource();
        byte myPosX = 0, myPosY = 0;

        // 解密读取线程（不创建 Connection，不走它的生命周期管理）
        _ = Task.Run(async () =>
        {
            var reader = dec.Reader;
            while (true)
            {
                var result = await reader.ReadAsync();
                if (result.IsCanceled || result.IsCompleted) break;
                var seq = result.Buffer;
                var p = seq.ToArray();
                Console.WriteLine($"RECV: t={p[0]:X2} c={p[2]:X2} s={(p.Length>3?p[3]:0):X2} l={p.Length}");
                if (p.Length >= 4 && p[0] == 0xC1 && p[2] == 0xF1 && p[3] == 0x00)
                    gseTcs.TrySetResult();
                if (p.Length >= 5 && p[2] == 0xF1 && p[3] == 0x01)
                    loginResultTcs.TrySetResult(p[4]);
                if (p.Length >= 8 && p[0] == 0xC1 && p[2] == 0xF3 && p[3] == 0x00)
                    charListTcs.TrySetResult();
                if (p.Length >= 42 && p[0] == 0xC3 && p[2] == 0xF3 && p[3] == 0x03)
                {
                    myPosX = p[4]; myPosY = p[5];
                    Console.WriteLine($"Pos from CharInfo: ({myPosX},{myPosY})");
                    charInfoTcs.TrySetResult();
                }
                // ObjectWalked: C1 [len] D4 [objId_BE2] [targetX] [targetY] ...
                if (p.Length >= 9 && p[0] == 0xC1 && p[2] == 0xD4)
                {
                    // ObjectWalkedRef: [0]=C1 [1]=len [2]=D4 [3-4]=objIdBE [5]=targetX [6]=targetY
                    ushort objId = (ushort)(p[3] << 8 | p[4]);
                    Console.WriteLine($"ObjWalked: id={objId} tgt=({p[5]},{p[6]})");
                    // 只更新自己的位置（objId == 自己）
                    // 暂时不更新，保持简单
                }
                // ObjectMoved: C1 07 0B 00 X Y
                if (p.Length >= 5 && p[0] == 0xC1 && p[2] == 0x0B && p[3] == 0x00)
                {
                    myPosX = p[4]; myPosY = p[5];
                    Console.WriteLine($"ObjMoved: pos=({myPosX},{myPosY})");
                }
                reader.AdvanceTo(seq.End);
            }
        });

        // 等待 GameServerEntered
        await gseTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("GSE received");
        await Task.Delay(200);

        // 构建登录包
        var usr = Encoding.UTF8.GetBytes("test8\0\0\0\0\0");
        var pwd = Encoding.UTF8.GetBytes("test8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0");
        var x3 = DefaultKeys.Xor3Keys;
        for (int i = 0; i < 10; i++) usr[i] ^= x3[i % 3];
        for (int i = 0; i < 20; i++) pwd[i] ^= x3[i % 3];

        uint tick = (uint)Environment.TickCount;
        var buf = new byte[60];
        buf[0] = 0xC3; buf[1] = 0x3C; buf[2] = 0xF1; buf[3] = 0x01;
        usr.CopyTo(buf, 4); pwd.CopyTo(buf, 14);
        buf[34] = (byte)(tick >> 24); buf[35] = (byte)(tick >> 16);
        buf[36] = (byte)(tick >> 8); buf[37] = (byte)tick;
        Encoding.UTF8.GetBytes("2.04d").CopyTo(buf, 38);
        Encoding.UTF8.GetBytes("k1Pk2jcET48mxL3b").CopyTo(buf, 43);

        // 发送登录包（加密）
        await x32.Writer.WriteAsync(buf);
        await x32.Writer.FlushAsync();

        var loginResult = await loginResultTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"Login result: {loginResult} (1=OK)");
        if (loginResult != 1) { sock.Close(); return; }
        await Task.Delay(200);

        // 请求角色列表
        await x32.Writer.WriteAsync(new byte[] { 0xC1, 0x05, 0xF3, 0x00, 0x00 });
        await x32.Writer.FlushAsync();
        await charListTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("Char list received");

        // 选角色
        await Task.Delay(500);
        var sel = new byte[14];
        sel[0] = 0xC1; sel[1] = 0x0E; sel[2] = 0xF3; sel[3] = 0x03;
        Encoding.UTF8.GetBytes("test8Dk\0\0").CopyTo(sel, 4);
        await x32.Writer.WriteAsync(sel);
        await x32.Writer.FlushAsync();

        await charInfoTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("Char info received, entering game!");

        // FocusCharacter + ClientReady（C1 走 x32 加密管线或明文 socket）
        await Task.Delay(200);
        var focus = new byte[14];
        focus[0] = 0xC1; focus[1] = 0x0E; focus[2] = 0xF3; focus[3] = 0x15;
        Encoding.UTF8.GetBytes("test8Dk\0\0").CopyTo(focus, 4);
        await sc.Output.WriteAsync(focus);
        await sc.Output.FlushAsync();

        await Task.Delay(200);
        await sc.Output.WriteAsync(new byte[] { 0xC1, 0x05, 0xF3, 0x12, 0x00 });
        await sc.Output.FlushAsync();

        Console.WriteLine($"=== In game at ({myPosX},{myPosY}), walking to (140,122)... ===");

        // 用明文 socket 发 D4，每步 2 秒
        for (int step = 0; step < 80; step++)
        {
            // 计算方向：dx = 140 - myPosX, dy = 122 - myPosY
            sbyte dx = (sbyte)(140 - myPosX);
            sbyte dy = (sbyte)(122 - myPosY);
            byte dir;
            if (Math.Abs(dx) >= Math.Abs(dy))
                dir = dx > 0 ? (byte)4 : (byte)8;  // 左=8, 右=4
            else
                dir = dy > 0 ? (byte)7 : (byte)2;  // 下=7, 上=2
            // 转 0-7 编码：8->0, 2->2, 4->3, 7->6
            byte dir07 = dir switch { 8 => 0, 2 => 1, 4 => 3, 7 => 6, _ => dir };
            byte targetRotation = dir07;
            byte stepCount = 1;

            var walk = new byte[7];
            walk[0] = 0xC1; walk[1] = 0x07; walk[2] = 0xD4;
            walk[3] = myPosX; walk[4] = myPosY;
            walk[5] = (byte)((targetRotation << 4) | stepCount);
            walk[6] = (byte)(dir07 << 4);  // 方向编码只用高4位

            Console.WriteLine($"Walk step {step}: pos=({myPosX},{myPosY}) -> dir={dir}(0-7={dir07}) target=({140},{122}) dx={dx} dy={dy}");
            try { await x32.Writer.WriteAsync(walk); await x32.Writer.FlushAsync(); }
            catch { Console.WriteLine("Socket write failed, exiting."); break; }
            await Task.Delay(2000);

            if (myPosX == 140 && myPosY == 122) { Console.WriteLine("=== Arrived! ==="); break; }
        }

        Console.WriteLine("=== Walking done ===");
        await Task.Delay(30000);
        sock.Close();
        Console.WriteLine("=== Test Complete ===");
    }
}
