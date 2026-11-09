using System.Net;
using System.Text;
using System.Text.Json;
using CommLab.Core;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CommLab.Modules;

/// <summary>
/// M08 MQTT + HTTP/REST：进程内真实 MQTT broker（MQTTnet.Server）+ 假 MES（HttpListener）。
/// 面试重点：发布/订阅模型、主题通配符、QoS0/1/2、保留消息、遗嘱消息、broker 事件；
/// HttpClient 超时、503 重试退避、状态码处理。
/// </summary>
public sealed class M08MqttHttp : IModule
{
    public string Title => "M08 MQTT 与 HTTP/REST(MES)";

    public async Task RunAsync()
    {
        const int MqttPort = 11883;   // 真实默认 1883；这里换端口避免与本机真实 broker 冲突
        const int HttpPort = 18080;

        // ---------- broker ----------
        var sf = new MqttServerFactory();
        var serverOptions = sf.CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(MqttPort)
            .Build();
        var broker = sf.CreateMqttServer(serverOptions);

        int pubSeen = 0, connectSeen = 0, disconnectSeen = 0;
        string lastPubTopic = "", lastPubFrom = "";
        broker.InterceptingPublishAsync += e =>
        {
            pubSeen++;
            lastPubTopic = e.ApplicationMessage.Topic;
            lastPubFrom = e.ClientId;
            Console.WriteLine($"    [broker] 拦截到发布: 来自 {e.ClientId}  主题={e.ApplicationMessage.Topic}");
            return Task.CompletedTask;
        };
        broker.ClientConnectedAsync += e =>
        {
            connectSeen++;
            Console.WriteLine($"    [broker] 客户端上线: {e.ClientId}");
            return Task.CompletedTask;
        };
        broker.ClientDisconnectedAsync += e =>
        {
            disconnectSeen++;
            Console.WriteLine($"    [broker] 客户端下线: {e.ClientId} (原因={e.DisconnectType})");
            return Task.CompletedTask;
        };
        await broker.StartAsync();
        Console.WriteLine($"  broker 已启动: 127.0.0.1:{MqttPort}");

        var cf = new MqttClientFactory();

        // HMI（上位机监控程序）：订阅者
        var hmi = cf.CreateMqttClient();
        var hmiMessages = new List<(string Topic, string Payload)>();
        hmi.ApplicationMessageReceivedAsync += e =>
        {
            lock (hmiMessages)
                hmiMessages.Add((e.ApplicationMessage.Topic, e.ApplicationMessage.ConvertPayloadToString()));
            return Task.CompletedTask;
        };

        Console.WriteLine();
        Console.WriteLine("=== 1. MQTT 总览 ===");
        Console.WriteLine("  MQTT = 基于 TCP 的轻量发布/订阅(Pub/Sub)消息协议，默认端口 1883，TLS 8883。");
        Console.WriteLine("  三个角色：");
        Console.WriteLine("    发布者(Publisher)  : 只发消息，如 PLC 网关、采集程序");
        Console.WriteLine("    订阅者(Subscriber) : 只收消息，如 HMI、MES、云平台");
        Console.WriteLine("    代理(Broker)       : 收发布、按主题转发给订阅者，本演示用 MQTTnet.Server 进程内 broker");
        Console.WriteLine("  主题(Topic)是层级化的字符串，如 factory/line1/temp：");
        Console.WriteLine("    通配符 + : 匹配单层，factory/+/temp 匹配 factory/line1/temp");
        Console.WriteLine("    通配符 # : 匹配多层(只能在末尾)，factory/# 匹配 factory 下全部");
        Console.WriteLine("  与 TCP 轮询的区别：");
        Console.WriteLine("    TCP 轮询 : 点对点请求-响应，上位机逐个设备读，N 台设备 N 条连接");
        Console.WriteLine("    MQTT    : 多对多，设备只管发，broker 转发，新增消费者不用改设备侧");
        Console.WriteLine("  适用：设备数量多、消费者多、网络弱(报文头最小 2 字节)、需要解耦的场景。");

        Console.WriteLine();
        Console.WriteLine("=== 2. 连接与 QoS 0/1/2 ===");
        var hmiResult = await hmi.ConnectAsync(cf.CreateClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", MqttPort)
            .WithClientId("HMI-01")
            .WithCleanStart(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .Build(), default);
        Console.WriteLine($"  HMI 连接结果: {hmiResult.ResultCode}  (KeepAlive=15s，心跳保活)");

        Console.WriteLine("  QoS 质量等级（发布方与订阅方取较小值生效）：");
        Console.WriteLine("    QoS0 至多一次 : 发完即忘，无确认，可能丢（适合温度等高频数据）");
        Console.WriteLine("    QoS1 至少一次 : PUBLISH→PUBACK 确认，可能重复，接收方需去重");
        Console.WriteLine("    QoS2 恰好一次 : PUBLISH→PUBREC→PUBREL→PUBCOMP 四步握手，不丢不重");

        // PLC 网关（发布者），带遗嘱
        var gw = cf.CreateMqttClient();
        await gw.ConnectAsync(cf.CreateClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", MqttPort)
            .WithClientId("PLC-GW")
            .WithCleanStart(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
            .WithWillTopic("factory/line1/status")
            .WithWillPayload("offline")
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), default);

        // HMI 订阅 factory/# (QoS1)
        var subResult = await hmi.SubscribeAsync(cf.CreateSubscribeOptionsBuilder()
            .WithTopicFilter("factory/#", MqttQualityOfServiceLevel.AtLeastOnce, false, false, MqttRetainHandling.SendAtSubscribe)
            .Build(), default);
        Console.WriteLine($"  HMI 订阅 factory/# 授权: {subResult.Items.First().ResultCode}");

        // 三种 QoS 各发一条
        await gw.PublishAsync(cf.CreateApplicationMessageBuilder()
            .WithTopic("factory/line1/temp").WithPayload("45.5")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce).Build(), default);
        await gw.PublishAsync(cf.CreateApplicationMessageBuilder()
            .WithTopic("factory/line1/temp").WithPayload("47.0")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), default);
        var qos2Result = await gw.PublishAsync(cf.CreateApplicationMessageBuilder()
            .WithTopic("factory/line1/setpoint").WithPayload("50.0")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
            .WithRetainFlag(true).Build(), default);
        Console.WriteLine($"  QoS2 发布结果: {qos2Result.ReasonCode} (四步握手完成后才返回)");
        await Task.Delay(500);

        Console.WriteLine("  HMI 收到的消息：");
        lock (hmiMessages)
            foreach (var (topic, payload) in hmiMessages)
                Console.WriteLine($"    {topic} = {payload}");

        Console.WriteLine();
        Console.WriteLine("=== 3. 保留消息(Retained) ===");
        Console.WriteLine("  带 Retain 标志的消息，broker 会存下该主题的'最后一条'；");
        Console.WriteLine("  新订阅者订阅时立即收到这条旧消息——适合'状态类'数据（在线状态、设定值）。");
        var scada = cf.CreateMqttClient();
        string? retained = null;
        scada.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic == "factory/line1/setpoint")
                retained = e.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await scada.ConnectAsync(cf.CreateClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", MqttPort).WithClientId("SCADA-NEW").WithCleanStart(true).Build(), default);
        await scada.SubscribeAsync(cf.CreateSubscribeOptionsBuilder()
            .WithTopicFilter("factory/line1/setpoint", MqttQualityOfServiceLevel.AtLeastOnce, false, false, MqttRetainHandling.SendAtSubscribe)
            .Build(), default);
        await Task.Delay(400);
        Console.WriteLine($"  新订阅者 SCADA-NEW 订阅 setpoint 后立即收到保留消息: {retained}");
        await scada.DisconnectAsync(cf.CreateClientDisconnectOptionsBuilder().Build(), default);

        Console.WriteLine();
        Console.WriteLine("=== 4. 遗嘱消息(Will) ===");
        Console.WriteLine("  连接时登记遗嘱：若客户端'异常断开'(没发 DISCONNECT)，broker 代它发布遗嘱。");
        Console.WriteLine("  用途：设备掉线报警。下面让 PLC-GW 直接 Dispose() 模拟宕机（不发 DISCONNECT）：");
        ((IDisposable)gw).Dispose();
        await Task.Delay(800);
        string? will = null;
        lock (hmiMessages)
            will = hmiMessages.LastOrDefault(m => m.Topic == "factory/line1/status").Payload;
        Console.WriteLine($"  HMI 收到遗嘱: factory/line1/status = {will}");

        Console.WriteLine();
        Console.WriteLine("=== 5. Broker 侧事件（连接管理/规则引擎的入口）===");
        Console.WriteLine($"  上线事件计数={connectSeen}  下线事件计数={disconnectSeen}  拦截发布计数={pubSeen}");
        Console.WriteLine($"  最后一条发布: 来自 {lastPubFrom}  主题={lastPubTopic} (遗嘱也是发布，会被拦截到)");
        Console.WriteLine("  工程用法：上线/下线事件做设备在线统计；InterceptingPublish 做日志、转发、规则引擎。");

        Console.WriteLine();
        Console.WriteLine("=== 6. HTTP/REST 对接 MES ===");
        Console.WriteLine("  MES(制造执行系统)通常暴露 REST API：GET 查订单/报工，POST 提交数据。");
        Console.WriteLine($"  启动假 MES: 127.0.0.1:{HttpPort} (HttpListener)");

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{HttpPort}/");
        listener.Start();

        int order503Count = 0;   // /api/orders/1001 前 2 次返回 503，演示重试
        async Task WriteJson(HttpListenerContext ctx, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "";
            if (req.HttpMethod == "GET" && path == "/api/health")
            {
                await WriteJson(ctx, 200, "{\"status\":\"ok\",\"service\":\"MES\"}");
            }
            else if (req.HttpMethod == "GET" && path == "/api/orders/1001")
            {
                if (order503Count < 2)
                {
                    order503Count++;
                    await WriteJson(ctx, 503, "{\"error\":\"MES 正在发布，稍后重试\"}");
                }
                else
                {
                    await WriteJson(ctx, 200, "{\"id\":1001,\"product\":\"轴承\",\"qty\":100}");
                }
            }
            else if (req.HttpMethod == "GET" && path == "/api/slow")
            {
                await Task.Delay(2500);   // 故意慢，演示客户端超时
                await WriteJson(ctx, 200, "{\"ok\":true}");
            }
            else if (req.HttpMethod == "POST" && path == "/api/orders")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("id", out _))
                    {
                        await WriteJson(ctx, 400, "{\"error\":\"缺少 id 字段\"}");
                        return;
                    }
                    await WriteJson(ctx, 201, "{\"code\":0,\"msg\":\"created\"}");
                }
                catch (JsonException)
                {
                    await WriteJson(ctx, 400, "{\"error\":\"JSON 解析失败\"}");
                }
            }
            else
            {
                await WriteJson(ctx, 404, "{\"error\":\"not found\"}");
            }
        }
        var serverTask = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                try { await HandleRequest(ctx); } catch { }
            }
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Console.WriteLine("  HttpClient 统一设置 Timeout=5s（防止 MES 无响应时线程被挂死）");

        // 6.1 GET /api/health（打印请求/响应头）
        var health = await http.GetAsync($"http://127.0.0.1:{HttpPort}/api/health");
        Console.WriteLine($"  请求: GET /api/health");
        Console.WriteLine($"  响应: {(int)health.StatusCode} {health.StatusCode}");
        foreach (var h in health.Headers)
            Console.WriteLine($"    {h.Key}: {string.Join(",", h.Value)}");
        var healthBody = await health.Content.ReadAsStringAsync();
        Console.WriteLine($"  体  : {healthBody}");

        // 6.2 GET /api/orders/1001：503 → 重试 + 退避
        Console.WriteLine();
        Console.WriteLine("  请求: GET /api/orders/1001 （服务端前 2 次故意返回 503）");
        int attempts = 0;
        int orderStatus = 0;
        string? orderJson = null;
        for (int i = 1; i <= 3; i++)
        {
            attempts = i;
            var resp = await http.GetAsync($"http://127.0.0.1:{HttpPort}/api/orders/1001");
            orderStatus = (int)resp.StatusCode;
            Console.WriteLine($"    第 {i} 次 → {(int)resp.StatusCode} {resp.StatusCode}");
            if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var delay = 200 * i;   // 线性退避，工程上常用指数退避+抖动
                Console.WriteLine($"      503 服务不可用 → {delay}ms 后重试");
                await Task.Delay(delay);
                continue;
            }
            if (resp.IsSuccessStatusCode) orderJson = await resp.Content.ReadAsStringAsync();
            break;
        }
        Console.WriteLine($"    最终: 第 {attempts} 次成功，订单 = {orderJson}");

        // 6.3 GET 不存在的订单 → 404
        var missing = await http.GetAsync($"http://127.0.0.1:{HttpPort}/api/orders/9999");
        Console.WriteLine($"  请求: GET /api/orders/9999 → {(int)missing.StatusCode} {missing.StatusCode} (404=业务上'不存在'，按业务处理而非报错)");

        // 6.4 POST 正常 JSON → 201
        var post = await http.PostAsync($"http://127.0.0.1:{HttpPort}/api/orders",
            new StringContent("{\"id\":1002,\"product\":\"密封圈\",\"qty\":50}", Encoding.UTF8, "application/json"));
        var postBody = await post.Content.ReadAsStringAsync();
        Console.WriteLine($"  请求: POST /api/orders (合法 JSON) → {(int)post.StatusCode} {post.StatusCode}  {postBody}");

        // 6.5 POST 坏 JSON → 400
        var badPost = await http.PostAsync($"http://127.0.0.1:{HttpPort}/api/orders",
            new StringContent("{不是JSON", Encoding.UTF8, "application/json"));
        var badPostBody = await badPost.Content.ReadAsStringAsync();
        Console.WriteLine($"  请求: POST /api/orders (坏 JSON) → {(int)badPost.StatusCode} {badPost.StatusCode}  {badPostBody}");

        // 6.6 超时演示
        bool timeoutHit = false;
        try
        {
            using var slowClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            await slowClient.GetAsync($"http://127.0.0.1:{HttpPort}/api/slow");
        }
        catch (TaskCanceledException)
        {
            timeoutHit = true;
        }
        Console.WriteLine($"  请求: GET /api/slow (服务端 2.5s 才返回, 客户端 Timeout=1s) → 触发超时 TaskCanceledException = {timeoutHit}");
        Console.WriteLine("  超时处理：捕获 TaskCanceledException，计入失败次数，走重试或降级，不能让整个采集线程卡死。");

        // 收尾
        listener.Stop();
        await serverTask;
        await broker.StopAsync(new MqttServerStopOptions());
        Console.WriteLine();
        Console.WriteLine("  broker 与假 MES 已停止。");

        Console.WriteLine();
        Console.WriteLine("=== 7. 自测 ===");
        SelfTest.CheckEq(MqttClientConnectResultCode.Success, hmiResult.ResultCode, "HMI 连接 broker 成功");
        SelfTest.CheckEq(MqttClientSubscribeResultCode.GrantedQoS1, subResult.Items.First().ResultCode, "订阅 factory/# 授权 QoS1");
        SelfTest.CheckEq(MqttClientPublishReasonCode.Success, qos2Result.ReasonCode, "QoS2 发布成功");
        SelfTest.CheckEq(3, CountHmi(hmiMessages, "factory/line1/temp"), "HMI 收到 2 条温度 + 1 条设定值");
        SelfTest.CheckEq("50.0", retained, "新订阅者立即收到保留消息 50.0");
        SelfTest.CheckEq("offline", will, "崩溃后 broker 代发布遗嘱 offline");
        SelfTest.Check(pubSeen >= 4, $"broker 拦截到 {pubSeen} 条发布(含遗嘱)");
        SelfTest.Check(disconnectSeen >= 1, "broker 感知到 PLC-GW 异常断开");
        SelfTest.CheckEq(200, (int)health.StatusCode, "GET /api/health = 200");
        SelfTest.CheckEq(200, orderStatus, "503 重试后 GET 订单 = 200");
        SelfTest.CheckEq(3, attempts, "共请求 3 次(2 次 503 + 1 次成功)");
        SelfTest.CheckEq(404, (int)missing.StatusCode, "不存在的订单 = 404");
        SelfTest.CheckEq(201, (int)post.StatusCode, "POST 合法 JSON = 201");
        SelfTest.CheckEq(400, (int)badPost.StatusCode, "POST 坏 JSON = 400");
        SelfTest.Check(timeoutHit, "慢接口触发客户端超时");

        Console.WriteLine();
        Console.WriteLine("=== 8. 面试话术 ===");
        Console.WriteLine("  MQTT 一句话：'基于 TCP 的轻量发布/订阅协议，为弱网和多对多场景设计'。");
        Console.WriteLine("  必背三件套：QoS0/1/2（至多/至少/恰好一次）、保留消息（状态数据）、遗嘱（掉线报警）。");
        Console.WriteLine("  工业链路：PLC → 边缘网关 → MQTT broker → MES/SCADA/云平台，设备与消费者解耦，");
        Console.WriteLine("  新增一个消费者不用改设备侧代码——这是它比 TCP 轮询强的地方。");
        Console.WriteLine("  HTTP/REST 工程要点：统一超时、5xx 重试+退避、4xx 按业务处理不重试、");
        Console.WriteLine("  连接复用(HttpClient 单例)、日志记录请求/响应。");
    }

    static int CountHmi(List<(string Topic, string Payload)> messages, string topic)
    {
        lock (messages)
            return messages.Count(m => m.Topic == topic || m.Topic == "factory/line1/setpoint");
    }
}
