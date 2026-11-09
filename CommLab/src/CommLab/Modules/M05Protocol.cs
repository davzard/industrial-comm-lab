using CommLab.Core;
using CommLab.Fakes;

namespace CommLab.Modules;

/// <summary>
/// M05 根据协议文档做通信：组包 / 解析 / 数据转换 / 序号匹配 / 超时重试 / 状态码。
/// 这是把 M01~M04 的零件组装成"真工程"的一课，也是面试"你接过最复杂的协议文档"的主场。
/// </summary>
public sealed class M05Protocol : IModule
{
    public string Title => "M05 协议设计与可靠性";

    // ---------- 协议文档（节选）→ 代码常量 ----------
    private static readonly byte[] Header = { 0x68, 0x68 };
    private const byte CmdReadTemp = 0x01;
    private const byte CmdReadStatus = 0x02;
    private const byte CmdSetSetpoint = 0x03;
    private const byte RespMask = 0x80;

    // 状态码表（文档定义）
    private static string StatusName(byte s) => s switch
    {
        0x00 => "成功",
        0x01 => "设备忙",
        0x02 => "参数错误",
        0x80 => "通信错误",
        _ => $"未知(0x{s:X2})"
    };

    public Task RunAsync()
    {
        Console.WriteLine("=== 1. 协议文档 → 代码 ===");
        Console.WriteLine("  帧格式: 68 68 | SEQ(1) | CMD(1) | LEN(1) | DATA(N) | CRC16(2,小端)");
        Console.WriteLine("  请求: 0x01读温度 0x02读状态 0x03设设定值；响应 CMD = 请求|0x80");
        Console.WriteLine("  响应 DATA = [状态码1B][载荷]；温度 = int16大端 ×0.1℃");
        Console.WriteLine("  工程习惯：文档里的每个字段/枚举都变成常量，禁止魔法数字。");

        var device = new FakeProtocolDevice();
        var parser = new FrameParser(
            Header,
            f => f.Length >= 5 ? 7 + f[4] : -1,   // 整帧长 = 5 + LEN + 2
            f => Crc.Crc16Modbus(f.AsSpan(0, f.Length - 2)) ==
                 ((f[^2] & 0xFF) | (f[^1] << 8))); // CRC 小端

        Console.WriteLine();
        Console.WriteLine("=== 2. 组包（按文档拼字节 + CRC） ===");
        byte seq = 1;
        var readTempReq = BuildFrame(ref seq, CmdReadTemp, Array.Empty<byte>());
        ByteUtils.PrintFrame("读温度请求", readTempReq);
        var resp = device.Process(readTempReq);
        var frames = parser.Feed(resp);
        Console.WriteLine($"  解析出 {frames.Count} 帧（状态机：粘包/拆包都靠它）");

        Console.WriteLine();
        Console.WriteLine("=== 3. 解析 + 数据转换（原始值 → 工程量） ===");
        var f = frames[0];
        byte rSeq = f[2], rCmd = f[3];
        var data = f.AsSpan(5, f[4]);
        Console.WriteLine($"  响应: SEQ={rSeq} CMD=0x{rCmd:X2}(=0x{rCmd - RespMask:X2}|80) 状态={StatusName(data[0])}");
        int raw = (data[1] << 8) | data[2];
        float temp = raw / 10f;
        Console.WriteLine($"  温度原始值 {raw} × 0.1 = {temp}℃");
        SelfTest.CheckEq(25.5f, temp, "读温度数据转换");

        Console.WriteLine();
        Console.WriteLine("=== 4. 位域解析（报警位掩码） ===");
        var stReq = BuildFrame(ref seq, CmdReadStatus, Array.Empty<byte>());
        var stResp = device.Process(stReq);
        var stData = parser.Feed(stResp)[0];
        byte alarm = stData[6]; // 帧[5]=状态码 [6]=报警位 [7]=错误码
        Console.WriteLine($"  报警位 = {alarm:00000000}b");
        Console.WriteLine($"    bit0 超温 = {(alarm & 1) != 0}");
        Console.WriteLine($"    bit1 过流 = {(alarm & 2) != 0}");
        Console.WriteLine($"    bit2 门开 = {(alarm & 4) != 0}");
        SelfTest.Check((alarm & 2) != 0, "bit1 过流报警置位");

        Console.WriteLine();
        Console.WriteLine("=== 5. 序号匹配：响应乱序到达也能对上 ===");
        // 真实场景：并发发多帧，网络/总线可能乱序 —— 必须靠 SEQ 配对，不能靠到达顺序
        var reqA = BuildFrame(ref seq, CmdReadTemp, Array.Empty<byte>());
        var reqB = BuildFrame(ref seq, CmdReadTemp, Array.Empty<byte>());
        var respA = device.Process(reqA);
        var respB = device.Process(reqB);
        Console.WriteLine($"  发出 SEQ={reqA[2]} 和 SEQ={reqB[2]}，响应乱序到达（先 B 后 A）：");
        foreach (var r in new[] { respB, respA }) // 模拟乱序
        {
            var fr = parser.Feed(r)[0];
            string owner = fr[2] == reqA[2] ? "请求A" : "请求B";
            Console.WriteLine($"    收到 SEQ={fr[2]} 的响应 → 交给 {owner} 的等待者");
        }
        SelfTest.Check(true, "乱序响应按 SEQ 匹配");

        Console.WriteLine();
        Console.WriteLine("=== 6. 超时重试 + 幂等（重发不会执行两次） ===");
        var setReq = BuildFrame(ref seq, CmdSetSetpoint, new byte[] { 0x01, 0xC2 }); // 45.0℃ ×10 = 450 = 0x01C2
        ByteUtils.PrintFrame("设设定值(45.0℃)", setReq);
        device.DropNext = true; // 模拟第一次"发出去了但没收到响应"
        var r1 = device.Process(setReq);
        Console.WriteLine($"  第 1 次: 收到 {r1.Length} 字节 → 超时！");
        Console.WriteLine("  重发规则: 同一帧原样重发（SEQ 不变），靠设备幂等保证不重复执行");
        var r2 = device.Process(setReq); // 同 SEQ 重发
        var st2 = parser.Feed(r2)[0];
        Console.WriteLine($"  第 2 次: 状态={StatusName(st2[5])}，设备实际生效次数 = {device.SetpointApplyCount}");
        SelfTest.CheckEq(1, device.SetpointApplyCount, "幂等：重发只生效一次");
        SelfTest.CheckEq(45.0f, device.Setpoint, "设定值正确");

        Console.WriteLine();
        Console.WriteLine("=== 7. 错误码处理（状态码表） ===");
        var badReq = BuildFrame(ref seq, CmdSetSetpoint, new byte[] { 0xFF, 0xFF }); // 655.35℃ 超范围
        var badResp = parser.Feed(device.Process(badReq))[0];
        Console.WriteLine($"  超范围设定 → 状态码 0x{badResp[5]:X2} = {StatusName(badResp[5])}");
        Console.WriteLine("  工程习惯：每个状态码都要有明确处理（重试/告警/人工介入），不许吞掉。");
        SelfTest.CheckEq((byte)0x02, badResp[5], "参数错误状态码");

        Console.WriteLine();
        Console.WriteLine("=== 8. 面试话术 ===");
        Console.WriteLine("  接新协议四步：1) 文档字段→常量 2) 组包/解析函数+自测帧");
        Console.WriteLine("  3) 序号配对+超时重试 4) 状态码全表处理。先写'协议自测'，再联调。");
        return Task.CompletedTask;
    }

    private static byte[] BuildFrame(ref byte seq, byte cmd, byte[] data)
    {
        byte[] head = { 0x68, 0x68, seq, cmd, (byte)data.Length };
        var body = ByteUtils.Combine(head, data);
        seq++; // 每次组包序号 +1（重发时直接用原帧，不走这里）
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }
}
