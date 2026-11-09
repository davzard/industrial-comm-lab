using CommLab.Core;

namespace CommLab.Fakes;

/// <summary>
/// 假 PLC：一个进程内模拟三种私有协议（西门子 S7 / 三菱 MC / 欧姆龙 FINS）。
/// 帧结构均按公开协议文档手工拼字节，不依赖任何 PLC 库。
/// </summary>
public sealed class FakePlc
{
    // ---------- 数据模型 ----------
    public byte[] Db1 = { 1, 2, 3, 4, 5, 6, 7, 8 };                                  // 西门子 DB1
    public ushort[] D = Enumerable.Range(0, 100).Select(i => (ushort)i).ToArray();       // 三菱 D0~D99
    public byte[] M = new byte[16];                                                     // 三菱 M 软元件(位)
    public ushort[] FinsD = Enumerable.Range(0, 100).Select(i => (ushort)(i * 2)).ToArray(); // 欧姆龙 D0~D99

    // ================= 西门子 S7（TPKT + COTP + S7comm） =================
    /// <summary>
    /// S7 帧布局（逐字节见 M06 注释）：
    ///   TPKT(4): 03 00 lenH lenL
    ///   COTP: 连接 F0(CR)/D0(DC)；数据 E0 00 00 00
    ///   S7请求头(10): 32 01 0000 seq(2) parLen(2) dataLen(2)
    ///   S7响应头(12): 32 03 0000 seq(2) parLen(2) dataLen(2) err(2)
    ///   参数(2): func(1) count(1)
    ///   item(12): 12 01 10 02 len(2) db(2) 84 addr(2) bit(1)
    ///   读响应数据项头(4): FF 02 00 dataLen(2) + data
    ///   偏移: func@18 len@24-25 db@26-27 area@28 start@29-30 bit@31 写数据@32..
    /// </summary>
    public byte[] ProcessS7(byte[] frame)
    {
        if (frame.Length < 8 || frame[0] != 0x03) return Array.Empty<byte>();
        byte cotp = frame[4];

        // 1) COTP 连接请求 CR → 回连接确认 DC
        if (cotp == 0xF0)
        {
            byte[] dc =
            {
                0x03, 0x00, 0x00, 0x16,
                0xD0, 0x11, 0x00, 0x00, 0x00, 0x01, 0x00,
                0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x00, 0xB0
            };
            return dc;
        }
        if (cotp != 0xE0) return Array.Empty<byte>(); // 只处理数据帧 DT

        // 2) S7 Job（读/写）
        byte func = frame[18];
        int len = (frame[24] << 8) | frame[25];
        int start = (frame[29] << 8) | frame[30];

        if (func == 0x04) // 读
        {
            var data = Db1.AsSpan(start, len).ToArray();
            byte[] head =
            {
                0x03, 0x00, 0x00, 0x00, // TPKT（长度下面填）
                0xE0, 0x00, 0x00, 0x00,
                0x32, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, (byte)(4 + data.Length), 0x00, 0x00,
                0x04, 0x01,
                0xFF, 0x02, 0x00, (byte)data.Length
            };
            int total = head.Length + data.Length;
            head[2] = (byte)(total >> 8);
            head[3] = (byte)total;
            return ByteUtils.Combine(head, data);
        }
        if (func == 0x05) // 写
        {
            for (int i = 0; i < len; i++) Db1[start + i] = frame[32 + i];
            byte[] head =
            {
                0x03, 0x00, 0x00, 0x00,
                0xE0, 0x00, 0x00, 0x00,
                0x32, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x01, 0xFF
            };
            int total = head.Length;
            head[2] = (byte)(total >> 8);
            head[3] = (byte)total;
            return head;
        }
        return Array.Empty<byte>();
    }

    // ================= 三菱 MC 协议（短帧） =================
    /// <summary>
    /// MC 短帧: P(1)=0x50 0x00 | 网络号(4) | 请求数据长度(2) | 命令(1) 子命令(1) | 请求数据
    /// 命令 0x01 读 / 0x10 写；子命令 0x04 读设备 / 0x05 写设备
    /// 请求数据: 设备类型(1) 设备号(3) 数量(2) [写: 数据]
    /// 响应数据前 2 字节 = 结束代码（0x00 正常）
    /// </summary>
    public byte[] ProcessMc(byte[] frame)
    {
        if (frame.Length < 12 || frame[0] != 0x50 || frame[1] != 0x00)
            return Array.Empty<byte>();
        int reqLen = (frame[6] << 8) | frame[7];
        byte cmd = frame[8], sub = frame[9];
        var req = frame.AsSpan(10, reqLen);

        ushort[] words = Array.Empty<ushort>();
        if (cmd == 0x01 && sub == 0x04) // 读设备
        {
            byte devType = req[0];
            int devNo = (req[1] << 16) | (req[2] << 8) | req[3];
            int n = (req[4] << 8) | req[5];
            words = new ushort[n];
            for (int i = 0; i < n; i++)
                words[i] = devType == 0x00
                    ? D[devNo + i]
                    : (ushort)((M[devNo / 8 + i / 8] >> (devNo + i) % 8) & 1);
        }
        else if (cmd == 0x10 && sub == 0x05) // 写设备
        {
            byte devType = req[0];
            int devNo = (req[1] << 16) | (req[2] << 8) | req[3];
            int n = (req[4] << 8) | req[5];
            for (int i = 0; i < n; i++)
                if (devType == 0x00)
                    D[devNo + i] = (ushort)((req[6 + i * 2] << 8) | req[7 + i * 2]);
        }
        else
            return BuildMc(0x01, 0x04, new byte[] { 0x03 }); // 结束代码 0x03 = 命令不支持

        var data = new byte[2 + words.Length * 2];
        data[0] = 0x00; data[1] = 0x00; // 正常结束
        for (int i = 0; i < words.Length; i++)
        {
            data[2 + i * 2] = (byte)(words[i] >> 8);
            data[3 + i * 2] = (byte)words[i];
        }
        return BuildMc(cmd, sub, data);
    }

    private static byte[] BuildMc(byte cmd, byte sub, byte[] data)
    {
        byte[] head = { 0x50, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)(data.Length >> 8), (byte)data.Length, cmd, sub };
        return ByteUtils.Combine(head, data);
    }

    // ================= 欧姆龙 FINS =================
    /// <summary>
    /// FINS 帧: 50 0A lenH lenL | 网络号(4) | 命令(1) 子命令(1) | 源节点(2) 目标节点(2)
    ///         | 错误1 错误2 | 请求数据
    /// 偏移: cmd@8 sub@9 area@16 devNo@17..19 n@20..21 写数据@22..
    /// 读设备: 01 10；写设备: 01 14
    /// </summary>
    public byte[] ProcessFins(byte[] frame)
    {
        if (frame.Length < 22 || frame[0] != 0x50 || frame[1] != 0x0A)
            return Array.Empty<byte>();
        byte cmd = frame[8], sub = frame[9];
        byte area = frame[16];
        int devNo = (frame[17] << 16) | (frame[18] << 8) | frame[19];
        int n = (frame[20] << 8) | frame[21];

        if (cmd == 0x01 && sub == 0x10) // 读设备
        {
            var data = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                ushort v = area == 0x00 ? FinsD[devNo + i] : (ushort)0;
                data[i * 2] = (byte)(v >> 8);
                data[i * 2 + 1] = (byte)v;
            }
            return BuildFins(0x00, 0x00, data);
        }
        if (cmd == 0x01 && sub == 0x14) // 写设备
        {
            for (int i = 0; i < n; i++)
                FinsD[devNo + i] = (ushort)((frame[22 + i * 2] << 8) | frame[23 + i * 2]);
            return BuildFins(0x00, 0x00, Array.Empty<byte>());
        }
        return BuildFins(0x01, 0x02, Array.Empty<byte>()); // 错误
    }

    private static byte[] BuildFins(byte err1, byte err2, byte[] data)
    {
        // 响应: 50 0B lenH lenL | 网络号(4) | 01 10 | 源节点(2) 目标节点(2) | err1 err2 | data
        int total = 16 + data.Length;
        byte[] head =
        {
            0x50, 0x0B, (byte)(total >> 8), (byte)total,
            0x00, 0x00, 0x01, 0x00, 0x01, 0x10, 0x00, 0x01, 0x00, 0x01, err1, err2
        };
        return ByteUtils.Combine(head, data);
    }
}
