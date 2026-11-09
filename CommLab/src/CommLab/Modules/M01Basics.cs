using CommLab.Core;

namespace CommLab.Modules;

/// <summary>
/// M01 基础：二进制 / 十六进制 / 字节数组 / 数据类型转换 / 大小端 / 校验和 / CRC16
/// 这是所有通信的地基：协议文档里的每一个 "0x" 数字，最终都是字节数组。
/// </summary>
public sealed class M01Basics : IModule
{
    public string Title => "M01 基础：字节、进制、大小端、校验";

    public Task RunAsync()
    {
        Console.WriteLine("\n=== 1. 二进制 / 十六进制 / 字节数组 ===");
        // 一个 ushort 在内存里就是 2 个字节；协议文档写 0x1234，线上就是 12 34 或 34 12
        ushort value = 0x1234;
        Console.WriteLine($"  ushort 0x1234 = 十进制 {value} = 二进制 {Convert.ToString(value, 2).PadLeft(16, '0')}");
        Console.WriteLine($"  大端字节: {ByteUtils.Hex(EndianUtils.ShortToBytes(value, bigEndian: true))}   小端字节: {ByteUtils.Hex(EndianUtils.ShortToBytes(value, bigEndian: false))}");

        // 位运算：Modbus 线圈位、状态字解析必备（第 3 位 = (v >> 3) & 1）
        ushort status = 0b0000_0000_0101_1000;
        Console.WriteLine($"  状态字 0x{status:X4}：bit3={(status >> 3) & 1} bit4={(status >> 4) & 1} bit5={(status >> 5) & 1}");

        Console.WriteLine("\n=== 2. 数据类型转换（按字节宽度） ===");
        // 协议里 "float 温度" = 4 字节 IEEE754；"int16 转速" = 2 字节
        float temp = 25.5f;
        var tempBytes = EndianUtils.FloatToBytes(temp, bigEndian: true);
        Console.WriteLine($"  float 25.5 → 大端 4 字节: {ByteUtils.Hex(tempBytes)}");
        Console.WriteLine($"  读回: {EndianUtils.BytesToFloat(tempBytes, bigEndian: true)}");

        // 缩放系数：PLC 常用 int16 × 0.1 表示一位小数（如 255 → 25.5 ℃）
        short raw = 255;
        double scaled = raw * 0.1;
        Console.WriteLine($"  原始 int16 {raw} × 0.1 = {scaled} ℃（PLC 最经典的「整数存小数」手法）");

        // 有符号 vs 无符号：0xFF 是 -1（int8）还是 255（byte/uint8）？看协议定义
        byte b = 0xFF;
        Console.WriteLine($"  0xFF 按 byte = {b}，按 sbyte = {(sbyte)b}");
        Console.WriteLine($"  0x8000 按 ushort = {0x8000}，按 short = {unchecked((short)0x8000)}（负数！）");

        Console.WriteLine("\n=== 3. 大小端与 Modbus 字序 ===");
        uint v32 = 0x12345678;
        Console.WriteLine($"  0x12345678 大端: {ByteUtils.Hex(EndianUtils.IntToBytes(v32, true))}");
        Console.WriteLine($"  0x12345678 小端: {ByteUtils.Hex(EndianUtils.IntToBytes(v32, false))}");
        // 32 位数据跨两个寄存器时的 4 种字序（面试高频）：
        // ABCD=大端  CDBA=小端  BADC=字节交换  DCBA=字交换（大端字序内小端字节）
        Console.WriteLine("  32 位 0x12345678 的四种字序：");
        Console.WriteLine($"    ABCD(大端)     : {ByteUtils.Hex(EndianUtils.IntToBytes(v32, true))}");
        Console.WriteLine($"    CDBA(小端)     : {ByteUtils.Hex(EndianUtils.IntToBytes(v32, false))}");
        Console.WriteLine($"    BADC(字节交换) : 34 12 78 56");
        Console.WriteLine($"    DCBA(字交换)   : 56 78 12 34");
        Console.WriteLine("  → 现场解析错数据，90% 是字序没对，先问设备手册要字节序。");

        Console.WriteLine("\n=== 4. 校验和与 CRC16 ===");
        var frame = ByteUtils.FromHex("01 03 00 00 00 0A"); // Modbus 读 10 个保持寄存器的请求体
        Console.WriteLine($"  报文: {ByteUtils.Hex(frame)}");
        Console.WriteLine($"  和校验(Sum) : 0x{Crc.ChecksumSum(frame):X2}");
        Console.WriteLine($"  异或校验(XOR): 0x{Crc.ChecksumXor(frame):X2}");
        var crc = Crc.Crc16ModbusLittleEndian(frame);
        Console.WriteLine($"  CRC16/MODBUS: 0x{Crc.Crc16Modbus(frame):X4}，帧尾小端存放: {ByteUtils.Hex(crc)}");
        Console.WriteLine($"  完整 RTU 帧: {ByteUtils.Hex(ByteUtils.Combine(frame, crc))}");

        // CRC 的妙用：整帧（含 CRC 小端）再算一次 CRC 恒为 0
        var fullFrame = ByteUtils.Combine(frame, crc);
        Console.WriteLine($"  验证：整帧再算 CRC = 0x{Crc.Crc16Modbus(fullFrame):X4}（恒为 0，接收端就靠这个验错）");

        Console.WriteLine("\n=== 自测 ===");
        SelfTest.CheckEq(0x4B37, Crc.Crc16Modbus("123456789"u8), "CRC16/MODBUS 标准校验向量 \"123456789\"");
        SelfTest.CheckEq(0x14, Crc.ChecksumSum(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x0A }), "和校验");
        SelfTest.CheckEq(0x00, Crc.ChecksumXor(new byte[] { 0x01, 0x02, 0x03, 0x01, 0x02, 0x03 }), "异或校验");
        SelfTest.CheckEq(0x1234, EndianUtils.BytesToShort(new byte[] { 0x12, 0x34 }, bigEndian: true), "大端读 ushort");
        SelfTest.CheckEq(0x3412, EndianUtils.BytesToShort(new byte[] { 0x12, 0x34 }, bigEndian: false), "小端读 ushort");
        SelfTest.CheckEq(0x12345678u, EndianUtils.BytesToInt(new byte[] { 0x78, 0x56, 0x34, 0x12 }, bigEndian: false), "小端读 uint");
        SelfTest.CheckEq(25.5f, EndianUtils.BytesToFloat(EndianUtils.FloatToBytes(25.5f, true), true), "float 往返转换");
        // 01 03 00 00 00 0A 是 Modbus 官方手册的经典示例帧，CRC 为 C5 CD
        SelfTest.CheckEq(new byte[] { 0xC5, 0xCD }, Crc.Crc16ModbusLittleEndian(ByteUtils.FromHex("01 03 00 00 00 0A")), "Modbus RTU 帧 CRC");
        return Task.CompletedTask;
    }
}
