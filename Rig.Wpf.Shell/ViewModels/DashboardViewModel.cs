using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm;

namespace Rig.Wpf.Shell.ViewModels;

/// <summary>
/// Page d'accueil affichée quand aucun tab n'est ouvert dans le shell.
/// Trois sections : KPI globaux, quick actions (cards par module), activité récente.
/// Les actions appellent ShellViewModel.OpenTabCommand via le delegate <c>openProcessus</c>.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    public DashboardViewModel(Action<string> openProcessus, SessionInfo session)
    {
        if (openProcessus is null) throw new ArgumentNullException(nameof(openProcessus));
        if (session is null) throw new ArgumentNullException(nameof(session));

        var now = DateTime.Now;
        Greeting = now.Hour < 13 ? "Bonjour" : (now.Hour < 18 ? "Bon après-midi" : "Bonsoir");
        UserName = session.UserName;
        RoleLabel = session.UserRole;
        UserInitials = session.UserInitials;
        TodayLabel = CultureInfo.GetCultureInfo("fr-FR").DateTimeFormat
            .GetDayName(now.DayOfWeek).Substring(0, 1).ToUpper()
            + CultureInfo.GetCultureInfo("fr-FR").DateTimeFormat
                .GetDayName(now.DayOfWeek).Substring(1)
            + " " + now.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
        GreffeLabel = $"Base {session.BaseDatabase} · {session.BaseServer}";

        Kpis = new[]
        {
            new DashboardKpi("Mandataires actifs",   "47",     "+3 ce mois", "success", "IconBriefcase",  "MANDATAIRE",  openProcessus),
            new DashboardKpi("Dossiers en cours",    "128",    "+12 cette sem.", "info",   "IconScale",      "DEMO_JUD_INSTANCE", openProcessus),
            new DashboardKpi("Audiences à tenir",    "12",     "Cette semaine",  "warning","IconClipboard",  "DEMO_JUD_AUDIENCE", openProcessus),
            new DashboardKpi("Factures impayées",    "240 €",  "3 dossiers",     "error",  "IconDocument",   "DEMO_COMPTA_FACTURE", openProcessus),
        };

        QuickActions = new[]
        {
            new DashboardActionCard("Mandataires",      "Tableau des avocats et mandataires judiciaires",
                "JUDICIAIRE", "IconBriefcase", "MANDATAIRE",          true, openProcessus),
            new DashboardActionCard("Instances",        "Dossiers ouverts devant le tribunal",
                "JUDICIAIRE", "IconScale",      "DEMO_JUD_INSTANCE",   false, openProcessus),
            new DashboardActionCard("Audiences",        "Planning, formations, présidence",
                "JUDICIAIRE", "IconClipboard",  "DEMO_JUD_AUDIENCE",   false, openProcessus),
            new DashboardActionCard("Sociétés",         "Personnes morales immatriculées",
                "RCS",        "IconBriefcase",  "DEMO_RCS_SOCIETE",    false, openProcessus),
            new DashboardActionCard("Actes déposés",    "Statuts, PV, modifications, comptes",
                "RCS",        "IconClipboard",  "DEMO_RCS_ACTE",       false, openProcessus),
            new DashboardActionCard("Éditions K-bis",   "Consultation des extraits K-bis officiels",
                "RCS",        "IconDocument",   "KBIS",                true,  openProcessus),
            new DashboardActionCard("Privilèges",       "Inscriptions au registre des sûretés",
                "INSCRIPTIONS","IconLock",       "DEMO_INS_PRIVILEGE",  false, openProcessus),
            new DashboardActionCard("Factures",         "Émission et suivi de facturation",
                "COMPTA",     "IconDocument",   "DEMO_COMPTA_FACTURE", false, openProcessus),
        };

        Recent = new[]
        {
            new DashboardRecent("SARL Lefebvre",           "Modification de gérance",      "il y a 2h",     "IconBriefcase",  "DEMO_RCS_SOCIETE",    openProcessus),
            new DashboardRecent("Me Dupont — DUPONT_AVO",  "Création mandataire",          "il y a 4h",     "IconBriefcase",  "MANDATAIRE",          openProcessus),
            new DashboardRecent("Référé · Salle 2",        "Audience à 09:00",             "demain",        "IconScale",      "DEMO_JUD_AUDIENCE",   openProcessus),
            new DashboardRecent("F/2026/04322",            "Expert+ Comptables · 288 €",   "envoyée hier",  "IconDocument",   "DEMO_COMPTA_FACTURE", openProcessus),
            new DashboardRecent("PC/2025/00128 · Bélier",  "Vérification créances",        "ouvert mardi",  "IconClipboard",  "DEMO_PC_DOSSIER",     openProcessus),
        };
    }

    public string Greeting { get; }
    public string UserName { get; }
    public string UserInitials { get; }
    public string RoleLabel { get; }
    public string TodayLabel { get; }
    public string GreffeLabel { get; }
    public IReadOnlyList<DashboardKpi> Kpis { get; }
    public IReadOnlyList<DashboardActionCard> QuickActions { get; }
    public IReadOnlyList<DashboardRecent> Recent { get; }
}

public sealed class DashboardKpi : ViewModelBase
{
    public DashboardKpi(string label, string value, string trend, string kind, string iconKey,
        string tabCode, Action<string> openProcessus)
    {
        Label = label;
        Value = value;
        Trend = trend;
        Kind = kind;
        IconKey = iconKey;
        OpenCommand = new RelayCommand(() => openProcessus(tabCode));
    }

    public string Label { get; }
    public string Value { get; }
    public string Trend { get; }
    public string Kind { get; }   // success / info / warning / error
    public string IconKey { get; }
    public RelayCommand OpenCommand { get; }
}

public sealed class DashboardActionCard : ViewModelBase
{
    public DashboardActionCard(string title, string subtitle, string domain, string iconKey,
        string tabCode, bool isFeatured, Action<string> openProcessus)
    {
        Title = title;
        Subtitle = subtitle;
        Domain = domain;
        IconKey = iconKey;
        IsFeatured = isFeatured;
        OpenCommand = new RelayCommand(() => openProcessus(tabCode));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Domain { get; }
    public string IconKey { get; }
    /// <summary>True pour le module phare (mis en avant visuellement).</summary>
    public bool IsFeatured { get; }
    public RelayCommand OpenCommand { get; }
}

public sealed class DashboardRecent : ViewModelBase
{
    public DashboardRecent(string label, string subLabel, string timeLabel, string iconKey,
        string tabCode, Action<string> openProcessus)
    {
        Label = label;
        SubLabel = subLabel;
        TimeLabel = timeLabel;
        IconKey = iconKey;
        OpenCommand = new RelayCommand(() => openProcessus(tabCode));
    }

    public string Label { get; }
    public string SubLabel { get; }
    public string TimeLabel { get; }
    public string IconKey { get; }
    public RelayCommand OpenCommand { get; }
}
