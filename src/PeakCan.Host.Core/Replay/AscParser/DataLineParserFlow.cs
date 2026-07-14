// src/PeakCan.Host.Core/Replay/AscParser/DataLineParserFlow.cs — v3.49.0 MINOR (T2 of 3)
// Q3: 改为 delegate 到 AscFormat.TryParseDataLine。
// 之前 171 LoC 拥有内联的全部 1-char single-hex + N*N 2-char hex +
// 'd'/'l' marker + Vector Rx/Tx + Length/BitCount/ID metadata 终止逻辑。
// 现在 ≈ 30 LoC。

namespace PeakCan.Host.Core.Replay;

public static partial class AscParser
{
    // Flow A: DataLineParser (v1.4.0 MINOR + v3.11.5 PATCH + earlier).
    // v3.49.0 Q3: try-parse-then-format → delegate to AscFormat.TryParseDataLine。
    // AscFormat 是 v3.49 新建的格式单源；语义与之前的 171-LoC 内联实现 1:1 等价。
    private static bool TryParseDataLine(string line, out ReplayFrame frame, out string reason)
        => AscFormat.TryParseDataLine(line, out frame, out reason);
}
