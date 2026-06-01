using System.Collections.Generic;

namespace Rig.Wpf.Shell.Demo;

/// <summary>
/// Catalogue de toutes les configurations de modules démo, regroupées par
/// domaine métier. Chaque entrée matérialise la vision UI finale du module :
/// colonnes typiques de la liste, échantillon de lignes, champs du formulaire
/// de saisie, et actions de la barre d'outils.
/// </summary>
public static class DemoModuleConfigs
{
    private static DemoRow Row(string status, string? statusLabel, params (string Key, object? Value)[] cells)
    {
        var r = new DemoRow { StatusKind = status, StatusLabel = statusLabel };
        foreach (var (k, v) in cells) r.Cells[k] = v;
        return r;
    }

    // ============================================================
    // JUDICIAIRE
    // ============================================================

    public static readonly DemoModuleConfig JudInstance = new(
        Code: "DEMO_JUD_INSTANCE",
        Domain: "JUDICIAIRE",
        Title: "Instances judiciaires",
        Subtitle: "Dossiers ouverts devant le tribunal, parties, postulants et plaidants.",
        IconKey: "IconScale",
        Eyebrow: "DOSSIERS",
        ListColumns: new[]
        {
            new DemoColumn("N° RG", "Rg", 110),
            new DemoColumn("Type d'affaire", "Type", 200),
            new DemoColumn("Demandeur", "Demandeur", 220),
            new DemoColumn("Défendeur", "Defendeur", 220),
            new DemoColumn("Date d'enrôlement", "Date", 140),
            new DemoColumn("État", "Etat", 120),
        },
        SampleRows: new[]
        {
            Row("info", "En cours", ("Rg", "2024/00128"), ("Type", "Référé commercial"),
                ("Demandeur", "SARL Lefebvre"), ("Defendeur", "SAS Distrimat"),
                ("Date", "12/03/2026"), ("Etat", "Mise en délibéré")),
            Row("warning", "À audiencer", ("Rg", "2024/00129"), ("Type", "Ouverture de redressement"),
                ("Demandeur", "Société Bélier"), ("Defendeur", "—"),
                ("Date", "14/03/2026"), ("Etat", "Audience à fixer")),
            Row("success", "Jugement rendu", ("Rg", "2024/00104"), ("Type", "Contestation de créance"),
                ("Demandeur", "Banque Populaire"), ("Defendeur", "SARL Cazaux"),
                ("Date", "27/02/2026"), ("Etat", "Notifié")),
            Row("info", "En cours", ("Rg", "2024/00131"), ("Type", "Difficulté d'entreprise"),
                ("Demandeur", "M. Petit"), ("Defendeur", "SCI Arrosoir"),
                ("Date", "15/03/2026"), ("Etat", "Convocation envoyée")),
            Row("error", "Radié", ("Rg", "2024/00098"), ("Type", "Recouvrement"),
                ("Demandeur", "EDF"), ("Defendeur", "SARL Tonelli"),
                ("Date", "10/01/2026"), ("Etat", "Désistement")),
        },
        SaisieFields: new[]
        {
            new DemoField("N° RG", DemoFieldKind.Text, "auto-attribué"),
            new DemoField("Type d'affaire", DemoFieldKind.Combo, Options:
                new[] { "Référé commercial", "Procédure au fond", "Difficulté d'entreprise", "Recouvrement" }),
            new DemoField("Date d'enrôlement", DemoFieldKind.Date),
            new DemoField("État", DemoFieldKind.Combo, Options:
                new[] { "En cours", "Mise en délibéré", "Jugement rendu", "Radié" }),
            new DemoField("Demandeur", DemoFieldKind.Text, "Raison sociale ou nom"),
            new DemoField("Postulant (avocat)", DemoFieldKind.Combo, Options:
                new[] { "Me Dupont — DUPONT_AVO", "Me Martin — MARTIN_SCP", "Me Bouchard — BOUCHARD" },
                HelperText: "Sélectionne un mandataire dans le tableau."),
            new DemoField("Défendeur", DemoFieldKind.Text, "Raison sociale ou nom"),
            new DemoField("Postulant adverse", DemoFieldKind.Combo, Options:
                new[] { "Me Pernet — PERNET_AVO", "Me Vialle — VIALLE", "(aucun)" }),
            new DemoField("Observations", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Imprimer convocation", "Notifier jugement", "Exporter PDF" });

    public static readonly DemoModuleConfig JudAudience = new(
        Code: "DEMO_JUD_AUDIENCE",
        Domain: "JUDICIAIRE",
        Title: "Audiences",
        Subtitle: "Planning des audiences, rôles, formation et présidence.",
        IconKey: "IconScale",
        Eyebrow: "PLANNING",
        ListColumns: new[]
        {
            new DemoColumn("Date", "Date", 110),
            new DemoColumn("Heure", "Heure", 80),
            new DemoColumn("Salle", "Salle", 100),
            new DemoColumn("Formation", "Formation", 180),
            new DemoColumn("Président", "President", 200),
            new DemoColumn("Dossiers", "Nb", 100),
        },
        SampleRows: new[]
        {
            Row("info", "À tenir", ("Date", "18/05/2026"), ("Heure", "09:00"),
                ("Salle", "Salle 2"), ("Formation", "Référés"), ("President", "M. Dubois"), ("Nb", "12")),
            Row("info", "À tenir", ("Date", "20/05/2026"), ("Heure", "14:00"),
                ("Salle", "Salle 1"), ("Formation", "Chambre commerciale"), ("President", "Mme Martin"), ("Nb", "8")),
            Row("success", "Tenue", ("Date", "11/05/2026"), ("Heure", "09:30"),
                ("Salle", "Salle 2"), ("Formation", "Difficultés d'entreprise"), ("President", "M. Roche"), ("Nb", "5")),
            Row("warning", "À compléter", ("Date", "25/05/2026"), ("Heure", "10:00"),
                ("Salle", "—"), ("Formation", "Référés"), ("President", "M. Dubois"), ("Nb", "3")),
        },
        SaisieFields: new[]
        {
            new DemoField("Date", DemoFieldKind.Date),
            new DemoField("Heure début", DemoFieldKind.Text, "HH:MM"),
            new DemoField("Salle", DemoFieldKind.Combo, Options: new[] { "Salle 1", "Salle 2", "Salle 3", "Visio" }),
            new DemoField("Formation", DemoFieldKind.Combo, Options:
                new[] { "Référés", "Chambre commerciale", "Difficultés d'entreprise", "Conciliation" }),
            new DemoField("Président", DemoFieldKind.Combo, Options:
                new[] { "M. Dubois", "Mme Martin", "M. Roche" }),
            new DemoField("Greffier", DemoFieldKind.Combo, Options: new[] { "Mme Lefranc", "M. Vandier" }),
        },
        ActionsBarre: new[] { "Éditer rôle", "Convoquer parties", "Imprimer planning" });

    public static readonly DemoModuleConfig JudDecision = new(
        Code: "DEMO_JUD_DECISION",
        Domain: "JUDICIAIRE",
        Title: "Décisions judiciaires",
        Subtitle: "Jugements, ordonnances et arrêts rendus par le tribunal.",
        IconKey: "IconClipboard",
        Eyebrow: "JUGEMENTS",
        ListColumns: new[]
        {
            new DemoColumn("N° décision", "Num", 140),
            new DemoColumn("Type", "Type", 180),
            new DemoColumn("RG concerné", "Rg", 110),
            new DemoColumn("Date délibéré", "Date", 130),
            new DemoColumn("Sens", "Sens", 180),
            new DemoColumn("Notif.", "Notif", 100),
        },
        SampleRows: new[]
        {
            Row("success", "Notifié", ("Num", "JUG/2026/0042"), ("Type", "Jugement contradictoire"),
                ("Rg", "2024/00104"), ("Date", "27/02/2026"), ("Sens", "Au profit du demandeur"), ("Notif", "Oui")),
            Row("warning", "À notifier", ("Num", "JUG/2026/0043"), ("Type", "Ordonnance de référé"),
                ("Rg", "2024/00128"), ("Date", "05/05/2026"), ("Sens", "Mesure conservatoire"), ("Notif", "Non")),
            Row("info", "Délai d'appel", ("Num", "JUG/2026/0040"), ("Type", "Jugement par défaut"),
                ("Rg", "2024/00099"), ("Date", "14/02/2026"), ("Sens", "Condamnation"), ("Notif", "Oui")),
        },
        SaisieFields: new[]
        {
            new DemoField("N° de décision", DemoFieldKind.Text, "auto"),
            new DemoField("RG concerné", DemoFieldKind.Combo, Options:
                new[] { "2024/00128", "2024/00129", "2024/00131" }),
            new DemoField("Type", DemoFieldKind.Combo, Options:
                new[] { "Jugement contradictoire", "Jugement par défaut", "Ordonnance de référé", "Arrêt sur opposition" }),
            new DemoField("Date délibéré", DemoFieldKind.Date),
            new DemoField("Sens du jugement", DemoFieldKind.Combo, Options:
                new[] { "Au profit du demandeur", "Au profit du défendeur", "Mesure conservatoire", "Sursis à statuer" }),
            new DemoField("Dispositif", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Signifier", "Édition minute", "Copie exécutoire" });

    public static readonly DemoModuleConfig JudParties = new(
        Code: "DEMO_JUD_PARTIES",
        Domain: "JUDICIAIRE",
        Title: "Parties",
        Subtitle: "Demandeurs, défendeurs, intervenants — personnes physiques ou morales.",
        IconKey: "IconUserGroup",
        Eyebrow: "ACTEURS",
        ListColumns: new[]
        {
            new DemoColumn("Qualité", "Qualite", 120),
            new DemoColumn("Dénomination / Nom", "Nom", 240),
            new DemoColumn("SIREN", "Siren", 120),
            new DemoColumn("Postulant", "Postulant", 200),
            new DemoColumn("Plaidant", "Plaidant", 200),
            new DemoColumn("RG", "Rg", 110),
        },
        SampleRows: new[]
        {
            Row("info", null, ("Qualite", "Demandeur"), ("Nom", "SARL Lefebvre"),
                ("Siren", "451 234 567"), ("Postulant", "Me Dupont"),
                ("Plaidant", "Me Vialle"), ("Rg", "2024/00128")),
            Row("info", null, ("Qualite", "Défendeur"), ("Nom", "SAS Distrimat"),
                ("Siren", "789 654 321"), ("Postulant", "Me Pernet"),
                ("Plaidant", "Me Pernet"), ("Rg", "2024/00128")),
            Row("info", null, ("Qualite", "Demandeur"), ("Nom", "Société Bélier"),
                ("Siren", "812 345 678"), ("Postulant", "Me Martin"),
                ("Plaidant", "—"), ("Rg", "2024/00129")),
        },
        SaisieFields: new[]
        {
            new DemoField("Qualité", DemoFieldKind.Combo, Options:
                new[] { "Demandeur", "Défendeur", "Intervenant volontaire", "Partie civile" }),
            new DemoField("Civilité", DemoFieldKind.Combo, Options:
                new[] { "PM (personne morale)", "M.", "Mme", "ND (non défini)" }),
            new DemoField("Dénomination / Nom", DemoFieldKind.Text, "Raison sociale ou nom"),
            new DemoField("SIREN", DemoFieldKind.Text, "9 chiffres"),
            new DemoField("Adresse", DemoFieldKind.Memo, ColumnSpan: 2),
            new DemoField("Postulant", DemoFieldKind.Combo, Options:
                new[] { "Me Dupont — DUPONT_AVO", "Me Martin — MARTIN_SCP", "Me Pernet — PERNET_AVO" },
                HelperText: "Avocat qui présente les actes auprès du greffe."),
            new DemoField("Plaidant", DemoFieldKind.Combo, Options:
                new[] { "Me Vialle", "Me Bouchard", "(le postulant plaide)" }),
        },
        ActionsBarre: new[] { "Rattacher à un dossier", "Imprimer fiche partie", "Désactiver" });

    // ============================================================
    // RCS (Registre du Commerce et des Sociétés)
    // ============================================================

    public static readonly DemoModuleConfig RcsSociete = new(
        Code: "DEMO_RCS_SOCIETE",
        Domain: "RCS",
        Title: "Sociétés",
        Subtitle: "Personnes morales immatriculées au registre du greffe.",
        IconKey: "IconBriefcase",
        Eyebrow: "ENTREPRISES",
        ListColumns: new[]
        {
            new DemoColumn("SIREN", "Siren", 130),
            new DemoColumn("Dénomination", "Denomination", 260),
            new DemoColumn("Forme juridique", "Forme", 180),
            new DemoColumn("Activité", "Activite", 220),
            new DemoColumn("Siège", "Siege", 200),
            new DemoColumn("Immat.", "Date", 120),
        },
        SampleRows: new[]
        {
            Row("success", "Active", ("Siren", "451 234 567"), ("Denomination", "Lefebvre SARL"),
                ("Forme", "SARL"), ("Activite", "Commerce de détail"),
                ("Siege", "12 rue Pasteur, Annecy"), ("Date", "12/06/2018")),
            Row("success", "Active", ("Siren", "789 654 321"), ("Denomination", "Distrimat SAS"),
                ("Forme", "SAS"), ("Activite", "Distribution alimentaire"),
                ("Siege", "ZA des Près, Sallanches"), ("Date", "04/09/2015")),
            Row("warning", "Modif. en cours", ("Siren", "812 345 678"), ("Denomination", "Bélier SA"),
                ("Forme", "SA à conseil"), ("Activite", "Travaux publics"),
                ("Siege", "8 av. de Genève, Thonon"), ("Date", "22/11/2012")),
            Row("error", "Radiée", ("Siren", "631 987 256"), ("Denomination", "Tonelli SARL"),
                ("Forme", "SARL"), ("Activite", "Plomberie"),
                ("Siege", "—"), ("Date", "10/01/2020")),
        },
        SaisieFields: new[]
        {
            new DemoField("SIREN", DemoFieldKind.Text, "9 chiffres", HelperText: "Sera vérifié auprès de l'INSEE."),
            new DemoField("Dénomination", DemoFieldKind.Text, "Raison sociale officielle"),
            new DemoField("Forme juridique", DemoFieldKind.Combo, Options:
                new[] { "SARL", "SAS", "SA", "SCI", "EURL", "SNC", "SCS" }),
            new DemoField("Capital", DemoFieldKind.Money, "Montant en euros"),
            new DemoField("Date constitution", DemoFieldKind.Date),
            new DemoField("Date immat. RCS", DemoFieldKind.Date),
            new DemoField("Activité principale", DemoFieldKind.Text, "Code NAF + libellé"),
            new DemoField("Adresse du siège", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Édition K-bis", "Voir dirigeants", "Voir actes déposés", "Radier" });

    public static readonly DemoModuleConfig RcsDirigeant = new(
        Code: "DEMO_RCS_DIRIGEANT",
        Domain: "RCS",
        Title: "Dirigeants",
        Subtitle: "Représentants légaux et organes de direction des sociétés.",
        IconKey: "IconUserGroup",
        Eyebrow: "MANDATAIRES",
        ListColumns: new[]
        {
            new DemoColumn("Nom", "Nom", 200),
            new DemoColumn("Fonction", "Fonction", 200),
            new DemoColumn("Société", "Societe", 220),
            new DemoColumn("Date nomination", "Date", 140),
            new DemoColumn("Durée mandat", "Duree", 130),
        },
        SampleRows: new[]
        {
            Row("info", "En fonction", ("Nom", "M. Lefebvre Jean"), ("Fonction", "Gérant"),
                ("Societe", "Lefebvre SARL"), ("Date", "12/06/2018"), ("Duree", "Illimitée")),
            Row("info", "En fonction", ("Nom", "Mme Distrimat Claire"), ("Fonction", "Présidente"),
                ("Societe", "Distrimat SAS"), ("Date", "04/09/2015"), ("Duree", "6 ans")),
            Row("warning", "Renouvellement à valider",
                ("Nom", "M. Bélier Jacques"), ("Fonction", "PDG"),
                ("Societe", "Bélier SA"), ("Date", "22/11/2018"), ("Duree", "6 ans")),
        },
        SaisieFields: new[]
        {
            new DemoField("Civilité", DemoFieldKind.Combo, Options: new[] { "M.", "Mme", "PM" }),
            new DemoField("Nom / Dénomination", DemoFieldKind.Text),
            new DemoField("Prénom", DemoFieldKind.Text),
            new DemoField("Fonction", DemoFieldKind.Combo, Options:
                new[] { "Gérant", "Président", "PDG", "Directeur Général", "Administrateur", "Commissaire aux comptes" }),
            new DemoField("Société", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL", "Distrimat SAS", "Bélier SA" }),
            new DemoField("Date de nomination", DemoFieldKind.Date),
            new DemoField("Durée du mandat", DemoFieldKind.Text, "Ex : 6 ans, illimité"),
        },
        ActionsBarre: new[] { "Imprimer extrait", "Mettre fin au mandat", "Modifier" });

    public static readonly DemoModuleConfig RcsActe = new(
        Code: "DEMO_RCS_ACTE",
        Domain: "RCS",
        Title: "Actes déposés",
        Subtitle: "Statuts, PV, modifications, comptes annuels, etc.",
        IconKey: "IconClipboard",
        Eyebrow: "DÉPÔTS",
        ListColumns: new[]
        {
            new DemoColumn("Date dépôt", "Date", 120),
            new DemoColumn("Société", "Societe", 200),
            new DemoColumn("Type d'acte", "Type", 220),
            new DemoColumn("Déposant", "Deposant", 200),
            new DemoColumn("Nb pages", "Pages", 90),
            new DemoColumn("Visa", "Visa", 110),
        },
        SampleRows: new[]
        {
            Row("success", "Visé", ("Date", "12/05/2026"), ("Societe", "Lefebvre SARL"),
                ("Type", "Modification de gérance"), ("Deposant", "Me Dupont"),
                ("Pages", "8"), ("Visa", "OK")),
            Row("warning", "En instance", ("Date", "13/05/2026"), ("Societe", "Distrimat SAS"),
                ("Type", "Comptes annuels 2025"), ("Deposant", "Cabinet Expert+"),
                ("Pages", "42"), ("Visa", "À viser")),
            Row("info", "Reçu", ("Date", "14/05/2026"), ("Societe", "Bélier SA"),
                ("Type", "Transfert de siège"), ("Deposant", "Me Martin"),
                ("Pages", "12"), ("Visa", "—")),
        },
        SaisieFields: new[]
        {
            new DemoField("Société concernée", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL", "Distrimat SAS", "Bélier SA" }),
            new DemoField("Type d'acte", DemoFieldKind.Combo, Options:
                new[] { "Statuts constitutifs", "Modification de statuts", "Comptes annuels",
                        "Modification de gérance", "Transfert de siège", "Augmentation de capital", "Dissolution" }),
            new DemoField("Date de l'acte", DemoFieldKind.Date),
            new DemoField("Date dépôt au greffe", DemoFieldKind.Date),
            new DemoField("Déposant", DemoFieldKind.Combo, Options:
                new[] { "Me Dupont — DUPONT_AVO", "Cabinet Expert+", "M. Lefebvre Jean (gérant)" }),
            new DemoField("Nombre de pages", DemoFieldKind.Number),
            new DemoField("Référence facture", DemoFieldKind.Text, "auto"),
            new DemoField("Observations", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Scanner / Importer PDF", "Apposer visa", "Émettre facture", "Publier BODACC" });

    public static readonly DemoModuleConfig RcsKbis = new(
        Code: "DEMO_RCS_KBIS",
        Domain: "RCS",
        Title: "Éditions K-bis",
        Subtitle: "Émission et reprographie d'extraits Kbis.",
        IconKey: "IconDocument",
        Eyebrow: "EXTRAITS",
        ListColumns: new[]
        {
            new DemoColumn("N° édition", "Num", 130),
            new DemoColumn("Société", "Societe", 240),
            new DemoColumn("SIREN", "Siren", 130),
            new DemoColumn("Date", "Date", 120),
            new DemoColumn("Mode", "Mode", 130),
            new DemoColumn("Demandeur", "Demandeur", 200),
        },
        SampleRows: new[]
        {
            Row("success", "Émis", ("Num", "KB/2026/04321"), ("Societe", "Lefebvre SARL"),
                ("Siren", "451 234 567"), ("Date", "13/05/2026"), ("Mode", "Papier"),
                ("Demandeur", "M. Lefebvre Jean")),
            Row("success", "Émis", ("Num", "KB/2026/04322"), ("Societe", "Distrimat SAS"),
                ("Siren", "789 654 321"), ("Date", "13/05/2026"), ("Mode", "Électronique"),
                ("Demandeur", "Cabinet Expert+")),
            Row("info", "En attente paiement", ("Num", "KB/2026/04323"), ("Societe", "Bélier SA"),
                ("Siren", "812 345 678"), ("Date", "14/05/2026"), ("Mode", "Papier"),
                ("Demandeur", "Me Martin")),
        },
        SaisieFields: new[]
        {
            new DemoField("Société", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL — 451234567", "Distrimat SAS — 789654321", "Bélier SA — 812345678" }),
            new DemoField("Mode d'édition", DemoFieldKind.Combo, Options:
                new[] { "Papier", "Électronique (XML signé)", "PDF tamponné" }),
            new DemoField("Nombre d'exemplaires", DemoFieldKind.Number),
            new DemoField("Demandeur", DemoFieldKind.Text, "Nom du demandeur"),
            new DemoField("Mode de paiement", DemoFieldKind.Combo, Options:
                new[] { "CB", "Chèque", "Virement", "Sur compte client" }),
        },
        ActionsBarre: new[] { "Émettre extrait", "Régénérer (duplicata)", "Annuler" });

    // ============================================================
    // INSCRIPTIONS / SÛRETÉS
    // ============================================================

    public static readonly DemoModuleConfig InsPrivilege = new(
        Code: "DEMO_INS_PRIVILEGE",
        Domain: "INSCRIPTIONS",
        Title: "Privilèges",
        Subtitle: "Inscriptions de privilèges, créances inscrites au registre.",
        IconKey: "IconLock",
        Eyebrow: "SÛRETÉS",
        ListColumns: new[]
        {
            new DemoColumn("N° inscription", "Num", 140),
            new DemoColumn("Bénéficiaire", "Beneficiaire", 200),
            new DemoColumn("Débiteur", "Debiteur", 200),
            new DemoColumn("Montant", "Montant", 130),
            new DemoColumn("Date insc.", "Date", 120),
            new DemoColumn("Date renouvell.", "Renouvell", 130),
        },
        SampleRows: new[]
        {
            Row("info", "En cours", ("Num", "PR/2024/00128"), ("Beneficiaire", "Banque Populaire"),
                ("Debiteur", "SARL Cazaux"), ("Montant", "45 000 €"),
                ("Date", "12/03/2024"), ("Renouvell", "12/03/2034")),
            Row("warning", "À renouveler", ("Num", "PR/2014/00043"), ("Beneficiaire", "Caisse d'Épargne"),
                ("Debiteur", "Société Bélier"), ("Montant", "120 000 €"),
                ("Date", "10/06/2014"), ("Renouvell", "10/06/2024")),
            Row("success", "Mainlevée donnée", ("Num", "PR/2020/00012"), ("Beneficiaire", "URSSAF"),
                ("Debiteur", "SARL Tonelli"), ("Montant", "8 500 €"),
                ("Date", "05/02/2020"), ("Renouvell", "—")),
        },
        SaisieFields: new[]
        {
            new DemoField("Type de privilège", DemoFieldKind.Combo, Options:
                new[] { "Vendeur de fonds de commerce", "Trésor public", "Sécurité sociale", "Nantissement de fonds" }),
            new DemoField("Bénéficiaire (créancier)", DemoFieldKind.Text),
            new DemoField("Débiteur (société)", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL", "Distrimat SAS", "Bélier SA" }),
            new DemoField("Montant principal", DemoFieldKind.Money),
            new DemoField("Date d'inscription", DemoFieldKind.Date),
            new DemoField("Durée (années)", DemoFieldKind.Number, HelperText: "Renouvellement décennal par défaut."),
            new DemoField("Acte fondateur", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Renouveler", "Donner mainlevée", "Imprimer état" });

    public static readonly DemoModuleConfig InsNantissement = new(
        Code: "DEMO_INS_NANTISSEMENT",
        Domain: "INSCRIPTIONS",
        Title: "Nantissements",
        Subtitle: "Nantissements de fonds de commerce, parts sociales, matériel.",
        IconKey: "IconLock",
        Eyebrow: "GAGES",
        ListColumns: new[]
        {
            new DemoColumn("N° insc.", "Num", 140),
            new DemoColumn("Objet", "Objet", 220),
            new DemoColumn("Créancier", "Creancier", 200),
            new DemoColumn("Débiteur", "Debiteur", 200),
            new DemoColumn("Montant", "Montant", 130),
            new DemoColumn("Échéance", "Echeance", 120),
        },
        SampleRows: new[]
        {
            Row("info", null, ("Num", "NT/2025/00041"), ("Objet", "Fonds de commerce"),
                ("Creancier", "BNP Paribas"), ("Debiteur", "Lefebvre SARL"),
                ("Montant", "85 000 €"), ("Echeance", "12/01/2030")),
            Row("info", null, ("Num", "NT/2024/00112"), ("Objet", "Parts sociales (40%)"),
                ("Creancier", "Caisse d'Épargne"), ("Debiteur", "Distrimat SAS"),
                ("Montant", "200 000 €"), ("Echeance", "—")),
        },
        SaisieFields: new[]
        {
            new DemoField("Type de nantissement", DemoFieldKind.Combo, Options:
                new[] { "Fonds de commerce", "Parts sociales", "Outillage et matériel", "Stocks" }),
            new DemoField("Créancier", DemoFieldKind.Text),
            new DemoField("Débiteur (société)", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL", "Distrimat SAS", "Bélier SA" }),
            new DemoField("Montant garanti", DemoFieldKind.Money),
            new DemoField("Date acte", DemoFieldKind.Date),
            new DemoField("Échéance", DemoFieldKind.Date),
            new DemoField("Description du bien nanti", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Renouveler", "Radier", "Délivrer état des inscriptions" });

    // ============================================================
    // COMPTABILITÉ / CLIENTS
    // ============================================================

    public static readonly DemoModuleConfig ComptaClient = new(
        Code: "DEMO_COMPTA_CLIENT",
        Domain: "COMPTABILITÉ",
        Title: "Comptes clients",
        Subtitle: "Comptes ouverts auprès du greffe pour facturation centralisée.",
        IconKey: "IconUserGroup",
        Eyebrow: "CLIENTS",
        ListColumns: new[]
        {
            new DemoColumn("N° compte", "Num", 100),
            new DemoColumn("Raison sociale", "Nom", 240),
            new DemoColumn("Type", "Type", 150),
            new DemoColumn("Solde", "Solde", 130),
            new DemoColumn("Encours", "Encours", 130),
            new DemoColumn("Échéance", "Echeance", 120),
        },
        SampleRows: new[]
        {
            Row("success", "À jour", ("Num", "C/001234"), ("Nom", "Cabinet Dupont & Associés"),
                ("Type", "Avocat"), ("Solde", "1 240,00 €"), ("Encours", "—"), ("Echeance", "—")),
            Row("warning", "Échéance proche", ("Num", "C/001456"), ("Nom", "Expert+ Comptables"),
                ("Type", "Expert comptable"), ("Solde", "0,00 €"), ("Encours", "320,00 €"),
                ("Echeance", "31/05/2026")),
            Row("error", "Impayé", ("Num", "C/001892"), ("Nom", "Étude Vialle Huissier"),
                ("Type", "Huissier"), ("Solde", "-180,00 €"), ("Encours", "180,00 €"),
                ("Echeance", "10/04/2026")),
        },
        SaisieFields: new[]
        {
            new DemoField("N° compte", DemoFieldKind.Text, "auto"),
            new DemoField("Raison sociale", DemoFieldKind.Text),
            new DemoField("SIREN", DemoFieldKind.Text),
            new DemoField("Type de client", DemoFieldKind.Combo, Options:
                new[] { "Avocat", "Expert comptable", "Huissier", "Notaire", "Société", "Particulier" }),
            new DemoField("Mode de règlement", DemoFieldKind.Combo, Options:
                new[] { "Prélèvement", "Virement", "Chèque", "CB" }),
            new DemoField("Adresse de facturation", DemoFieldKind.Memo, ColumnSpan: 2),
            new DemoField("Plafond d'encours", DemoFieldKind.Money),
        },
        ActionsBarre: new[] { "Relancer", "Suspendre", "Émettre relevé" });

    public static readonly DemoModuleConfig ComptaFacture = new(
        Code: "DEMO_COMPTA_FACTURE",
        Domain: "COMPTABILITÉ",
        Title: "Factures",
        Subtitle: "Factures émises pour dépôts d'actes, K-bis, formalités diverses.",
        IconKey: "IconDocument",
        Eyebrow: "FACTURATION",
        ListColumns: new[]
        {
            new DemoColumn("N° facture", "Num", 140),
            new DemoColumn("Client", "Client", 240),
            new DemoColumn("Date", "Date", 110),
            new DemoColumn("HT", "HT", 110),
            new DemoColumn("TTC", "TTC", 110),
            new DemoColumn("Statut", "Statut", 130),
        },
        SampleRows: new[]
        {
            Row("success", "Payée", ("Num", "F/2026/04321"), ("Client", "Cabinet Dupont"),
                ("Date", "10/05/2026"), ("HT", "180,00 €"), ("TTC", "216,00 €"), ("Statut", "Réglée")),
            Row("warning", "À encaisser", ("Num", "F/2026/04322"), ("Client", "Expert+ Comptables"),
                ("Date", "12/05/2026"), ("HT", "240,00 €"), ("TTC", "288,00 €"), ("Statut", "Envoyée")),
            Row("error", "Impayée", ("Num", "F/2026/04220"), ("Client", "Étude Vialle"),
                ("Date", "12/04/2026"), ("HT", "150,00 €"), ("TTC", "180,00 €"), ("Statut", "En relance")),
        },
        SaisieFields: new[]
        {
            new DemoField("Client", DemoFieldKind.Combo, Options:
                new[] { "Cabinet Dupont", "Expert+ Comptables", "Étude Vialle", "M. Lefebvre Jean" }),
            new DemoField("Date facture", DemoFieldKind.Date),
            new DemoField("Date d'échéance", DemoFieldKind.Date),
            new DemoField("Référence dossier / acte", DemoFieldKind.Text),
            new DemoField("Désignation", DemoFieldKind.Memo, ColumnSpan: 2),
            new DemoField("Montant HT", DemoFieldKind.Money),
            new DemoField("TVA (%)", DemoFieldKind.Combo, Options: new[] { "20,0", "10,0", "5,5", "0,0" }),
        },
        ActionsBarre: new[] { "Imprimer / PDF", "Envoyer par mail", "Encaisser", "Annuler" });

    public static readonly DemoModuleConfig ComptaEncaissement = new(
        Code: "DEMO_COMPTA_ENCAISSEMENT",
        Domain: "COMPTABILITÉ",
        Title: "Encaissements",
        Subtitle: "Règlements reçus, lettrage automatique avec factures.",
        IconKey: "IconCheck",
        Eyebrow: "RÈGLEMENTS",
        ListColumns: new[]
        {
            new DemoColumn("Date", "Date", 120),
            new DemoColumn("Mode", "Mode", 110),
            new DemoColumn("Référence", "Ref", 160),
            new DemoColumn("Montant", "Montant", 130),
            new DemoColumn("Client", "Client", 220),
            new DemoColumn("Lettrage", "Lettrage", 130),
        },
        SampleRows: new[]
        {
            Row("success", null, ("Date", "13/05/2026"), ("Mode", "CB"), ("Ref", "TXN/77821"),
                ("Montant", "216,00 €"), ("Client", "Cabinet Dupont"), ("Lettrage", "F/2026/04321")),
            Row("success", null, ("Date", "13/05/2026"), ("Mode", "Virement"), ("Ref", "VIR/24-1843"),
                ("Montant", "1 240,00 €"), ("Client", "Distrimat SAS"), ("Lettrage", "F/2026/04250")),
            Row("warning", "Non lettré", ("Date", "14/05/2026"), ("Mode", "Chèque"), ("Ref", "CHQ/008412"),
                ("Montant", "240,00 €"), ("Client", "—"), ("Lettrage", "—")),
        },
        SaisieFields: new[]
        {
            new DemoField("Date", DemoFieldKind.Date),
            new DemoField("Mode de règlement", DemoFieldKind.Combo, Options:
                new[] { "CB", "Virement", "Chèque", "Espèces" }),
            new DemoField("Référence (Txn / N° chèque)", DemoFieldKind.Text),
            new DemoField("Montant", DemoFieldKind.Money),
            new DemoField("Client", DemoFieldKind.Combo, Options:
                new[] { "Cabinet Dupont", "Distrimat SAS", "Expert+ Comptables" }),
            new DemoField("Facture à lettrer", DemoFieldKind.Combo, Options:
                new[] { "F/2026/04322 (288 €)", "F/2026/04220 (180 €)", "Acompte (non lettré)" }),
        },
        ActionsBarre: new[] { "Lettrer", "Relancer non lettré", "Export comptable" });

    // ============================================================
    // EDI / FLUX
    // ============================================================

    public static readonly DemoModuleConfig EdiEntrant = new(
        Code: "DEMO_EDI_ENTRANT",
        Domain: "EDI / FLUX",
        Title: "Flux entrants",
        Subtitle: "Réception des messages EDI : Infogreffe, RPVA, BODACC, partenaires.",
        IconKey: "IconArrowDown",
        Eyebrow: "INBOUND",
        ListColumns: new[]
        {
            new DemoColumn("Reçu le", "Date", 140),
            new DemoColumn("Source", "Source", 160),
            new DemoColumn("Type message", "Type", 220),
            new DemoColumn("Référence", "Ref", 180),
            new DemoColumn("Taille", "Taille", 100),
            new DemoColumn("État", "Etat", 120),
        },
        SampleRows: new[]
        {
            Row("success", "Traité", ("Date", "14/05/2026 09:12"), ("Source", "Infogreffe"),
                ("Type", "Dépôt acte numérique"), ("Ref", "INF/2026/884412"),
                ("Taille", "1,2 Mo"), ("Etat", "Importé")),
            Row("warning", "À valider", ("Date", "14/05/2026 09:25"), ("Source", "RPVA"),
                ("Type", "Notification de conclusions"), ("Ref", "RPVA/77123"),
                ("Taille", "412 Ko"), ("Etat", "En attente")),
            Row("error", "En échec", ("Date", "14/05/2026 08:51"), ("Source", "Tribunal Digital"),
                ("Type", "Production de créance"), ("Ref", "TD/45128"),
                ("Taille", "812 Ko"), ("Etat", "Erreur XML")),
        },
        SaisieFields: new[]
        {
            new DemoField("Source", DemoFieldKind.Combo, Options:
                new[] { "Infogreffe", "RPVA", "Tribunal Digital", "BODACC", "Partenaire bancaire" }),
            new DemoField("Type de message", DemoFieldKind.Combo, Options:
                new[] { "Dépôt acte", "Notification", "Production créance", "Demande K-bis" }),
            new DemoField("Référence externe", DemoFieldKind.Text),
            new DemoField("Date / heure réception", DemoFieldKind.Date),
            new DemoField("Charge utile (XML)", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Importer", "Rejeter", "Relancer parsing" });

    public static readonly DemoModuleConfig EdiSortant = new(
        Code: "DEMO_EDI_SORTANT",
        Domain: "EDI / FLUX",
        Title: "Flux sortants",
        Subtitle: "Émission de notifications vers les partenaires.",
        IconKey: "IconArrowUp",
        Eyebrow: "OUTBOUND",
        ListColumns: new[]
        {
            new DemoColumn("Émis le", "Date", 140),
            new DemoColumn("Destinataire", "Dest", 200),
            new DemoColumn("Type message", "Type", 220),
            new DemoColumn("Référence", "Ref", 160),
            new DemoColumn("Statut envoi", "Statut", 140),
        },
        SampleRows: new[]
        {
            Row("success", "Acquitté", ("Date", "13/05/2026 17:42"), ("Dest", "Infogreffe national"),
                ("Type", "Mise à jour société"), ("Ref", "OUT/2026/021541"), ("Statut", "ACK reçu")),
            Row("warning", "En cours", ("Date", "14/05/2026 09:00"), ("Dest", "BODACC"),
                ("Type", "Annonce dissolution"), ("Ref", "OUT/2026/021542"), ("Statut", "En file")),
        },
        SaisieFields: new[]
        {
            new DemoField("Destinataire", DemoFieldKind.Combo, Options:
                new[] { "Infogreffe national", "BODACC", "Tribunal Digital", "Partenaire bancaire" }),
            new DemoField("Type de message", DemoFieldKind.Combo, Options:
                new[] { "Mise à jour société", "Annonce BODACC", "Notification de décision", "Confirmation paiement" }),
            new DemoField("Référence interne", DemoFieldKind.Text),
            new DemoField("Date émission", DemoFieldKind.Date),
            new DemoField("Charge utile", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Renvoyer", "Annuler", "Voir accusé" });

    public static readonly DemoModuleConfig EdiBodacc = new(
        Code: "DEMO_EDI_BODACC",
        Domain: "EDI / FLUX",
        Title: "BODACC",
        Subtitle: "Publications au Bulletin Officiel des Annonces Civiles et Commerciales.",
        IconKey: "IconDocument",
        Eyebrow: "PUBLICATIONS",
        ListColumns: new[]
        {
            new DemoColumn("Date pub.", "Date", 120),
            new DemoColumn("Type annonce", "Type", 200),
            new DemoColumn("Société", "Societe", 240),
            new DemoColumn("Texte", "Texte", 280),
            new DemoColumn("État", "Etat", 110),
        },
        SampleRows: new[]
        {
            Row("success", "Publiée", ("Date", "10/05/2026"), ("Type", "Immatriculation"),
                ("Societe", "Lefebvre SARL"), ("Texte", "Constitution SARL au capital de 10 000 €..."),
                ("Etat", "OK")),
            Row("info", "À publier", ("Date", "18/05/2026"), ("Type", "Procédure collective"),
                ("Societe", "Tonelli SARL"), ("Texte", "Jugement d'ouverture de liquidation..."),
                ("Etat", "Programmée")),
        },
        SaisieFields: new[]
        {
            new DemoField("Type d'annonce", DemoFieldKind.Combo, Options:
                new[] { "Immatriculation", "Modification", "Dissolution", "Procédure collective", "Vente fonds de commerce" }),
            new DemoField("Société concernée", DemoFieldKind.Combo, Options:
                new[] { "Lefebvre SARL", "Distrimat SAS", "Bélier SA", "Tonelli SARL" }),
            new DemoField("Date de publication", DemoFieldKind.Date),
            new DemoField("Texte de l'annonce", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Soumettre au BODACC", "Vérifier publication", "Annuler" });

    // ============================================================
    // PROCÉDURES COLLECTIVES
    // ============================================================

    public static readonly DemoModuleConfig PcDossier = new(
        Code: "DEMO_PC_DOSSIER",
        Domain: "PROCÉDURES COLLECTIVES",
        Title: "Dossiers PC",
        Subtitle: "Sauvegarde, redressement, liquidation judiciaire.",
        IconKey: "IconBriefcase",
        Eyebrow: "DOSSIERS PC",
        ListColumns: new[]
        {
            new DemoColumn("N° PC", "Num", 130),
            new DemoColumn("Procédure", "Type", 160),
            new DemoColumn("Débiteur", "Debiteur", 220),
            new DemoColumn("Ouverture", "Ouverture", 130),
            new DemoColumn("Mandataire", "Mand", 180),
            new DemoColumn("Phase", "Phase", 150),
        },
        SampleRows: new[]
        {
            Row("info", null, ("Num", "PC/2025/00128"), ("Type", "Redressement"),
                ("Debiteur", "Société Bélier"), ("Ouverture", "20/03/2025"),
                ("Mand", "SCP Martin Mandataires"), ("Phase", "Observation")),
            Row("warning", null, ("Num", "PC/2024/00077"), ("Type", "Liquidation"),
                ("Debiteur", "Tonelli SARL"), ("Ouverture", "15/11/2024"),
                ("Mand", "Maître Pernet"), ("Phase", "Vérification créances")),
            Row("success", null, ("Num", "PC/2023/00043"), ("Type", "Sauvegarde"),
                ("Debiteur", "Atelier Charcot"), ("Ouverture", "02/06/2023"),
                ("Mand", "SCP Martin Mandataires"), ("Phase", "Plan adopté")),
        },
        SaisieFields: new[]
        {
            new DemoField("Type de procédure", DemoFieldKind.Combo, Options:
                new[] { "Sauvegarde", "Redressement judiciaire", "Liquidation judiciaire", "Conciliation" }),
            new DemoField("Débiteur", DemoFieldKind.Combo, Options:
                new[] { "Société Bélier", "Tonelli SARL", "Atelier Charcot" }),
            new DemoField("Date d'ouverture", DemoFieldKind.Date),
            new DemoField("Mandataire judiciaire", DemoFieldKind.Combo, Options:
                new[] { "SCP Martin Mandataires", "Maître Pernet", "AJ Associés" }),
            new DemoField("Administrateur judiciaire", DemoFieldKind.Combo, Options:
                new[] { "Cabinet Roland", "Maître Vialle", "(non désigné)" }),
            new DemoField("Phase actuelle", DemoFieldKind.Combo, Options:
                new[] { "Observation", "Vérification créances", "Plan", "Cession", "Clôture" }),
        },
        ActionsBarre: new[] { "Convoquer débiteur", "Imprimer ordonnance", "Publier BODACC" });

    public static readonly DemoModuleConfig PcCreancier = new(
        Code: "DEMO_PC_CREANCIER",
        Domain: "PROCÉDURES COLLECTIVES",
        Title: "Créanciers",
        Subtitle: "Créanciers déclarants et productions de créance.",
        IconKey: "IconUserGroup",
        Eyebrow: "DÉCLARANTS",
        ListColumns: new[]
        {
            new DemoColumn("Créancier", "Nom", 240),
            new DemoColumn("Mandataire représentant", "Mand", 220),
            new DemoColumn("Dossier PC", "PC", 130),
            new DemoColumn("Montant déclaré", "Montant", 140),
            new DemoColumn("Nature", "Nature", 150),
            new DemoColumn("État", "Etat", 130),
        },
        SampleRows: new[]
        {
            Row("info", null, ("Nom", "Banque Populaire"), ("Mand", "Me Dupont"),
                ("PC", "PC/2025/00128"), ("Montant", "240 000 €"), ("Nature", "Chirographaire"), ("Etat", "Admise")),
            Row("warning", "À vérifier", ("Nom", "URSSAF"), ("Mand", "(en direct)"),
                ("PC", "PC/2025/00128"), ("Montant", "18 400 €"), ("Nature", "Privilégiée"), ("Etat", "À vérifier")),
            Row("error", "Contestée", ("Nom", "Fournisseur Charcot"), ("Mand", "Me Vialle"),
                ("PC", "PC/2024/00077"), ("Montant", "32 100 €"), ("Nature", "Chirographaire"), ("Etat", "Contestée")),
        },
        SaisieFields: new[]
        {
            new DemoField("Dossier PC", DemoFieldKind.Combo, Options:
                new[] { "PC/2025/00128 — Bélier", "PC/2024/00077 — Tonelli" }),
            new DemoField("Créancier (raison sociale)", DemoFieldKind.Text),
            new DemoField("Représenté par (mandataire)", DemoFieldKind.Combo, Options:
                new[] { "Me Dupont", "Me Martin", "Me Vialle", "(en direct)" }),
            new DemoField("Montant déclaré", DemoFieldKind.Money),
            new DemoField("Nature de la créance", DemoFieldKind.Combo, Options:
                new[] { "Chirographaire", "Privilégiée générale", "Privilégiée spéciale", "Super-privilégiée (salaires)" }),
            new DemoField("Date de la déclaration", DemoFieldKind.Date),
        },
        ActionsBarre: new[] { "Admettre", "Contester", "Demander pièces" });

    // ============================================================
    // ADMINISTRATION
    // ============================================================

    public static readonly DemoModuleConfig AdminUtilisateur = new(
        Code: "DEMO_ADMIN_USER",
        Domain: "ADMINISTRATION",
        Title: "Utilisateurs",
        Subtitle: "Comptes des agents du greffe, rôles et droits.",
        IconKey: "IconUserGroup",
        Eyebrow: "COMPTES",
        ListColumns: new[]
        {
            new DemoColumn("Login", "Login", 150),
            new DemoColumn("Nom complet", "Nom", 220),
            new DemoColumn("Rôle", "Role", 200),
            new DemoColumn("Greffe", "Greffe", 120),
            new DemoColumn("Dernière connex.", "Last", 160),
            new DemoColumn("État", "Etat", 110),
        },
        SampleRows: new[]
        {
            Row("success", "Actif", ("Login", "amanet"), ("Nom", "Auguste Manet"),
                ("Role", "Greffier en chef"), ("Greffe", "7401"), ("Last", "14/05/2026 08:15"), ("Etat", "OK")),
            Row("success", "Actif", ("Login", "rcombe"), ("Nom", "Renée Combe"),
                ("Role", "Greffier"), ("Greffe", "7401"), ("Last", "13/05/2026 17:50"), ("Etat", "OK")),
            Row("warning", "À renouveler", ("Login", "ploiselay"), ("Nom", "Pierre Loiselay"),
                ("Role", "Greffier"), ("Greffe", "7401"), ("Last", "02/04/2026"), ("Etat", "MDP expiré")),
            Row("error", "Suspendu", ("Login", "jmarat"), ("Nom", "Jean Marat"),
                ("Role", "Stagiaire"), ("Greffe", "7401"), ("Last", "—"), ("Etat", "Désactivé")),
        },
        SaisieFields: new[]
        {
            new DemoField("Login", DemoFieldKind.Text, "Format : initiale + nom"),
            new DemoField("Nom complet", DemoFieldKind.Text),
            new DemoField("Email professionnel", DemoFieldKind.Text),
            new DemoField("Rôle", DemoFieldKind.Combo, Options:
                new[] { "Greffier en chef", "Greffier", "Stagiaire", "Administrateur technique", "Consultation seule" }),
            new DemoField("Greffe de rattachement", DemoFieldKind.Combo, Options:
                new[] { "7401 — Annecy", "7402 — Bonneville", "7403 — Thonon" }),
            new DemoField("Actif", DemoFieldKind.Check),
        },
        ActionsBarre: new[] { "Réinitialiser MDP", "Suspendre", "Voir audit" });

    public static readonly DemoModuleConfig AdminGreffe = new(
        Code: "DEMO_ADMIN_GREFFE",
        Domain: "ADMINISTRATION",
        Title: "Greffes",
        Subtitle: "Configuration des greffes hébergés et de leurs paramètres.",
        IconKey: "IconBriefcase",
        Eyebrow: "INSTALLATIONS",
        ListColumns: new[]
        {
            new DemoColumn("Code", "Code", 100),
            new DemoColumn("Tribunal", "Trib", 240),
            new DemoColumn("Ville", "Ville", 200),
            new DemoColumn("Base SQL", "Db", 200),
            new DemoColumn("Version", "Version", 130),
        },
        SampleRows: new[]
        {
            Row("success", "Opérationnel", ("Code", "7401"), ("Trib", "Tribunal de commerce d'Annecy"),
                ("Ville", "Annecy"), ("Db", "RIG_7401_PROD"), ("Version", "v4.32.18")),
            Row("success", "Opérationnel", ("Code", "7402"), ("Trib", "Tribunal de commerce de Bonneville"),
                ("Ville", "Bonneville"), ("Db", "RIG_7402_PROD"), ("Version", "v4.32.18")),
            Row("warning", "Migration en cours", ("Code", "7403"), ("Trib", "Tribunal de commerce de Thonon"),
                ("Ville", "Thonon-les-Bains"), ("Db", "RIG_7403_PROD"), ("Version", "v4.32.16")),
        },
        SaisieFields: new[]
        {
            new DemoField("Code greffe", DemoFieldKind.Text, "4 chiffres"),
            new DemoField("Nom du tribunal", DemoFieldKind.Text),
            new DemoField("Ville", DemoFieldKind.Text),
            new DemoField("Base SQL associée", DemoFieldKind.Text, "Server / Database"),
            new DemoField("Version logicielle", DemoFieldKind.Text),
            new DemoField("Adresse postale", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Tester connexion", "Mettre à jour", "Désactiver" });

    public static readonly DemoModuleConfig AdminParametres = new(
        Code: "DEMO_ADMIN_PARAM",
        Domain: "ADMINISTRATION",
        Title: "Paramètres",
        Subtitle: "Paramètres techniques globaux, limites, intégrations.",
        IconKey: "IconSettings",
        Eyebrow: "RÉGLAGES",
        ListColumns: new[]
        {
            new DemoColumn("Clé", "Cle", 280),
            new DemoColumn("Valeur", "Valeur", 260),
            new DemoColumn("Portée", "Portee", 130),
            new DemoColumn("Modifié le", "Date", 140),
        },
        SampleRows: new[]
        {
            Row("info", null, ("Cle", "Greffe.NomComplet"), ("Valeur", "TC d'Annecy"),
                ("Portee", "Greffe 7401"), ("Date", "10/04/2025")),
            Row("info", null, ("Cle", "Edi.Infogreffe.Endpoint"), ("Valeur", "https://api.infogreffe.fr/v3"),
                ("Portee", "Global"), ("Date", "22/03/2025")),
            Row("info", null, ("Cle", "Facturation.PlafondEncours"), ("Valeur", "5 000 €"),
                ("Portee", "Global"), ("Date", "01/01/2026")),
        },
        SaisieFields: new[]
        {
            new DemoField("Clé", DemoFieldKind.Text, HelperText: "Notation point. Ex : Edi.Bodacc.Endpoint"),
            new DemoField("Valeur", DemoFieldKind.Text),
            new DemoField("Portée", DemoFieldKind.Combo, Options:
                new[] { "Global", "Greffe 7401", "Greffe 7402", "Greffe 7403", "Utilisateur courant" }),
            new DemoField("Description", DemoFieldKind.Memo, ColumnSpan: 2),
        },
        ActionsBarre: new[] { "Sauvegarder", "Restaurer défaut", "Voir historique" });
}
