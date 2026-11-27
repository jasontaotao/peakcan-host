using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using PeakCan.Host.Infrastructure.Zlg;

namespace ZlgSmoke;

/// <summary>
/// ZLG USBCANFD 只读冒烟工具：验证 DLL 位数、ZCAN 入口、设备枚举、
/// 可选 Init/Start/监听。不发送任何报文，便于外部用户安全回传输出。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitDll = 2;
    private const int ExitNoDevice = 3;
    private const int ExitInit = 4;
    private const uint MaxPlausibleFrames = 100_000;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Diagnostic tool must report every failure and return a stable exit code.")]
    private static int Main(string[] args)
    {
        var options = SmokeOptions.Parse(args);
        if (options is null)
        {
            PrintUsage();
            return ExitUsage;
        }

        try
        {
            PrintEnvironment();
            PrintAbi();
            PrintDllSearchDiagnostics();
            var found = EnumerateDevices();
            if (options.Listen is { } listen)
                return Listen(options, listen);

            return found ? ExitOk : ExitNoDevice;
        }
        catch (DllNotFoundException ex)
        {
            Console.Error.WriteLine(
                $"[FATAL] zlgcan.dll 未找到（必须是 x86 且与 exe 同目录）：{ex.Message}");
            return ExitDll;
        }
        catch (BadImageFormatException ex)
        {
            Console.Error.WriteLine(
                $"[FATAL] zlgcan.dll 位数不匹配（本工具必须 32 位/x86）：{ex.Message}");
            return ExitDll;
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.Error.WriteLine(
                $"[FATAL] zlgcan.dll 版本不匹配（缺少 ZCAN 入口）：{ex.Message}");
            return ExitDll;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return ExitDll;
        }
    }

    private static void PrintEnvironment()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "zlgcan.dll");
        Console.WriteLine("== Environment ==");
        Console.WriteLine(FormattableString.Invariant(
            $"Is64BitProcess={Environment.Is64BitProcess} IntPtr.Size={IntPtr.Size}"));
        Console.WriteLine($"OS={RuntimeInformation.OSDescription}");
        Console.WriteLine($"DLL={dllPath} exists={File.Exists(dllPath)}");
    }

    private static void PrintAbi()
    {
        Console.WriteLine("== ABI ==");
        Console.WriteLine(FormattableString.Invariant(
            $"can_frame={Marshal.SizeOf<ZcanCanFrame>()} canfd_frame={Marshal.SizeOf<ZcanCanFdFrame>()} receive={Marshal.SizeOf<ZcanReceiveData>()} receivefd={Marshal.SizeOf<ZcanReceiveFdData>()} initcfg={Marshal.SizeOf<ZcanChannelInitConfig>()} devinfo={Marshal.SizeOf<ZcanDeviceInfo>()}"));
    }

    /// <summary>
    /// ZCAN_OpenDevice 返回 0 时，最常见的原因不是驱动缺失，而是 zlgcan.dll
    /// 按裸名 LoadLibraryA("USBCANFD.dll") 加载插件时命中了 System32/PATH 里的
    /// 旧版本，忽略了随包 kerneldlls。这里按同一顺序复现一次加载路径，
    /// 把"最终用的是哪个文件"变成可回传的事实。加载的模块故意不释放：
    /// Windows 会复用已加载的模块名，后续 zlgcan.dll 自己的 LoadLibraryA 拿到的
    /// 就是这里记录的那份。
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Diagnostic probe must keep reporting after any loader failure.")]
    private static void PrintDllSearchDiagnostics()
    {
        Console.WriteLine("\n== DLL search diagnosis ==");

        var exeDir = AppContext.BaseDirectory;
        var kernelDir = Path.Combine(exeDir, "kerneldlls");
        Console.WriteLine($"ExeDir={exeDir}");
        Console.WriteLine($"CurrentDirectory={Directory.GetCurrentDirectory()}");
        Console.WriteLine(FormattableString.Invariant(
            $"kerneldlls dir={kernelDir} exists={Directory.Exists(kernelDir)} dll_cfg.ini={File.Exists(Path.Combine(kernelDir, "dll_cfg.ini"))} usbcanfd={File.Exists(Path.Combine(kernelDir, "USBCANFD.dll"))}"));

        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        foreach (var name in new[] { "USBCANFD.dll", "USBCANFD800U.dll" })
        {
            Console.WriteLine(
                $"  System32\\{name} exists={File.Exists(Path.Combine(sys32, name))} " +
                $"SysWOW64\\{name} exists={File.Exists(Path.Combine(sysWow64, name))}");
        }

        // 1) zlgcan.dll 自身的实际加载位置（P/Invoke 惰性绑定，先显式加载才能查到）。
        var zlgcanHandle = LoadLibraryA(Path.Combine(exeDir, "zlgcan.dll"));
        Console.WriteLine(
            $"  LoadLibraryA(zlgcan.dll) handle=0x{zlgcanHandle.ToInt64():X} " +
            $"err={Marshal.GetLastWin32Error()} path={ModulePath(zlgcanHandle)}");

        // 2) 复现 zlgcan.dll 的插件搜索顺序：先裸名。exe 同目录里没有
        //    USBCANFD.dll（它在 kerneldlls 子目录），所以裸名只能命中
        //    System32/PATH 里的版本，或者直接失败。
        var bareHandle = LoadLibraryA("USBCANFD.dll");
        var barePath = ModulePath(bareHandle);
        Console.WriteLine(
            $"  LoadLibraryA(\"USBCANFD.dll\") handle=0x{bareHandle.ToInt64():X} " +
            $"err={Marshal.GetLastWin32Error()} path={barePath}");

        // 3) 裸名命中了随包目录以外的文件 = 版本被遮蔽。卸载它，改加载随包那份；
        //    否则 zlgcan.dll 之后自己调 LoadLibraryA("USBCANFD.dll") 仍会拿到旧版本。
        var bundledPath = Path.Combine(kernelDir, "USBCANFD.dll");
        if (bareHandle != IntPtr.Zero
            && !barePath.StartsWith(kernelDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  [SHADOWED] bare-name load picked a file outside kerneldlls");
            FreeLibrary(bareHandle);
        }

        if (GetModuleHandle("USBCANFD.dll") == IntPtr.Zero)
        {
            var bundled = LoadLibraryA(bundledPath);
            Console.WriteLine(
                $"  LoadLibraryA(kerneldlls\\USBCANFD.dll) handle=0x{bundled.ToInt64():X} " +
                $"err={Marshal.GetLastWin32Error()} path={ModulePath(bundled)}");
        }

        var resolvedByZlgcan = GetModuleHandle("USBCANFD.dll");
        Console.WriteLine(
            "  => plugin actually visible to zlgcan.dll: " +
            (resolvedByZlgcan == IntPtr.Zero ? "(none loaded)" : ModulePath(resolvedByZlgcan)));
    }

    private static string ModulePath(IntPtr module)
    {
        if (module == IntPtr.Zero) return "(null)";
        var buffer = new char[1024];
        var length = GetModuleFileName(module, buffer, (uint)buffer.Length);
        return length == 0 ? "(unknown)" : new string(buffer, 0, (int)length);
    }

    // 诊断用 kernel32 绑定；smoke 工具单独声明，不进入生产程序集。
    [SuppressMessage(
        "Interoperability",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "Module names are ASCII.")]
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibraryA([MarshalAs(UnmanagedType.LPStr)] string fileName);

    [SuppressMessage(
        "Interoperability",
        "CA2101:Specify marshaling for P/Invoke string arguments",
        Justification = "Module names are ASCII.")]
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPStr)] string moduleName);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(
        IntPtr module,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] char[] path,
        uint size);

    private static bool EnumerateDevices()
    {
        Console.WriteLine("\n== Devices ==");
        var found = false;

        foreach (var type in ZlgDeviceType.All)
        {
            for (uint idx = 0; idx < 4; idx++)
            {
                var handle = ZlgNative.ZCAN_OpenDevice(type, idx, 0);
                if (handle == IntPtr.Zero)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"type={type} idx={idx} OpenDevice=0"));
                    continue;
                }

                found = true;
                Console.WriteLine(FormattableString.Invariant(
                    $"type={type} idx={idx} OpenDevice handle=0x{handle.ToInt64():X}"));

                var info = ZcanDeviceInfo.Create();
                var ret = ZlgNative.ZCAN_GetDeviceInf(handle, ref info);
                Console.WriteLine(FormattableString.Invariant(
                    $"  GetDeviceInf ret={ret} can_Num={info.CanNum} hwType={ReadAscii(info.HwType)} serial={ReadAscii(info.SerialNum)}"));

                Console.WriteLine(FormattableString.Invariant(
                    $"  CloseDevice ret={ZlgNative.ZCAN_CloseDevice(handle)}"));
            }
        }

        return found;
    }

    private static int Listen(SmokeOptions options, TimeSpan duration)
    {
        Console.WriteLine(FormattableString.Invariant(
            $"\n== Listen type={options.Type} dev={options.Dev} idx={options.Index} fd={options.Fd} baud={options.Baud} dbit={options.Dbit} =="));

        var handle = ZlgNative.ZCAN_OpenDevice(options.Type, options.Dev, 0);
        Console.WriteLine(FormattableString.Invariant(
            $"OpenDevice handle=0x{handle.ToInt64():X}"));
        if (handle == IntPtr.Zero) return ExitNoDevice;

        try
        {
            SetValue(handle, options.Index, "canfd_abit_baud_rate",
                options.Baud.ToString(CultureInfo.InvariantCulture));

            var config = new ZcanChannelInitConfig
            {
                CanType = options.Fd ? ZcanCanType.CanFd : ZcanCanType.Can,
            };

            if (options.Fd)
            {
                SetValue(handle, options.Index, "canfd_dbit_baud_rate",
                    options.Dbit.ToString(CultureInfo.InvariantCulture));
                SetValue(handle, options.Index, "canfd_standard", "0");
                config.CanFd = new ZcanCanFdInit { AccMask = 0xFFFF_FFFF };
            }
            else
            {
                config.Can = new ZcanCanInit { AccMask = 0xFFFF_FFFF };
            }

            foreach (var (path, value) in options.Sets)
                Console.WriteLine(FormattableString.Invariant(
                    $"SetValue {path}={value} ret={ZlgNative.ZCAN_SetValue(handle, path, value)}"));

            var channel = ZlgNative.ZCAN_InitCAN(handle, options.Index, ref config);
            Console.WriteLine(FormattableString.Invariant(
                $"InitCAN handle=0x{channel.ToInt64():X}"));
            if (channel == IntPtr.Zero) return ExitInit;

            var start = ZlgNative.ZCAN_StartCAN(channel);
            Console.WriteLine(FormattableString.Invariant($"StartCAN ret={start}"));
            if (start != 1)
            {
                _ = ZlgNative.ZCAN_ResetCAN(channel);
                return ExitInit;
            }

            var classicBuffer = CreateClassicBuffer();
            var fdbuffer = CreateFdBuffer();
            var deadline = DateTime.UtcNow + duration;
            var total = 0;

            while (DateTime.UtcNow < deadline)
            {
                total += DrainClassic(channel, classicBuffer);
                total += DrainFd(channel, fdbuffer);
                Thread.Sleep(5);
            }

            Console.WriteLine(FormattableString.Invariant($"frames_received={total}"));
            Console.WriteLine(FormattableString.Invariant(
                $"ResetCAN ret={ZlgNative.ZCAN_ResetCAN(channel)}"));
            return ExitOk;
        }
        finally
        {
            Console.WriteLine(FormattableString.Invariant(
                $"CloseDevice ret={ZlgNative.ZCAN_CloseDevice(handle)}"));
        }
    }

    private static void SetValue(IntPtr handle, uint canIndex, string path, string value)
    {
        var fullPath = string.Create(
            CultureInfo.InvariantCulture, $"{canIndex}/{path}");
        var ret = ZlgNative.ZCAN_SetValue(handle, fullPath, value);
        Console.WriteLine(FormattableString.Invariant(
            $"SetValue {fullPath}={value} ret={ret}"));
    }

    private static int DrainClassic(IntPtr channel, ZcanReceiveData[] buffer)
    {
        var available = ZlgNative.ZCAN_GetReceiveNum(channel, ZcanReceiveType.Can);
        if (available == 0 || available > MaxPlausibleFrames) return 0;

        var want = Math.Min(available, (uint)buffer.Length);
        var read = ZlgNative.ZCAN_Receive(channel, buffer, want, 0);
        for (uint i = 0; i < read && i < (uint)buffer.Length; i++)
        {
            var frame = buffer[i].Frame;
            var id = frame.CanId & ZcanCanIdFlags.IdMask;
            var extended = (frame.CanId & ZcanCanIdFlags.Extended) != 0 ? 1 : 0;
            Console.WriteLine(FormattableString.Invariant(
                $"  CAN  id=0x{id:X} ext={extended} dlc={frame.CanDlc} data={HexBytes(frame.Data, frame.CanDlc)} ts_us={buffer[i].Timestamp}"));
        }

        return (int)read;
    }

    private static int DrainFd(IntPtr channel, ZcanReceiveFdData[] buffer)
    {
        var available = ZlgNative.ZCAN_GetReceiveNum(channel, ZcanReceiveType.CanFd);
        if (available == 0 || available > MaxPlausibleFrames) return 0;

        var want = Math.Min(available, (uint)buffer.Length);
        var read = ZlgNative.ZCAN_ReceiveFD(channel, buffer, want, 0);
        for (uint i = 0; i < read && i < (uint)buffer.Length; i++)
        {
            var frame = buffer[i].Frame;
            var id = frame.CanId & ZcanCanIdFlags.IdMask;
            var extended = (frame.CanId & ZcanCanIdFlags.Extended) != 0 ? 1 : 0;
            Console.WriteLine(FormattableString.Invariant(
                $"  CANFD id=0x{id:X} ext={extended} len={frame.Len} flags=0x{frame.Flags:X2} data={HexBytes(frame.Data, frame.Len)} ts_us={buffer[i].Timestamp}"));
        }

        return (int)read;
    }

    private static ZcanReceiveData[] CreateClassicBuffer()
    {
        var buffer = new ZcanReceiveData[32];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = ZcanReceiveData.Create();
        return buffer;
    }

    private static ZcanReceiveFdData[] CreateFdBuffer()
    {
        var buffer = new ZcanReceiveFdData[32];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = ZcanReceiveFdData.Create();
        return buffer;
    }

    private static string ReadAscii(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end).Trim();
    }

    private static string HexBytes(byte[]? data, int length)
    {
        if (data is null) return string.Empty;
        var count = Math.Min(Math.Max(length, 0), data.Length);
        return Convert.ToHexString(data.AsSpan(0, count));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            zlg-smoke —— ZLG USBCANFD 只读诊断工具（x86）

            用法:
              zlg-smoke.exe [选项]

            选项:
              --type <41|42|43|59|76>  设备类型，默认 41 (USBCANFD-200U)
              --dev <n>                设备索引，默认 0
              --idx <n>                通道索引，默认 0
              --baud <bps>             仲裁段波特率，默认 500000
              --dbit <bps>             FD 数据段波特率，默认 2000000
              --fd                     以 CAN FD 模式初始化
              --listen <5s|500ms>      初始化并只读监听指定时长
              --set <path=value>       透传 ZCAN_SetValue（可重复），path 为完整路径
              --help                   显示本帮助

            退出码: 0=发现设备/成功, 2=DLL 加载失败, 3=无设备, 4=Init/Start 失败
            """);
    }
}

/// <summary>命令行选项。</summary>
internal sealed record SmokeOptions(
    uint Type,
    uint Dev,
    uint Index,
    int Baud,
    int Dbit,
    bool Fd,
    TimeSpan? Listen,
    IReadOnlyList<(string Path, string Value)> Sets)
{
    public static SmokeOptions? Parse(string[] args)
    {
        var type = ZlgDeviceType.USBCANFD_200U;
        uint dev = 0;
        uint index = 0;
        var baud = 500_000;
        var dbit = 2_000_000;
        var fd = false;
        TimeSpan? listen = null;
        var sets = new List<(string Path, string Value)>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type":
                    if (!TryUInt(args, ref i, out type)) return null;
                    break;
                case "--dev":
                    if (!TryUInt(args, ref i, out dev)) return null;
                    break;
                case "--idx":
                    if (!TryUInt(args, ref i, out index)) return null;
                    break;
                case "--baud":
                    if (!TryInt(args, ref i, out baud)) return null;
                    break;
                case "--dbit":
                    if (!TryInt(args, ref i, out dbit)) return null;
                    break;
                case "--fd":
                    fd = true;
                    break;
                case "--listen":
                    if (i + 1 >= args.Length || !TryParseDuration(args[++i], out var duration))
                        return null;
                    listen = duration;
                    break;
                case "--set":
                    if (i + 1 >= args.Length) return null;
                    var pair = args[++i];
                    var equals = pair.IndexOf('=', StringComparison.Ordinal);
                    if (equals <= 0) return null;
                    sets.Add((pair[..equals], pair[(equals + 1)..]));
                    break;
                case "--help":
                case "-h":
                    return null;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    return null;
            }
        }

        return new SmokeOptions(type, dev, index, baud, dbit, fd, listen, sets);
    }

    private static bool TryUInt(string[] args, ref int index, out uint value)
    {
        value = 0;
        return index + 1 < args.Length
            && uint.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(string[] args, ref int index, out int value)
    {
        value = 0;
        return index + 1 < args.Length
            && int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;

        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                text.AsSpan(0, text.Length - 2),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var milliseconds)
            && milliseconds > 0)
        {
            value = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }

        if (text.EndsWith('s')
            && int.TryParse(
                text.AsSpan(0, text.Length - 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds > 0)
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)
            && plain > 0)
        {
            value = TimeSpan.FromSeconds(plain);
            return true;
        }

        return false;
    }
}
