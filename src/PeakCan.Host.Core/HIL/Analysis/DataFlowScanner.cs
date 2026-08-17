using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.HIL.Core.HIL.Uds;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// 整树数据流扫描器（§5.8 ①-⑥ 树遍历规则）。
/// 递归走 case 树，维护"已写入变量集"(definite 必然写入 / conditional 可能写入) +
/// IndexVar 作用域栈 + 嵌套深度。
/// </summary>
/// <remarks>
/// 规则职责：
/// - ①：while 守卫引用必然 undefined（无前置 writer、未用 isUndefined() 逃生）→ Critical。
/// - ①′：while 守卫引用可能 undefined（writer 在条件分支内）→ High。
/// - ②/②′：did.0xXXXX 引用必然/可能 undefined → Critical/High。
/// - ③：signal.* 不在已加载 DBC（DBC 未加载时跳过）→ Critical。
/// - ⑤c：嵌套深度 &gt; 10 → Medium。
/// - ⑥：AssignStep.Assign 与作用域内任一 IndexVar 同名 → Critical。
/// writer：ReadDid（默认键 did_0x{Did:X4} 或自定义 OutputVar）、AssignStep.Assign、
/// RoutineControl/IOControl.OutputVar。
/// 逃生舱：守卫含 isUndefined(x) 时，① 不升级 Critical。
/// 数据流模型：
/// - definite：顺序流必然写入（顶层 + 当前作用域内顺序 writer）。
/// - conditional：容器 body 内 writer 导出（分支/循环可能不执行）。
/// 守卫/did 引用检查：definite→OK；conditional→High(①′/②′)；都不在→Critical(①/②)。
/// 在每个节点同时回调 per-kind validator 做局部直接检查（语法/④/⑤a/⑤b）。
/// </remarks>
internal sealed class DataFlowScanner
{
    private readonly ExpressionEvaluator _evaluator;
    private readonly IDbcSignalLookup? _dbcLookup;
    private readonly IReadOnlyDictionary<TestCaseStepKind, IStepValidator> _validators;

    /// <summary>嵌套深度上限（§5.8 ⑤c）。</summary>
    private const int MaxNestingDepth = 10;

    public DataFlowScanner(
        ExpressionEvaluator evaluator,
        IDbcSignalLookup? dbcLookup,
        IReadOnlyDictionary<TestCaseStepKind, IStepValidator> validators)
    {
        _evaluator = evaluator;
        _dbcLookup = dbcLookup;
        _validators = validators;
    }

    /// <summary>扫描整个 case 的步骤树，返回所有问题。</summary>
    public IReadOnlyList<ValidationIssue> ScanCase(TestCase testCase)
    {
        var issues = new List<ValidationIssue>();
        var state = new ScanState();
        WalkSteps(testCase.Steps, depth: 0, pathPrefix: null, state, issues);
        return issues;
    }

    /// <summary>数据流状态：definite（必然写入）+ conditional（可能写入）+ IndexVar 作用域栈。</summary>
    private sealed class ScanState
    {
        /// <summary>当前作用域内顺序流必然写入的变量键。</summary>
        public HashSet<string> Definite = new();

        /// <summary>条件分支/循环 body 内写入的变量键（可能不执行）。</summary>
        public HashSet<string> Conditional = new();

        /// <summary>IndexVar 作用域栈（Loop/Repeat 压栈，Assign 检查遮蔽）。</summary>
        public Stack<HashSet<string>> IndexScope = new();
    }

    // ─────────────────────────────────────────────────────────────
    //  递归遍历
    // ─────────────────────────────────────────────────────────────

    private void WalkSteps(
        IReadOnlyList<TestCaseStep> steps,
        int depth,
        string? pathPrefix,
        ScanState state,
        List<ValidationIssue> issues)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string path = pathPrefix is null ? i.ToString() : $"{pathPrefix}.{i}";
            WalkStep(step, depth, path, state, issues);
        }
    }

    private void WalkStep(
        TestCaseStep step, int depth, string path,
        ScanState state, List<ValidationIssue> issues)
    {
        // 1. per-kind validator 局部直接检查（语法/④/⑤a/⑤b）
        if (_validators.TryGetValue(step.Kind, out var validator))
        {
            var ctx = new StepValidationContext(_evaluator, path, depth, step.Label);
            issues.AddRange(validator.Validate(step, in ctx));
        }

        // 2. ⑤c 嵌套深度（容器步骤）
        if (IsContainer(step.Kind) && depth > MaxNestingDepth)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Medium, "⑤c", "Nesting depth",
                $"nesting depth {depth} > {MaxNestingDepth} (§5.8 ⑤c readability)",
                path, step.Label));
        }

        // 3. 按类型做数据流分析
        switch (step.Kind)
        {
            case TestCaseStepKind.Assign:
                HandleAssign(step, path, state, issues);
                break;
            case TestCaseStepKind.If:
                HandleIf(step, depth, path, state, issues);
                break;
            case TestCaseStepKind.Repeat:
                HandleRepeat(step, depth, path, state, issues);
                break;
            case TestCaseStepKind.Loop:
                HandleLoop(step, depth, path, state, issues);
                break;
            default:
                // 非容器叶步骤：无控制流递归，仅收集 writer（ReadDid/RoutineControl/IOControl）。
                // 新增带 writer 的叶步骤需在 HandleLeafWriter 添加 case。
                HandleLeafWriter(step, state);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  按步骤类型的数据流处理
    // ─────────────────────────────────────────────────────────────

    private void HandleAssign(
        TestCaseStep step, string path, ScanState state, List<ValidationIssue> issues)
    {
        var a = (AssignStep)step.Parameters;

        // ⑥ Assign 与作用域内任一 IndexVar 同名 → Critical（写入被 loop 绑定遮蔽）
        foreach (var frame in state.IndexScope)
        {
            if (frame.Contains(a.Assign))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "⑥", "Assign shadows IndexVar",
                    $"AssignStep.Assign '{a.Assign}' shadows an in-scope IndexVar (§5.8 ⑥)",
                    path, step.Label));
                break;
            }
        }

        // writer: Assign 名加入当前作用域 definite
        state.Definite.Add(a.Assign);

        // ②③ Expression 内 did/signal 引用检查
        CheckDidAndSignalRefs(a.Expression, path, step.Label, state, issues);
    }

    private void HandleIf(
        TestCaseStep step, int depth, string path, ScanState state, List<ValidationIssue> issues)
    {
        var ifP = (IfStep)step.Parameters;

        // ②③ Condition 内 did/signal 引用检查
        CheckDidAndSignalRefs(ifP.Condition, path, step.Label, state, issues);

        // 递归 Body / ElseBody：body 内 writer 导出为 conditional（分支可能不执行）
        if (ifP.Body is not null)
            WalkBodyConditional(ifP.Body, depth, path, state, issues);
        if (ifP.ElseBody is not null)
            WalkBodyConditional(ifP.ElseBody, depth, path, state, issues);
    }

    private void HandleRepeat(
        TestCaseStep step, int depth, string path, ScanState state, List<ValidationIssue> issues)
    {
        var rp = (RepeatStep)step.Parameters;

        // IndexVar 作用域压栈（规则 ⑥）
        bool pushed = PushIndexVar(state.IndexScope, rp.IndexVar);
        try
        {
            if (rp.Mode == RepeatMode.While)
            {
                // ①/①′ while 守卫变量引用检查 + ②③ did/signal 引用
                // 守卫先于 body 求值，故此时 body writer 尚未加入（正确反映首迭代）
                CheckWhileGuard(rp.Condition ?? "false", path, step.Label, state, issues);
            }
            else // Fixed
            {
                // ②③ Count 表达式引用检查
                CheckDidAndSignalRefs(rp.Count ?? "1", path, step.Label, state, issues);
            }

            // 递归 Body：循环可能 0 次 → body writer 导出 conditional
            if (rp.Body is not null)
                WalkBodyConditional(rp.Body, depth, path, state, issues);
        }
        finally
        {
            if (pushed) state.IndexScope.Pop();
        }
    }

    private void HandleLoop(
        TestCaseStep step, int depth, string path, ScanState state, List<ValidationIssue> issues)
    {
        var lp = (LoopStep)step.Parameters;

        // ②③ From/To/Step 表达式引用检查
        CheckDidAndSignalRefs(lp.From, path, step.Label, state, issues);
        CheckDidAndSignalRefs(lp.To, path, step.Label, state, issues);
        CheckDidAndSignalRefs(lp.Step, path, step.Label, state, issues);

        // IndexVar 作用域压栈（规则 ⑥）
        bool pushed = PushIndexVar(state.IndexScope, lp.IndexVar);
        try
        {
            if (lp.Body is not null)
                WalkBodyConditional(lp.Body, depth, path, state, issues);
        }
        finally
        {
            if (pushed) state.IndexScope.Pop();
        }
    }

    /// <summary>
    /// 容器 body 递归遍历：用 definite 的 copy 遍历（body 内顺序 writer 对 body 内后续是 definite），
    /// body 结束后把新增 writer（非父 definite）导出为 conditional（分支/循环可能不执行）。
    /// </summary>
    private void WalkBodyConditional(
        IReadOnlyList<TestCaseStep> body, int depth, string path,
        ScanState state, List<ValidationIssue> issues)
    {
        var bodyState = new ScanState
        {
            Definite = new HashSet<string>(state.Definite), // 继承父 definite，body 内局部累加
            Conditional = state.Conditional,               // 全局 conditional 共享
            IndexScope = state.IndexScope,                 // 作用域栈共享（body 内 Loop/Repeat 压弹平衡）
        };
        WalkSteps(body, depth + 1, path, bodyState, issues);
        // 导出：body 内新增 writer（不在父 definite）→ conditional
        foreach (var w in bodyState.Definite)
            if (!state.Definite.Contains(w)) state.Conditional.Add(w);
    }

    /// <summary>叶步骤 writer 收集：ReadDid/RoutineControl/IOControl 的 OutputVar。</summary>
    private static void HandleLeafWriter(TestCaseStep step, ScanState state)
    {
        switch (step.Parameters)
        {
            case ReadDidStep r:
                // ReadDid 总会写入：自定义 OutputVar 或默认键 did_0x{Did:X4}
                state.Definite.Add(r.OutputVar ?? DidVariableKey.Format(r.Did));
                break;
            case RoutineControlStep rc when rc.OutputVar is not null:
                state.Definite.Add(rc.OutputVar);
                break;
            case IOControlStep io when io.OutputVar is not null:
                state.Definite.Add(io.OutputVar);
                break;
            // 其他叶步骤（SendFrame/AssertSignal/...）无变量 writer，显式跳过
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  规则 ①/①′ while 守卫 + ②/②′ did 引用 + ③ signal 检查
    // ─────────────────────────────────────────────────────────────

    /// <summary>①/①′ while 守卫变量引用检查 + ②③ did/signal 引用。</summary>
    private void CheckWhileGuard(
        string condition, string path, string? label,
        ScanState state, List<ValidationIssue> issues)
    {
        var parse = _evaluator.Parse(condition);
        if (parse.IsError) return; // 语法错误已由 RepeatValidator 报

        // ②③ did/signal 引用
        CheckDidAndSignalRefsAst(parse.Ast, path, label, state, issues);

        // ①/①′ while 守卫变量引用：收集 isUndefined 逃生 name，再收集普通 VariableRef
        var escapeNames = new HashSet<string>();
        CollectEscapeNames(parse.Ast, escapeNames);

        var varRefs = new HashSet<string>();
        CollectVariableRefs(parse.Ast, varRefs);

        foreach (var name in varRefs)
        {
            if (escapeNames.Contains(name)) continue;    // isUndefined 逃生
            if (state.Definite.Contains(name)) continue;  // 必然已写入
            if (state.Conditional.Contains(name))
            {
                // ①′ High — 可能 undefined（writer 在条件分支/循环体内，可能不执行）
                issues.Add(new ValidationIssue(
                    ValidationSeverity.High, "①′", "While guard possibly undefined reference",
                    $"while guard references variable '{name}' which may be undefined " +
                    $"(writer in conditional branch/loop body, §5.8 ①′)",
                    path, label));
            }
            else
            {
                // ① Critical — 必然 undefined（首迭代，无前置 writer）
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "①", "While guard undefined reference",
                    $"while guard references variable '{name}' which is undefined on first iteration " +
                    $"(no preceding writer; use isUndefined() escape hatch or add AssignStep before loop, §5.8 ①)",
                    path, label));
            }
        }
    }

    /// <summary>②②′③ 解析表达式并检查 did/signal 引用悬空。</summary>
    private void CheckDidAndSignalRefs(
        string expr, string path, string? label,
        ScanState state, List<ValidationIssue> issues)
    {
        var parse = _evaluator.Parse(expr);
        if (parse.IsError) return; // 语法错误由 per-kind validator 报
        CheckDidAndSignalRefsAst(parse.Ast, path, label, state, issues);
    }

    /// <summary>②②′③ 直接对 AST 检查 did/signal 引用悬空。</summary>
    private void CheckDidAndSignalRefsAst(
        AstNode? ast, string path, string? label,
        ScanState state, List<ValidationIssue> issues)
    {
        // ②/②′ did.0xXXXX 引用必然/可能 undefined
        var didPaths = new List<string>();
        CollectSourceRefs(ast, SourceRefKind.Did, didPaths);
        foreach (var didPath in didPaths)
        {
            if (!TryParseDidKey(didPath, out var key)) continue;
            if (state.Definite.Contains(key)) continue;
            if (state.Conditional.Contains(key))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.High, "②′", "Did reference possibly undefined",
                    $"did.{didPath} reference may be undefined (writer in conditional branch/loop body, §5.8 ②′)",
                    path, label));
            }
            else
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "②", "Did reference undefined",
                    $"did.{didPath} reference has no preceding writer (key '{key}' not in written set, §5.8 ②)",
                    path, label));
            }
        }

        // ③ signal.* 不在已加载 DBC（DBC 未加载时跳过）
        if (_dbcLookup is not null && _dbcLookup.IsLoaded)
        {
            var signalPaths = new List<string>();
            CollectSourceRefs(ast, SourceRefKind.Signal, signalPaths);
            foreach (var sigPath in signalPaths)
            {
                if (!_dbcLookup.ContainsSignal(sigPath))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Critical, "③", "Signal not in DBC",
                        $"signal.{sigPath} not found in loaded DBC (§5.8 ③)",
                        path, label));
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  AST 遍历辅助（exhaustive，新增 AstNode 子类会 fail-fast）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 递归访问 AST 所有节点（含自身）。exhaustive switch：已知叶子 break，
    /// 未知类型 throw（fail-fast，防止未来新增带子节点的 AstNode 静默丢子树）。
    /// </summary>
    private static void VisitDescendants(AstNode? node, Action<AstNode> visit)
    {
        if (node is null) return;
        visit(node);
        switch (node)
        {
            case BinaryOp b:
                VisitDescendants(b.Left, visit);
                VisitDescendants(b.Right, visit);
                break;
            case UnaryOp u:
                VisitDescendants(u.Operand, visit);
                break;
            case FunctionCall f:
                for (int i = 0; i < f.Arguments.Count; i++)
                    VisitDescendants(f.Arguments[i], visit);
                break;
            case NumberLiteral:
            case HexLiteral:
            case BoolLiteral:
            case StringLiteral:
            case BytesLiteral:
            case VariableRef:
            case SourceRef:
                break; // 叶子/无子节点
            default:
                throw new InvalidOperationException(
                    $"Unhandled AstNode type '{node.GetType().Name}'; add case to VisitDescendants");
        }
    }

    /// <summary>收集 isUndefined(x) 调用中的变量名（逃生舱识别）。</summary>
    private static void CollectEscapeNames(AstNode? ast, HashSet<string> escape)
    {
        VisitDescendants(ast, n =>
        {
            if (n is FunctionCall { Name: "isUndefined" } f)
            {
                foreach (var arg in f.Arguments)
                    if (arg is VariableRef v) escape.Add(v.Name);
            }
        });
    }

    /// <summary>收集所有 VariableRef 名（含 isUndefined 参数内，检查时按逃生集跳过）。</summary>
    private static void CollectVariableRefs(AstNode? ast, HashSet<string> refs)
    {
        VisitDescendants(ast, n =>
        {
            if (n is VariableRef v) refs.Add(v.Name);
        });
    }

    /// <summary>收集指定 kind 的 SourceRef.Path（did/signal）。</summary>
    private static void CollectSourceRefs(AstNode? ast, SourceRefKind kind, List<string> paths)
    {
        VisitDescendants(ast, n =>
        {
            if (n is SourceRef s && s.Kind == kind) paths.Add(s.Path);
        });
    }

    /// <summary>did.0xXXXX 的 Path → 变量键 did_0x{X4}（DidVariableKey.Format）。</summary>
    private static bool TryParseDidKey(string path, [NotNullWhen(true)] out string? key)
    {
        // Path 可能是 "0xF190" 或 "F190"（Hex token 原样或去前缀）
        var hex = path.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? path[2..] : path;
        if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var did))
        {
            key = DidVariableKey.Format(did);
            return true;
        }
        key = null;
        return false; // 非数字 DID（如 did.VIN 命名引用）不适用 ②
    }

    // ─────────────────────────────────────────────────────────────
    //  辅助
    // ─────────────────────────────────────────────────────────────

    private static bool IsContainer(TestCaseStepKind kind)
        => kind is TestCaseStepKind.If or TestCaseStepKind.Repeat or TestCaseStepKind.Loop;

    /// <summary>IndexVar 非空时压入作用域栈。</summary>
    private static bool PushIndexVar(Stack<HashSet<string>> indexScope, string? indexVar)
    {
        if (string.IsNullOrEmpty(indexVar)) return false;
        indexScope.Push(new HashSet<string> { indexVar! });
        return true;
    }
}
