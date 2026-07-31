using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Analysis;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Analysis;

public class Sprint14Tests
{
    private static TestCaseResult MakeCase(string id, string name, bool passed, string? reason, StepResult[] steps)
        => new(id, name, passed, reason, 10, Math.Max(1, steps.Length), passed ? steps.Length : 0, passed ? 0 : 1, 0, 0, steps);

    private static TestSuiteResult CreateFailedResult()
    {
        return new TestSuiteResult("Suite", 3, 2, 1, 0, 100,
            Array.Empty<string>(), new TestCaseResult[]
            {
                MakeCase("p1", "Pass1", true, null, Array.Empty<StepResult>()),
                MakeCase("p2", "Pass2", true, null, Array.Empty<StepResult>()),
                MakeCase("f1", "Fail1", false, "assertion failed", new[]
                {
                    new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed, "out of tolerance", "5", "10", 0)
                }),
            });
    }

    [Fact]
    public void PromptBuilder_ExcludesPassedCases_OnlyFailedInPrompt()
    {
        var result = CreateFailedResult();
        var prompt = HilPromptBuilder.Build(result);

        Assert.Contains("Fail1", prompt);
        Assert.DoesNotContain("Pass1", prompt);
        Assert.DoesNotContain("Pass2", prompt);
    }

    [Fact]
    public void PromptBuilder_FramesTruncated_At20Frames()
    {
        var frames = Enumerable.Range(0, 30).Select(i =>
            new CanFrame(
                new CanId(0x123, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { (byte)i }),
                FrameFlags.None, ChannelId.None, new Timestamp((ulong)(i * 1000)))).ToList();

        var step = new StepResult(0, TestCaseStepKind.AssertSignal, "s1", StepStatus.Failed,
            "fail", null, null, 0, frames);

        var caseResult = MakeCase("f1", "FailCase", false, "fail", new[] { step });
        var result = new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(), new[] { caseResult });

        var prompt = HilPromptBuilder.Build(result);

        var frameLines = prompt.Split('\n').Count(l => l.TrimStart().StartsWith("0x", StringComparison.Ordinal));
        Assert.True(frameLines <= 20, $"Expected <= 20 frame lines, got {frameLines}");
    }

    [Fact]
    public void PromptBuilder_WithEcuScript_IncludesEcuConfiguration()
    {
        var result = CreateFailedResult();
        var prompt = HilPromptBuilder.Build(result, new EcuScript("TestEcu", null!, null!));

        Assert.Contains("## ECU Configuration", prompt);
        Assert.Contains("TestEcu", prompt);
    }

    [Fact]
    public void PromptBuilder_WithoutEcuScript_OmitsEcuConfiguration()
    {
        var result = CreateFailedResult();
        var prompt = HilPromptBuilder.Build(result);

        Assert.DoesNotContain("## ECU Configuration", prompt);
    }

    [Fact]
    public async Task AnalysisService_MockHttpClient_ReturnsContent()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "Root cause: signal drift detected" } } }
            }))
        });

        var httpClient = new HttpClient(mockHandler);
        var credentialStore = new SimpleCredentialStore();
        await credentialStore.SetAsync("deepseek-api-key", "test-key");

        var service = new HilAnalysisService(httpClient, credentialStore, Options.Create(new DeepSeekOptions()));
        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.False(result.IsUnavailable);
        Assert.Contains("signal drift", result.Content);
    }

    [Fact]
    public async Task AnalysisService_MissingApiKey_ReturnsUnavailable()
    {
        var credentialStore = new SimpleCredentialStore();
        var service = new HilAnalysisService(new HttpClient(), credentialStore, Options.Create(new DeepSeekOptions()));
        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.True(result.IsUnavailable);
    }

    [Fact]
    public async Task AnalysisService_HttpError_ReturnsError()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var httpClient = new HttpClient(mockHandler);
        var credentialStore = new SimpleCredentialStore();
        await credentialStore.SetAsync("deepseek-api-key", "test-key");

        var service = new HilAnalysisService(httpClient, credentialStore, Options.Create(new DeepSeekOptions()));
        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.True(result.IsUnavailable);
    }

    [Fact]
    public async Task CredentialStore_SetThenGet_ReturnsValue()
    {
        var store = new SimpleCredentialStore();
        await store.SetAsync("deepseek-api-key", "my-key");

        var value = await store.GetAsync("deepseek-api-key");
        Assert.Equal("my-key", value);
    }

    [Fact]
    public async Task CredentialStore_GetFromEnvVar_ReturnsValue()
    {
        Environment.SetEnvironmentVariable("HIL_DEEPSEEK_API_KEY", "env-key");
        try
        {
            var store = new SimpleCredentialStore();
            var value = await store.GetAsync("deepseek-api-key");
            Assert.Equal("env-key", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HIL_DEEPSEEK_API_KEY", null);
        }
    }
}

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public MockHttpMessageHandler(HttpResponseMessage response) => _response = response;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_response);
}
