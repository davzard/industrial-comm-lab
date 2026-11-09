using Opc.Ua;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CommLab.Fakes;

/// <summary>
/// 假 OPC UA 服务端：进程内地址空间 + 读写 + 订阅（MonitoredItem/死区）。
/// 数据模型直接用真实的 Opc.Ua 类型（NodeId/DataValue/Variant/StatusCode），
/// 语义与真实 OPC UA 一致，只是不走网络/编码层，便于在控制台里演示与自测。
/// </summary>
public sealed class FakeOpcUaServer
{
    // ---------- 地址空间 ----------
    private sealed class NodeInfo
    {
        public required string DisplayName { get; init; }
        public required NodeClass NodeClass { get; init; }
        public required bool Writable { get; init; }
        public Variant Value { get; set; }
    }

    private readonly Dictionary<NodeId, NodeInfo> _nodes = new();
    private readonly List<FakeMonitoredItem> _monitoredItems = new();

    // 安全相关（教学用：真实 OPC UA 连接时由服务端下发/协商）
    public string SecurityPolicy { get; set; } = SecurityPolicies.Basic256Sha256;
    public string SecurityMode { get; set; } = "SignAndEncrypt";
    public X509Certificate2? ServerCertificate { get; set; }

    public FakeOpcUaServer()
    {
        // 预置一个"机器"对象 + 若干变量 + 一个方法，覆盖三种 NodeClass
        AddObject("ns=2;s=Machine1", "Machine1");
        AddMethod("ns=2;s=Machine1.Start", "Start");
        AddVariable("ns=2;s=Machine1.Temperature", "温度", 45.5, writable: false);
        AddVariable("ns=2;s=Machine1.Pressure", "压力", 101.3, writable: false);
        AddVariable("ns=2;s=Machine1.Setpoint", "设定值", 50.0, writable: true);
        AddVariable("ns=2;s=Machine1.Running", "运行中", true, writable: true);
        AddVariable("ns=2;s=Machine1.Mode", "模式", "Auto", writable: true);
        AddVariable("ns=2;s=Machine1.TotalOutput", "累计产量", 12345, writable: false);
    }

    private void AddObject(string id, string name)
        => _nodes[NodeId.Parse(id)] = new NodeInfo { DisplayName = name, NodeClass = NodeClass.Object, Writable = false, Value = new Variant() };

    private void AddMethod(string id, string name)
        => _nodes[NodeId.Parse(id)] = new NodeInfo { DisplayName = name, NodeClass = NodeClass.Method, Writable = false, Value = new Variant() };

    private void AddVariable(string id, string name, object value, bool writable)
        => _nodes[NodeId.Parse(id)] = new NodeInfo { DisplayName = name, NodeClass = NodeClass.Variable, Writable = writable, Value = ToVariant(value) };

    private static Variant ToVariant(object value) => value switch
    {
        double d => new Variant(d),
        float f => new Variant(f),
        int i => new Variant(i),
        long l => new Variant(l),
        bool b => new Variant(b),
        string s => new Variant(s),
        _ => throw new NotSupportedException($"演示不支持的类型: {value.GetType()}")
    };

    // ---------- 读 ----------
    /// <summary>读节点。未知节点返回 BadNodeIdUnknown(0x80340000)。</summary>
    public DataValue Read(NodeId id)
    {
        if (_nodes.TryGetValue(id, out var n))
            return new DataValue(n.Value, new StatusCode(0), new DateTimeUtc(DateTime.UtcNow), new DateTimeUtc(DateTime.UtcNow));
        return DataValue.FromStatusCode(new StatusCode(0x80340000)); // BadNodeIdUnknown
    }

    // ---------- 写 ----------
    /// <summary>写节点。未知→BadNodeIdUnknown；只读→BadNotWritable(0x803B0000)；成功→Good。</summary>
    public StatusCode Write(NodeId id, Variant value)
    {
        if (!_nodes.TryGetValue(id, out var n)) return new StatusCode(0x80340000); // BadNodeIdUnknown
        if (!n.Writable) return new StatusCode(0x803B0000);                       // BadNotWritable
        n.Value = value;
        return new StatusCode(0); // Good
    }

    // ---------- 过程值模拟 ----------
    /// <summary>模拟"过程值变化"：真实服务端由驱动/PLC 把新值写进地址空间，这里直接改 Temperature。</summary>
    public void SimulateProcess(double temperature)
    {
        var id = NodeId.Parse("ns=2;s=Machine1.Temperature");
        if (_nodes.TryGetValue(id, out var n)) n.Value = new Variant(temperature);
    }

    // ---------- 浏览 ----------
    public IReadOnlyList<(NodeId Id, string DisplayName, NodeClass NodeClass, string ValueText)> Browse()
        => _nodes
            .Select(kv => (Id: kv.Key, DisplayName: kv.Value.DisplayName, NodeClass: kv.Value.NodeClass, ValueText: kv.Value.Value.ToString()))
            .OrderBy(x => x.Id.ToString())
            .ToList();

    // ---------- 订阅 ----------
    /// <summary>创建监视项（教学版 MonitoredItem）：绑定节点 + 死区，值变化时触发 Changed。</summary>
    public FakeMonitoredItem CreateMonitoredItem(NodeId id, double deadband = 0)
    {
        var mi = new FakeMonitoredItem
        {
            NodeId = id,
            Deadband = deadband,
            LastValue = Read(id).WrappedValue
        };
        _monitoredItems.Add(mi);
        return mi;
    }

    /// <summary>模拟服务端"发布周期"：检查所有监视项，超死区则推送。返回本次推送条数。</summary>
    public int Publish()
    {
        int count = 0;
        foreach (var mi in _monitoredItems)
        {
            var current = Read(mi.NodeId);
            if (HasChanged(mi.LastValue, current.WrappedValue, mi.Deadband))
            {
                mi.LastValue = current.WrappedValue;
                mi.RaiseChanged(mi.NodeId, current);
                count++;
            }
        }
        return count;
    }

    private static bool HasChanged(Variant oldV, Variant newV, double deadband)
    {
        // 数值死区：变化量 < 死区 则不推送（省带宽，工业现场常用）
        if (deadband > 0 && oldV.TryGetValue(out double od) && newV.TryGetValue(out double nd))
            return Math.Abs(nd - od) >= deadband;
        return !oldV.Equals(newV);
    }

    // ---------- 证书（教学：OPC UA 用 X.509 证书互相认证） ----------
    /// <summary>生成一张自签名 X.509 证书，模拟"服务端证书"。真实环境用 OPC Foundation 工具链签发/信任。</summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        // OPC UA 专用 EKU：1.3.6.1.4.1.14668.2
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.4.1.14668.2") }, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}

/// <summary>教学版 MonitoredItem：绑定一个节点，值变化（超死区）时触发事件。</summary>
public sealed class FakeMonitoredItem
{
    public required NodeId NodeId { get; init; }
    public double Deadband { get; init; }
    public Variant LastValue { get; set; }
    public event Action<NodeId, DataValue>? Changed;

    /// <summary>由服务端在发布周期内调用（事件只能在声明类内触发）。</summary>
    internal void RaiseChanged(NodeId id, DataValue value) => Changed?.Invoke(id, value);
}
