using System.Collections.ObjectModel;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.App.ViewModels.TestSuiteBuilder;

/// <summary>可编辑测试用例。保留全部字段以保证 ToCase/FromCase round-trip 保真。</summary>
public sealed class EditableTestCase
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PreConditions { get; set; }
    public string? PostConditions { get; set; }
    public List<string> Tags { get; } = new();
    public int TimeoutMs { get; set; }
    public IReadOnlyList<string>? CaseFixtureKeys { get; set; }
    public ObservableCollection<EditableTestCaseStep> Steps { get; } = new();

    public TestCase ToCase() => new(
        Id, Name, Description, PreConditions,
        Steps.Select(s => s.ToStep()).ToList(),
        PostConditions, Tags, TimeoutMs, CaseFixtureKeys);

    public static EditableTestCase FromCase(TestCase c)
    {
        var e = new EditableTestCase
        {
            Id = c.Id,
            Name = c.Name,
            Description = c.Description ?? "",
            PreConditions = c.PreConditions,
            PostConditions = c.PostConditions,
            TimeoutMs = c.TimeoutMs,
            CaseFixtureKeys = c.CaseFixtureKeys,
        };
        if (c.Tags is { } tags) e.Tags.AddRange(tags);
        foreach (var s in c.Steps) e.Steps.Add(EditableTestCaseStep.FromStep(s));
        return e;
    }
}
