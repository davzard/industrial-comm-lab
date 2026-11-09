namespace CommLab.Core;

/// <summary>
/// 通用帧解析器（状态机）——串口/TCP/Modbus 收数据都靠它。
/// 核心思想：
///  1. 接收事件 ≠ 完整报文：一次 Read 可能只收到半个帧，也可能粘着两个帧；
///  2. 先把字节累积进缓冲区，找到帧头，按长度算出帧尾；
///  3. 校验通过才输出完整帧；校验失败丢弃一个字节重新同步（抗干扰）。
/// 面试点：这就是"拆包/粘包"处理的标准答案。
/// </summary>
public sealed class FrameParser
{
    private readonly byte[] _header;
    private readonly Func<byte[], int> _lengthOf;   // 从帧头开始算整帧长度
    private readonly Func<byte[], bool> _isValid;   // 校验（CRC/和校验）
    private byte[] _buffer = new byte[4096];
    private int _used;

    /// <param name="header">帧头，如 {0xAA,0x55}</param>
    /// <param name="lengthOf">根据帧头处的字节算出整帧长度（字节数）</param>
    /// <param name="isValid">整帧校验</param>
    public FrameParser(byte[] header, Func<byte[], int> lengthOf, Func<byte[], bool> isValid)
    {
        _header = header;
        _lengthOf = lengthOf;
        _isValid = isValid;
    }

    /// <summary>喂入一段收到的字节，返回其中所有完整帧（0 个或多个）</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> chunk)
    {
        var frames = new List<byte[]>();
        Append(chunk);
        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                // 没找到帧头：只保留最后 (header-1) 个字节（可能是半个帧头），其余当噪声丢弃
                int keep = Math.Max(0, _header.Length - 1);
                keep = Math.Min(keep, _used);
                if (_used > keep)
                {
                    Array.Copy(_buffer, _used - keep, _buffer, 0, keep);
                    _used = keep;
                }
                break;
            }
            if (start > 0) // 帧头前有噪声，丢弃
            {
                Array.Copy(_buffer, start, _buffer, 0, _used - start);
                _used -= start;
            }
            if (_used < _header.Length + 1) break; // 至少要能读到长度字段，等更多数据

            int total = _lengthOf(_buffer.AsSpan(0, _used).ToArray());
            if (total <= 0) break;                 // 长度字段还不可信，等更多数据
            if (_used < total) break;              // 帧还没收全，等更多数据

            var frame = _buffer.AsSpan(0, total).ToArray();
            if (_isValid(frame))
            {
                frames.Add(frame);
                RemovePrefix(total);
            }
            else
            {
                // 校验失败：丢弃帧头后第一个字节，重新找帧头（现场抗干扰的关键）
                RemovePrefix(_header.Length + 1);
            }
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

    private int FindHeader()
    {
        for (int i = 0; i + _header.Length <= _used; i++)
        {
            bool ok = true;
            for (int j = 0; j < _header.Length; j++)
                if (_buffer[i + j] != _header[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private void RemovePrefix(int n)
    {
        n = Math.Min(n, _used);
        Array.Copy(_buffer, n, _buffer, 0, _used - n);
        _used -= n;
    }
}
