using System;
using System.IO;
using Rig.Wpf.RigMetier.Legacy;

namespace Rig.Wpf.RigMetier.Legacy.Tests;

/// <summary>
/// Fixture xUnit qui initialise une seule fois le bootstrap RigMetier legacy
/// pour toute la classe (et entre classes via <see cref="ICollectionFixture"/>).
/// </summary>
public sealed class LegacyRigMetierFixture : IDisposable
{
    public LegacyRigMetierFixture()
    {
        var code = Environment.GetEnvironmentVariable("RIG_WPF_TEST_GREFFE")
                   ?? "7401";

        Bootstrap = new LegacyRigMetierBootstrap();
        IsAvailable = Directory.Exists(LegacyRigMetierBootstrap.DefaultBinCDirectory)
                      && Directory.Exists(@"C:\rig\Bin Dot Net Gac")
                      && Bootstrap.Initialize(code);
    }

    public LegacyRigMetierBootstrap Bootstrap { get; }
    public bool IsAvailable { get; }

    public void Dispose() { /* nothing to clean */ }
}

[Xunit.CollectionDefinition("Legacy")]
public sealed class LegacyCollection : Xunit.ICollectionFixture<LegacyRigMetierFixture> { }
