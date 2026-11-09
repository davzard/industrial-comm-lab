namespace CommLab.Core;

/// <summary>
/// 大小端（字节序）转换。
/// 面试点：
///  - 大端（Big-Endian / 网络序）：高位字节在前，如 0x1234 → [12 34]
///  - 小端（Little-Endian / x86 本机序）：低位字节在前，如 0x1234 → [34 12]
///  - Modbus 寄存器内部、PLC 字序（ABCD/CDBA/BA DC/DCBA）都源于此，是现场最经典的坑。
/// </summary>
public static class EndianUtils
{
    public static byte[] ShortToBytes(ushort v, bool bigEndian)
        => bigEndian ? new[] { (byte)(v >> 8), (byte)v }
                     : new[] { (byte)v, (byte)(v >> 8) };

    public static ushort BytesToShort(ReadOnlySpan<byte> b, bool bigEndian)
        => bigEndian ? (ushort)((b[0] << 8) | b[1])
                     : (ushort)((b[1] << 8) | b[0]);

    public static byte[] IntToBytes(uint v, bool bigEndian)
    {
        var b = new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        if (!bigEndian) Array.Reverse(b);
        return b;
    }

    public static uint BytesToInt(ReadOnlySpan<byte> b, bool bigEndian)
    {
        var arr = b.ToArray();
        if (!bigEndian) Array.Reverse(arr);
        return (uint)((arr[0] << 24) | (arr[1] << 16) | (arr[2] << 8) | arr[3]);
    }

    /// <summary>float（IEEE 754 单精度）按指定字节序转字节</summary>
    public static byte[] FloatToBytes(float v, bool bigEndian)
    {
        var b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian == bigEndian) Array.Reverse(b);
        return b;
    }

    public static float BytesToFloat(ReadOnlySpan<byte> b, bool bigEndian)
    {
        var arr = b.ToArray();
        if (BitConverter.IsLittleEndian == bigEndian) Array.Reverse(arr);
        return BitConverter.ToSingle(arr, 0);
    }
}
