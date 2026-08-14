namespace PeakCan.Host.App.Composition.Icons;

/// <summary>
/// Segoe Fluent Icons（Windows 11 系统字体）单字符码点常量。
/// 所有码点经 <c>C:\Windows\Fonts\SegoeIcons.ttf</c> 验证存在（FluentIconGlyphsTests）。
/// XAML 用 <c>{x:Static icons:FluentIconGlyphs.X}</c> 引用，配合
/// <c>FontFamily="Segoe Fluent Icons"</c> 渲染。
/// </summary>
public static class FluentIconGlyphs
{
    public const string Settings     = "\uE713"; // ⚙ 设备设置
    public const string Record       = "\uE7C8"; // ● 录制
    public const string Play         = "\uE768"; // ▶ 运行
    public const string Pause        = "\uE769"; // ⏸ 暂停
    public const string Stop         = "\uE71A"; // ⏹ 停止
    public const string Save         = "\uE74E"; // 💾 保存
    public const string OpenFolder   = "\uE8B7"; // 📂 打开
    public const string Dismiss      = "\uE711"; // ✕ 关闭/清除
    public const string Search       = "\uE721"; // 🔍 搜索
    public const string ChevronUp    = "\uE74A"; // ▲ 上移
    public const string ChevronDown  = "\uE74B"; // ▼ 下移
    public const string ChevronLeft  = "\uE76B"; // ← 后退
    public const string ChevronRight = "\uE76C"; // → 前进
    public const string Link         = "\uE71B"; // 🔗 UDS 连接
    public const string Lightning    = "\uE945"; // ⚡ 闪电
    public const string Plug         = "\uE8A5"; // 连接 / HIL Hardware
    public const string Power        = "\uE94A"; // 断开
    public const string Bot          = "\uE9F5"; // 🤖 AI 聊天
    public const string Sparkle      = "\uE734"; // ✨ 格式化（FavoriteStar 替代，视觉待确认）
    public const string Replay       = "\uE81C"; // HIL TraceReplay（替代磁带）
    public const string Laptop       = "\uE7F8"; // HIL VirtualEcu
    public const string Help         = "\uE894"; // ❓ 未知/帮助
}
