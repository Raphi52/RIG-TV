using System;
using FluentAssertions;
using Rig.Wpf.Kbis.TestViewer.ViewModels;
using Xunit;

namespace Rig.Rapture.Tests.RaptureUi;

public class ScenarioRunStateTests
{
    [Fact]
    public void New_DefaultsToQueued()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        s.Phase.Should().Be(ScenarioPhase.Queued);
        s.Verdict.Should().Be(Verdict.Pending);
        s.StartedAt.Should().BeNull();
    }

    [Fact]
    public void MarkStarted_SetsPhaseLaunchingAndStartTime()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        var t = DateTime.UtcNow;
        s.MarkStarted(workerPid: 1234, desktopName: "RigSmoke_1234", startedAt: t);
        s.Phase.Should().Be(ScenarioPhase.Launching);
        s.WorkerPid.Should().Be(1234);
        s.DesktopName.Should().Be("RigSmoke_1234");
        s.StartedAt.Should().Be(t);
    }

    [Fact]
    public void Duration_IsComputedFromStartedAt()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        var t0 = DateTime.UtcNow.AddSeconds(-90);
        s.MarkStarted(1234, "RigSmoke_1234", t0);
        s.Duration.TotalSeconds.Should().BeApproximately(90, 2);
    }

    [Fact]
    public void MarkFinished_PassSetsDoneAndPassVerdict()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        s.MarkStarted(1234, "RigSmoke_1234", DateTime.UtcNow);
        s.MarkFinished(Verdict.Pass);
        s.Phase.Should().Be(ScenarioPhase.Done);
        s.Verdict.Should().Be(Verdict.Pass);
    }

    [Fact]
    public void MarkFinished_FailSetsFailPhaseAndFailVerdict()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        s.MarkStarted(1234, "RigSmoke_1234", DateTime.UtcNow);
        s.MarkFinished(Verdict.Fail);
        s.Phase.Should().Be(ScenarioPhase.Fail);
        s.Verdict.Should().Be(Verdict.Fail);
    }

    [Fact]
    public void AppendLog_RollingBufferCapsAt500Lines()
    {
        var s = new ScenarioRunState { Id = "cas-foo", JsonPath = "/x.json" };
        for (int i = 0; i < 600; i++) s.AppendLog($"line {i}");
        s.LogsTail.Split('\n').Length.Should().BeLessThanOrEqualTo(500);
        s.LogsTail.Should().Contain("line 599");
        s.LogsTail.Should().NotContain("line 0");
    }
}
