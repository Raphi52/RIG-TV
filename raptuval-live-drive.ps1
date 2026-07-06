# =============================================================================
# OUTIL de test MANUEL/live du cockpit RAPTUVAL — pilotage sur bureau REEL (clics natifs).
# -----------------------------------------------------------------------------
# ⚠ PAS un scenario CI : prend la souris/le clavier (~3 min) -> annoncer a l'utilisateur.
# Raison d'etre : les clics UIA/PostMessage ne ROUTENT PAS sur les boutons du cockpit en
# HDESK headless (mur harnais documente) ; sur le bureau reel avec de VRAIS clics, tout
# route (Ecarter -> DB 1->3, gardes, dialogs). Prouve le 2026-07-06 (session f810a84a).
#
# RECETTE COMPLETE (testee) :
#  1. Lancer RigClientAccueil.exe (cwd C:\rig\exe). Login = 1 clic « Se connecter »
#     (form pre-remplie ; form custom = Panes UIA sans nom -> cliquer par RECT UIA du pane,
#     rangee du bas, ex (854,709 118x37) -> centre (913,727)).
#  2. Ouvrir le proc via la BARRE DE RECHERCHE de l'accueil (taper le code + clic loupe)
#     — plus fiable que le rail de menus.
#  3. Actions = clics par coordonnees ABSOLUES (rect fenetre via UIA root + captures lues).
#  4. Dialogs MODAUX : PrintWindow(MainWindow) ne les capture PAS -> GetForegroundWindow()
#     + PrintWindow dessus ; bouton par defaut = SendKeys ENTER.
#  5. La capture peut etre EN RETARD sur l'etat reel (repaint async) -> TOUJOURS croiser
#     avec la DB (DEMAT_RAPTURE.RAPTU_ETAT) avant de conclure a un echec.
#  6. L'etat PowerShell ne persiste pas entre appels d'outils -> ce script est AUTONOME
#     (Add-Type + clics + capture dans le meme process) ; le relancer par etape.
#
# Usage : -Clicks "x,y[,double]" (coordonnées ABSOLUES écran) -OutPng <path> [-SettleMs n] [-Focus]
# Ex    : .\raptuval-live-drive.ps1 -Focus -Clicks @('190,245','524,137') -OutPng C:\tmp\detail.png
# =============================================================================
param(
    [string[]] $Clicks = @(),
    [string] $OutPng = '',
    [int] $SettleMs = 2500,
    [switch] $Focus
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$sig = @'
using System;using System.Runtime.InteropServices;using System.Drawing;using System.Drawing.Imaging;
public class DriveNative {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
  public static void ClickAt(int x, int y, bool dbl) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150); // sleep-ok: pacing input natif curseur->down (pas de signal pollable)
    mouse_event(2,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4,0,0,0,IntPtr.Zero); // sleep-ok: pacing down->up souris
    if (dbl) { System.Threading.Thread.Sleep(80); mouse_event(2,0,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4,0,0,0,IntPtr.Zero); } // sleep-ok: pacing double-clic natif
  }
  public static void Cap(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r); int w=r.R-r.L, ht=r.B-r.T; if (w<1||ht<1) return;
    using (var bmp = new Bitmap(w, ht)) {
      using (var g = Graphics.FromImage(bmp)) { IntPtr hdc = g.GetHdc(); PrintWindow(h, hdc, 2); g.ReleaseHdc(hdc); }
      bmp.Save(path, ImageFormat.Png);
    }
  }
}
'@
Add-Type -TypeDefinition $sig -ReferencedAssemblies System.Drawing
$p = Get-Process RigClientAccueil -ErrorAction Stop
$h = $p.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
$wr = $root.Current.BoundingRectangle
Write-Output ("fenetre '{0}' rect=({1},{2} {3}x{4})" -f $p.MainWindowTitle, [int]$wr.X, [int]$wr.Y, [int]$wr.Width, [int]$wr.Height)
if ($Focus) { [DriveNative]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400 }  # sleep-ok: settle focus avant clics
foreach ($c in $Clicks) {
    $parts = $c -split ','
    $dbl = ($parts.Count -ge 3 -and $parts[2] -eq 'double')
    [DriveNative]::ClickAt([int]$parts[0], [int]$parts[1], $dbl)
    Write-Output ("clic ({0},{1}) double={2}" -f $parts[0], $parts[1], $dbl)
    Start-Sleep -Milliseconds $SettleMs   # sleep-ok: settle UI apres action (rendu WinForms), pas de condition pollable generique
}
if ($OutPng -ne '') {
    # re-resoudre le handle : un dialog modal peut etre devenu la MainWindow
    $p2 = Get-Process RigClientAccueil -ErrorAction Stop
    [DriveNative]::Cap($p2.MainWindowHandle, $OutPng)
    Write-Output ("capture: {0} ({1} octets) titre='{2}'" -f $OutPng, (Get-Item $OutPng -ErrorAction SilentlyContinue).Length, $p2.MainWindowTitle)
}
