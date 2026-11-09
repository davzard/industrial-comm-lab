using CommLab.Core;
using CommLab.Fakes;

namespace CommLab.Modules;

/// <summary>
/// M04 Modbus（RTU + TCP）—— 工控现场的第一大协议
/// 知识点：四大数据区与地址映射、功能码、RTU 帧与 CRC16、MBAP 头、异常码
/// 面试点：为什么 Modbus 统治工控？——简单（8 字节就能读一个寄存器）、开放、无处不在。
/// </summary>
public sealed class M04Modbus : IModule
{
    public string Title => "M04 Modbus（RTU/TCP）";

    public async Task RunAsync()
    {
        var plc = new FakeModbusPlc(unitId: 1);
        // 预置一些"现场数据"
        plc.HoldingRegisters[0] = 0x0102;   // 40001 设备编号
        plc.HoldingRegisters[1] = 100;      // 40002 设定值
        plc.HoldingRegisters[2] = 255;      // 40003 温度 ×0.1℃ = 25.5℃（整数存小数）
        plc.HoldingRegisters[3] = 0x41CC;   // 40004/40005 float 25.5 高字
        plc.HoldingRegisters[4] = 0x0000;   // float 25.5 低字
        plc.InputRegisters[0] = 300;        // 41001 模拟量输入
        plc.Coils[0] = true;                // 00001 运行标志
        plc.DiscreteInputs[0] = true;       // 10001 急停按钮状态

        Console.WriteLine("\n=== 1. 四大数据区与地址映射（最经典的坑） ===");
        Console.WriteLine("  0xxxx 线圈(可读写开关量) | 1xxxx 离散输入(只读开关量)");
        Console.WriteLine("  3xxxx 保持寄存器(可读写)  | 4xxxx 输入寄存器(只读)");
        Console.WriteLine("  注意：报文里的 PDU 地址 = 五位地址 - 1，如 40001 ↔ 地址 0");
        Console.WriteLine("  不同软件(PLC/组态/上位机)对'起始地址是 0 还是 1'约定不同，看文档！");

        Console.WriteLine("\n=== 2. RTU 帧格式：地址 + 功能码 + 数据 + CRC16(小端) ===");
        Console.WriteLine("  读 10 个保持寄存器（经典帧，背下来）：");
        var read10 = RtuFrame(1, 0x03, 0, 10);
        ByteUtils.PrintFrame("请求", read10);
        var resp10 = plc.ProcessRtu(read10);
        ByteUtils.PrintFrame("响应", resp10);
        Console.WriteLine($"  响应解析: fc={resp10[1]:X2} 字节数={resp10[2]} (=10寄存器×2) 首寄存器={resp10[3]:X2}{resp10[4]:X2}");
        SelfTest.CheckEq((ushort)0x0102, (ushort)((resp10[3] << 8) | resp10[4]), "40001 读出设备编号 0x0102");
        SelfTest.CheckEq((ushort)255, (ushort)((resp10[7] << 8) | resp10[8]), "40003 读出温度原始值 255");

        Console.WriteLine("\n=== 3. CRC16/MODBUS 的性质：整帧再算一遍 = 0 ===");
        var whole = Crc.Crc16Modbus(resp10);
        Console.WriteLine($"  对整帧(含CRC)再算 CRC16 = 0x{whole:X4}（恒为 0，接收端就这么验错）");
        SelfTest.CheckEq((ushort)0, whole, "整帧 CRC 验算为 0");

        Console.WriteLine("\n=== 4. 功能码 01 读线圈（位打包） ===");
        plc.Coils[0] = true; plc.Coils[1] = false; plc.Coils[2] = true;
        // 注意：fc 01/02 的 qty 是 1 字节（fc 03/04 才是 2 字节）—— 协议细节坑
        var readCoils = RtuFrameRaw(1, 0x01, new byte[] { 0x00, 0x00, 0x08 });
        var respCoils = plc.ProcessRtu(readCoils);
        ByteUtils.PrintFrame("请求", readCoils);
        ByteUtils.PrintFrame("响应", respCoils);
        int bit0 = (respCoils[3] >> 0) & 1, bit2 = (respCoils[3] >> 2) & 1;
        Console.WriteLine($"  8 个线圈打包成 1 字节 = {respCoils[3]:X2}；bit0={bit0} bit1=0 bit2={bit2}");
        SelfTest.Check(bit0 == 1 && bit2 == 1, "线圈位解析正确");

        Console.WriteLine("\n=== 5. 功能码 05 写单个线圈（ON=0xFF00, OFF=0x0000） ===");
        var writeCoil = RtuFrameWithValue(1, 0x05, 0, 0xFF00); // 把 00001 置 ON
        var respCoil = plc.ProcessRtu(writeCoil);
        ByteUtils.PrintFrame("请求", writeCoil);
        ByteUtils.PrintFrame("响应(原样回显)", respCoil);
        SelfTest.Check(plc.Coils[0], "写线圈后从站状态已变");

        Console.WriteLine("\n=== 6. 功能码 16(0x10) 写多个寄存器 ===");
        var writeReg = RtuFrame(1, 0x10, 0, new ushort[] { 7, 8, 9 }); // 写 40001~40003
        var respReg = plc.ProcessRtu(writeReg);
        ByteUtils.PrintFrame("请求", writeReg);
        ByteUtils.PrintFrame("响应", respReg);
        Console.WriteLine($"  写后: 40001={plc.HoldingRegisters[0]} 40002={plc.HoldingRegisters[1]} 40003={plc.HoldingRegisters[2]}");
        SelfTest.CheckEq((ushort)8, plc.HoldingRegisters[1], "多寄存器写入生效");

        Console.WriteLine("\n=== 7. 跨寄存器 float（字序坑） ===");
        var readFloat = RtuFrame(1, 0x03, 3, 2);
        var respFloat = plc.ProcessRtu(readFloat);
        float f = EndianUtils.BytesToFloat(respFloat.AsSpan(3, 4), bigEndian: true);
        Console.WriteLine($"  40004/40005 = {respFloat[3]:X2}{respFloat[4]:X2} {respFloat[5]:X2}{respFloat[6]:X2} → float {f}（ABCD 大端）");
        Console.WriteLine("  字序变体：ABCD / CDAB(字交换) / BADC / DCBA —— 文档不写就挨个试，现场经典坑");
        SelfTest.CheckEq(25.5f, f, "跨寄存器 float 解析 25.5");

        Console.WriteLine("\n=== 8. 异常响应：fc|0x80 + 异常码 ===");
        var badAddr = RtuFrame(1, 0x03, 95, 10); // 95+10 超出 100 个寄存器
        var respEx = plc.ProcessRtu(badAddr);
        ByteUtils.PrintFrame("越界读请求", badAddr);
        ByteUtils.PrintFrame("异常响应", respEx);
        Console.WriteLine($"  fc=0x{respEx[1]:X2}(=03|80) 异常码={respEx[2]}（01非法功能码 02非法地址 03非法值 04设备故障）");
        SelfTest.CheckEq((byte)0x83, respEx[1], "异常功能码 = 0x83");
        SelfTest.CheckEq((byte)0x02, respEx[2], "异常码 02 = 非法数据地址");

        Console.WriteLine("\n=== 9. Modbus TCP：MBAP 头 + 同样的 PDU ===");
        Console.WriteLine("  MBAP = 事务号2B + 协议号2B(恒0) + 长度2B + 单元号1B，之后就是 RTU 的 PDU；");
        Console.WriteLine("  端口 502；RTU 的 CRC 没了（TCP 自己有校验），多了事务号用于请求/响应配对。");
        ushort txId = 0x0001;
        var pdu10 = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0A };
        var tcpReq = ByteUtils.Combine(
            EndianUtils.ShortToBytes(txId, true),
            EndianUtils.ShortToBytes(0, true),
            EndianUtils.ShortToBytes((ushort)(1 + pdu10.Length), true),
            new[] { (byte)1 }, pdu10);
        ByteUtils.PrintFrame("TCP请求", tcpReq);
        var tcpPdu = plc.ProcessTcp(1, tcpReq.AsSpan(7));
        var tcpResp = ByteUtils.Combine(
            EndianUtils.ShortToBytes(txId, true),
            EndianUtils.ShortToBytes(0, true),
            EndianUtils.ShortToBytes((ushort)(1 + tcpPdu.Length), true),
            new[] { (byte)1 }, tcpPdu);
        ByteUtils.PrintFrame("TCP响应", tcpResp);
        SelfTest.CheckEq(txId, EndianUtils.BytesToShort(tcpResp.AsSpan(0, 2), true), "事务号原样返回（请求响应配对）");
        SelfTest.CheckEq((ushort)9, EndianUtils.BytesToShort(tcpResp.AsSpan(13, 2), true), "TCP 读回 40003 = 9（第 6 节写入的值）");

        Console.WriteLine("\n=== 10. RTU vs TCP 对照（面试常问） ===");
        Console.WriteLine("  RTU: 串口, 帧间静默 3.5 字符时间, CRC16 校验, 地址在帧里, 从站最多 247 个");
        Console.WriteLine("  TCP: 以太网 502 端口, 无帧间隔(靠 MBAP 长度), 无 CRC, 单元号在 MBAP 里");
        Console.WriteLine("  两者 PDU 完全相同 —— 所以很多设备 RTU/TCP 只是'换了个壳'");
        await Task.CompletedTask;
    }

    /// <summary>组 RTU 帧：地址 + fc + 数据 + CRC16 小端</summary>
    private static byte[] RtuFrame(byte unit, byte fc, ushort start, ushort qty)
    {
        var body = ByteUtils.Combine(new[] { unit, fc }, EndianUtils.ShortToBytes(start, true), EndianUtils.ShortToBytes(qty, true));
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }

    /// <summary>组 RTU 帧（原始数据）：地址 + fc + data + CRC16 小端</summary>
    private static byte[] RtuFrameRaw(byte unit, byte fc, byte[] data)
    {
        var body = ByteUtils.Combine(new[] { unit, fc }, data);
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }

    /// <summary>组 RTU 帧（写线圈/写单寄存器：addr+value 各 2 字节）</summary>
    private static byte[] RtuFrameWithValue(byte unit, byte fc, ushort addr, ushort value)
    {
        var body = ByteUtils.Combine(new[] { unit, fc }, EndianUtils.ShortToBytes(addr, true), EndianUtils.ShortToBytes(value, true));
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }

    /// <summary>组 RTU 帧（写多寄存器：值数组）</summary>
    private static byte[] RtuFrame(byte unit, byte fc, ushort start, ushort[] values)
    {
        var data = new byte[5 + values.Length * 2];
        data[0] = (byte)(start >> 8); data[1] = (byte)start;
        data[2] = (byte)(values.Length >> 8); data[3] = (byte)values.Length;
        data[4] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            data[5 + i * 2] = (byte)(values[i] >> 8);
            data[6 + i * 2] = (byte)values[i];
        }
        var body = ByteUtils.Combine(new[] { unit, fc }, data);
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }
}
