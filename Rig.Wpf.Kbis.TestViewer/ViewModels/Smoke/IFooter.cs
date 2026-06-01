// SPDX-License-Identifier: Proprietary
// SmokePage template — interface footer disk usage (Phase 1).

using System.ComponentModel;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Bandeau footer affiché en bas de SmokePageView : disk usage des self-snaps PNG
/// (cumulé sur N derniers runs) + lien "Tout effacer".
///
/// Calculé via Services.SnapDiskUsage.ComputeTotal() — recompute après chaque batch
/// (RefreshSnapDiskUsage côté implémentation).
/// </summary>
public interface IFooter : INotifyPropertyChanged
{
    /// <summary>String human-readable (ex. "53.4 MB sur 4 runs"). Affiché dans le footer.
    /// FallbackValue "(calcul...)" pendant le compute initial.</summary>
    string? DiskUsageHuman { get; }

    /// <summary>Lien "🗑 Tout effacer" : supprime tous les self-snap PNG du disque,
    /// garde les 10 derniers runs si possible. Refresh DiskUsageHuman après.
    /// Demande confirmation modale (action destructive).</summary>
    ICommand CleanupAllSnapsCommand { get; }
}
