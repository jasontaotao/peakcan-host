using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.HIL.Core.HIL.Uds;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// 整树数据流扫描器（§5.8 ①-⑥ 树遍历规则）。
/// 递归走 case 树，维护"已写入变量集"+ IndexVar 作用域栈 + 嵌套深度。
/// </summary>
/// <remarks>
/// 规则职责：
/// - ①：while 守卫引用必然 undefined（首次写入在循环体内、无前置 writer、未用 isUndefined() 逃生）→ Critical。
/// - ②：did.0xXXXX 引用必然 undefined（case 内无前置 writer）→ Critical。
/// - ③：signal.* 不在已加载 DBC（DBC 未加载时跳过）→ Critical。
/// - ⑤c：嵌套深度 &gt; 10 → Medium。
/// - ⑥：AssignStep.Assign 与作用域内任一 IndexVar 同名 → Critical。
/// writer：ReadDid（默认键 did_0x{Did:X4} 或自定义 OutputVar）、AssignStep.Assign、RoutineControl/IOControl.OutputVar。
/// 逃生舱：守卫含 isUndefined(x) 时，① 不升级 Critical。
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
        var definite = new HashSet<string>();
        var indexScope = new Stack<HashSet<string>>();
        WalkSteps(testCase.Steps, depth: 0, pathPrefix: null, definite, indexScope, issues);
        return issues;
    }

    // ─────────────────────────────────────────────────────────────
    //  递归遍历
    // ─────────────────────────────────────────────────────────────

    private void WalkSteps(
        IReadOnlyList<TestCaseStep> steps,
        int depth,
        string? pathPrefix,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string path = pathPrefix is null ? i.ToString() : $"{pathPrefix}.{i}";
            WalkStep(step, depth, path, definite, indexScope, issues);
        }
    }

    private void WalkStep(
        TestCaseStep step,
        int depth,
        string path,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
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
                HandleAssign(step, path, definite, indexScope, issues);
                break;
            case TestCaseStepKind.If:
                HandleIf(step, depth, path, definite, indexScope, issues);
                break;
            case TestCaseStepKind.Repeat:
                HandleRepeat(step, depth, path, definite, indexScope, issues);
                break;
            case TestCaseStepKind.Loop:
                HandleLoop(step, depth, path, definite, indexScope, issues);
                break;
            default:
                HandleLeafWriter(step, definite);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  按步骤类型的数据流处理
    // ─────────────────────────────────────────────────────────────

    private void HandleAssign(
        TestCaseStep step, string path,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
    {
        var a = (AssignStep)step.Parameters;

        // ⑥ Assign 与作用域内任一 IndexVar 同名 → Critical（写入被 loop 绑定遮蔽）
        foreach (var frame in indexScope)
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

        // writer: Assign 名加入已写入集
        definite.Add(a.Assign);

        // ②③ Expression 内 did/signal 引用检查
        CheckDidAndSignalRefs(a.Expression, path, step.Label, definite, issues);
    }

    private void HandleIf(
        TestCaseStep step, int depth, string path,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
    {
        var ifP = (IfStep)step.Parameters;

        // ②③ Condition 内 did/signal 引用检查
        CheckDidAndSignalRefs(ifP.Condition, path, step.Label, definite, issues);

        // 递归 Body / ElseBody（depth+1，body 内 writer 加入 definite）
        if (ifP.Body is not null)
            WalkSteps(ifP.Body, depth + 1, path, definite, indexScope, issues);
        if (ifP.ElseBody is not null)
            WalkSteps(ifP.ElseBody, depth + 1, path, definite, indexScope, issues);
    }

    private void HandleRepeat(
        TestCaseStep step, int depth, string path,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
    {
        var rp = (RepeatStep)step.Parameters;

        // IndexVar 作用域压栈（规则 ⑥）
        bool pushed = PushIndexVar(indexScope, rp.IndexVar);
        try
        {
            if (rp.Mode == RepeatMode.While)
            {
                // ① while 守卫变量引用检查 + ②③ did/signal 引用
                CheckWhileGuard(rp.Condition ?? "false", path, step.Label, definite, issues);
            }
            else // Fixed
            {
                // ②③ Count 表达式引用检查
                CheckDidAndSignalRefs(rp.Count ?? "1", path, step.Label, definite, issues);
            }

            // 递归 Body（depth+1）
            if (rp.Body is not null)
                WalkSteps(rp.Body, depth + 1, path, definite, indexScope, issues);
        }
        finally
        {
            if (pushed) indexScope.Pop();
        }
    }

    private void HandleLoop(
        TestCaseStep step, int depth, string path,
        HashSet<string> definite,
        Stack<HashSet<string>> indexScope,
        List<ValidationIssue> issues)
    {
        var lp = (LoopStep)step.Parameters;

        // ②③ From/To/Step 表达式引用检查
        CheckDidAndSignalRefs(lp.From, path, step.Label, definite, issues);
        CheckDidAndSignalRefs(lp.To, path, step.Label, definite, issues);
        CheckDidAndSignalRefs(lp.Step, path, step.Label, definite, issues);

        // IndexVar 作用域压栈（规则 ⑥）
        bool pushed = PushIndexVar(indexScope, lp.IndexVar);
        try
        {
            if (lp.Body is not null)
                WalkSteps(lp.Body, depth + 1, path, definite, indexScope, issues);
        }
        finally
        {
            if (pushed) indexScope.Pop();
        }
    }

    /// <summary>叶步骤 writer 收集：ReadDid/RoutineControl/IOControl 的 OutputVar。</summary>
    private static void HandleLeafWriter(TestCaseStep step, HashSet<string> definite)
    {
        switch (step.Parameters)
        {
            case ReadDidStep r:
                definite.Add(r.OutputVar ?? DidVariableKey.Format(r.Did));
                break;
            case RoutineControlStep rc when rc.OutputVar is not null:
                definite.Add(rc.OutputVar);
                break;
            case IOControlStep io when io.OutputVar is not null:
                definite.Add(io.OutputVar);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  规则 ① while 守卫 + ②③ did/signal 引用检查
    // ─────────────────────────────────────────────────────────────

    /// <summary>① while 守卫变量引用检查（必然 undefined → Critical）+ ②③ did/signal。</summary>
    private void CheckWhileGuard(
        string condition, string path, string? label,
        HashSet<string> definite, List<ValidationIssue> issues)
    {
        var parse = _evaluator.Parse(condition);
        if (parse.IsError) return; // 语法错误已由 RepeatValidator 报

        // ②③ did/signal 引用
        CheckDidAndSignalRefsAst(parse.Ast, path, label, definite, issues);

        // ① while 守卫变量引用：收集 isUndefined 逃生 name，再收集普通 VariableRef
        var escapeNames = new HashSet<string>();
        CollectEscapeNames(parse.Ast, escapeNames);

        var varRefs = new HashSet<string>();
        CollectVariableRefs(parse.Ast, varRefs);

        foreach (var name in varRefs)
        {
            if (escapeNames.Contains(name)) continue;   // isUndefined 逃生
            if (definite.Contains(name)) continue;        // 前置 writer 存在
            // ① Critical — 守卫引用必然 undefined（首迭代，无前置 writer）
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "①", "While guard undefined reference",
                $"while guard references variable '{name}' which is undefined on first iteration " +
                $"(no preceding writer; use isUndefined() escape hatch or add AssignStep before loop, §5.8 ①)",
                path, label));
        }
    }

    /// <summary>②③ 解析表达式并检查 did/signal 引用悬空。</summary>
    private void CheckDidAndSignalRefs(
        string expr, string path, string? label,
        HashSet<string> definite, List<ValidationIssue> issues)
    {
        var parse = _evaluator.Parse(expr);
        if (parse.IsError) return; // 语法错误由 per-kind validator 报
        CheckDidAndSignalRefsAst(parse.Ast, path, label, definite, issues);
    }

    /// <summary>②③ 直接对 AST 检查 did/signal 引用悬空。</summary>
    private void CheckDidAndSignalRefsAst(
        AstNode? ast, string path, string? label,
        HashSet<string> definite, List<ValidationIssue> issues)
    {
        // ② did.0xXXXX 引用必然 undefined（无前置 writer）
        var didPaths = new List<string>();
        CollectSourceRefs(ast, SourceRefKind.Did, didPaths);
        foreach (var didPath in didPaths)
        {
            if (TryParseDidKey(didPath, out var key) && !definite.Contains(key))
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
    //  AST 遍历辅助
    // ─────────────────────────────────────────────────────────────

    /// <summary>收集 isUndefined(x) 调用中的变量名（逃生舱识别）。</summary>
    private static void CollectEscapeNames(AstNode? node, HashSet<string> escape)
    {
        switch (node)
        {
            case FunctionCall f when f.Name == "isUndefined":
                foreach (var arg in f.Arguments)
                    if (arg is VariableRef v) escape.Add(v.Name);
                break;
            case FunctionCall f:
                foreach (var arg in f.Arguments) CollectEscapeNames(arg, escape);
                break;
            case UnaryOp u:
                CollectEscapeNames(u.Operand, escape);
                break;
            case BinaryOp b:
                CollectEscapeNames(b.Left, escape);
                CollectEscapeNames(b.Right, escape);
                break;
            // literals / VariableRef / SourceRef: no escape children
        }
    }

    /// <summary>收集所有 VariableRef 名（含 isUndefined 参数内，检查时按逃生集跳过）。</summary>
    private static void CollectVariableRefs(AstNode? node, HashSet<string> refs)
    {
        switch (node)
        {
            case VariableRef v:
                refs.Add(v.Name);
                break;
            case FunctionCall f:
                foreach (var arg in f.Arguments) CollectVariableRefs(arg, refs);
                break;
            case UnaryOp u:
                CollectVariableRefs(u.Operand, refs);
                break;
            case BinaryOp b:
                CollectVariableRefs(b.Left, refs);
                CollectVariableRefs(b.Right, refs);
                break;
        }
    }

    /// <summary>收集指定 kind 的 SourceRef.Path（did/signal）。</summary>
    private static void CollectSourceRefs(AstNode? node, SourceRefKind kind, List<string> paths)
    {
        switch (node)
        {
            case SourceRef s when s.Kind == kind:
                paths.Add(s.Path);
                break;
            case SourceRef:
                break; // 其他 kind 跳过
            case FunctionCall f:
                foreach (var arg in f.Arguments) CollectSourceRefs(arg, kind, paths);
                break;
            case UnaryOp u:
                CollectSourceRefs(u.Operand, kind, paths);
                break;
            case BinaryOp b:
                CollectSourceRefs(b.Left, kind, paths);
                CollectSourceRefs(b.Right, kind, paths);
                break;
        }
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
