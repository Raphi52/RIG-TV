using System.Collections.Generic;
using FluentAssertions;
using Rig.Wpf.Mvvm.Processus;
using Xunit;

namespace Rig.Wpf.Mvvm.Tests.Processus;

public class ProcessusViewModelBaseTests
{
    private sealed class StubEtape : EtapeViewModelBase
    {
        public StubEtape(string code, bool initiallyValid)
            : base(code, code) => IsValid = initiallyValid;

        public new bool IsValid
        {
            get => base.IsValid;
            set => SetIsValid(value);
        }
    }

    private sealed class StubProcessus : ProcessusViewModelBase
    {
        public StubProcessus(IEnumerable<EtapeViewModelBase> etapes)
            : base("STUB", "Stub processus", etapes) { }
    }

    [Fact]
    public void NewInstance_HasFirstEtapeAsCurrent()
    {
        var e1 = new StubEtape("E1", initiallyValid: true);
        var e2 = new StubEtape("E2", initiallyValid: false);
        var sut = new StubProcessus(new[] { e1, e2 });

        sut.CurrentEtape.Should().BeSameAs(e1);
        sut.Etapes.Should().HaveCount(2);
    }

    [Fact]
    public void GoNext_AdvancesToNextEtape_WhenCurrentIsValid()
    {
        var e1 = new StubEtape("E1", initiallyValid: true);
        var e2 = new StubEtape("E2", initiallyValid: false);
        var sut = new StubProcessus(new[] { e1, e2 });

        sut.GoNextCommand.Execute(null);

        sut.CurrentEtape.Should().BeSameAs(e2);
    }

    [Fact]
    public void GoNext_BlockedWhenCurrentInvalid()
    {
        var e1 = new StubEtape("E1", initiallyValid: false);
        var e2 = new StubEtape("E2", initiallyValid: true);
        var sut = new StubProcessus(new[] { e1, e2 });

        sut.GoNextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void GoNext_NotifiesCanExecute_WhenEtapeBecomesValid()
    {
        var e1 = new StubEtape("E1", initiallyValid: false);
        var e2 = new StubEtape("E2", initiallyValid: true);
        var sut = new StubProcessus(new[] { e1, e2 });
        var raised = 0;
        sut.GoNextCommand.CanExecuteChanged += (_, _) => raised++;

        e1.IsValid = true;

        raised.Should().BeGreaterThan(0);
        sut.GoNextCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void GoPrevious_ReturnsToPriorEtape()
    {
        var e1 = new StubEtape("E1", initiallyValid: true);
        var e2 = new StubEtape("E2", initiallyValid: true);
        var sut = new StubProcessus(new[] { e1, e2 });
        sut.GoNextCommand.Execute(null);

        sut.GoPreviousCommand.Execute(null);

        sut.CurrentEtape.Should().BeSameAs(e1);
    }

    [Fact]
    public void GoPrevious_BlockedOnFirstEtape()
    {
        var e1 = new StubEtape("E1", initiallyValid: true);
        var sut = new StubProcessus(new[] { e1 });

        sut.GoPreviousCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void GoNext_BlockedOnLastEtape()
    {
        var e1 = new StubEtape("E1", initiallyValid: true);
        var sut = new StubProcessus(new[] { e1 });

        sut.GoNextCommand.CanExecute(null).Should().BeFalse();
    }
}
