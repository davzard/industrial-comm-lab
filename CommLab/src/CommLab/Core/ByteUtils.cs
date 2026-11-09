using System.Text;

namespace CommLab.Core;

/// <summary>
/// 字节数组与十六进制工具。
/// 面试点：为什么用十六进制看报文？——每个字节 2 个 hex 字符，紧凑且与协议文档一致。
/// </summary>
public static class ByteUtils
{
    /// <summary>字节数组 → 十六进制字符串，如 "01 03 00 00 00 0A C5 CD"</summary>
    public static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>十六进制字符串 → 字节数组（支持空格/逗号/0x 前缀，便于和抓包工具对照）</summary>
    public static byte[] FromHex(string hex)
    {
        var clean = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != ' ').ToArray())
            .Replace("0x", "").Replace("0X", "");
        var result = new byte[clean.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(clean.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return result;
    }

    /// <summary>拼接多个字节数组（组包常用）</summary>
    public static byte[] Combine(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>打印报文：十六进制 + ASCII 双视图（抓包分析的基本功）</summary>
    public static void PrintFrame(string label, ReadOnlySpan<byte> data)
    {
        var ascii = string.Concat(data.ToArray().Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.'));
        Console.WriteLine($"    {label,-14} HEX : {Hex(data)}");
        Console.WriteLine($"    {label,-14} ASC : '{ascii}'");
    }
}
