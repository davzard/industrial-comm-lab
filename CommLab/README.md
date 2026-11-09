# CommLab —— 上位机通信知识点示例项目

一个**零硬件、可运行**的 C#/.NET 8 控制台项目，把上位机通信学习路线上的每个知识点都做成可运行的示例 + 自测断言。所有"设备"都是进程内假实现（Fake），不需要真实 PLC、串口线或网络环境。

## 运行

```powershell
cd D:\简历\resume\project\通信\CommLab

dotnet build
dotnet run --project src/CommLab            # 交互式菜单
dotnet run --project src/CommLab -- 0       # 一键跑全部 9 个模块（含自测汇总）
dotnet run --project src/CommLab -- 4       # 只跑 M04 Modbus
```

> 每个模块末尾有 `[PASS]/[FAIL]` 自测断言，`-- 0` 跑完会打印总汇总；任何 FAIL 都会让进程退出码为 1。

## 模块索引

| # | 模块 | 覆盖知识点 | 自测 |
|---|------|-----------|------|
| M01 | 基础 | 二进制/十六进制/字节数组、数据类型转换、大小端、校验和与 CRC16 | ✔ |
| M02 | 串口 | RS-232/RS-485、串口参数(波特率/数据位/校验/停止位)、半双工、自定义帧收发 | ✔ |
| M03 | TCP/UDP | Socket、客户端/服务端、端口、超时、心跳、断线重连、粘包与拆包(长度前缀/分隔符) | ✔ |
| M04 | Modbus | RTU 与 TCP、设备地址、功能码 01~06、线圈/寄存器、报文格式、CRC、MBAP 头、异常码 | ✔ |
| M05 | 协议设计 | 按协议文档组包/解析、数据转换(int16 BE ×0.1℃)、状态码/错误码处理 | ✔ |
| M06 | PLC 通信 | 西门子 S7(TPKT/COTP/S7comm)、三菱 MC、欧姆龙 FINS 报文与点位表 | ✔ |
| M07 | OPC UA | 节点/NodeId、读写(DataValue/Variant/StatusCode)、订阅(死区)、安全策略与 X.509 证书 | ✔ |
| M08 | MQTT + HTTP/REST | 发布/订阅、主题通配符、QoS0/1/2、保留消息、遗嘱、broker 事件；HttpClient 超时/503 重试/状态码 | ✔ |
| M09 | 工程实践 | Channel 通信队列、并发锁、超时重试(退避+抖动)、断线重连状态机、数据质量(Stale)、Serilog 日志、行业协议概念卡 | ✔ |

## 目录结构

```
CommLab/
├── CommLab.sln
├── src/CommLab/
│   ├── Program.cs              # 菜单入口
│   ├── Core/                   # 通用工具：字节/CRC/大小端/帧解析/自测
│   ├── Fakes/                  # 进程内假设备：假串口/假Modbus/假PLC/假OPC UA
│   └── Modules/                # M01~M09 各模块
└── docs/
    ├── 行业协议概念卡.md        # CAN/EtherCAT/PROFINET/EtherNet/IP/机器人/相机/MES
    └── 知识点对照.md            # 学习路线 → 模块章节 对照表
```

## 面试速记（每个模块末尾都有"面试话术"章节）

1. **Modbus**："RTU 是串口上的原始帧+CRC，TCP 是 MBAP 头+PDU；功能码 01/02 读线圈/离散输入，03/04 读保持/输入寄存器，05/06 写单个，15/16 写多个；异常码 = 功能码|0x80+代码。"
2. **TCP 粘包**："TCP 是字节流没有边界，必须靠协议定边界——长度前缀或分隔符，接收端用流式解析器累积到完整帧才处理。"
3. **MQTT**："轻量 Pub/Sub，QoS0/1/2、保留消息存状态、遗嘱消息做掉线报警。"
4. **OPC UA**："带类型带安全的工业数据标准，地址空间+服务+安全三层；订阅+死区代替轮询。"
5. **工程实践**："Channel 解耦+背压、每设备一把锁、可恢复错误才重试、连接状态机+心跳、数据带质量位、结构化日志。"

## 接真实硬件（可选）

本项目默认全假设备；要接真实设备时：

- **串口**：装 com0com 虚拟串口对（COM3↔COM4），把 `M02` 里的 `FakeSerial` 换成 `System.IO.Ports.SerialPort`（包已引 `System.IO.Ports`），两端各开一个进程即可对发；
- **Modbus**：Modbus Poll/Slave 软件当从站，`M04` 的客户端代码换成真实 TCP 连接 `127.0.0.1:502`；
- **PLC**：西门子 TIA Portal 仿真器 / 三菱 GX Simulator / 欧姆龙 CX-Simulator，IP 指向仿真器；
- **OPC UA**：用 UaExpert 连任意真实服务器（如 Prosys 试用版），把 `FakeOpcUaServer` 换成 `Opc.Ua.Client` 的 `Session`；
- **MQTT**：把进程内 broker 换成 Mosquitto/EMQX，客户端代码不变。

## 依赖

| 包 | 用途 |
|---|---|
| MQTTnet 5.2 + MQTTnet.Server 5.2 | M08 进程内 broker + 客户端 |
| OPCFoundation.NetStandard.Opc.Ua 2.0.1 | M07 真实 OPC UA 类型 |
| Serilog 4.4 + Console + File | M09 结构化日志 |
| System.IO.Ports 10 | M02 串口（假实现，接真机时直接用） |
