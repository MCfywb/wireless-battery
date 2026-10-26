using Windows.Storage.Streams;

/// <summary>
/// Socket 输入流的帧读取器。
/// </summary>
/// <remarks>
/// <para>
/// WinRT 的 <see cref="DataReader"/> 不允许同时存在多个挂起的 <c>LoadAsync</c>：
/// 超时后若再发起一次，先前那个挂起读的句柄会失效，表现为
/// <c>COMException 0x800710DD（操作标识符不正确）</c>。
/// </para>
/// <para>
/// 本类保证<b>任何时刻至多一个挂起的读</b>：超时只是返回 null 并保留挂起任务，
/// 下一次调用继续等待同一个任务，而不是发起新的读。
/// </para>
/// </remarks>
public sealed class SocketFrameReader : IDisposable
{
    private readonly DataReader _reader;
    private Task<uint>? _pending;
    private bool _disposed;

    public SocketFrameReader(IInputStream inputStream)
    {
        _reader = new DataReader(inputStream) { InputStreamOptions = InputStreamOptions.Partial };
    }

    /// <summary>
    /// 读取一段数据。返回 null 表示本次等待超时（挂起读被保留，下一次继续等待）或流已结束。
    /// </summary>
    public async Task<byte[]?> ReadAsync(int timeoutMs)
    {
        if (_disposed)
        {
            return null;
        }

        _pending ??= _reader.LoadAsync(4096).AsTask();

        var completed = await Task.WhenAny(_pending, Task.Delay(timeoutMs));
        if (completed != _pending)
        {
            // 超时：保留挂起读，不发起新的 LoadAsync
            return null;
        }

        uint count;
        try
        {
            count = await _pending;
        }
        catch (Exception ex)
        {
            _pending = null;
            throw new IOException($"读取 socket 输入流失败：{ex.Message}", ex);
        }

        _pending = null;
        if (count == 0)
        {
            return null;
        }

        var buffer = new byte[count];
        _reader.ReadBytes(buffer);
        return buffer;
    }

    /// <summary>在给定时间窗内读取并拼接全部数据。</summary>
    public async Task<List<byte>> CollectAsync(int windowMs)
    {
        var all = new List<byte>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < windowMs)
        {
            var remaining = (int)Math.Max(1, windowMs - sw.ElapsedMilliseconds);
            var chunk = await ReadAsync(Math.Min(remaining, 500));
            if (chunk is not null)
            {
                all.AddRange(chunk);
            }
        }

        return all;
    }

    public void Dispose()
    {
        _disposed = true;
        _pending = null;
        _reader.Dispose();
    }
}
