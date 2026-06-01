using System;
using FluentAssertions;
using Rig.Wpf.Mvvm.Navigation;
using Xunit;

namespace Rig.Wpf.Mvvm.Tests.Navigation;

public class NavigationServiceTests
{
    [Fact]
    public void NewInstance_HasNoCurrent_AndCannotGoBack()
    {
        var sut = new NavigationService();

        sut.Current.Should().BeNull();
        sut.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void NavigateTo_SetsCurrent_AndRaisesEvent()
    {
        var sut = new NavigationService();
        NavigatedEventArgs? captured = null;
        sut.Navigated += (_, e) => captured = e;
        var vm = new object();

        sut.NavigateTo(vm);

        sut.Current.Should().BeSameAs(vm);
        captured.Should().NotBeNull();
        captured!.From.Should().BeNull();
        captured.To.Should().BeSameAs(vm);
    }

    [Fact]
    public void NavigateTo_Twice_PushesPreviousToBackStack()
    {
        var sut = new NavigationService();
        var first = new object();
        var second = new object();

        sut.NavigateTo(first);
        sut.NavigateTo(second);

        sut.Current.Should().BeSameAs(second);
        sut.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void GoBack_RestoresPrevious()
    {
        var sut = new NavigationService();
        var first = new object();
        var second = new object();
        sut.NavigateTo(first);
        sut.NavigateTo(second);

        sut.GoBack();

        sut.Current.Should().BeSameAs(first);
        sut.CanGoBack.Should().BeFalse();
    }

    [Fact]
    public void GoBack_WhenNoHistory_IsNoOp()
    {
        var sut = new NavigationService();
        var raised = 0;
        sut.Navigated += (_, _) => raised++;

        sut.GoBack();

        raised.Should().Be(0);
    }

    [Fact]
    public void NavigateTo_Null_Throws()
    {
        var sut = new NavigationService();

        FluentActions.Invoking(() => sut.NavigateTo(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
