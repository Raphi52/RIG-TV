using System;
using FluentAssertions;
using Rig.Wpf.Core.Session;
using Xunit;

namespace Rig.Wpf.Core.Tests.Session;

public class InMemorySessionContextTests
{
    [Fact]
    public void NewInstance_IsNotAuthenticated()
    {
        var sut = new InMemorySessionContext();

        sut.IsAuthenticated.Should().BeFalse();
        sut.CodeUtilisateur.Should().BeNull();
        sut.CodeGreffe.Should().BeNull();
    }

    [Fact]
    public void SignIn_PopulatesIdentity_AndRaisesSessionChanged()
    {
        var sut = new InMemorySessionContext();
        var raised = 0;
        sut.SessionChanged += (_, _) => raised++;

        sut.SignIn("USR1", "G001");

        sut.IsAuthenticated.Should().BeTrue();
        sut.CodeUtilisateur.Should().Be("USR1");
        sut.CodeGreffe.Should().Be("G001");
        raised.Should().Be(1);
    }

    [Fact]
    public void SignIn_WithEmptyUser_Throws()
    {
        var sut = new InMemorySessionContext();

        FluentActions.Invoking(() => sut.SignIn(" ", "G001"))
            .Should().Throw<ArgumentException>()
            .WithParameterName("codeUtilisateur");
    }

    [Fact]
    public void SignOut_ClearsIdentity_AndRaisesSessionChanged()
    {
        var sut = new InMemorySessionContext();
        sut.SignIn("USR1", "G001");
        var raised = 0;
        sut.SessionChanged += (_, _) => raised++;

        sut.SignOut();

        sut.IsAuthenticated.Should().BeFalse();
        sut.CodeUtilisateur.Should().BeNull();
        sut.CodeGreffe.Should().BeNull();
        raised.Should().Be(1);
    }

    [Fact]
    public void SignOut_OnAnonymousSession_DoesNotRaiseEvent()
    {
        var sut = new InMemorySessionContext();
        var raised = 0;
        sut.SessionChanged += (_, _) => raised++;

        sut.SignOut();

        raised.Should().Be(0);
    }

    [Fact]
    public void SwitchGreffe_ToDifferentCode_RaisesEvent()
    {
        var sut = new InMemorySessionContext();
        sut.SignIn("USR1", "G001");
        var raised = 0;
        sut.SessionChanged += (_, _) => raised++;

        sut.SwitchGreffe("G002");

        sut.CodeGreffe.Should().Be("G002");
        raised.Should().Be(1);
    }

    [Fact]
    public void SwitchGreffe_ToSameCode_DoesNotRaiseEvent()
    {
        var sut = new InMemorySessionContext();
        sut.SignIn("USR1", "G001");
        var raised = 0;
        sut.SessionChanged += (_, _) => raised++;

        sut.SwitchGreffe("G001");

        raised.Should().Be(0);
    }
}
