namespace CommLab.Core;

/// <summary>
/// 校验算法。
/// 面试点：
///  - 校验和（Sum/XOR）：实现简单，只能发现单字节错误，用于对可靠性要求不高的私有协议。
///  - CRC16/MODBUS：多项式 0x8005（反射后 0xA001），初值 0xFFFF，无反射无终值异或。
///    标准校验向量："123456789" → 0x4B37。
///  - Modbus RTU 的 CRC 在报文末尾按【小端】（低字节在前）存放。
///  - 性质：把 CRC（小端）追加到报文后，对整帧再算一次 CRC16 结果恒为 0 —— 接收端就靠这个验错。
/// </summary>
public static class Crc
{
    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>计算 CRC16 并按小端（低字节在前）返回 2 字节，可直接追加到 RTU 帧尾</summary>
    public static byte[] Crc16ModbusLittleEndian(ReadOnlySpan<byte> data)
    {
        var crc = Crc16Modbus(data);
        return new[] { (byte)(crc & 0xFF), (byte)(crc >> 8) };
    }

    /// <summary>和校验：所有字节求和取低 8 位（温度传感器等私有协议常见）</summary>
    public static byte ChecksumSum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (var b in data) sum += b;
        return (byte)(sum & 0xFF);
    }

    /// <summary>异或校验（CAN 报文、部分私有协议常见）</summary>
    public static byte ChecksumXor(ReadOnlySpan<byte> data)
    {
        byte x = 0;
        foreach (var b in data) x ^= b;
        return x;
    }
}
