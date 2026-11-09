using System.IO.Ports;
using CommLab.Core;

namespace CommLab.Fakes;

/// <summary>
/// 串口链路抽象。真实实现包 SerialPort（硬件），假实现用内存回环（无硬件也能跑）。
/// 面试点：把硬件藏在接口后面，是上位机可测试性的第一课。
/// </summary>
public interface ISerialLink : IDisposable
{
    string Name { get; }
    int BaudRate { get; }
    bool IsOpen { get; }
    /// <summary>收到数据事件（注意：事件 ≠ 完整报文，可能只收到半个帧）</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;
    void Open();
    void Close();
    void Write(byte[] data);
    /// <summary>读当前缓冲区里的字节；timeoutMs 内没有数据返回 null（超时）</summary>
    Task<byte[]?> ReadAsync(int timeoutMs);
}

/// <summary>
/// 内存串口对：A、B 两端用"线"连起来，模拟 RS-232/RS-485。
/// 关键模拟：数据不是一次到齐的——每次只切 1~3 个字节触发 DataReceived，
/// 逼着接收端必须做帧重组（这正是真实串口的行为）。
/// </summary>
public sealed class FakeSerialPort : ISerialLink
{
    private readonly Queue<byte> _rx = new();
    private readonly object _gate = new();
    private FakeSerialPort? _peer;
    private int _chunkCounter;

    public string Name { get; }
    public int BaudRate { get; } = 9600;
    public bool IsOpen { get; private set; }
    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public FakeSerialPort(string name) => Name = name;

    /// <summary>把两个端口用"线"连起来</summary>
    public static (FakeSerialPort a, FakeSerialPort b) Connect(string nameA = "虚拟串口A", string nameB = "虚拟串口B")
    {
        var a = new FakeSerialPort(nameA);
        var b = new FakeSerialPort(nameB);
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public void Open() => IsOpen = true;

    public void Close()
    {
        IsOpen = false;
        lock (_gate) _rx.Clear();
    }

    public void Write(byte[] data)
    {
        if (!IsOpen) throw new InvalidOperationException($"{Name} 未打开");
        var peer = _peer;
        if (peer is null) return;
        // 异步 + 小分片投递，模拟真实串口"一次 Read 收不全一帧"
        _ = Task.Run(async () =>
        {
            foreach (var b in data)
            {
                lock (peer._gate) peer._rx.Enqueue(b);
                await Task.Delay(5);
            }
            // 按 1~3 字节的小分片触发接收事件
            while (true)
            {
                byte[] chunk;
                lock (peer._gate)
                {
                    int n = Math.Min(1 + (Interlocked.Increment(ref peer._chunkCounter) % 3), peer._rx.Count);
                    if (n == 0) break;
                    chunk = new byte[n];
                    for (int i = 0; i < n; i++) chunk[i] = peer._rx.Dequeue();
                }
                // 模拟 9600 波特率下 1 字节 ≈ 1ms（10 bit/字节），加快演示用 2ms
                await Task.Delay(2);
                peer.DataReceived?.Invoke(chunk);
            }
        });
    }

    public Task<byte[]?> ReadAsync(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            byte[] data;
            lock (_gate)
            {
                if (_rx.Count == 0)
                {
                    data = Array.Empty<byte>();
                }
                else
                {
                    data = new byte[_rx.Count];
                    for (int i = 0; i < data.Length; i++) data[i] = _rx.Dequeue();
                }
            }
            if (data.Length > 0) return Task.FromResult<byte[]?>(data);
            if (sw.ElapsedMilliseconds >= timeoutMs) return Task.FromResult<byte[]?>(null);
            Task.Delay(5).Wait();
        }
    }

    public void Dispose() => Close();
}

/// <summary>
/// 假温度传感器（挂在串口 B 端）：收到 "读温度" 命令就回一帧。
/// 帧格式：AA 55 | LEN | CMD | DATA... | SUM(和校验)
/// </summary>
public sealed class FakeTempSensor
{
    private readonly ISerialLink _port;
    private readonly FrameParser _parser;
    private bool _powered = true;

    public FakeTempSensor(ISerialLink port)
    {
        _port = port;
        _parser = new FrameParser(
            header: new byte[] { 0xAA, 0x55 },
            lengthOf: f => f.Length >= 4 ? f[2] + 3 : 0,
            isValid: f => f[f.Length - 1] == Crc.ChecksumSum(f.AsSpan(0, f.Length - 1)));
        _port.DataReceived += chunk =>
        {
            foreach (var frame in _parser.Feed(chunk.Span))
                OnFrame(frame);
        };
    }

    /// <summary>断电/上电，用于演示超时与重连</summary>
    public void SetPowered(bool on) => _powered = on;

    private void OnFrame(byte[] frame)
    {
        if (!_powered) return; // 设备断电：不响应（演示超时的关键）
        byte cmd = frame[3];
        byte[] reply;
        switch (cmd)
        {
            case 0x01: // 读温度：回 2 字节 int16（×0.1℃，大端）
                short raw = 255; // 25.5 ℃
                reply = BuildFrame(0x81, new[] { (byte)(raw >> 8), (byte)raw });
                break;
            case 0x02: // 读设备名：回 ASCII
                reply = BuildFrame(0x82, ByteUtils.Ascii("COMM-TEMP-01"));
                break;
            default:
                return;
        }
        _port.Write(reply);
    }

    /// <summary>组帧：AA 55 LEN CMD DATA... SUM。LEN = LEN 字段之后的字节数（CMD+DATA+SUM）</summary>
    public static byte[] BuildFrame(byte cmd, byte[] data)
    {
        var body = ByteUtils.Combine(new byte[] { 0xAA, 0x55, (byte)(2 + data.Length), cmd }, data);
        return ByteUtils.Combine(body, new[] { Crc.ChecksumSum(body) });
    }
}
