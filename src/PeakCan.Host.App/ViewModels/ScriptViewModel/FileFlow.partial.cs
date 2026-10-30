using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: 脚本文件的打开 / 保存（ScriptView 工具栏那两个曾经是死绑定的按钮）。
    //
    // 关键约束：打开文件后除了改 ScriptText，还必须**请求视图把整段文本推回
    // WebView2**。只改 VM 的话，编辑器 textarea 里还是旧内容，用户下一次键入
    // 会用旧文本把刚打开的文件覆盖掉（ScriptView 有 input → scripts text 的单向回传）。
    // 用专门的事件而不是订阅 ScriptText：订阅会让用户每敲一个字都被回灌一次，
    // 光标每次都被顶回开头。
    //
    // 另两条数据安全约束（review 2026-10-29）：
    //   1. 按文件真实编码读写（UTF-8 / BOM / 系统 OEM 代码页），不能一路 UTF-8：
    //      GBK 脚本用替换回退读会静默变成 U+FFFD，用户改一行再保存就把原文写坏。
    //   2. textarea 会把 CRLF 归一成 LF；打开时先归一，保存时按源文件风格还原，
    //      否则"什么都没改"也会被判脏、一保存整份文件的行尾全变。

    private const string ScriptFileFilter = "JavaScript (*.js)|*.js|所有文件|*.*";

    /// <summary>最近一次打开/保存的路径；null = 还没和磁盘上的文件绑定。</summary>
    private string? _currentPath;

    /// <summary>磁盘上的内容（打开时的原文 / 最近一次保存的文本），行尾已归一为 LF。</summary>
    private string _savedText = "";

    /// <summary>源文件编码（保存时原样写回）。默认 UTF-8 无 BOM = 新建脚本。</summary>
    private Encoding _sourceEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>源文件 BOM（保存时原样写回；新建脚本为空）。</summary>
    private byte[] _sourcePreamble = Array.Empty<byte>();

    /// <summary>源文件换行风格（保存时还原；默认 LF）。</summary>
    private string _sourceNewLine = "\n";

    /// <summary>
    /// 打开时解码有损（OEM 回退后仍出现替换字符，或退到 Latin-1）。保存会覆盖原文件，
    /// 因此保存前要再问一次。
    /// </summary>
    private bool _sourceWasLossy;

    /// <summary>编辑器内容是否与磁盘不一致（两侧都是归一后的 LF 文本）。</summary>
    public bool HasUnsavedChanges => ScriptText != _savedText;

    /// <summary>当前脚本文件路径（未打开/未保存过为 null）。</summary>
    public string? CurrentFilePath => _currentPath;

    /// <summary>
    /// C# 侧要求**整段替换**编辑器内容的请求（打开文件后触发）。
    /// 视图订阅它把新文本推回 WebView2；WebView 尚未初始化时视图可安全忽略——
    /// 页面重建时用的是 <see cref="ScriptText"/>，内容不会丢。
    /// </summary>
    public event Action<string>? EditorTextReplaceRequested;

    /// <summary>打开一个脚本文件到编辑器。</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        if (_fileDialog is null)
        {
            // 装配缺失时也要说出来：否则按钮又变回"点了没反应"。
            StatusText = "文件对话框不可用";
            return;
        }

        var path = _fileDialog.ShowOpenDialog(ScriptFileFilter);
        if (path is null)
        {
            StatusText = "已取消打开";
            return;
        }

        await OpenPathAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// 把指定路径的脚本装载进编辑器。对话框入口与「最近打开」共用同一条路径，
    /// 因而未保存确认、编码处理、草稿清理对所有入口都一致。
    /// </summary>
    private async Task<bool> OpenPathAsync(string path)
    {
        // 有未保存改动先问一句：打开会整段覆盖编辑器。
        // _prompt 为 null（测试构造点）时不做确认——生产 DI 必注入。
        if (HasUnsavedChanges && !string.IsNullOrWhiteSpace(ScriptText) && _prompt is not null)
        {
            var result = await _prompt.ShowAsync(
                "打开脚本",
                "当前脚本有未保存的改动，打开新文件会丢弃它们。\n\n继续打开吗？",
                Application.Current?.MainWindow).ConfigureAwait(true);
            if (result != MessageBoxResult.Yes)
            {
                StatusText = "已取消打开";
                return false;
            }
        }

        bool opened;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            var (text, encoding, preamble, lossy) = DecodeScript(bytes);
            // 先定基线再改 ScriptText（顺序约束见 ApplyBaseline 注释）。
            ApplyBaseline(text, encoding, preamble, lossy, path);

            // 这是"装载"不是"用户编辑"：打开期间不要触发草稿落盘。
            _suppressDraftPersist = true;
            try
            {
                ScriptText = _savedText;
            }
            finally
            {
                _suppressDraftPersist = false;
            }
            // 文本与当前内容相同时 setter 不触发通知，这里补一次。
            OnPropertyChanged(nameof(HasUnsavedChanges));

            StatusText = _sourceWasLossy
                ? $"已打开 {Path.GetFileName(path)}（部分字符无法解码，保存前请确认内容）"
                : $"已打开 {Path.GetFileName(path)}";
            LogScriptOpened(_logger, path);
            EditorTextReplaceRequested?.Invoke(_savedText);
            // 之前的草稿属于别的脚本，已经过期。
            PersistDraft();
            opened = true;
        }
        catch (Exception ex)
        {
            // 不静默：读不到文件（权限/文件被删）要留在状态栏上。
            StatusText = $"打开失败：{ex.Message}";
            LogScriptOpenFailed(_logger, path, ex);
            opened = false;
        }

        // 最近列表是旁路信息：它自己出问题不能让"文件已经打开了"变成"打开失败"。
        if (opened) RememberScript(path);
        return opened;
    }

    /// <summary>保存当前脚本；没有绑定路径时先弹保存框。</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_fileDialog is null)
        {
            StatusText = "文件对话框不可用";
            return;
        }

        var path = _currentPath;
        if (path is null)
        {
            path = _fileDialog.ShowSaveDialog(ScriptFileFilter, ".js", null);
            if (path is null)
            {
                StatusText = "已取消保存";
                return;
            }
        }

        if (await WriteScriptAsync(path).ConfigureAwait(true))
        {
            StatusText = $"已保存 {Path.GetFileName(path)}";
        }
    }

    /// <summary>另存为：始终弹保存框（绑定过路径后也能换文件名）。</summary>
    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_fileDialog is null)
        {
            StatusText = "文件对话框不可用";
            return;
        }

        var path = _fileDialog.ShowSaveDialog(
            ScriptFileFilter, ".js", Path.GetDirectoryName(_currentPath));
        if (path is null)
        {
            StatusText = "已取消另存为";
            return;
        }

        if (await WriteScriptAsync(path).ConfigureAwait(true))
        {
            StatusText = $"已另存为 {Path.GetFileName(path)}";
        }
    }

    /// <summary>
    /// 按源文件口径（编码 / BOM / 行尾）把当前文本写到 <paramref name="path"/>。
    /// 失败时把原因写进状态栏并返回 false。
    /// </summary>
    private async Task<bool> WriteScriptAsync(string path)
    {
        // 有损解码只威胁"覆盖原文件"；另存到新路径不覆盖任何东西，警告就是假话。
        if (_sourceWasLossy && _prompt is not null
            && string.Equals(path, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            var result = await _prompt.ShowAsync(
                "保存脚本",
                "这个文件里有无法解码的字符（编码可能不标准），保存会覆盖原文件。\n\n仍要保存吗？",
                Application.Current?.MainWindow).ConfigureAwait(true);
            if (result != MessageBoxResult.Yes)
            {
                StatusText = "已取消保存";
                return false;
            }
        }

        bool saved;
        try
        {
            // 先取快照再写：await 期间用户可能继续敲键。写完再读 ScriptText 会把
            // "磁盘上是旧文本、状态却显示已保存" 这种谎报留下，下一次打开就不问了。
            var toSave = ScriptText;
            var content = _sourceNewLine == "\r\n"
                ? toSave.Replace("\n", "\r\n", StringComparison.Ordinal)
                : toSave;
            var body = _sourceEncoding.GetBytes(content);
            var bytes = _sourcePreamble.Length == 0
                ? body
                : _sourcePreamble.Concat(body).ToArray();

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            _currentPath = path;
            _savedText = toSave;
            // 原文件里读不出来的字节已经被这次写入取代：不必每次保存都再问一遍。
            _sourceWasLossy = false;
            OnPropertyChanged(nameof(CurrentFilePath));
            OnPropertyChanged(nameof(HasUnsavedChanges));
            LogScriptSaved(_logger, path);
            // 保存后内容与磁盘一致 → 草稿应当消失。
            PersistDraft();
            saved = true;
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
            LogScriptSaveFailed(_logger, path, ex);
            saved = false;
        }

        // 磁盘已经写成功了，最近列表出问题不能反向把这次保存报成失败。
        // 刚保存的文件也是"以后可能还要开"的文件。
        if (saved) RememberScript(path);
        return saved;
    }

    /// <summary>
    /// 设定"磁盘基线"（内容 / 编码 / BOM / 行尾 / 路径）。
    /// 调用方必须在改 <see cref="ScriptText"/> **之前**调用：ScriptText 的 setter 依赖
    /// `_savedText` 判脏，顺序反了会把刚打开的干净文件算成脏的。
    /// </summary>
    private void ApplyBaseline(string text, Encoding encoding, byte[] preamble, bool lossy, string path)
    {
        _sourceEncoding = encoding;
        _sourcePreamble = preamble;
        _sourceWasLossy = lossy;
        _sourceNewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _savedText = NormalizeNewLines(text);
        _currentPath = path;
        OnPropertyChanged(nameof(CurrentFilePath));
    }

    /// <summary>
    /// 同步读取磁盘文件并重建基线。恢复草稿用（打开文件走异步版本），
    /// 两边共用 <see cref="DecodeScript"/> 与 <see cref="ApplyBaseline"/>，避免解码口径漂移。
    /// </summary>
    private bool TryLoadBaseline(string path, out string? error)
    {
        try
        {
            var (text, encoding, preamble, lossy) = DecodeScript(File.ReadAllBytes(path));
            ApplyBaseline(text, encoding, preamble, lossy, path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // 路径来自草稿文件（外部数据），非法字符会抛 ArgumentException 一族；
            // 这些都必须变成"基线读不到"而不是从启动路径逃出去。
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 解码脚本文件：BOM 优先 → 严格 UTF-8 → 系统 OEM 代码页 → Latin-1 兜底。
    /// 与 <c>DbcService.ReadDbcText</c> 同一取向：宁可用 OEM 读出正确字形，
    /// 也不要静默替换成 U+FFFD 后把原文件写坏。
    /// </summary>
    private static (string Text, Encoding Encoding, byte[] Preamble, bool Lossy) DecodeScript(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return (utf8.GetString(bytes, 3, bytes.Length - 3), utf8, new byte[] { 0xEF, 0xBB, 0xBF }, false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2),
                    Encoding.Unicode, new byte[] { 0xFF, 0xFE }, false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2),
                    Encoding.BigEndianUnicode, new byte[] { 0xFE, 0xFF }, false);
        }

        // 无 BOM：严格 UTF-8 —— 一个坏序列就抛，才能走到 OEM 回退。
        // CodePagesEncodingProvider 由 App.OnStartup 注册（GBK / CP932 / CP949…）。
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return (strictUtf8.GetString(bytes), strictUtf8, Array.Empty<byte>(), false);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var oem = Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
                var text = oem.GetString(bytes);
                // OEM 也可能留下替换字符（真正的坏字节）：标记出来，保存前要问。
                return (text, oem, Array.Empty<byte>(), text.Contains('\uFFFD'));
            }
            catch (NotSupportedException)
            {
                // 没有代码页提供程序：Latin-1 一字节一字符，不会产生替换字符，
                // 但非 Latin-1 字形会是错的 —— 同样按"有损"处理。
                return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, Array.Empty<byte>(), true);
            }
        }
    }

    /// <summary>把 CRLF / 孤立 CR 归一成 LF（与 textarea 的取值口径一致）。</summary>
    private static string NormalizeNewLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
