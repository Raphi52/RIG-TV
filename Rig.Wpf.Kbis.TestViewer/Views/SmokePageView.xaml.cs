// SPDX-License-Identifier: Proprietary
// SmokePageView — vue PARTAGÉE des pages smoke (2026-05-29 soir).
// Source de vérité UNIQUE du corps d'une page smoke : bandeau header (slot) + corps 3-colonnes
// (MosaicView / FocusView / ScenarioListView) + FooterView (disk usage).
//
// But (demande utilisateur) :
//   1. Nouvelle catégorie = identique aux autres PAR CONSTRUCTION (elle réutilise ce UserControl).
//   2. Éditer le corps ICI = affecte TOUTES les catégories qui consomment SmokePageView.
//
// Le HEADER reste propre à chaque module (slot HeaderContent) : RAPTURE a un bandeau riche
// (Apply/Mode/Parallelism via BatchControlsView), les modules legacy un bandeau simple (titre +
// Run + stress). On ne force donc pas un header unique — on partage le CORPS (qui, lui, est
// identique partout : 3-col + footer, bindé via IScenarioCatalog / IFocusContext).
//
// Usage (MainWindow.xaml) :
//   <views:SmokePageView Catalog="{Binding DcadematCatalog}" FocusContext="{Binding DcadematFocus}">
//       <views:SmokePageView.HeaderContent> ... bandeau du module ... </views:SmokePageView.HeaderContent>
//   </views:SmokePageView>

using System.Windows;
using System.Windows.Controls;

namespace Rig.Wpf.Kbis.TestViewer.Views;

public partial class SmokePageView : UserControl
{
    public SmokePageView()
    {
        InitializeComponent();
    }

    /// <summary>Bandeau header injecté par le module (titre + Run + stress, ou BatchControlsView pour RAPTURE).</summary>
    public static readonly DependencyProperty HeaderContentProperty =
        DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SmokePageView), new PropertyMetadata(null));
    public object HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>Catalogue des scénarios (IScenarioCatalog) — alimente MosaicView + ScenarioListView.</summary>
    public static readonly DependencyProperty CatalogProperty =
        DependencyProperty.Register(nameof(Catalog), typeof(object), typeof(SmokePageView), new PropertyMetadata(null));
    public object Catalog
    {
        get => GetValue(CatalogProperty);
        set => SetValue(CatalogProperty, value);
    }

    /// <summary>Contexte de la vue focus (IFocusContext) — alimente FocusView.</summary>
    public static readonly DependencyProperty FocusContextProperty =
        DependencyProperty.Register(nameof(FocusContext), typeof(object), typeof(SmokePageView), new PropertyMetadata(null));
    public object FocusContext
    {
        get => GetValue(FocusContextProperty);
        set => SetValue(FocusContextProperty, value);
    }

    /// <summary>Contenu du drawer latéral (ex. RunHistoryDrawer pour RAPTURE). Affiché en overlay
    /// à droite du corps. Null = pas de drawer (KBIS / ALERTES / DCADEMAT).</summary>
    public static readonly DependencyProperty DrawerContentProperty =
        DependencyProperty.Register(nameof(DrawerContent), typeof(object), typeof(SmokePageView), new PropertyMetadata(null));
    public object DrawerContent
    {
        get => GetValue(DrawerContentProperty);
        set => SetValue(DrawerContentProperty, value);
    }

    /// <summary>True quand le drawer est ouvert → ScenarioListView se cache (overlap UX).</summary>
    public static readonly DependencyProperty IsDrawerOpenProperty =
        DependencyProperty.Register(nameof(IsDrawerOpen), typeof(bool), typeof(SmokePageView), new PropertyMetadata(false));
    public bool IsDrawerOpen
    {
        get => (bool)GetValue(IsDrawerOpenProperty);
        set => SetValue(IsDrawerOpenProperty, value);
    }
}
