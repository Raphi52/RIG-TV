using System;

namespace Rig.Wpf.Processus.Ipe;

/// <summary>
/// Service métier qui encapsule la logique non-UI du Processus IPE.
/// Reproduit notamment <c>PROC_IPE.PrefixFacturationChangement2016</c> du legacy :
/// le préfixe de facturation dépend de la date de saisine au greffe (cutoffs
/// 2016-05-01 puis 2018-05-01).
/// </summary>
public sealed class IpeOrchestrator
{
    private static readonly DateTime Cutoff2016 = new(2016, 5, 1);
    private static readonly DateTime Cutoff2018 = new(2018, 5, 1);

    public string GetPrefixFacturation(DateTime? dateSaisineGreffe)
    {
        if (!dateSaisineGreffe.HasValue) return "";
        var d = dateSaisineGreffe.Value;
        if (d < Cutoff2016) return "";
        if (d < Cutoff2018) return "16-";
        return "18-";
    }
}
