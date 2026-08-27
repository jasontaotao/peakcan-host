using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using System.Text.Json;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// 互操作契约测试（Direction B）—— host 消费 peakcan-studio 产物。
///
/// 以下两个 JSON 是 peakcan-studio InteropTests
/// （Studio_Serializes_Artifacts_For_Host）生成产物的 verbatim 拷贝
/// （%TEMP%\peakcan-studio-interop\）。它们证明 Direction B：studio 侧
/// serializer 输出的 suite/ECU 脚本可被 host 侧 loader 直接消费。
/// 任何一侧对模型签名（字段、类型、$kind 判别器）的改动若未同步，
/// 此处断言会先红。与 studio 侧 InteropTests（Direction A）构成
/// format-freeze 双向验收，见两仓库 README 的格式冻结约束。
/// </summary>
public class StudioInteropTests
{
    // ── Direction B: Host reads peakcan-studio artifacts ────────────────────

    [Fact]
    public void Host_Loads_Studio_EcuScript()
    {
        const string json = """
        {
          "name": "StudioProducedEcu",
          "canIds": {
            "requestId": "0x7E0",
            "responseId": "0x7E8",
            "isExtendedFrame": false
          },
          "rules": [
            {
              "serviceId": "0x3E",
              "subFunction": 0,
              "responseData": [
                126
              ]
            },
            {
              "serviceId": "0x22",
              "subFunction": 241,
              "dataMask": [
                255
              ],
              "dataPattern": [
                144
              ],
              "responseData": [
                98,
                241,
                144,
                1,
                2,
                3
              ]
            }
          ]
        }
        """;

        var script = EcuScriptLoader.Parse(json);

        Assert.Equal("StudioProducedEcu", script.Name);
        // 文件视角 requestId/responseId 互换到 ECU 视角（同 bms_sim 契约）。
        Assert.Equal(0x7E8u, script.CanIds.RequestId);
        Assert.Equal(0x7E0u, script.CanIds.ResponseId);
        // rules 格式 → 2 条 wildcard transition。
        Assert.Equal(2, script.StateMachine.Transitions.Count);
    }

    [Fact]
    public void Host_Loads_Studio_TestSuite()
    {
        const string json = """
        {
          "name": "StudioProducedSuite",
          "cases": [
            {
              "id": "c1",
              "name": "TP",
              "description": "interop smoke",
              "steps": [
                {
                  "parameters": {
                    "$kind": "delay",
                    "milliseconds": 100,
                    "kind": "Delay"
                  }
                }
              ],
              "tags": [],
              "timeoutMs": 0
            }
          ],
          "globalCaseFixtureKeys": [],
          "suiteFixtureKeys": [],
          "config": {
            "failurePolicy": "ContinueAll",
            "continueAfterSetupFailure": true
          },
          "timeoutMs": 0
        }
        """;

        var suite = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);

        Assert.NotNull(suite);
        Assert.Equal("StudioProducedSuite", suite!.Name);
        var tc = Assert.Single(suite.Cases);
        Assert.Equal("c1", tc.Id);
        // $kind 判别器正确还原 delay 步骤类型。
        var step = Assert.Single(tc.Steps);
        Assert.IsType<DelayStep>(step.Parameters);
        Assert.Equal("100", ((DelayStep)step.Parameters).Milliseconds);
    }

    // ── 0.14.0 新 kind（spec 2026-08-27 §3.4）: host 读 studio 的 studio-suite-taskc.json 产物 ──

    [Fact]
    public void Host_Loads_Studio_TaskC_Steps()
    {
        // studio InteropTests.Studio_Serializes_TaskC_Steps_For_Host 产物的 verbatim 拷贝。
        // 覆盖 0.14.0 新增：AssertSignalWithin/AssertStable $kind 判别器 + UDS 步骤 TargetChannel 透传。
        const string json = """
        {
          "name": "StudioTaskCSuite",
          "cases": [
            {
              "id": "c1",
              "name": "TC",
              "description": "task c steps",
              "steps": [
                {
                  "parameters": {
                    "$kind": "assertSignalWithin",
                    "signalName": "BMS.EngineRPM",
                    "expected": "100",
                    "tolerance": "5",
                    "windowMs": "200",
                    "mode": "Any",
                    "targetChannel": "bus-a",
                    "kind": "AssertSignalWithin"
                  }
                },
                {
                  "parameters": {
                    "$kind": "assertStable",
                    "signalName": "BMS.EngineRPM",
                    "windowMs": "200",
                    "maxDelta": "5",
                    "minSamples": "3",
                    "kind": "AssertStable"
                  }
                },
                {
                  "parameters": {
                    "$kind": "readDid",
                    "did": 61840,
                    "targetChannel": "bus-a",
                    "kind": "ReadDid"
                  }
                }
              ],
              "tags": [],
              "timeoutMs": 0
            }
          ],
          "globalCaseFixtureKeys": [],
          "suiteFixtureKeys": [],
          "config": {
            "failurePolicy": "ContinueAll",
            "continueAfterSetupFailure": true
          },
          "timeoutMs": 0
        }
        """;

        var suite = JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default);

        Assert.NotNull(suite);
        Assert.Equal("StudioTaskCSuite", suite!.Name);
        var tc = Assert.Single(suite.Cases);
        Assert.Equal(3, tc.Steps.Count);

        // $kind 判别器还原 0.14.0 新 kind。
        var sw = Assert.IsType<AssertSignalWithinStep>(tc.Steps[0].Parameters);
        Assert.Equal("BMS.EngineRPM", sw.SignalName);
        Assert.Equal("200", sw.WindowMs);
        Assert.Equal(MatchMode.Any, sw.Mode);
        Assert.Equal("bus-a", sw.TargetChannel);   // TargetChannel 透传

        Assert.IsType<AssertStableStep>(tc.Steps[1].Parameters);
        var rd = Assert.IsType<ReadDidStep>(tc.Steps[2].Parameters);
        Assert.Equal(0xF190, rd.Did);
        Assert.Equal("bus-a", rd.TargetChannel);   // UDS 步骤 TargetChannel 透传
    }
}
