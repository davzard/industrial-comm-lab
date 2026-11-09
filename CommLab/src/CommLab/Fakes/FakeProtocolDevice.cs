using CommLab.Core;

namespace CommLab.Fakes;

/// <summary>
/// 假温控器：按"设备协议文档"实现。
/// 协议（文档节选）：
///   帧格式: 68 68 | SEQ(1) | CMD(1) | LEN(1) | DATA(N) | CRC16(2,小端)
///   请求 CMD: 0x01 读温度 | 0x02 读状态 | 0x03 设温度设定值
///   响应 CMD: 请求 CMD | 0x80，DATA = [状态码1B][载荷]
///   状态码: 0x00 成功 | 0x01 设备忙 | 0x02 参数错误 | 0x80 通信错误(上位机侧)
///   温度: int16 大端 ×0.1℃；设定值范围 0~99.9℃
///   幂等: 同一 SEQ 的 0x03 只执行一次（重发安全）
/// </summary>
public sealed class FakeProtocolDevice
{
    private readonly Dictionary<byte, byte[]> _idempotentCache = new();
    public int SetpointApplyCount;   // 设定值真正生效次数（验证幂等）
    public bool DropNext;            // 模拟下一次请求"丢包"（设备没回）
    public float Setpoint = 25.0f;
    public float Temp => 25.5f;
    public byte AlarmMask = 0b00000010; // bit1 过流报警

    public byte[] Process(byte[] frame)
    {
        if (frame.Length < 7) return Array.Empty<byte>();
        var crc = Crc.Crc16Modbus(frame.AsSpan(0, frame.Length - 2));
        if (frame[^2] != (byte)(crc & 0xFF) || frame[^1] != (byte)(crc >> 8))
            return Array.Empty<byte>();

        if (DropNext) { DropNext = false; return Array.Empty<byte>(); } // 丢包：不回

        byte seq = frame[2], cmd = frame[3], len = frame[4];
        var data = frame.AsSpan(5, len);
        byte[] respData;

        switch (cmd)
        {
            case 0x01: // 读温度
                respData = ByteUtils.Combine(
                    new[] { (byte)0x00 },
                    EndianUtils.ShortToBytes((ushort)((int)(Temp * 10)), true));
                break;

            case 0x02: // 读状态：报警位 + 错误码
                respData = new byte[] { 0x00, AlarmMask, 0x00 };
                break;

            case 0x03: // 设设定值（幂等：同 SEQ 只执行一次）
                if (_idempotentCache.TryGetValue(seq, out var cached))
                    return BuildResponse(seq, cmd, cached);
                int sv = (data[0] << 8) | data[1];
                if (sv < 0 || sv > 999)
                    respData = new byte[] { 0x02, 0, 0 }; // 参数错误
                else
                {
                    Setpoint = sv / 10f;
                    SetpointApplyCount++;
                    respData = new byte[] { 0x00, (byte)(sv >> 8), (byte)sv };
                }
                _idempotentCache[seq] = respData;
                break;

            default:
                respData = new byte[] { 0x02, 0, 0 };
                break;
        }
        return BuildResponse(seq, cmd, respData);
    }

    private static byte[] BuildResponse(byte seq, byte reqCmd, byte[] respData)
    {
        byte[] body = { 0x68, 0x68, seq, (byte)(reqCmd | 0x80), (byte)respData.Length };
        var full = ByteUtils.Combine(body, respData);
        return ByteUtils.Combine(full, Crc.Crc16ModbusLittleEndian(full));
    }
}
