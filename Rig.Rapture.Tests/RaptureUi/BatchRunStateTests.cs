using System;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.ViewModels;
using Xunit;

namespace Rig.Rapture.Tests.RaptureUi;

public class BatchRunStateTests
{
    [Fact]
    public void New_StateIsIdle_ZeroCounts()
    {
        var b = new BatchRunState();
        b.Phase.Should().Be(BatchPhase.Idle);
        b.PassCount.Should().Be(0);
        b.FailCount.Should().Be(0);
        b.QueuedCount.Should().Be(0);
        b.Scenarios.Should().BeEmpty();
    }

    [Fact]
    public void AddScenarios_UpdatesQueuedCount()
    {
        var b = new BatchRunState();
        b.Scenarios.Add(new ScenarioRunState { Id = "cas-a", JsonPath = "a.json" });
        b.Scenarios.Add(new ScenarioRunState { Id = "cas-b", JsonPath = "b.json" });
        b.RefreshCounts();
        b.QueuedCount.Should().Be(2);
        b.TotalCount.Should().Be(2);
    }

    [Fact]
    public void OnePass_OneFail_CountsCorrect()
    {
        var b = new BatchRunState();
        var s1 = new ScenarioRunState { Id = "a", JsonPath = "a.json" };
        var s2 = new ScenarioRunState { Id = "b", JsonPath = "b.json" };
        b.Scenarios.Add(s1);
        b.Scenarios.Add(s2);
        s1.MarkStarted(1, "d1", DateTime.UtcNow);
        s1.MarkFinished(Verdict.Pass);
        s2.MarkStarted(2, "d2", DateTime.UtcNow);
        s2.MarkFinished(Verdict.Fail);
        b.RefreshCounts();
        b.PassCount.Should().Be(1);
        b.FailCount.Should().Be(1);
        b.QueuedCount.Should().Be(0);
    }

    [Fact]
    public void SelectedScenario_PersistsAcrossPhaseChange()
    {
        var b = new BatchRunState();
        var s = new ScenarioRunState { Id = "a", JsonPath = "a.json" };
        b.Scenarios.Add(s);
        b.SelectedScenario = s;
        s.MarkStarted(1, "d1", DateTime.UtcNow);
        s.MarkFinished(Verdict.Pass);
        b.SelectedScenario.Should().Be(s);
    }
}
