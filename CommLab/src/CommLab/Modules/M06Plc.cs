using CommLab.Core;
using CommLab.Fakes;

namespace CommLab.Modules;

/// <summary>
/// M06 PLC 专用协议：西门子 S7 / 三菱 MC / 欧姆龙 FINS 手工组帧 + 假 PLC。
/// 面试重点：各家私有协议与端口、点位表、S7 的 TPKT/COTP/S7comm 三层结构。
/// </summary>
public sealed class M06Plc : IModule
{
    public string Title => "M06 PLC 通信（S7/MC/FINS）";

    public Task RunAsync()
    {
        var plc = new FakePlc();

        Console.WriteLine("=== 1. PLC 通信总览 ===");
        Console.WriteLine("  西门子 S7   : S7comm over TCP 102（TPKT+COTP+S7comm 三层）");
        Console.WriteLine("  三菱 MC     : 短帧 TCP 6000 / 长帧 UDP 5007");
        Console.WriteLine("  欧姆龙 FINS : UDP 9600（以太网 FINS）");
        Console.WriteLine("  倍福 ADS    : TCP 4889（NetId 寻址，TwinCAT 生态）");
        Console.WriteLine("  点位表(Point Table) = 上位机与 PLC 之间的'合同'：");
        Console.WriteLine("    点位名 | 协议地址 | 数据类型 | 读写 | 缩放系数");

        Console.WriteLine();
        Console.WriteLine("=== 2. 西门子 S7：TPKT + COTP + S7comm ===");
        Console.WriteLine("  三层结构：TPKT(4B 总长度) → COTP(ISO 连接) → S7comm(Job/Ack)");
        Console.WriteLine("  必须先 COTP 连接请求(CR) → 连接确认(DC)，才能发数据帧(DT)。");

        // 2.1 COTP 连接请求 CR（22 字节）
        byte[] cr =
        {
            0x03, 0x00, 0x00, 0x16,             // TPKT: 版本3, 总长度22
            0xF0, 0x11, 0x00, 0x01, 0x01,       // COTP CR: 0xF0=连接请求, 长度17, TPDU编号1, 参数长1
            0x00, 0x00,                          // 保留
            0xC0, 0x01, 0x0A,                     // 参数: 最大 PDU 长度 1024
            0xC1, 0x02, 0x01, 0x00,         //   源 TSAP = 0x0100
            0xC2, 0x02, 0x00, 0xB0          //   目标 TSAP = 0x00B0（机架0/槽2）
        };
        ByteUtils.PrintFrame("COTP CR 连接请求", cr);
        var dc = plc.ProcessS7(cr);
        ByteUtils.PrintFrame("COTP DC 连接确认", dc);

        // 2.2 读 DB1 起始 0 共 4 字节（32 字节请求）
        // 布局: TPKT(4) + DT(4) + S7请求头(10) + 参数(2) + item(12)
        byte[] readReq =
        {
            0x03, 0x00, 0x00, 0x20,        // TPKT: 总长度 0x20=32
            0xE0, 0x00, 0x00, 0x00,        // COTP DT 数据帧
            0x32, 0x01, 0x00, 0x00, 0x00,  // S7请求头(10): 0x32=协议标识, PDU类型=请求
            0x01, 0x00, 0x0E, 0x00, 0x00,  //   序列号1, 参数长14, 数据长0
            0x04, 0x01,                     // 功能码 04 读, 1 个 item
            0x12, 0x01, 0x10, 0x02,        // item: 长12, 标志, 传输长度=字节, 返回码
            0x00, 0x04,                     //   读长度 = 4 字节
            0x00, 0x01,                     //   DB 号 = 1
            0x84,                           //   区域 = DB
            0x00, 0x00,                     //   起始地址 = 0
            0x00                            //   位偏移 = 0
        };
        ByteUtils.PrintFrame("S7 读 DB1 起始0 共4字节", readReq);
        var readResp = plc.ProcessS7(readReq);
        ByteUtils.PrintFrame("S7 读响应", readResp);
        // 响应布局: TPKT(4)+DT(4)+S7响应头(12)+参数(2)+数据项头(4)+数据 → 数据在偏移 26
        int s7Len = (readResp[24] << 8) | readResp[25];
        var s7Data = readResp.AsSpan(26, s7Len).ToArray();
        Console.WriteLine($"  解析: 数据长度={s7Len}, DB1 前 4 字节 = {string.Join(' ', s7Data.Select(b => b.ToString()))}");

        // 2.3 写 DB1 偏移 2 共 2 字节（12 34），再读回验证
        byte[] writeReq =
        {
            0x03, 0x00, 0x00, 0x22,        // TPKT: 总长度 0x22=34
            0xE0, 0x00, 0x00, 0x00,
            0x32, 0x01, 0x00, 0x00, 0x00,  // S7请求头(10)
            0x02, 0x00, 0x0E, 0x00, 0x02,  //   序列号2, 参数长14, 数据长2
            0x05, 0x01,                     // 功能码 05 写, 1 个 item
            0x12, 0x01, 0x10, 0x02,
            0x00, 0x02,                     //   写长度 = 2 字节
            0x00, 0x01,                     //   DB 号 = 1
            0x84, 0x00, 0x02, 0x00,         //   区域=DB, 起始=2, 位=0
            0x12, 0x34                      // 要写入的数据
        };
        ByteUtils.PrintFrame("S7 写 DB1 偏移2 共2字节", writeReq);
        var writeAck = plc.ProcessS7(writeReq);
        ByteUtils.PrintFrame("S7 写确认（末字节 FF = 成功）", writeAck);
        var readBack = plc.ProcessS7(readReq);
        var s7Back = readBack.AsSpan(26, 4).ToArray();
        Console.WriteLine($"  读回 DB1 前 4 字节 = {string.Join(' ', s7Back.Select(b => b.ToString()))}（第 3、4 字节已变 18 52）");

        Console.WriteLine();
        Console.WriteLine("=== 3. 三菱 MC 短帧 ===");
        Console.WriteLine("  帧: P(2)=50 00 | 网络号(4) | 请求长度(2) | 命令(1) 子命令(1) | 数据");
        Console.WriteLine("  命令 01 读 / 10 写；子命令 04 读设备 / 05 写设备");

        // 3.1 读 D0 起 3 个字
        byte[] mcRead =
        {
            0x50, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x06,
            0x01, 0x04,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x03  // 类型00=D 设备号0 数量3
        };
        ByteUtils.PrintFrame("MC 读 D0×3", mcRead);
        var mcResp = plc.ProcessMc(mcRead);
        ByteUtils.PrintFrame("MC 读响应", mcResp);
        int mcEndCode = (mcResp[10] << 8) | mcResp[11];
        Console.WriteLine($"  结束代码 = 0x{mcEndCode:X4}（0x0000 正常）");
        for (int i = 0; i < 3; i++)
            Console.WriteLine($"    D{i} = 0x{mcResp[12 + i * 2]:X2}{mcResp[13 + i * 2]:X2} = {(mcResp[12 + i * 2] << 8) | mcResp[13 + i * 2]}");

        // 3.2 写 D10 = 1234，读回验证
        byte[] mcWrite =
        {
            0x50, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x08,
            0x10, 0x05,
            0x00, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x04, (byte)0xD2 // D10 = 0x04D2
        };
        ByteUtils.PrintFrame("MC 写 D10=1234", mcWrite);
        var mcWriteResp = plc.ProcessMc(mcWrite);
        Console.WriteLine($"  写结束代码 = 0x{(mcWriteResp[10] << 8) | mcWriteResp[11]:X4}（0x0000 正常）");
        byte[] mcReadBack =
        {
            0x50, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x06,
            0x01, 0x04,
            0x00, 0x00, 0x00, 0x0A, 0x00, 0x01  // D10 读 1 个字
        };
        var mcBack = plc.ProcessMc(mcReadBack);
        int d10 = (mcBack[12] << 8) | mcBack[13];
        Console.WriteLine($"  读回 D10 = {d10}");

        Console.WriteLine();
        Console.WriteLine("=== 4. 欧姆龙 FINS ===");
        Console.WriteLine("  帧: 50 0A len(2) | 网络号(4) | 命令(1) 子命令(1) | 源节点(2) 目标节点(2) | 错误(2) | 数据");
        Console.WriteLine("  读设备 01 10 / 写设备 01 14；数据 = 区域码(1) 设备号(3) 数量(2) [写:数据]");

        // 4.1 读 D0 起 2 个字
        byte[] finsRead =
        {
            0x50, 0x0A, 0x00, 0x16,
            0x00, 0x00, 0x01, 0x00,
            0x01, 0x10,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x02  // 区域00=D 设备号0 数量2
        };
        ByteUtils.PrintFrame("FINS 读 D0×2", finsRead);
        var finsResp = plc.ProcessFins(finsRead);
        ByteUtils.PrintFrame("FINS 读响应", finsResp);
        int finsErr = (finsResp[14] << 8) | finsResp[15];
        Console.WriteLine($"  错误码 = 0x{finsErr:X4}（0x0000 正常）");
        for (int i = 0; i < 2; i++)
            Console.WriteLine($"    D{i} = {(finsResp[16 + i * 2] << 8) | finsResp[17 + i * 2]}");

        // 4.2 写 D10 = 1500，读回验证
        byte[] finsWrite =
        {
            0x50, 0x0A, 0x00, 0x18,
            0x00, 0x00, 0x01, 0x00,
            0x01, 0x14,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x05, (byte)0xDC // D10 = 0x05DC
        };
        ByteUtils.PrintFrame("FINS 写 D10=1500", finsWrite);
        var finsWriteResp = plc.ProcessFins(finsWrite);
        Console.WriteLine($"  写错误码 = 0x{(finsWriteResp[14] << 8) | finsWriteResp[15]:X4}（0x0000 正常）");
        byte[] finsReadBack =
        {
            0x50, 0x0A, 0x00, 0x16,
            0x00, 0x00, 0x01, 0x00,
            0x01, 0x10,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x0A, 0x00, 0x01  // D10 读 1 个字
        };
        var finsBack = plc.ProcessFins(finsReadBack);
        int finsD10 = (finsBack[16] << 8) | finsBack[17];
        Console.WriteLine($"  读回 D10 = {finsD10}");

        Console.WriteLine();
        Console.WriteLine("=== 5. 点位表（上位机与 PLC 的合同） ===");
        Console.WriteLine("  同一个物理量，在不同 PLC 上地址不同，上位机用点位表屏蔽差异：");
        Console.WriteLine("  ┌──────────┬──────────────┬──────────────┬──────────────┬──────┐");
        Console.WriteLine("  │ 点位名   │ 西门子 S7    │ 三菱 MC      │ 欧姆龙 FINS  │ 类型 │");
        Console.WriteLine("  ├──────────┼──────────────┼──────────────┼──────────────┼──────┤");
        Console.WriteLine("  │ 温度     │ DB1.DBD0     │ D0           │ D0           │ INT  │");
        Console.WriteLine("  │ 设定值   │ DB1.DBW2     │ D10          │ D10          │ INT  │");
        Console.WriteLine("  │ 运行状态 │ DB1.DBX4.0   │ M0           │ CIO0.0       │ BOOL │");
        Console.WriteLine("  └──────────┴──────────────┴──────────────┴──────────────┴──────┘");
        Console.WriteLine("  工程上点位表常做成 CSV/Excel 导入，代码里用字典<点位名, 地址>。");

        Console.WriteLine();
        Console.WriteLine("=== 6. 倍福 ADS 概念（只讲不实现） ===");
        Console.WriteLine("  ADS = Automation Device Specification，倍福 TwinCAT 的通信协议。");
        Console.WriteLine("  端口 4889；用 NetId(如 192.168.0.1.1.1) 寻址设备；");
        Console.WriteLine("  分 AMS 网关(管理) 和 ADS 路由(数据)；上位机一般用 ADS .NET 库。");

        Console.WriteLine();
        Console.WriteLine("=== 7. 自测 ===");
        SelfTest.CheckEq((byte)0xD0, dc[4], "S7 COTP 连接确认 PDUType=0xD0");
        SelfTest.CheckEq(4, s7Len, "S7 读响应数据长度=4");
        SelfTest.CheckEq((byte)1, s7Data[0], "S7 读 DB1[0]=1");
        SelfTest.CheckEq((byte)4, s7Data[3], "S7 读 DB1[3]=4");
        SelfTest.CheckEq((byte)0x12, s7Back[2], "S7 写后读回 DB1[2]=0x12");
        SelfTest.CheckEq((byte)0x34, s7Back[3], "S7 写后读回 DB1[3]=0x34");
        SelfTest.CheckEq(0, mcEndCode, "MC 读结束代码 0x0000");
        SelfTest.CheckEq(0, (mcResp[12] << 8) | mcResp[13], "MC 读 D0=0");
        SelfTest.CheckEq(1, (mcResp[14] << 8) | mcResp[15], "MC 读 D1=1");
        SelfTest.CheckEq(1234, d10, "MC 写 D10=1234 读回");
        SelfTest.CheckEq(0, finsErr, "FINS 读错误码 0x0000");
        SelfTest.CheckEq(0, (finsResp[16] << 8) | finsResp[17], "FINS 读 D0=0");
        SelfTest.CheckEq(2, (finsResp[18] << 8) | finsResp[19], "FINS 读 D1=2");
        SelfTest.CheckEq(1500, finsD10, "FINS 写 D10=1500 读回");

        Console.WriteLine();
        Console.WriteLine("=== 8. 面试话术 ===");
        Console.WriteLine("  接 PLC 三步：1) 确认品牌+协议+端口 2) 拿点位表 3) 用官方库或手拼帧联调。");
        Console.WriteLine("  S7 记住三层 TPKT/COTP/S7comm，必须先连接再读写；MC/FINS 注意结束代码。");
        return Task.CompletedTask;
    }
}
