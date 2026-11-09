using System.Net;
using System.Net.Sockets;
using System.Text;
using CommLab.Core;

namespace CommLab.Modules;

/// <summary>
/// M03 TCP/UDP 通信
/// 知识点：客户端/服务端、端口、粘包拆包、心跳、断线检测、重连、UDP 去重、广播发现
/// 全部跑在 127.0.0.1 回环上，零外部依赖。
/// </summary>
public sealed class M03TcpUdp : IModule
{
    public string Title => "M03 TCP/UDP 通信";

    public async Task RunAsync()
    {
        int port = GetFreePort();

        Console.WriteLine("\n=== 1. TCP：客户端/服务端与端口 ===");
        Console.WriteLine("  TCP = 面向连接的字节流：先三次握手建连，再收发，最后断开；");
        Console.WriteLine("  端口 = 同一台机器上区分不同程序/服务的编号（一个程序可开多个端口）；");
        Console.WriteLine("  客户端主动 Connect(服务器IP, 端口)；服务端 Listen(端口) 后 Accept 等连接。");
        var server = new TcpEchoServer(port);
        await server.StartAsync();
        Console.WriteLine($"  服务端已监听 127.0.0.1:{port}");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var net = client.GetStream();
        var parser = new LengthPrefixParser();
        var received = new List<string>();
        var recvDone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = await net.ReadAsync(buf); } catch { break; }
                if (n <= 0) break;
                foreach (var m in parser.Feed(buf.AsSpan(0, n)))
                {
                    var s = Encoding.UTF8.GetString(m);
                    received.Add(s);
                    Console.WriteLine($"    客户端收到: {s}");
                    if (received.Count >= 3) recvDone.TrySetResult(true);
                }
            }
        });

        Console.WriteLine("\n=== 2. 粘包/拆包：一次写 3 条消息，接收端必须正确拆开 ===");
        Console.WriteLine("  TCP 是字节流，没有消息边界。解决：长度前缀 [4B长度][数据]");
        var sticky = ByteUtils.Combine(
            LengthPrefixParser.Encode("msg-1"),
            LengthPrefixParser.Encode("msg-2"),
            LengthPrefixParser.Encode("msg-3"));
        ByteUtils.PrintFrame("一次写入(粘包)", sticky);
        await net.WriteAsync(sticky);
        await recvDone.Task.WaitAsync(TimeSpan.FromSeconds(3));
        SelfTest.CheckEq(3, received.Count, "粘包被拆成 3 条消息");
        SelfTest.CheckEq("ECHO:msg-2", received.Count > 1 ? received[1] : "", "第 2 条内容正确");

        Console.WriteLine("\n=== 3. 心跳：及时发现对端掉线 ===");
        Console.WriteLine("  网络断了，TCP 不会立刻通知你（可能几分钟才知道）——必须自己发心跳：");
        Console.WriteLine("  客户端每 100ms 发 PING，服务端回 PONG；超过 400ms 没 PONG → 判定掉线。");
        using var c2 = new TcpClient();
        await c2.ConnectAsync(IPAddress.Loopback, port);
        var s2 = c2.GetStream();
        var p2 = new LengthPrefixParser();
        DateTime lastPong = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = await s2.ReadAsync(buf); } catch { break; }
                if (n <= 0) break;
                foreach (var m in p2.Feed(buf.AsSpan(0, n)))
                    if (Encoding.UTF8.GetString(m) == "PONG") lastPong = DateTime.UtcNow;
            }
        });
        for (int i = 0; i < 6; i++)
        {
            try { await s2.WriteAsync(LengthPrefixParser.Encode("PING")); } catch { }
            await Task.Delay(100);
        }
        Console.WriteLine("  正常期间：PING → PONG 持续往返，lastPong 一直在刷新");
        server.Stop(); // 服务端直接关掉，模拟设备掉线
        bool detected = false;
        for (int i = 0; i < 40 && !detected; i++)
        {
            if (DateTime.UtcNow - lastPong > TimeSpan.FromMilliseconds(400)) { detected = true; break; }
            await Task.Delay(50);
        }
        Console.WriteLine($"  服务端掉线后 {Math.Max(0, (int)(DateTime.UtcNow - lastPong).TotalMilliseconds)}ms 未收到 PONG → 判定掉线");
        SelfTest.Check(detected, "心跳超时检测到掉线");

        Console.WriteLine("\n=== 4. 断线重连 ===");
        Console.WriteLine("  检测到掉线后：重新 Connect（指数退避：1s → 2s → 4s → 上限30s），连上后补发/重同步。");
        var server2 = new TcpEchoServer(port);
        await server2.StartAsync();
        using var c3 = new TcpClient();
        await c3.ConnectAsync(IPAddress.Loopback, port);
        var s3 = c3.GetStream();
        await s3.WriteAsync(LengthPrefixParser.Encode("hello-after-reconnect"));
        var p3 = new LengthPrefixParser();
        var buf3 = new byte[4096];
        int n3 = await s3.ReadAsync(buf3);
        var msgs3 = p3.Feed(buf3.AsSpan(0, n3));
        bool okReconnect = msgs3.Count == 1 && Encoding.UTF8.GetString(msgs3[0]) == "ECHO:hello-after-reconnect";
        Console.WriteLine($"  重连成功，收发正常: {okReconnect}");
        SelfTest.Check(okReconnect, "重连后收发正常");
        server2.Stop();

        Console.WriteLine("\n=== 5. UDP：无连接、不保证送达、快 ===");
        Console.WriteLine("  UDP = 发完不管：没有握手、不重传、不保序 → 快、开销小；");
        Console.WriteLine("  适用：视频流、广播发现、短轮询；代价：丢了就丢了，可靠性靠上层（去重/重发）。");
        using var echoUdp = new UdpClient();
        echoUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        int echoPort = ((IPEndPoint)echoUdp.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            var r = await echoUdp.ReceiveAsync(CancellationToken.None);
            await echoUdp.SendAsync(r.Buffer.ToArray(), r.Buffer.Length, r.RemoteEndPoint);
        });
        using var u2 = new UdpClient();
        var payload = LengthPrefixParser.Encode("udp-hello");
        ByteUtils.PrintFrame("UDP 发送", payload);
        await u2.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, echoPort));
        var reply2 = await u2.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var udpMsgs = new LengthPrefixParser().Feed(reply2.Buffer);
        bool okUdp = udpMsgs.Count == 1 && Encoding.UTF8.GetString(udpMsgs[0]) == "udp-hello";
        Console.WriteLine($"    UDP 收到回显: {Encoding.UTF8.GetString(reply2.Buffer)}（长度前缀同样适用）");
        SelfTest.Check(okUdp, "UDP 回显正确");

        Console.WriteLine("\n=== 6. UDP 遥测去重（重发会产生重复包） ===");
        var dedup = new TelemetryDeduper();
        Console.WriteLine("  设备重发机制导致上位机可能收到重复 seq，按 seq 去重：");
        int fresh = 0;
        foreach (var seq in new[] { 1, 2, 3, 3, 4 })
        {
            int isNew = dedup.OnPacket(seq, $"temp-{seq}");
            fresh += isNew;
            Console.WriteLine($"    seq={seq} → {(isNew == 1 ? "新数据，入库" : "重复包，丢弃")}");
        }
        SelfTest.CheckEq(4, fresh, "5 个包去重后剩 4 条");

        Console.WriteLine("\n=== 7. 设备发现（广播） ===");
        Console.WriteLine("  真实场景：上位机向 255.255.255.255:50200 广播 DISCOVER，局域网内设备回自己的 IP/型号；");
        Console.WriteLine("  回环上广播不会折返，这里用单播模拟同一过程：");
        using var deviceUdp = new UdpClient();
        deviceUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        int devPort = ((IPEndPoint)deviceUdp.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            var r = await deviceUdp.ReceiveAsync(CancellationToken.None);
            if (Encoding.UTF8.GetString(r.Buffer) == "DISCOVER")
            {
                var reply = Encoding.UTF8.GetBytes($"DEV-01@192.168.1.100:{devPort}");
                await deviceUdp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
        });
        using var hostUdp = new UdpClient();
        hostUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        hostUdp.Client.EnableBroadcast = true; // 真实环境要开广播权限
        await hostUdp.SendAsync(Encoding.UTF8.GetBytes("DISCOVER"), 8, new IPEndPoint(IPAddress.Loopback, devPort));
        var disc = await hostUdp.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var devInfo = Encoding.UTF8.GetString(disc.Buffer);
        Console.WriteLine($"    发现设备: {devInfo}");
        SelfTest.Check(devInfo.StartsWith("DEV-01@"), "广播发现收到设备信息");
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint!).Port;
        l.Stop();
        return p;
    }
}

/// <summary>UDP 按 seq 去重：只保留最近 100 个 seq，防止内存无限增长</summary>
sealed class TelemetryDeduper
{
    private readonly Queue<int> _order = new();
    private readonly HashSet<int> _seen = new();

    /// <returns>1=新数据 0=重复包</returns>
    public int OnPacket(int seq, string payload)
    {
        if (!_seen.Add(seq)) return 0;
        _order.Enqueue(seq);
        while (_order.Count > 100) _seen.Remove(_order.Dequeue());
        return 1;
    }
}

/// <summary>长度前缀回显服务器：收到 PING 回 PONG，其余回 ECHO:xxx</summary>
sealed class TcpEchoServer
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = new();
    private readonly object _gate = new();

    public TcpEchoServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public Task StartAsync()
    {
        _listener.Start();
        _ = AcceptLoop();
        return Task.CompletedTask;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            lock (_gate) _clients.Add(c);
            _ = HandleAsync(c);
        }
    }

    private async Task HandleAsync(TcpClient c)
    {
        try
        {
            var net = c.GetStream();
            var parser = new LengthPrefixParser();
            var buf = new byte[4096];
            while (true)
            {
                int n = await net.ReadAsync(buf);
                if (n <= 0) break;
                foreach (var m in parser.Feed(buf.AsSpan(0, n)))
                {
                    var s = Encoding.UTF8.GetString(m);
                    var reply = s == "PING" ? "PONG" : "ECHO:" + s;
                    await net.WriteAsync(LengthPrefixParser.Encode(reply));
                }
            }
        }
        catch { /* 客户端断开 */ }
        finally
        {
            lock (_gate) _clients.Remove(c);
            c.Close();
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_gate)
        {
            foreach (var c in _clients) c.Close();
            _clients.Clear();
        }
    }
}
