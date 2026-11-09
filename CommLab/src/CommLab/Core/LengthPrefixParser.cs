using System.Text;

namespace CommLab.Core;

/// <summary>
/// 长度前缀分帧（解决 TCP 粘包/拆包最常用的一招）：
///   [4B 大端长度][payload...]
/// 与 FrameParser 同思想：喂字节、攒缓冲、凑齐一帧才输出。
/// 面试点：TCP 是字节流，没有"消息"边界，必须靠协议自己定边界——
///         长度前缀 / 固定帧长 / 分隔符 / 转义分隔符 四种，长度前缀最常用。
/// </summary>
public sealed class LengthPrefixParser
{
    private byte[] _buffer = new byte[65536];
    private int _used;

    /// <summary>组包：4 字节大端长度 + UTF8 载荷</summary>
    public static byte[] Encode(string s)
    {
        var payload = Encoding.UTF8.GetBytes(s);
        var result = new byte[4 + payload.Length];
        result[0] = (byte)(payload.Length >> 24);
        result[1] = (byte)(payload.Length >> 16);
        result[2] = (byte)(payload.Length >> 8);
        result[3] = (byte)payload.Length;
        payload.CopyTo(result, 4);
        return result;
    }

    /// <summary>喂入一段收到的字节，返回其中所有完整消息（0 个或多个）</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> chunk)
    {
        var frames = new List<byte[]>();
        Append(chunk);
        while (_used >= 4)
        {
            int len = (_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3];
            if (len < 0 || len > 1_000_000)
            {
                RemovePrefix(1); // 长度字段异常（流错位了）：丢 1 字节重新同步
                continue;
            }
            if (_used < 4 + len) break; // 这帧还没收全
            frames.Add(_buffer.AsSpan(4, len).ToArray());
            RemovePrefix(4 + len);
        }
        return frames;
    }

    public void Reset() => _used = 0;

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_used + chunk.Length > _buffer.Length)
        {
            var bigger = new byte[_buffer.Length * 2];
            _buffer.AsSpan(0, _used).CopyTo(bigger);
            _buffer = bigger;
        }
        chunk.CopyTo(_buffer.AsSpan(_used));
        _used += chunk.Length;
    }

    private void RemovePrefix(int n)
    {
        n = Math.Min(n, _used);
        Array.Copy(_buffer, n, _buffer, 0, _used - n);
        _used -= n;
    }
}
