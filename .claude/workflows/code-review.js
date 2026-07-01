export const meta = {
  name: 'peakcan-code-review',
  description: 'Multi-agent line-by-line review of peakcan-host, with cross-verification',
  phases: [
    { title: 'Review', detail: '4 parallel agents review by architecture layer' },
    { title: 'Cross-Verify', detail: 'Verifier checks all findings for false positives and contradictions' },
  ],
};

const REVIEW_PROMPT = `You are a senior .NET/WPF code reviewer. Read EVERY file listed below line by line. For each file, produce a structured list of findings.

For each finding, output JSON with exactly these fields:
- file: relative path
- line: line number (approximate OK)
- severity: "CRITICAL" | "HIGH" | "MEDIUM" | "LOW"
- category: "bug" | "perf" | "maintainability" | "correctness" | "threading" | "resource-leak" | "style"
- title: one-line summary
- detail: 2-4 sentence explanation with code reference

Rules:
- CRITICAL = will cause data loss, crash, or security vulnerability in production
- HIGH = likely bug under normal usage, or significant correctness issue
- MEDIUM = code smell, potential issue under edge conditions, maintainability concern
- LOW = style, naming, minor optimization

Be EXACT. Reference specific line numbers and code snippets. Do NOT hallucinate — if you're unsure, mark severity LOW and note uncertainty.

Output a JSON array of findings. No markdown fences, just the raw JSON array.`;

// ── Phase 1: 4 parallel layer reviewers ──
phase('Review');

const coreFiles = [
  'src/PeakCan.Host.Core/CanFrame.cs',
  'src/PeakCan.Host.Core/CanId.cs',
  'src/PeakCan.Host.Core/ChannelId.cs',
  'src/PeakCan.Host.Core/Dbc/ByteOrder.cs',
  'src/PeakCan.Host.Core/Dbc/DbcDocument.cs',
  'src/PeakCan.Host.Core/Dbc/DbcErrorCode.cs',
  'src/PeakCan.Host.Core/Dbc/DbcParseException.cs',
  'src/PeakCan.Host.Core/Dbc/DbcParser.cs',
  'src/PeakCan.Host.Core/Dbc/DbcTokenizer.cs',
  'src/PeakCan.Host.Core/Dbc/Message.cs',
  'src/PeakCan.Host.Core/Dbc/Node.cs',
  'src/PeakCan.Host.Core/Dbc/Signal.cs',
  'src/PeakCan.Host.Core/Dbc/SignalDecoder.cs',
  'src/PeakCan.Host.Core/Dbc/Token.cs',
  'src/PeakCan.Host.Core/Dbc/TokenType.cs',
  'src/PeakCan.Host.Core/Dbc/ValueTable.cs',
  'src/PeakCan.Host.Core/Dbc/ValueType.cs',
  'src/PeakCan.Host.Core/Error.cs',
  'src/PeakCan.Host.Core/ErrorCode.cs',
  'src/PeakCan.Host.Core/FrameFlags.cs',
  'src/PeakCan.Host.Core/FrameFormat.cs',
  'src/PeakCan.Host.Core/FrameType.cs',
  'src/PeakCan.Host.Core/ICanChannel.cs',
  'src/PeakCan.Host.Core/IChannelFactory.cs',
  'src/PeakCan.Host.Core/IChannelProbe.cs',
  'src/PeakCan.Host.Core/Result.cs',
  'src/PeakCan.Host.Core/Timestamp.cs',
  'src/PeakCan.Host.Core/Unit.cs',
];

const infraFiles = [
  'src/PeakCan.Host.Infrastructure/Channel/ChannelException.cs',
  'src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs',
  'src/PeakCan.Host.Infrastructure/Channel/IFrameSink.cs',
  'src/PeakCan.Host.Infrastructure/Channel/IFrameSource.cs',
  'src/PeakCan.Host.Infrastructure/Channel/PeakCanFrameFormatter.cs',
  'src/PeakCan.Host.Infrastructure/Peak/ChannelConnectGate.cs',
  'src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs',
  'src/PeakCan.Host.Infrastructure/Peak/PeakCanChannelFactory.cs',
  'src/PeakCan.Host.Infrastructure/Peak/PeakChannelProbe.cs',
  'src/PeakCan.Host.Infrastructure/Peak/PeakError.cs',
  'src/PeakCan.Host.Infrastructure/Peak/PeakErrorMapper.cs',
  'src/PeakCan.Host.Infrastructure/Statistics/BusStatisticsCollector.cs',
];

const appSvcVmFiles = [
  'src/PeakCan.Host.App/Composition/AppHostBuilder.cs',
  'src/PeakCan.Host.App/Composition/SinkWiringService.cs',
  'src/PeakCan.Host.App/Services/DbcDecodeBackgroundService.cs',
  'src/PeakCan.Host.App/Services/DbcService.cs',
  'src/PeakCan.Host.App/Services/SendService.cs',
  'src/PeakCan.Host.App/Services/StatisticsService.cs',
  'src/PeakCan.Host.App/Services/TraceService.cs',
  'src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/DbcMessageViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/DbcViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/DispatcherExtensions.cs',
  'src/PeakCan.Host.App/ViewModels/SendViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/SignalEntry.cs',
  'src/PeakCan.Host.App/ViewModels/SignalViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/StatsViewModel.cs',
  'src/PeakCan.Host.App/ViewModels/TraceEntry.cs',
  'src/PeakCan.Host.App/ViewModels/TraceViewModel.cs',
];

const appViewFiles = [
  'src/PeakCan.Host.App/App.xaml',
  'src/PeakCan.Host.App/App.xaml.cs',
  'src/PeakCan.Host.App/AppShell.xaml',
  'src/PeakCan.Host.App/AppShell.xaml.cs',
  'src/PeakCan.Host.App/Views/DbcView.xaml',
  'src/PeakCan.Host.App/Views/DbcView.xaml.cs',
  'src/PeakCan.Host.App/Views/SendView.xaml',
  'src/PeakCan.Host.App/Views/SendView.xaml.cs',
  'src/PeakCan.Host.App/Views/SignalView.xaml',
  'src/PeakCan.Host.App/Views/SignalView.xaml.cs',
  'src/PeakCan.Host.App/Views/StatsView.xaml',
  'src/PeakCan.Host.App/Views/StatsView.xaml.cs',
  'src/PeakCan.Host.App/Views/TraceView.xaml',
  'src/PeakCan.Host.App/Views/TraceView.xaml.cs',
];

const FINDING_SCHEMA = {
  type: 'object',
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          file: { type: 'string' },
          line: { type: 'number' },
          severity: { type: 'string', enum: ['CRITICAL', 'HIGH', 'MEDIUM', 'LOW'] },
          category: { type: 'string', enum: ['bug', 'perf', 'maintainability', 'correctness', 'threading', 'resource-leak', 'style'] },
          title: { type: 'string' },
          detail: { type: 'string' },
        },
        required: ['file', 'severity', 'category', 'title', 'detail'],
      },
    },
  },
  required: ['findings'],
};

const fileArg = (files) => files.map(f => `D:/claude_proj2/peakcan-host/${f}`).join('\n');

const coreReview = await agent(
  `${REVIEW_PROMPT}\n\nFiles to review (read each one):\n${fileArg(coreFiles)}`,
  { label: 'review:core', phase: 'Review', schema: FINDING_SCHEMA }
);

const infraReview = await agent(
  `${REVIEW_PROMPT}\n\nFiles to review (read each one):\n${fileArg(infraFiles)}`,
  { label: 'review:infra', phase: 'Review', schema: FINDING_SCHEMA }
);

const appSvcReview = await agent(
  `${REVIEW_PROMPT}\n\nFiles to review (read each one):\n${fileArg(appSvcVmFiles)}`,
  { label: 'review:app-svc-vm', phase: 'Review', schema: FINDING_SCHEMA }
);

const appViewReview = await agent(
  `${REVIEW_PROMPT}\n\nFiles to review (read each one):\n${fileArg(appViewFiles)}`,
  { label: 'review:app-views', phase: 'Review', schema: FINDING_SCHEMA }
);

// ── Phase 2: Cross-verification ──
phase('Cross-Verify');

const allFindings = [
  ...(coreReview?.findings || []),
  ...(infraReview?.findings || []),
  ...(appSvcReview?.findings || []),
  ...(appViewReview?.findings || []),
];

log(`Phase 1 complete: ${allFindings.length} raw findings across 4 reviewers`);

const VERIFY_PROMPT = `You are an adversarial code review verifier. You are given a list of findings from 4 independent code reviewers who reviewed the peakcan-host WPF/.NET 10 project.

Your job:
1. Read each finding carefully
2. For findings that reference specific files/lines, READ the actual source code to verify the claim is accurate
3. Mark each finding as:
   - CONFIRMED: the issue is real and the description is accurate
   - FALSE_POSITIVE: the reviewer misread the code, the issue doesn't exist
   - DUPLICATE: same issue found by another reviewer (keep the better description)
   - NEEDS_CONTEXT: can't verify without runtime info, but plausible

4. Cross-check: if two reviewers contradict each other (one says bug, another says fine for same code), flag it explicitly

5. After verification, produce a FINAL consolidated list sorted by severity (CRITICAL first), deduplicated.

For each final finding, output JSON:
{
  "verdict": "CONFIRMED" | "FALSE_POSITIVE" | "DUPLICATE" | "NEEDS_CONTEXT",
  "originalReviewer": "core|infra|app-svc-vm|app-views",
  "file": "relative path",
  "line": number,
  "severity": "CRITICAL" | "HIGH" | "MEDIUM" | "LOW",
  "category": "bug|perf|maintainability|correctness|threading|resource-leak|style",
  "title": "one-line summary",
  "detail": "verification result + any corrections",
  "confidence": "high" | "medium" | "low"
}

Output a JSON object with two fields:
- "verificationNotes": string (summary of your verification process, contradictions found, etc.)
- "findings": array of verified findings (only CONFIRMED ones, deduplicated, sorted by severity)

The source code is at D:/claude_proj2/peakcan-host/src/ — read the actual files to verify.`;

const VERIFIER_SCHEMA = {
  type: 'object',
  properties: {
    verificationNotes: { type: 'string' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          verdict: { type: 'string' },
          originalReviewer: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'number' },
          severity: { type: 'string' },
          category: { type: 'string' },
          title: { type: 'string' },
          detail: { type: 'string' },
          confidence: { type: 'string' },
        },
        required: ['verdict', 'file', 'severity', 'category', 'title', 'detail'],
      },
    },
  },
  required: ['verificationNotes', 'findings'],
};

const verified = await agent(
  `${VERIFY_PROMPT}\n\nHere are all ${allFindings.length} raw findings from the 4 reviewers:\n${JSON.stringify(allFindings, null, 2)}`,
  { label: 'verifier', phase: 'Cross-Verify', schema: VERIFIER_SCHEMA }
);

return {
  rawCount: allFindings.length,
  verifiedCount: verified?.findings?.length || 0,
  notes: verified?.verificationNotes || '',
  findings: verified?.findings || [],
};
