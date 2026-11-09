using CommLab.Core;
using CommLab.Modules;

// CommLab —— 上位机通信知识点示例项目
// 用法：
//   dotnet run                 交互式菜单
//   dotnet run -- 0            一键跑全部模块（每个模块末尾有自测）
//   dotnet run -- 4            只跑 M04 Modbus

IModule[] modules =
{
    new M01Basics(),
    new M02Serial(),
    new M03TcpUdp(),
    new M04Modbus(),
    new M05Protocol(),
    new M06Plc(),
    new M07OpcUa(),
    new M08MqttHttp(),
    new M09Engineering(),
};

if (args.Length > 0 && int.TryParse(args[0], out int idx))
{
    if (idx == 0)
    {
        Console.WriteLine("CommLab：一键跑全部模块");
        foreach (var m in modules)
        {
            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"▶ {m.Title}");
            await m.RunAsync();
        }
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("全部模块执行完毕");
        SelfTest.PrintSummary();
        return;
    }
    if (idx >= 1 && idx <= modules.Length)
    {
        Console.WriteLine($"CommLab：只跑 {modules[idx - 1].Title}");
        await modules[idx - 1].RunAsync();
        SelfTest.PrintSummary();
        return;
    }
    Console.WriteLine($"参数无效：0=全部，1~{modules.Length}=对应模块");
    return;
}

// 交互菜单
while (true)
{
    Console.WriteLine("\n========= CommLab 上位机通信示例 =========");
    Console.WriteLine("  0) 一键跑全部模块（含自测）");
    for (int i = 0; i < modules.Length; i++)
        Console.WriteLine($"  {i + 1}) {modules[i].Title}");
    Console.WriteLine("  q) 退出");
    Console.Write("请选择: ");
    var input = Console.ReadLine()?.Trim();
    if (input is "q" or "Q") break;
    if (int.TryParse(input, out int sel) && sel >= 0 && sel <= modules.Length)
    {
        if (sel == 0)
        {
            foreach (var m in modules)
            {
                Console.WriteLine(new string('=', 70));
                Console.WriteLine($"▶ {m.Title}");
                await m.RunAsync();
            }
            SelfTest.PrintSummary();
        }
        else
        {
            await modules[sel - 1].RunAsync();
            SelfTest.PrintSummary();
        }
    }
}
