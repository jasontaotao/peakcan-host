using IO = System.IO;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 读 profile 文件时先试独占打开（spec §10.1.2「profile 写入中不读」）。
/// <para>
/// 编辑器保存时文件可能正被写：这时读出来的是半截 YAML，解析会失败或更糟——
/// 解析出一个字段缺失的 profile 并被当成合法配置。用 <see cref="IO.FileShare.None"/>
/// 试开一次即可判定「是否有人在写」，读不到就明确返回 false，
/// 由调用方等下一个事件，而不是猜一个内容。
/// </para>
/// </summary>
public static class NmProfileFileReader
{
    /// <summary>
    /// 尝试读取。<paramref name="content"/> 在返回 false 时为 null。
    /// </summary>
    public static bool TryRead(string path, out string content)
    {
        content = string.Empty;
        try
        {
            using var stream = new IO.FileStream(
                path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.None);
            using var reader = new IO.StreamReader(stream);
            content = reader.ReadToEnd();
            return true;
        }
        catch (IO.IOException)
        {
            // 文件被独占（正在写）或读失败：都当作「这次读不到」，
            // 不能把异常吞掉当成空内容返回——那会让调用方解析出一个空 profile。
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
