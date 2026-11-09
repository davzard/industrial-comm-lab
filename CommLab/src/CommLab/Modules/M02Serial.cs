using CommLab.Core;
using CommLab.Fakes;

namespace CommLab.Modules;

/// <summary>M02 串口通信（RS-232/RS-485）：参数、半双工、帧、超时、重连</summary>
public sealed class M02Serial : IModule
{
    public string Title => "M02 串口通信（RS-232/RS-485）";

    public async Task RunAsync()
    {
        Console.WriteLine("\n=== 1. 串口参数（两线必须完全一致，否则全是乱码） ===");
        Console.WriteLine("  波特率 BaudRate : 9600 / 115200 —— 每秒多少 bit，收发双方必须相同");
        Console.WriteLine("  数据位 DataBits : 8（几乎都用 8）");
        Console.WriteLine("  校验位 Parity   : None/Even/Odd —— 多 1 bit 查错");
        Console.WriteLine("  停止位 StopBits : 1 / 1.5 / 2");
        Console.WriteLine("  常用组合：9600,8,N,1 或 115200,8,N,1");
        Console.WriteLine("  RS-232：点对点，3 根线(TXD/RXD/GND)，电平 ±12V，距离短");
        Console.WriteLine("  RS-485：总线型，2 根线(A/B)挂多台设备，半双工，距离远(1200m)，");
        Console.WriteLine("          总线两端要加 120Ω 终端电阻；A/B 接反 = 收不到数据(经典坑)");

        Console.WriteLine("\n=== 2. 帧格式（自定义私有协议示例） ===");
        Console.WriteLine("  AA 55 | LEN | CMD | DATA... | SUM(和校验)");
        Console.WriteLine("  帧头2B  长度1B 命令1B 数据nB  校验1B");
        Console.WriteLine("  LEN 字段 = 其后字节数（CMD+DATA+SUM），整帧长 = LEN + 3");
        var cmdFrame = FakeTempSensor.BuildFrame(0x01, Array.Empty<byte>());
        ByteUtils.PrintFrame("读温度命令", cmdFrame);

        Console.WriteLine("\n=== 3. 发送 + 接收（接收事件 ≠ 完整报文） ===");
        var (host, devicePort) = FakeSerialPort.Connect();
        host.Open();
        devicePort.Open();
        var sensor = new FakeTempSensor(devicePort);

        // 上位机侧：帧解析器负责把"小碎块"拼回完整帧
        var parser = new FrameParser(
            header: new byte[] { 0xAA, 0x55 },
            lengthOf: f => f.Length >= 4 ? f[2] + 3 : 0,
            isValid: f => f[f.Length - 1] == Crc.ChecksumSum(f.AsSpan(0, f.Length - 1)));
        byte[]? response = null;
        host.DataReceived += chunk =>
        {
            Console.WriteLine($"    上位机收到碎块: {ByteUtils.Hex(chunk.Span)}（注意：不是完整帧！）");
            foreach (var frame in parser.Feed(chunk.Span))
                if (frame[3] == 0x81) response = frame;
        };

        host.Write(cmdFrame);
        for (int i = 0; i < 100 && response is null; i++) await Task.Delay(10);
        if (response is not null)
        {
            ByteUtils.PrintFrame("温度响应", response);
            short raw = (short)((response[4] << 8) | response[5]);
            Console.WriteLine($"    解析: 原始 int16 = {raw}，×0.1 = {raw * 0.1} ℃");
        }

        Console.WriteLine("\n=== 4. 超时（设备没应答怎么办） ===");
        sensor.SetPowered(false); // 设备断电
        host.Write(FakeTempSensor.BuildFrame(0x01, Array.Empty<byte>()));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[]? r = null;
        while (sw.ElapsedMilliseconds < 300)
        {
            r = await host.ReadAsync(50);
            if (r is not null) break;
        }
        Console.WriteLine("    设备断电，等待 300ms 后仍无数据 → 判定超时（上位机必须设超时，否则会卡死）");

        Console.WriteLine("\n=== 5. 断线重连 ===");
        host.Close();
        Console.WriteLine("    上位机关闭串口 → 重新 Open 再发命令（重连 = 重新打开 + 重发）");
        host.Open();
        sensor.SetPowered(true); // 设备上电
        parser.Reset();
        response = null;
        host.Write(FakeTempSensor.BuildFrame(0x01, Array.Empty<byte>()));
        for (int i = 0; i < 100 && response is null; i++) await Task.Delay(10);
        string replyHex = response is not null ? ByteUtils.Hex(response) : "失败";
        Console.WriteLine($"    重连后收到响应: {replyHex}");

        Console.WriteLine("\n=== 6. RS-485 半双工与总线锁 ===");
        Console.WriteLine("  半双工：同一时刻总线上只能有一台设备在发，双方发收必须错开；");
        Console.WriteLine("  多主站场景用 SemaphoreSlim 做'总线锁'，防止两帧撞车：");
        var busLock = new SemaphoreSlim(1, 1);
        async Task BusWrite(ISerialLink p, byte[] frame)
        {
            await busLock.WaitAsync();
            try { p.Write(frame); }
            finally { busLock.Release(); }
        }
        var (h1, d1) = FakeSerialPort.Connect("主站1", "从站");
        var (h2, _) = FakeSerialPort.Connect("主站2", "从站");
        h1.Open(); h2.Open();
        await Task.WhenAll(
            BusWrite(h1, FakeTempSensor.BuildFrame(0x01, Array.Empty<byte>())),
            BusWrite(h2, FakeTempSensor.BuildFrame(0x02, Array.Empty<byte>())));
        Console.WriteLine("    两个主站并发发送被总线锁串行化，不会撞车（RS-485 多主站必备）");

        Console.WriteLine("\n=== 自测 ===");
        SelfTest.Check(response is not null, "重连后收到温度响应");
        SelfTest.CheckEq((short)255, (short)((response![4] << 8) | response[5]), "温度原始值 25.5℃");
        SelfTest.Check(r is null, "设备断电时读超时返回 null");

        // 帧解析器：把一帧按 1 字节喂进去，也能拼回来
        var p2 = new FrameParser(
            new byte[] { 0xAA, 0x55 },
            f => f.Length >= 4 ? f[2] + 3 : 0,
            f => f[f.Length - 1] == Crc.ChecksumSum(f.AsSpan(0, f.Length - 1)));
        var full = FakeTempSensor.BuildFrame(0x01, Array.Empty<byte>());
        var collected = new List<byte[]>();
        foreach (var b in full)
            collected.AddRange(p2.Feed(new[] { b }));
        SelfTest.CheckEq(1, collected.Count, "逐字节喂入也能重组出完整帧");
        SelfTest.CheckEq(full, collected[0], "重组帧与原帧一致");
    }
}
