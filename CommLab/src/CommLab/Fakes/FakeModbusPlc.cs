using CommLab.Core;

namespace CommLab.Fakes;

/// <summary>
/// 假 Modbus 从站（PLC）：内存里放 4 个数据区，按 Modbus 规范处理 PDU。
/// RTU 入口负责 CRC 校验，TCP 入口由上层处理 MBAP 头，两者共用同一套 PDU 逻辑。
/// 面试点：Modbus 的"协议核心"就是 PDU（功能码 + 数据），RTU/TCP 只是给它套了不同的壳。
/// </summary>
public sealed class FakeModbusPlc
{
    public byte UnitId { get; }

    // Modbus 四大数据区（真实 PLC 里就是这些点）
    public bool[] Coils { get; } = new bool[100];            // 0xxxx 线圈：可读写，开关量输出
    public bool[] DiscreteInputs { get; } = new bool[100];   // 1xxxx 离散输入：只读开关量
    public ushort[] HoldingRegisters { get; } = new ushort[100]; // 3xxxx 保持寄存器：可读写
    public ushort[] InputRegisters { get; } = new ushort[100];   // 4xxxx 输入寄存器：只读

    public FakeModbusPlc(byte unitId = 1) => UnitId = unitId;

    /// <summary>RTU 入口：整帧 = 地址 + PDU + CRC16(小端)。CRC 错直接丢帧（真实从站行为）</summary>
    public byte[] ProcessRtu(byte[] frame)
    {
        if (frame.Length < 4) return Array.Empty<byte>();
        var crc = Crc.Crc16Modbus(frame.AsSpan(0, frame.Length - 2));
        if (frame[^2] != (byte)(crc & 0xFF) || frame[^1] != (byte)(crc >> 8))
            return Array.Empty<byte>(); // 校验失败：静默丢弃，上位机靠超时感知
        var pdu = ProcessPdu(frame[1], frame.AsSpan(2, frame.Length - 3));
        var body = ByteUtils.Combine(new[] { UnitId }, pdu);
        return ByteUtils.Combine(body, Crc.Crc16ModbusLittleEndian(body));
    }

    /// <summary>TCP 入口：MBAP 头由上层处理，这里只回 PDU；MBAP 组装见 M04 模块</summary>
    public byte[] ProcessTcp(byte unitId, ReadOnlySpan<byte> pduAndFc)
    {
        if (unitId != UnitId) return Array.Empty<byte>();
        return ProcessPdu(pduAndFc[0], pduAndFc[1..]);
    }

    /// <summary>核心：按功能码处理 PDU。返回 [fc, 数据...] 或异常响应 [fc|0x80, 异常码]</summary>
    public byte[] ProcessPdu(byte fc, ReadOnlySpan<byte> data)
    {
        ushort start, qty;
        try
        {
            switch (fc)
            {
                case 0x01: // 读线圈
                    start = (ushort)(data[0] << 8 | data[1]); qty = data[2];
                    if (start + qty > Coils.Length) return Exception(fc, 0x02);
                    var coilBytes = PackBits(Coils, start, qty);
                    return ByteUtils.Combine(new[] { fc, (byte)coilBytes.Length }, coilBytes);

                case 0x02: // 读离散输入
                    start = (ushort)(data[0] << 8 | data[1]); qty = data[2];
                    if (start + qty > DiscreteInputs.Length) return Exception(fc, 0x02);
                    var diBytes = PackBits(DiscreteInputs, start, qty);
                    return ByteUtils.Combine(new[] { fc, (byte)diBytes.Length }, diBytes);

                case 0x03: // 读保持寄存器
                    start = (ushort)(data[0] << 8 | data[1]); qty = (ushort)(data[2] << 8 | data[3]);
                    if (start + qty > HoldingRegisters.Length) return Exception(fc, 0x02);
                    return ByteUtils.Combine(new[] { fc, (byte)(qty * 2) }, PackWords(HoldingRegisters, start, qty));

                case 0x04: // 读输入寄存器
                    start = (ushort)(data[0] << 8 | data[1]); qty = (ushort)(data[2] << 8 | data[3]);
                    if (start + qty > InputRegisters.Length) return Exception(fc, 0x02);
                    return ByteUtils.Combine(new[] { fc, (byte)(qty * 2) }, PackWords(InputRegisters, start, qty));

                case 0x05: // 写单个线圈（0xFF00=ON, 0x0000=OFF，其他值=异常）
                    start = (ushort)(data[0] << 8 | data[1]);
                    ushort val = (ushort)(data[2] << 8 | data[3]);
                    if (val != 0xFF00 && val != 0x0000) return Exception(fc, 0x03);
                    if (start >= Coils.Length) return Exception(fc, 0x02);
                    Coils[start] = val == 0xFF00;
                    return data.ToArray(); // 原样回显

                case 0x06: // 写单个寄存器
                    start = (ushort)(data[0] << 8 | data[1]);
                    if (start >= HoldingRegisters.Length) return Exception(fc, 0x02);
                    HoldingRegisters[start] = (ushort)(data[2] << 8 | data[3]);
                    return data.ToArray(); // 原样回显

                case 0x0F: // 写多个线圈
                    start = (ushort)(data[0] << 8 | data[1]); qty = data[2];
                    if (start + qty > Coils.Length) return Exception(fc, 0x02);
                    UnpackBits(Coils, start, qty, data[4..]);
                    return new[] { fc, (byte)(start >> 8), (byte)start, (byte)(qty >> 8), (byte)qty };

                case 0x10: // 写多个寄存器
                    start = (ushort)(data[0] << 8 | data[1]); qty = (ushort)(data[2] << 8 | data[3]);
                    if (start + qty > HoldingRegisters.Length) return Exception(fc, 0x02);
                    for (int i = 0; i < qty; i++)
                        HoldingRegisters[start + i] = (ushort)(data[5 + i * 2] << 8 | data[6 + i * 2]);
                    return new[] { fc, (byte)(start >> 8), (byte)start, (byte)(qty >> 8), (byte)qty };

                default:
                    return Exception(fc, 0x01); // 非法功能码
            }
        }
        catch (IndexOutOfRangeException)
        {
            return Exception(fc, 0x03); // PDU 长度不对
        }
    }

    private static byte[] Exception(byte fc, byte code) => new[] { (byte)(fc | 0x80), code };

    private static byte[] PackBits(bool[] src, int start, int qty)
    {
        var bytes = new byte[(qty + 7) / 8];
        for (int i = 0; i < qty; i++)
            if (src[start + i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return bytes;
    }

    private static void UnpackBits(bool[] dst, int start, int qty, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < qty; i++)
            dst[start + i] = (bytes[i / 8] & (1 << (i % 8))) != 0;
    }

    private static byte[] PackWords(ushort[] src, int start, int qty)
    {
        var bytes = new byte[qty * 2];
        for (int i = 0; i < qty; i++)
        {
            bytes[i * 2] = (byte)(src[start + i] >> 8); // 寄存器值大端
            bytes[i * 2 + 1] = (byte)src[start + i];
        }
        return bytes;
    }
}
