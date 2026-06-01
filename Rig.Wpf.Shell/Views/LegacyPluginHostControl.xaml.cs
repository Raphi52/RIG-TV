using System;
using System.Windows;
using System.Windows.Controls;
using Rig.Wpf.Shell.ViewModels;
using WinForms = System.Windows.Forms;

namespace Rig.Wpf.Shell.Views;

/// <summary>
/// Héberge un plugin legacy WinForms (PROC_*.dll → FORM_&lt;CODE&gt;) à l'intérieur
/// d'un onglet WPF via <c>WindowsFormsHost</c>. Le binding de la Form au host
/// se fait au moment du Loaded ; au Unloaded, la Form est disposée pour libérer
/// les handles GDI/Win32.
/// </summary>
public partial class LegacyPluginHostControl : UserControl
{
    private WinForms.Form? _hostedForm;
    private WinForms.UserControl? _hostedUserControl;

    public LegacyPluginHostControl() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LegacyPluginTabViewModel vm) return;
        if (Host.Child is not null) return; // déjà attaché

        Serilog.Log.Information("Chargement plugin legacy {Code} ({Type})",
            vm.CodeProcessus, vm.Descriptor.FormType.FullName);
        try
        {
            // Les FORM_<CODE> legacy attendent typiquement un ctor (string param).
            // On essaie d'abord ce ctor avec une chaîne vide, puis le ctor sans paramètre.
            var instance = CreateLegacyInstance(vm.Descriptor.FormType);
            switch (instance)
            {
                case WinForms.Form form:
                    form.TopLevel = false;
                    form.FormBorderStyle = WinForms.FormBorderStyle.None;
                    form.Dock = WinForms.DockStyle.Fill;
                    _hostedForm = form;
                    Host.Child = form;
                    form.Show();
                    break;

                case WinForms.UserControl uc:
                    uc.Dock = WinForms.DockStyle.Fill;
                    _hostedUserControl = uc;
                    Host.Child = uc;
                    break;

                default:
                    Host.Child = new WinForms.Label
                    {
                        Text =
                            $"Plugin '{vm.CodeProcessus}' : type non supporté ({vm.Descriptor.FormType.FullName}).",
                        Dock = WinForms.DockStyle.Fill
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            // Un plugin legacy défaillant ne doit pas tuer le shell.
            // On affiche un message d'erreur dans le tab et on log.
            Serilog.Log.Error(ex, "Échec instanciation du plugin legacy {Code}", vm.CodeProcessus);
            Host.Child = new WinForms.Label
            {
                Text = $"Plugin '{vm.CodeProcessus}' : échec d'instanciation\r\n\r\n"
                       + ex.GetType().Name + " : " + ex.Message,
                Dock = WinForms.DockStyle.Fill,
                AutoSize = false,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
        }
    }

    private static object? CreateLegacyInstance(System.Type formType)
    {
        var ctorWithString = formType.GetConstructor(new[] { typeof(string) });
        if (ctorWithString is not null)
            return ctorWithString.Invoke(new object?[] { string.Empty });

        var defaultCtor = formType.GetConstructor(System.Type.EmptyTypes);
        if (defaultCtor is not null)
            return defaultCtor.Invoke(null);

        return null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Host.Child = null;
        _hostedForm?.Dispose();
        _hostedForm = null;
        _hostedUserControl?.Dispose();
        _hostedUserControl = null;
    }
}
