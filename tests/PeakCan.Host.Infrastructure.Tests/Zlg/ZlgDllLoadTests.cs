using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 不需要硬件的加载守卫：测试宿主必须是 x86，随包 zlgcan.dll 必须是 x86，
/// 且所有 ZCAN 入口存在（P/Invoke 是惰性绑定，名字/参数错误只有真调用时才炸；
/// 这里用 NativeLibrary 逐个查导出名，不打开任何真实设备）。
/// </summary>
public sealed class ZlgDllLoadTests
{
    private static readonly string[] ZcanExports =
    {
        "ZCAN_OpenDevice",
        "ZCAN_CloseDevice",
        "ZCAN_GetDeviceInf",
        "ZCAN_InitCAN",
        "ZCAN_StartCAN",
        "ZCAN_ResetCAN",
        "ZCAN_ClearBuffer",
        "ZCAN_GetReceiveNum",
        "ZCAN_Receive",
        "ZCAN_ReceiveFD",
        "ZCAN_Transmit",
        "ZCAN_TransmitFD",
        "ZCAN_SetValue",
    };

    [Fact]
    public void TestHost_IsX86()
    {
        Environment.Is64BitProcess.Should().BeFalse(
            "zlgcan.dll 是 32 位，测试宿主必须同为 x86");
    }

    [Fact]
    public void BundledDll_IsX86()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "zlgcan.dll");
        File.Exists(path).Should().BeTrue($"expected bundled DLL at {path}");
        ReadPeMachine(path).Should().Be(0x014C, "zlgcan.dll must be an i386 PE");
    }

    [Fact]
    public void ZcanEntryPoints_Resolve()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "zlgcan.dll");
        var library = NativeLibrary.Load(path);
        try
        {
            foreach (var export in ZcanExports)
            {
                NativeLibrary.TryGetExport(library, export, out _)
                    .Should().BeTrue($"{export} must be exported by zlgcan.dll");
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void BundledKernelDlls_Contain200URuntime()
    {
        var kernelDir = Path.Combine(AppContext.BaseDirectory, "kerneldlls");
        var configPath = Path.Combine(kernelDir, "dll_cfg.ini");

        File.Exists(configPath).Should().BeTrue(
            "ZCAN_OpenDevice(41, ...) requires the vendor kernel-plugin configuration");
        File.Exists(Path.Combine(kernelDir, "USBCANFD.dll")).Should().BeTrue(
            "device type 41 maps to kerneldlls/USBCANFD.dll");
        File.Exists(Path.Combine(kernelDir, "devices_property", "usbcanfd-200u.xml")).Should().BeTrue(
            "the matching device-property payload must ship with the kernel plugin");
        File.ReadAllText(configPath).Should().Contain(
            "USBCANFD.dll 41 32 1", "the vendor config must map USBCANFD-200U to its kernel DLL");
    }

    [Fact]
    public void BundledUsbCanFdKernelDll_IsX86()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "kerneldlls", "USBCANFD.dll");
        File.Exists(path).Should().BeTrue($"expected bundled kernel DLL at {path}");
        ReadPeMachine(path).Should().Be(0x014C, "USBCANFD.dll must match the x86 host");
    }

    [Fact]
    public void BundledZlgLicense_Exists()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "zlgcan License.txt");
        File.Exists(path).Should().BeTrue(
            "the vendor binary-redistribution license must ship beside zlgcan.dll");
    }

    private static ushort ReadPeMachine(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        stream.Position = peOffset + 4;
        return reader.ReadUInt16();
    }
}
