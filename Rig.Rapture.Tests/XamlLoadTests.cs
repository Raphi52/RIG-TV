using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace Rig.Rapture.Tests
{
    /// <summary>
    /// TERRAIN front-end WPF — signal auto-prouvant qui attrape les erreurs XAML RUNTIME que `dotnet build`
    /// ne voit pas. Instancie chaque UserControl de l'assembly TestViewer sur un thread STA (avec les
    /// ResourceDictionaries de l'app mergés) : `InitializeComponent` charge le BAML → un binding cassé
    /// (ex. ElementName + RelativeSource sur le même binding), une StaticResource manquante, etc. jette
    /// une XamlParseException → TEST ROUGE. Aurait attrapé le crash « ça ouvre rien » de cette session.
    /// </summary>
    public class XamlLoadTests
    {
        // Exécute une action sur un thread STA dédié (xUnit tourne en MTA ; WPF exige STA).
        private static Exception RunSta(Action action)
        {
            Exception captured = null;
            var t = new Thread(() => { try { action(); } catch (Exception e) { captured = e; } });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join(TimeSpan.FromSeconds(60));
            return captured;
        }

        // Crée l'Application + merge Palette/Controls (mêmes dicts que App.xaml) une seule fois sur ce STA.
        private static void EnsureApp()
        {
            if (Application.Current == null) new Application();
            var res = Application.Current.Resources;
            // déjà mergé ? (RunSta crée un nouveau thread mais Application.Current est process-wide)
            if (res.MergedDictionaries.Any(d => d.Source != null && d.Source.OriginalString.Contains("Palette"))) return;
            foreach (var rel in new[] { "Resources/Palette.xaml", "Resources/Controls.xaml" })
            {
                res.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/Rig.Wpf.Kbis.TestViewer;component/{rel}", UriKind.Relative)
                });
            }
        }

        /// <summary>Toutes les vues (UserControl) de TestViewer se chargent sans XamlParseException.</summary>
        [Fact]
        public void AllViews_load_without_xaml_error()
        {
            var failures = new List<string>();
            var ex = RunSta(() =>
            {
                EnsureApp();
                var asm = typeof(Rig.Wpf.Kbis.TestViewer.Views.SmokeHeaderView).Assembly;
                var views = asm.GetTypes()
                    .Where(t => typeof(UserControl).IsAssignableFrom(t)
                                && !t.IsAbstract
                                && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.Name);
                foreach (var v in views)
                {
                    try { Activator.CreateInstance(v); }
                    catch (Exception e)
                    {
                        var root = e; while (root.InnerException != null) root = root.InnerException;
                        // On ne retient que les erreurs de CHARGEMENT XAML (parse/binding/resource),
                        // pas un éventuel throw métier d'un ctor (rare sur un UserControl).
                        if (e is XamlParseException || root is XamlParseException
                            || e.ToString().Contains("Xaml") || e.ToString().Contains("RelativeSource")
                            || e.ToString().Contains("ResourceReference"))
                            failures.Add($"{v.Name}: {root.GetType().Name}: {root.Message}");
                    }
                }
            });
            Assert.True(ex == null, $"Le runner STA a jeté : {ex}");
            Assert.True(failures.Count == 0,
                "Vues avec erreur de chargement XAML (build vert ne les voit pas) :\n  - " + string.Join("\n  - ", failures));
        }

        /// <summary>CONTRÔLE NÉGATIF : un binding invalide (ElementName + RelativeSource) DOIT jeter — prouve
        /// que le test ci-dessus attrape réellement la classe de bug du crash de cette session.</summary>
        [Fact]
        public void NegativeControl_invalid_binding_is_caught()
        {
            const string badXaml =
                "<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<TextBlock Margin=\"{Binding Foo, ElementName=root, RelativeSource={RelativeSource Self}}\"/>" +
                "</StackPanel>";
            var ex = RunSta(() => XamlReader.Parse(badXaml));
            Assert.NotNull(ex); // le binding invalide DOIT jeter ; si null, le filet ne capte rien.
        }
    }
}
