using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Rig.Wpf.Kbis.TestViewer.Views
{
    /// <summary>
    /// Panneau responsive : dispose ses enfants CÔTE-À-CÔTE (pondéré, remplit la largeur) quand la
    /// largeur disponible >= <see cref="Breakpoint"/>, ou EMPILÉS verticalement (chacun pleine largeur,
    /// hauteur pondérée) quand c'est plus étroit. Remplace le combo WrapPanel (empile mais ne remplit
    /// pas) / Grid star (remplit mais n'empile jamais). Le poids horizontal ET vertical de chaque enfant
    /// vient de la propriété attachée <see cref="WeightProperty"/> (défaut 1). Les enfants Collapsed sont
    /// ignorés (→ quand la liste se cache pour le drawer, les 2 restants remplissent sans trou).
    /// </summary>
    public class AdaptivePanel : Panel
    {
        public static readonly DependencyProperty BreakpointProperty =
            DependencyProperty.Register(nameof(Breakpoint), typeof(double), typeof(AdaptivePanel),
                new FrameworkPropertyMetadata(1180.0,
                    FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        /// <summary>Largeur dispo en dessous de laquelle on bascule en empilement vertical.</summary>
        public double Breakpoint
        {
            get => (double)GetValue(BreakpointProperty);
            set => SetValue(BreakpointProperty, value);
        }

        public static readonly DependencyProperty WeightProperty =
            DependencyProperty.RegisterAttached("Weight", typeof(double), typeof(AdaptivePanel),
                new FrameworkPropertyMetadata(1.0,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static void SetWeight(UIElement element, double value) => element.SetValue(WeightProperty, value);
        public static double GetWeight(UIElement element) => (double)element.GetValue(WeightProperty);

        private List<UIElement> VisibleChildren()
        {
            var list = new List<UIElement>();
            foreach (UIElement c in InternalChildren)
                if (c != null && c.Visibility != Visibility.Collapsed) list.Add(c);
            return list;
        }

        // Empilé quand la largeur est finie ET < Breakpoint. (Largeur infinie = mesure d'un parent non
        // contraint : on reste en mode côte-à-côte pour ne pas empiler à tort.)
        private bool IsStacked(double availableWidth)
            => !double.IsInfinity(availableWidth) && availableWidth < Breakpoint;

        protected override Size MeasureOverride(Size availableSize)
        {
            var children = VisibleChildren();
            if (children.Count == 0) return new Size(0, 0);

            double totalWeight = 0;
            foreach (var c in children) totalWeight += Math.Max(0.0001, GetWeight(c));

            if (IsStacked(availableSize.Width))
            {
                double w = availableSize.Width;
                bool hInf = double.IsInfinity(availableSize.Height);
                double totalH = 0, maxW = 0;
                foreach (var c in children)
                {
                    double ch = hInf ? double.PositiveInfinity
                                     : availableSize.Height * (GetWeight(c) / totalWeight);
                    c.Measure(new Size(w, ch));
                    totalH += hInf ? c.DesiredSize.Height : ch;
                    maxW = Math.Max(maxW, c.DesiredSize.Width);
                }
                return new Size(double.IsInfinity(w) ? maxW : w, totalH);
            }
            else
            {
                bool wInf = double.IsInfinity(availableSize.Width);
                double availW = wInf ? 0 : availableSize.Width;
                double sumW = 0, maxH = 0;
                foreach (var c in children)
                {
                    double cw = wInf ? double.PositiveInfinity
                                     : availW * (GetWeight(c) / totalWeight);
                    c.Measure(new Size(cw, availableSize.Height));
                    sumW += wInf ? c.DesiredSize.Width : cw;
                    maxH = Math.Max(maxH, c.DesiredSize.Height);
                }
                double h = double.IsInfinity(availableSize.Height) ? maxH : availableSize.Height;
                return new Size(wInf ? sumW : availW, h);
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var children = VisibleChildren();
            if (children.Count == 0) return finalSize;

            double totalWeight = 0;
            foreach (var c in children) totalWeight += Math.Max(0.0001, GetWeight(c));

            if (IsStacked(finalSize.Width))
            {
                double y = 0;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    double ch = finalSize.Height * (GetWeight(c) / totalWeight);
                    if (i == children.Count - 1) ch = Math.Max(0, finalSize.Height - y); // dernier = reste (anti-arrondi)
                    c.Arrange(new Rect(0, y, finalSize.Width, ch));
                    y += ch;
                }
            }
            else
            {
                double x = 0;
                for (int i = 0; i < children.Count; i++)
                {
                    var c = children[i];
                    double cw = finalSize.Width * (GetWeight(c) / totalWeight);
                    if (i == children.Count - 1) cw = Math.Max(0, finalSize.Width - x); // dernier = reste
                    c.Arrange(new Rect(x, 0, cw, finalSize.Height));
                    x += cw;
                }
            }
            return finalSize;
        }
    }
}
