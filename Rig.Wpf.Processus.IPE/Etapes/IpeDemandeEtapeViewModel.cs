using System;
using System.Collections.Generic;
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.Ipe.Etapes;

public sealed class IpeDemandeEtapeViewModel : EtapeViewModelBase
{
    private TypeIp? _typeIp;
    private DateTime? _dateSaisineGreffe;

    public IpeDemandeEtapeViewModel()
        : base("IPE_DEMANDE", "Demande IP")
    {
        Recompute();
    }

    public IReadOnlyList<TypeIp> TypesIpDisponibles { get; } =
        new[] { Etapes.TypeIp.Standard, Etapes.TypeIp.TribunalDigital };

    public TypeIp? TypeIp
    {
        get => _typeIp;
        set { if (SetProperty(ref _typeIp, value)) Recompute(); }
    }

    public DateTime? DateSaisineGreffe
    {
        get => _dateSaisineGreffe;
        set { if (SetProperty(ref _dateSaisineGreffe, value)) Recompute(); }
    }

    private void Recompute()
        => SetIsValid(_typeIp.HasValue && _dateSaisineGreffe.HasValue);
}
