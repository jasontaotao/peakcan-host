using System.Collections.Concurrent;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 设备生命周期管理器。ZLG API 的设备句柄是进程级共享的：
/// OpenDevice 在同一设备上多次调用返回相同句柄，CloseDevice 一次关闭所有。
/// 本管理器维护引用计数，确保最后一个通道断开时才真正关闭设备。
/// </summary>
public sealed class ZlgDeviceManager : IDisposable
{
    // (devType, devIdx) → 引用计数
    private readonly ConcurrentDictionary<(uint, uint), int> _refCount = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>打开设备，引用计数 +1。如果设备尚未打开，调用 VCI_OpenDevice。</summary>
    public uint AcquireDevice(uint devType, uint devIdx)
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            var key = (devType, devIdx);
            var exists = _refCount.TryGetValue(key, out var count);
            if (exists)
            {
                _refCount[key] = count + 1;
                return ZlgError.Success;
            }

            // 首次打开
            var ret = ZlgNative.ZCAN_OpenDevice(devType, devIdx, 0);
            if (ret == ZlgError.Success)
                _refCount[key] = 1;
            return ret;
        }
    }

    /// <summary>释放设备，引用计数 -1。计数归零时调用 VCI_CloseDevice。</summary>
    public uint ReleaseDevice(uint devType, uint devIdx)
    {
        lock (_lock)
        {
            if (_disposed) return 0;

            var key = (devType, devIdx);
            if (!_refCount.TryGetValue(key, out var count) || count <= 0)
                return ZlgError.Failed;

            count--;
            if (count > 0)
            {
                _refCount[key] = count;
                return ZlgError.Success;
            }

            _refCount.TryRemove(key, out _);
            return ZlgNative.ZCAN_CloseDevice(devType, devIdx);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            // 关闭所有仍持有的设备
            foreach (var key in _refCount.Keys)
            {
                try { ZlgNative.ZCAN_CloseDevice(key.Item1, key.Item2); }
                catch { /* best-effort */ }
            }
            _refCount.Clear();
        }
    }
}