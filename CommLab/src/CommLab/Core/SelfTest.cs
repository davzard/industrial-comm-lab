namespace CommLab.Core;

/// <summary>
/// 自测断言工具：每个模块跑完打印 PASS/FAIL，"0" 一键跑全部时用于汇总。
/// </summary>
public static class SelfTest
{
    public static int FailedCount;
    public static int PassedCount;

    public static void Check(bool condition, string name)
    {
        if (condition)
        {
            PassedCount++;
            Console.WriteLine($"    [PASS] {name}");
        }
        else
        {
            FailedCount++;
            Console.WriteLine($"    [FAIL] {name}");
        }
    }

    public static void CheckEq<T>(T expected, T actual, string name)
        => Check(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{name}（期望 {expected}，实际 {actual}）");

    public static void CheckEq(byte[] expected, byte[] actual, string name)
        => Check(expected.AsSpan().SequenceEqual(actual),
            $"{name}（期望 {ByteUtils.Hex(expected)}，实际 {ByteUtils.Hex(actual)}）");

    public static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"  自测汇总：通过 {PassedCount} 项，失败 {FailedCount} 项");
        if (FailedCount > 0) Environment.ExitCode = 1;
    }
}
