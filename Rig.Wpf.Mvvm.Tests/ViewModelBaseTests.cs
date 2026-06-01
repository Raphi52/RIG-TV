using FluentAssertions;
using Xunit;

namespace Rig.Wpf.Mvvm.Tests;

public class ViewModelBaseTests
{
    private sealed class SampleViewModel : ViewModelBase
    {
        private string? _name;
        public string? Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
    }

    [Fact]
    public void SetProperty_RaisesPropertyChanged_WhenValueDiffers()
    {
        var sut = new SampleViewModel();
        var raised = 0;
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SampleViewModel.Name)) raised++;
        };

        sut.Name = "Alice";

        sut.Name.Should().Be("Alice");
        raised.Should().Be(1);
    }

    [Fact]
    public void SetProperty_DoesNotRaise_WhenValueIsTheSame()
    {
        var sut = new SampleViewModel { Name = "Alice" };
        var raised = 0;
        sut.PropertyChanged += (_, _) => raised++;

        sut.Name = "Alice";

        raised.Should().Be(0);
    }
}
