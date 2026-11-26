using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 设备生命周期管理器。ZCAN 的设备句柄是进程级共享资源：
/// 同一 (devType, devIdx) 重复 OpenDevice 会返回同一句柄，CloseDevice 一次即关闭。
/// 本管理器按引用计数持有句柄，保证最后一个通道断开时才真正关闭设备；
/// 枚举器也复用本管理器，避免探测时把正在使用的设备关掉。
/// </summary>
public sealed class ZlgDeviceManager : IDisposable
{
    private readonly IZlgApi _api;
    private readonly ILogger<ZlgDeviceManager> _logger;
    private readonly Dictionary<(uint DevType, uint DevIdx), Entry> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public ZlgDeviceManager(IZlgApi api, ILogger<ZlgDeviceManager>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger ?? NullLogger<ZlgDeviceManager>.Instance;
    }

    /// <summary>打开设备（首次）或增加引用计数。句柄为 0 表示失败。</summary>
    public bool TryAcquire(uint devType, uint devIdx, out IntPtr deviceHandle)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                deviceHandle = IntPtr.Zero;
                return false;
            }

            var key = (devType, devIdx);
            if (_devices.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                deviceHandle = entry.Handle;
                return true;
            }

            var handle = _api.OpenDevice(devType, devIdx, 0);
            if (handle == IntPtr.Zero)
            {
                deviceHandle = IntPtr.Zero;
                _logger.LogDebug(
                    "ZLG OpenDevice returned 0: devType={DevType} devIdx={DevIdx}", devType, devIdx);
                return false;
            }

            _devices[key] = new Entry { Handle = handle, RefCount = 1 };
            deviceHandle = handle;
            return true;
        }
    }

    /// <summary>减少引用计数；归零时关闭设备。</summary>
    public bool Release(uint devType, uint devIdx)
    {
        lock (_lock)
        {
            if (_disposed) return false;

            var key = (devType, devIdx);
            if (!_devices.TryGetValue(key, out var entry) || entry.RefCount <= 0)
                return false;

            entry.RefCount--;
            if (entry.RefCount > 0) return true;

            _devices.Remove(key);
            try
            {
                return _api.CloseDevice(entry.Handle) == ZlgError.Success;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ZLG CloseDevice threw: devType={DevType} devIdx={DevIdx}", devType, devIdx);
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var entry in _devices.Values)
            {
                try { _ = _api.CloseDevice(entry.Handle); }
                catch { /* best-effort：进程退出/设备已拔出 */ }
            }
            _devices.Clear();
        }
    }

    private sealed class Entry
    {
        public IntPtr Handle;
        public int RefCount;
    }
}
