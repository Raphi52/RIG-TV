using System.Collections.Generic;

namespace Rig.Wpf.Core.Abstractions;

public interface IGreffeService
{
    GreffeInfo? GetCurrent();
    IReadOnlyList<GreffeInfo> GetAvailable();
}

public sealed record GreffeInfo(
    string Code,
    string Libelle);
