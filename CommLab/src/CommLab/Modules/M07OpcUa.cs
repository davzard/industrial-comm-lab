using CommLab.Core;
using CommLab.Fakes;
using Opc.Ua;

namespace CommLab.Modules;

/// <summary>
/// M07 OPC UA：真实 Opc.Ua 数据模型（NodeId/DataValue/Variant/StatusCode）+ 进程内假服务端。
/// 面试重点：地址空间与 NodeId、读写、订阅（死区/推送）、安全策略与 X.509 证书。
/// </summary>
public sealed class M07OpcUa : IModule
{
    public string Title => "M07 OPC UA";

    public Task RunAsync()
    {
        var server = new FakeOpcUaServer();

        Console.WriteLine("=== 1. OPC UA 总览 ===");
        Console.WriteLine("  OPC UA = 平台无关、带类型、带安全标准的工业数据访问协议（IEC 62541）。");
        Console.WriteLine("  与 Modbus 的本质区别：");
        Console.WriteLine("    Modbus : 只有'无类型的寄存器'，无安全，语义全靠点位表约定");
        Console.WriteLine("    OPC UA : 地址空间(树形节点) + 类型系统 + 安全(签名/加密) + 服务(读/写/订阅/方法)");
        Console.WriteLine("  端点 URL 形如 opc.tcp://主机:4840（4840 是默认端口）");
        Console.WriteLine("  历史：OPC DA 基于 COM/DCOM 只能跑 Windows；OPC UA 跨平台，可走 HTTPS，");
        Console.WriteLine("        所以能上云、上 Web——这是面试常考的'UA 相对 DA 的进步'。");

        Console.WriteLine();
        Console.WriteLine("=== 2. 节点与 NodeId ===");
        Console.WriteLine("  地址空间是一棵树，每个节点有唯一 NodeId 和 NodeClass：");
        Console.WriteLine("    NodeClass: Object(对象) / Variable(变量) / Method(方法) / DataType / View / ReferenceType");
        Console.WriteLine("  NodeId 两种形式：");

        var idStr = NodeId.Parse("ns=2;s=Machine1.Temperature"); // 字符串标识
        var idNum = new NodeId(1234, 2);                          // 数字标识
        Console.WriteLine($"    字符串 ID: {idStr}   (NamespaceIndex={idStr.NamespaceIndex})");
        Console.WriteLine($"    数字 ID  : {idNum}");
        Console.WriteLine("  ns=0 是标准命名空间，ns=1 是服务端自身，ns>=2 是厂商自定义。");
        Console.WriteLine("  本工程用官方 OPCFoundation.NetStandard.Opc.Ua 2.0.1 的真实类型，");
        Console.WriteLine("  服务端是进程内假实现（地址空间=字典，不走网络编码层）。");

        Console.WriteLine();
        Console.WriteLine("=== 3. 读与写（DataValue / Variant / StatusCode） ===");
        Console.WriteLine("  读返回 DataValue = { 值(Variant) + 状态(StatusCode) + 源/服务器时间戳 }");
        Console.WriteLine("  Variant 是'类型化盒子'：值 + 类型，这是 OPC UA 带类型的核心。");

        var tempId = NodeId.Parse("ns=2;s=Machine1.Temperature");
        var setpointId = NodeId.Parse("ns=2;s=Machine1.Setpoint");
        var runningId = NodeId.Parse("ns=2;s=Machine1.Running");
        var modeId = NodeId.Parse("ns=2;s=Machine1.Mode");
        var outputId = NodeId.Parse("ns=2;s=Machine1.TotalOutput");

        var dvTemp = server.Read(tempId);
        dvTemp.WrappedValue.TryGetValue(out double temp);
        Console.WriteLine($"  Read(Temperature)  = {temp}  状态=0x{dvTemp.StatusCode.Code:X8} (Good)");

        var dvOut = server.Read(outputId);
        dvOut.WrappedValue.TryGetValue(out int output);
        Console.WriteLine($"  Read(TotalOutput)  = {output} (int)  状态=0x{dvOut.StatusCode.Code:X8}");

        var dvRun = server.Read(runningId);
        dvRun.WrappedValue.TryGetValue(out bool running);
        Console.WriteLine($"  Read(Running)      = {running} (bool)");

        var dvMode = server.Read(modeId);
        dvMode.WrappedValue.TryGetValue(out string mode);
        Console.WriteLine($"  Read(Mode)         = {mode} (string)");

        // 读未知节点 → BadNodeIdUnknown
        var dvUnknown = server.Read(NodeId.Parse("ns=2;s=NoSuchNode"));
        Console.WriteLine($"  Read(未知节点)     状态=0x{dvUnknown.StatusCode.Code:X8} (BadNodeIdUnknown)");

        // 写：成功 / 只读拒绝
        var scWrite = server.Write(setpointId, new Variant(55.0));
        Console.WriteLine($"  Write(Setpoint, 55.0) 状态=0x{scWrite.Code:X8} (Good)");
        var dvSp = server.Read(setpointId);
        dvSp.WrappedValue.TryGetValue(out double setpoint);
        Console.WriteLine($"  读回 Setpoint = {setpoint}");

        var scRo = server.Write(tempId, new Variant(99.0));
        Console.WriteLine($"  Write(Temperature, 99.0) 状态=0x{scRo.Code:X8} (BadNotWritable，只读节点)");

        var scUnknown = server.Write(NodeId.Parse("ns=2;s=NoSuchNode"), new Variant(1));
        Console.WriteLine($"  Write(未知节点) 状态=0x{scUnknown.Code:X8} (BadNodeIdUnknown)");

        Console.WriteLine();
        Console.WriteLine("=== 4. 订阅（Subscription / MonitoredItem / 死区） ===");
        Console.WriteLine("  轮询浪费带宽：几百个点位每 100ms 全量读一遍。");
        Console.WriteLine("  订阅 = 服务端每 PublishInterval 检查一次，只有'变化量 >= 死区'才推送。");
        Console.WriteLine("  死区(Deadband)：数值型可设绝对死区，变化太小不推——工业现场标配。");

        int pushCount = 0;
        double lastPushed = 0;
        var miTemp = server.CreateMonitoredItem(tempId, deadband: 1.0);
        miTemp.Changed += (id, dv) =>
        {
            dv.WrappedValue.TryGetValue(out double v);
            lastPushed = v;
            pushCount++;
            Console.WriteLine($"  [推送] {id} = {v}");
        };

        server.SimulateProcess(46.0); // 45.5→46.0 变化 0.5 < 死区 1.0
        server.Publish();
        Console.WriteLine($"  45.5→46.0 变化 0.5 < 死区 1.0 → 不推送（推送数 {pushCount}）");

        server.SimulateProcess(47.5); // 45.5→47.5 变化 2.0 >= 死区 1.0
        server.Publish();
        Console.WriteLine($"  45.5→47.5 变化 2.0 >= 死区 1.0 → 推送（推送数 {pushCount}）");

        // 布尔型无死区：写一次状态，立即推送
        int boolPush = 0;
        var miRun = server.CreateMonitoredItem(runningId);
        miRun.Changed += (id, dv) =>
        {
            dv.WrappedValue.TryGetValue(out bool b);
            boolPush++;
            Console.WriteLine($"  [推送] {id} = {b}");
        };
        server.Write(runningId, new Variant(false));
        server.Publish();
        Console.WriteLine($"  Running true→false → 布尔变化必推送（推送数 {boolPush}）");

        Console.WriteLine();
        Console.WriteLine("=== 5. 安全（SecurityPolicy / X.509 证书 / 会话） ===");
        Console.WriteLine("  三层安全：");
        Console.WriteLine("    1) 传输安全模式 SecurityMode: None / Sign / SignAndEncrypt");
        Console.WriteLine("    2) 安全策略 SecurityPolicy（加密算法组合），常用：");
        Console.WriteLine($"       None             = {SecurityPolicies.None}");
        Console.WriteLine($"       Basic256Sha256   = {SecurityPolicies.Basic256Sha256}");
        Console.WriteLine($"       Aes128_Sha256_RsaOaep = {SecurityPolicies.Aes128_Sha256_RsaOaep}");
        Console.WriteLine($"       Aes256_Sha256_RsaPss  = {SecurityPolicies.Aes256_Sha256_RsaPss}");
        Console.WriteLine($"       RSA_DH_AesGcm    = {SecurityPolicies.RSA_DH_AesGcm}");
        Console.WriteLine("       （Basic128Rsa15 / Basic256 已废弃，算法强度不够）");
        Console.WriteLine("    3) 用户认证：Anonymous / 用户名密码 / X.509 证书（用户证书）");
        Console.WriteLine("  互信机制：双方各持 X.509 证书，首次连接交换证书，");
        Console.WriteLine("  管理员把对方证书加入'信任列表'后才允许连接（防中间人）。");
        Console.WriteLine("  会话(Session)：客户端连接后建立会话，后续服务都挂在会话上，");
        Console.WriteLine("  带 SessionId 与超时，断线后凭会话恢复或重连。");

        var cert = FakeOpcUaServer.CreateSelfSignedCertificate("opc.tcp://factory-pc:4840");
        server.SecurityPolicy = SecurityPolicies.Basic256Sha256;
        server.SecurityMode = "SignAndEncrypt";
        server.ServerCertificate = cert;
        Console.WriteLine($"  生成本地自签名证书演示：");
        Console.WriteLine($"    Subject     = {cert.Subject}");
        Console.WriteLine($"    指纹        = {cert.Thumbprint}");
        Console.WriteLine($"    有效期      = {cert.NotBefore:yyyy-MM-dd} ~ {cert.NotAfter:yyyy-MM-dd}");
        Console.WriteLine("  真实项目用 OPC Foundation 的证书工具签发/分发，证书带 OPC UA 专用 EKU(1.3.6.1.4.1.14668.2)。");

        Console.WriteLine();
        Console.WriteLine("=== 6. 自测 ===");
        SelfTest.CheckEq(45.5, temp, "读 Temperature=45.5");
        SelfTest.CheckEq(12345, output, "读 TotalOutput=12345(int)");
        SelfTest.CheckEq(true, running, "读 Running=true(bool)");
        SelfTest.CheckEq("Auto", mode, "读 Mode=Auto(string)");
        SelfTest.CheckEq(0x80340000u, dvUnknown.StatusCode.Code, "读未知节点=BadNodeIdUnknown");
        SelfTest.CheckEq(0u, scWrite.Code, "写 Setpoint 成功 Good");
        SelfTest.CheckEq(55.0, setpoint, "写后读回 Setpoint=55.0");
        SelfTest.CheckEq(0x803B0000u, scRo.Code, "写只读节点=BadNotWritable");
        SelfTest.CheckEq(0x80340000u, scUnknown.Code, "写未知节点=BadNodeIdUnknown");
        SelfTest.CheckEq(1, pushCount, "死区过滤后只推送 1 次");
        SelfTest.CheckEq(47.5, lastPushed, "推送值=47.5");
        SelfTest.CheckEq(1, boolPush, "布尔变化推送 1 次");
        SelfTest.CheckEq("CN=opc.tcp://factory-pc:4840", cert.Subject, "证书 Subject 正确");
        SelfTest.CheckEq(8, server.Browse().Count, "地址空间 8 个节点");
        SelfTest.Check(NodeId.Equals(tempId, NodeId.Parse("ns=2;s=Machine1.Temperature")), "NodeId 相等性");

        Console.WriteLine();
        Console.WriteLine("=== 7. 面试话术 ===");
        Console.WriteLine("  OPC UA 一句话：'带类型、带安全、平台无关'的工业数据标准，DA 的跨平台继任者。");
        Console.WriteLine("  三个层次讲清楚：地址空间(节点/NodeId/类型) → 服务(读写/订阅/方法) → 安全(策略+证书+认证)。");
        Console.WriteLine("  工程要点：点位多就用订阅+死区别轮询；连不上先查'端点/策略/证书信任列表'三件事；");
        Console.WriteLine("  调试用 UA Expert 浏览地址空间；.NET 用官方 OPCFoundation SDK。");
        return Task.CompletedTask;
    }
}
