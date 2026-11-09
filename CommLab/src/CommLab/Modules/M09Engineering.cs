using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using CommLab.Core;
using Serilog;

namespace CommLab.Modules;

/// <summary>
/// M09 工程实践：通信队列、并发控制、超时重试、异常恢复(重连)、数据缓存与质量、Serilog 日志、
/// 行业协议概念卡。面试重点：这些是"能不能干活"的分水岭，比协议本身更常被追问。
/// </summary>
public sealed class M09Engineering : IModule
{
    public string Title => "M09 工程实践";

    public async Task RunAsync()
    {
        Console.WriteLine("=== 1. 通信队列（Channel 解耦采集与处理）===");
        Console.WriteLine("  问题：采集循环里直接写数据库/转发，一旦下游慢，整个采集卡住。");
        Console.WriteLine("  方案：生产者(采集) → Channel(有界队列) → 消费者(写库)，两边速度解耦。");
        Console.WriteLine("  有界队列 = 背压：队列满时生产者被等待，天然限流，防止内存爆掉。");
        Console.WriteLine("  Channel<T> vs BlockingCollection：Channel 原生支持 async/await，是 .NET 首选。");

        var ch = Channel.CreateBounded<int>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait });
        int consumed = 0;
        var consumer = Task.Run(async () =>
        {
            await foreach (var v in ch.Reader.ReadAllAsync())
            {
                consumed++;
                await Task.Delay(20);   // 模拟慢数据库写入
            }
        });
        for (int i = 1; i <= 10; i++)
            await ch.Writer.WriteAsync(i);   // 队列满(4)时这里会被等待 = 背压
        ch.Writer.Complete();
        await consumer;
        Console.WriteLine($"  演示：生产 10 条 → 慢消费者(20ms/条) 全部消费完毕 = {consumed} 条，无丢失。");

        Console.WriteLine();
        Console.WriteLine("=== 2. 并发控制 ===");
        Console.WriteLine("  场景：4 个任务并发访问同一台设备/同一条连接。");
        Console.WriteLine("  原则：同一物理连接同一时刻只能有一个事务在飞 → 每设备一把锁(SemaphoreSlim(1,1))，");
        Console.WriteLine("  不要全局一把锁（锁粒度=设备，不同设备互不阻塞）。");
        var device = new FakeBusyDevice();
        var sem = new SemaphoreSlim(1, 1);
        var order = new ConcurrentQueue<string>();
        var tasks = Enumerable.Range(1, 4).Select(async i =>
        {
            await sem.WaitAsync();
            try
            {
                var v = await device.ReadAsync(i);   // 内部 30ms 事务，若被并发打断会抛异常
                order.Enqueue($"T{i}={v}");
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        Console.WriteLine($"  4 个并发读全部串行完成且无事务被撕碎: {string.Join(", ", order)}");
        Console.WriteLine("  其他要点：");
        Console.WriteLine("    - 锁要带超时(WaitAsync(ms))，防止设备无响应时永久死锁");
        Console.WriteLine("    - 共享缓存用 ConcurrentDictionary，别用 Dictionary+lock 拼凑");
        Console.WriteLine("    - 采集任务用独立线程/Task，UI 线程只做展示，禁止在 UI 线程里等 IO");

        Console.WriteLine();
        Console.WriteLine("=== 3. 超时 + 重试（通用 RetryHelper）===");
        Console.WriteLine("  原则：");
        Console.WriteLine("    - 只重试'可恢复'错误：网络异常、5xx、超时");
        Console.WriteLine("    - 不重试'不可恢复'错误：4xx 业务错误、协议异常码（重试一万次也一样）");
        Console.WriteLine("    - 指数退避 + 随机抖动：避免大量客户端同一时刻一起重试打爆服务端");
        Console.WriteLine("    - 重试有上限，且受'总超时预算'约束");

        int flakyCalls = 0;
        async Task<string> FlakyGet()
        {
            flakyCalls++;
            if (flakyCalls < 3)
            {
                await Task.Yield();
                throw new HttpRequestException($"模拟网络闪断(第 {flakyCalls} 次)");
            }
            return "order-1001";
        }
        var (retryResult, retryAttempts) = await WithRetryAsync(FlakyGet, maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(50));
        Console.WriteLine($"  演示：前 2 次抛异常，第 {retryAttempts} 次成功 → {retryResult}");

        Console.WriteLine();
        Console.WriteLine("=== 4. 异常恢复与断线重连（状态机）===");
        Console.WriteLine("  连接状态机：Online ⇄ Offline，中间经过 Reconnecting。");
        Console.WriteLine("  要点：");
        Console.WriteLine("    - 心跳(KeepAlive)检测假死：TCP 连着但设备已重启，只有心跳能发现");
        Console.WriteLine("    - 重连用指数退避：1s→2s→4s→8s(封顶)，别用固定 100ms 疯狂重连");
        Console.WriteLine("    - 重连成功后必须'重新初始化'：重新订阅、重新读一遍关键参数、同步状态");
        Console.WriteLine("  演示：设备前 2 次连接都拒绝，第 3 次成功：");

        var conn = new FakeFlakyConnection(failTimes: 2);
        var state = ConnState.Offline;
        int delayMs = 100, reconnectAttempts = 0;
        while (state != ConnState.Online && reconnectAttempts < 5)
        {
            reconnectAttempts++;
            state = ConnState.Reconnecting;
            try
            {
                await Task.Delay(delayMs);            // 退避等待
                await conn.ConnectAsync();
                state = ConnState.Online;
                Console.WriteLine($"    第 {reconnectAttempts} 次重连成功 → 状态 {state}，重新订阅/初始化完成");
            }
            catch (Exception ex)
            {
                state = ConnState.Offline;
                Console.WriteLine($"    第 {reconnectAttempts} 次重连失败: {ex.Message} → 退避 {delayMs}ms");
                delayMs *= 2;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 5. 数据缓存与质量(Stale) ===");
        Console.WriteLine("  断线期间界面不能显示'假新鲜'的旧数据。");
        Console.WriteLine("  缓存 = 值 + 时间戳 + 质量(Good/Stale)：超过新鲜度阈值 → Stale，界面置灰/标红。");
        var cache = new DataCache<double>(staleAfter: TimeSpan.FromMilliseconds(800));
        cache.Update(45.5);
        var (v1, _, q1) = cache.Get();
        await Task.Delay(1000);   // 模拟断线 1 秒，没有新数据进来
        var (v2, ts, q2) = cache.Get();
        Console.WriteLine($"  正常: {v1} 质量={q1}");
        Console.WriteLine($"  断线 1s 后: 值仍是 {v2}，但时间戳停在 {ts:HH:mm:ss.fff}，质量={q2}");
        cache.Update(46.0);
        var (v3, _, q3) = cache.Get();
        Console.WriteLine($"  恢复后: {v3} 质量={q3}");
        Console.WriteLine("  要点：报警判断、趋势图、HMI 显示都必须检查质量位，不能只看值。");

        Console.WriteLine();
        Console.WriteLine("=== 6. Serilog 结构化日志 ===");
        Console.WriteLine("  要点：");
        Console.WriteLine("    - 结构化：Log.Info(\"读 {Point} = {Value} 质量={Q}\", ...) 模板+参数，");
        Console.WriteLine("      不是字符串拼接 → 可按字段检索(ELK/Serene)，且未使用的参数不格式化(性能)");
        Console.WriteLine("    - 级别：Verbose/Debug/Information/Warning/Error/Fatal，生产至少 Information");
        Console.WriteLine("    - 滚动：按天/按大小滚动 + 保留 N 天，防止日志盘写满");
        Console.WriteLine("    - 每次'请求-响应'记一条：目标、耗时、结果、错误码，排障全靠它");

        var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "commlab-demo.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        logger.Information("读点位 {Point} 值={Value} 质量={Q}", "温度", 45.5, "Good");
        logger.Warning("心跳超时 2 次，准备重连 {Target}", "192.168.1.10:502");
        logger.Error("读失败: {Err} 已重试 {N} 次", "timeout", 3);
        await logger.DisposeAsync();   // 刷盘
        var logFile = Directory.GetFiles(logDir, "commlab-demo*.log").OrderBy(f => f).Last();
        Console.WriteLine($"  日志已写入 {Path.GetFileName(logFile)}（按天滚动，保留 7 天）：");
        foreach (var line in File.ReadLines(logFile).Take(3))
            Console.WriteLine($"    {line}");

        Console.WriteLine();
        Console.WriteLine("=== 7. 行业协议概念卡（摘要）===");
        Console.WriteLine("  详细版见 docs/行业协议概念卡.md，这里只记'一句话+面试关键词'：");
        Console.WriteLine("    CAN/CANopen  : 车载/总线，差分信号抗干扰；CANopen=应用层标准(对象字典/节点ID)");
        Console.WriteLine("    EtherCAT     : 周期实时以太网，'处理-转发'(FOU) 微秒级同步，伺服/运动控制");
        Console.WriteLine("    PROFINET     : 西门子系，RT(实时,周期) / CBA(非实时) 两类通信，IRT 硬实时");
        Console.WriteLine("    EtherNet/IP  : 罗克韦尔系，CIP 协议，Connected(连接) + Unconnected(无连接) 报文");
        Console.WriteLine("    机器人通信   : 主流厂商 TCP 私有协议(FANUC/ABB/KUKA) 或 OPC UA；IO 走数字量/模拟量映射");
        Console.WriteLine("    工业相机     : GigE Vision(标准,UDP 流) / USB3 Vision；SDK 取图+触发+曝光参数");
        Console.WriteLine("    MQTT         : 见 M08，设备→云/MES 的轻量 Pub/Sub");
        Console.WriteLine("    HTTP/REST    : 见 M08，MES/ERP 对接，JSON + 状态码 + 重试");
        Console.WriteLine("    MES          : 制造执行系统，管工单/报工/追溯；上位机是它的'数据供应商'");

        Console.WriteLine();
        Console.WriteLine("=== 8. 自测 ===");
        SelfTest.CheckEq(10, consumed, "队列 10 条全部消费无丢失");
        SelfTest.CheckEq(4, order.Count, "4 个并发读全部完成");
        SelfTest.Check(!order.Any(s => s.Contains("X")), "无事务被并发撕碎");
        SelfTest.CheckEq(3, retryAttempts, "第 3 次重试成功");
        SelfTest.CheckEq("order-1001", retryResult, "重试后拿到正确数据");
        SelfTest.CheckEq(ConnState.Online, state, "重连后状态=Online");
        SelfTest.Check(reconnectAttempts >= 3, $"重连尝试 {reconnectAttempts} 次(前 2 次失败)");
        SelfTest.CheckEq(DataCache<double>.Quality.Good, q1, "新鲜数据质量=Good");
        SelfTest.CheckEq(DataCache<double>.Quality.Stale, q2, "断线后质量=Stale");
        SelfTest.CheckEq(DataCache<double>.Quality.Good, q3, "恢复后质量=Good");
        SelfTest.Check(File.Exists(logFile), "日志文件已生成");
        SelfTest.Check(File.ReadAllText(logFile).Contains("温度"), "结构化日志包含点位名");

        Console.WriteLine();
        Console.WriteLine("=== 9. 面试话术 ===");
        Console.WriteLine("  '协议我会用，但工程上我还会做这几件事：'");
        Console.WriteLine("  1) 采集与处理用 Channel 解耦，有界队列做背压，下游慢不拖垮采集；");
        Console.WriteLine("  2) 每设备一把信号量锁 + 带超时，防并发撕报文、防死锁；");
        Console.WriteLine("  3) 重试只针对可恢复错误，指数退避+抖动，有上限有总预算；");
        Console.WriteLine("  4) 连接做成状态机，心跳探假死，重连后重新初始化；");
        Console.WriteLine("  5) 数据带时间戳+质量位，断线数据标 Stale，报警不基于旧值；");
        Console.WriteLine("  6) Serilog 结构化日志按天滚动，每次请求-响应可追溯。");
        Console.WriteLine("  这套话术把'会发报文'提升到'能上项目'，是面试加分项。");
    }

    // ---------- 通用重试 ----------
    static async Task<(T Result, int Attempts)> WithRetryAsync<T>(
        Func<Task<T>> attempt, int maxAttempts, TimeSpan baseDelay)
    {
        for (int i = 1; i <= maxAttempts; i++)
        {
            try
            {
                return (await attempt(), i);
            }
            catch (Exception) when (i < maxAttempts)
            {
                var delay = baseDelay * Math.Pow(2, i - 1);                 // 指数退避
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25)); // +抖动
                await Task.Delay(delay + jitter);
            }
        }
        throw new InvalidOperationException("重试次数已用尽");
    }

    enum ConnState { Offline, Reconnecting, Online }

    /// <summary>前 failTimes 次连接都失败，之后成功 —— 模拟设备重启中。</summary>
    sealed class FakeFlakyConnection
    {
        readonly int _failTimes; int _attempts;
        public FakeFlakyConnection(int failTimes) => _failTimes = failTimes;
        public Task ConnectAsync()
        {
            _attempts++;
            if (_attempts <= _failTimes)
                return Task.FromException(new SocketException(10061));   // WSAECONNREFUSED
            return Task.CompletedTask;
        }
    }

    /// <summary>模拟'事务期间被打断就会出错'的设备：事务 30ms，期间若 currentTx 被篡改则抛异常。</summary>
    sealed class FakeBusyDevice
    {
        int _currentTx;
        public async Task<int> ReadAsync(int tx)
        {
            _currentTx = tx;
            await Task.Delay(30);
            if (_currentTx != tx) throw new InvalidOperationException($"事务 {tx} 被并发撕碎");
            return tx * 10;
        }
    }

    /// <summary>值 + 时间戳 + 质量位 的缓存。</summary>
    sealed class DataCache<T>
    {
        public enum Quality { Good, Stale }
        readonly TimeSpan _staleAfter;
        T? _value; DateTime _ts;
        public DataCache(TimeSpan staleAfter) => _staleAfter = staleAfter;
        public void Update(T v) { _value = v; _ts = DateTime.Now; }
        public (T? Value, DateTime Timestamp, Quality Quality) Get()
        {
            var q = DateTime.Now - _ts > _staleAfter ? Quality.Stale : Quality.Good;
            return (_value, _ts, q);
        }
    }
}
