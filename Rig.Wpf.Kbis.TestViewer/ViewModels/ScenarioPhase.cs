namespace Rig.Wpf.Kbis.TestViewer.ViewModels;

public enum ScenarioPhase
{
    Queued,
    Running,    // worker vivant, exécution en cours (KBIS : entre spawn et ligne finale)
    Launching,
    Login,
    OpenProcRetaud,
    SelectAudience,
    ClickImporter,
    Recap,
    Apply,
    Done,
    Fail
}
