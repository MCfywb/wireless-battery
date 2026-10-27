using Windows.Storage.Streams;

namespace PeripheralBattery.RfcommResidentProbe;

/// <summary>
/// socket 输入流读取器：区分「超时」「对端关闭」「底层错误」三种结局。
/// </summary>
/// <remarks>
/// <para><b>为什么要区分</b>：常驻连接实验的核心问题之一是「耳机合盖断电后，
/// socket 到底以什么方式死掉」——读到 0 字节（EOF）、抛 <c>IOException</c>、
/// 还是抛 COM 错误，处置方式完全不同。因此这里<b>不吞异常</b>，
/// 让原始异常（含 HResult）一路冒到调用方去记录。</para>
/// <para>WinRT 的 <see cref="DataReader"/> 不允许同时存在多个挂起的 <c>LoadAsync</c>，
/// 否则报 <c>COMException 0x800710DD</c>。故任何时刻至多保留一个挂起读：
/// 超时只是返回 <c>null</c> 并保留挂起任务，下一次调用继续等同一个任务。</para>
/// </remarks>
internal sealed class GaiaSocketReader : IDisposable
{
    private readonly DataReader _reader;
    private Task<uint>? _pending;
    private bool _disposed;

    public GaiaSocketReader(IInputStream inputStream)
        => _reader = new DataReader(inputStream) { InputStreamOptions = InputStreamOptions.Partial };

    /// <summary>
    /// 读取一段数据。
    /// </summary>
    /// <returns>
    /// 正常读到数据时返回字节数组；等待超时返回 <c>null</c>（挂起读保留，下次继续等）；
    /// 对端关闭时抛 <see cref="EndOfStreamException"/>；底层错误原样抛出。
    /// </returns>
    public async Task<byte[]?> ReadAsync(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _pending ??= _reader.LoadAsync(4096).AsTask();

        var completed = await Task.WhenAny(_pending, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != _pending)
        {
            return null;
        }

        uint count;
        try
        {
            count = await _pending.ConfigureAwait(false);
        }
        catch
        {
            _pending = null;
            throw;
        }

        _pending = null;

        if (count == 0)
        {
            throw new EndOfStreamException("对端关闭连接：socket 输入流读到 0 字节");
        }

        var buffer = new byte[count];
        _reader.ReadBytes(buffer);
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 挂起读可能在此刻以异常结束；显式观察，避免未观测任务异常在进程退出时冒泡。
        if (_pending is not null)
        {
            _ = _pending.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        _pending = null;
        _reader.Dispose();
    }
}
