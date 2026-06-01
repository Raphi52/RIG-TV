using FluentAssertions;
using Rig.Wpf.Mvvm.Mapping;
using Xunit;

namespace Rig.Wpf.Mvvm.Tests.Mapping;

public class DbObjectMapperTests
{
    private sealed class FakeEntity
    {
        public string Code { get; set; } = "";
        public string? Libelle { get; set; }
    }

    private sealed class FakeViewModel : ViewModelBase
    {
        private string? _code;
        private string? _libelle;

        public string? Code
        {
            get => _code;
            set => SetProperty(ref _code, value);
        }

        public string? Libelle
        {
            get => _libelle;
            set => SetProperty(ref _libelle, value);
        }
    }

    private sealed class FakeMapper : DbObjectMapper<FakeEntity, FakeViewModel>
    {
        protected override void DoLoad(FakeEntity source, FakeViewModel target)
        {
            target.Code = source.Code;
            target.Libelle = source.Libelle;
        }

        protected override void DoSave(FakeViewModel source, FakeEntity target)
        {
            target.Code = source.Code ?? "";
            target.Libelle = source.Libelle;
        }
    }

    [Fact]
    public void Load_PopulatesViewModel_FromEntity()
    {
        var sut = new FakeMapper();
        var entity = new FakeEntity { Code = "X", Libelle = "Test" };
        var vm = new FakeViewModel();

        sut.Load(entity, vm);

        vm.Code.Should().Be("X");
        vm.Libelle.Should().Be("Test");
    }

    [Fact]
    public void Save_PopulatesEntity_FromViewModel()
    {
        var sut = new FakeMapper();
        var vm = new FakeViewModel { Code = "Y", Libelle = "Saved" };
        var entity = new FakeEntity();

        sut.Save(vm, entity);

        entity.Code.Should().Be("Y");
        entity.Libelle.Should().Be("Saved");
    }

    [Fact]
    public void Load_NullSource_Throws()
    {
        var sut = new FakeMapper();

        FluentActions.Invoking(() => sut.Load(null!, new FakeViewModel()))
            .Should().Throw<System.ArgumentNullException>();
    }

    [Fact]
    public void Save_NullTarget_Throws()
    {
        var sut = new FakeMapper();

        FluentActions.Invoking(() => sut.Save(new FakeViewModel(), null!))
            .Should().Throw<System.ArgumentNullException>();
    }

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var sut = new FakeMapper();
        var original = new FakeEntity { Code = "RT", Libelle = "RoundTrip" };
        var vm = new FakeViewModel();
        var roundTripped = new FakeEntity();

        sut.Load(original, vm);
        sut.Save(vm, roundTripped);

        roundTripped.Code.Should().Be(original.Code);
        roundTripped.Libelle.Should().Be(original.Libelle);
    }
}
