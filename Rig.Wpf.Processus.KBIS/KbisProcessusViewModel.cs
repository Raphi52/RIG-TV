using System;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.Kbis.Etapes;
using Rig.Wpf.RigMetier.Repositories;
using Rig.Wpf.RigMetier.Services;

namespace Rig.Wpf.Processus.Kbis;

public sealed class KbisProcessusViewModel : ProcessusViewModelBase
{
    public KbisProcessusViewModel(
        ISocieteRepository societes,
        IKbisGenerator generator,
        string codeGreffe)
        : base("KBIS", "Extraits K-bis",
            new EtapeViewModelBase[]
            {
                new KbisConsultationEtapeViewModel(societes, generator, codeGreffe),
            })
    {
        if (societes is null) throw new ArgumentNullException(nameof(societes));
        if (generator is null) throw new ArgumentNullException(nameof(generator));
    }
}
