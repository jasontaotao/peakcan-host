using System.Text.Json;

namespace PeakCan.Host.App.Services.Scripting;

/// <summary>
/// 脚本编辑器页面的 HTML 生成器（纯函数，可单测）。
/// <para>
/// 页面与 C# 之间只说一种话：<c>window.chrome.webview.postMessage(JSON)</c>，
/// 消息形如 <c>{type:'text'|'run', text}</c>，由 <see cref="ScriptEditorBridge"/> 解析。
/// 反过来 C# 不需要往页面里灌数据——初始草稿直接嵌在 HTML 里，
/// 既避免了 <c>NavigationCompleted</c> 的时序竞争，也让每次 tab 重入
/// 重建 textarea 时内容自然恢复。
/// </para>
/// <para>
/// 转义：初始脚本经 <see cref="JsonSerializer"/> 生成 JS 字面量。默认 encoder 会把
/// 尖括号与 &amp; 转成 unicode 转义序列，所以脚本里即使写了结束标签也不会提前闭合
/// 脚本块（<c>ScriptEditorHtmlTests</c> 用注入用例钉住这一点）。
/// </para>
/// </summary>
public static class ScriptEditorHtml
{
    /// <summary>生成编辑器页面。</summary>
    /// <param name="initialScript">要回填到编辑器里的草稿（null 视作空）。</param>
    public static string Build(string? initialScript)
    {
        // 注意：这里是 $$"""（双 $）——插值写成 {{...}}，JS 里的单个 { 才是字面量。
        var literal = JsonSerializer.Serialize(initialScript ?? "");
        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <style>
                body {
                    margin: 0;
                    padding: 0;
                    overflow: hidden;
                    background: #1E1E1E;
                }
                #editor {
                    height: 100vh;
                    width: 100vw;
                }
            </style>
        </head>
        <body>
            <div id="editor"></div>
            <script type="module">
                // 初始草稿由 C# 侧序列化后嵌入，避免导航完成后再灌值的时序竞争。
                const initialScript = {{literal}};

                const editor = document.getElementById('editor');

                // Create a simple textarea as fallback.
                const textarea = document.createElement('textarea');
                textarea.style.cssText = `
                    width: 100%;
                    height: 100%;
                    background: #1E1E1E;
                    color: #D4D4D4;
                    font-family: 'Consolas', monospace;
                    font-size: 14px;
                    border: none;
                    outline: none;
                    resize: none;
                    padding: 8px;
                    box-sizing: border-box;
                `;
                textarea.placeholder = '// Write your JavaScript script here...\n// Example: can.onFrame((frame) => { log(frame.id); });';
                textarea.value = initialScript;
                editor.appendChild(textarea);

                function post(message) {
                    window.chrome?.webview?.postMessage(JSON.stringify(message));
                }

                // 每次改动就把全文送回 C#。刻意不做防抖：改完立刻切 tab 或立刻运行
                // 都不能丢字，而脚本体积只有几 KB。
                textarea.addEventListener('input', () => post({ type: 'text', text: textarea.value }));

                // Ctrl+Enter 运行：文本随消息一起带上，不依赖上一条 text 消息的到达顺序。
                textarea.addEventListener('keydown', (e) => {
                    if (e.ctrlKey && e.key === 'Enter') {
                        e.preventDefault();
                        post({ type: 'run', text: textarea.value });
                    }
                });
            </script>
        </body>
        </html>
        """;
    }
}
