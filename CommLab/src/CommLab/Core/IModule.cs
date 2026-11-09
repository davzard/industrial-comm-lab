namespace CommLab.Core;

/// <summary>每个通信模块的统一入口，由主菜单调用</summary>
public interface IModule
{
    string Title { get; }
    Task RunAsync();
}
