using System.Collections.Generic;
using Rig.Wpf.Core.Abstractions;

namespace Rig.Wpf.Core.Session;

/// <summary>
/// Implémentation neutre de <see cref="IGreffeService"/>.
/// </summary>
public sealed class NullGreffeService : IGreffeService
{
    public GreffeInfo? GetCurrent() => null;
    public IReadOnlyList<GreffeInfo> GetAvailable() => System.Array.Empty<GreffeInfo>();
}
