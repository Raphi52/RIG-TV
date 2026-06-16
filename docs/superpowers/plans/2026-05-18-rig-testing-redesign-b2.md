# Rig Testing — Redesign B2 « Rail latéral » — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Donner à `Rig.Wpf.Kbis.TestViewer` l'identité visuelle « B2 Rail latéral » (rail vertical bleu nuit + accent doré, canvas clair, console encadrée) sans toucher au comportement.

**Architecture:** Pur visuel. `App.xaml` éclaté en `Resources/Palette.xaml` (tokens) + `Resources/Controls.xaml` (styles), mergés. La palette remappe AUSSI les anciennes clés (`Accent`, `BgPanel`…) vers les valeurs B2 → la plupart des panneaux basculent au look B2 sans édition de style. Le shell `MainWindow.xaml` passe d'une barre de pills horizontale à un rail vertical + header. Approche 1 : chrome OS Windows conservé.

**Tech Stack:** WPF .NET Framework 4.8, SDK-style csproj (`Microsoft.NET.Sdk` + `UseWPF` → `.xaml` auto-inclus en `Page`, AUCUN edit csproj requis), x86, zéro nouvelle dépendance NuGet.

**Spec de référence:** `docs/superpowers/specs/2026-05-18-rig-testing-redesign-b2-design.md`

> ⚠ **Commits (CLAUDE.md règle 6) :** ce repo interdit tout `git commit` sans
> autorisation explicite de l'utilisateur, par tour. Les steps "Commit" du plan
> sont rédigés mais l'exécutant DOIT obtenir un "go" avant chaque commit. Ne
> jamais committer silencieusement.

> ⚠ **Vérification (CLAUDE.md règle 15) :** chaque tâche se termine par build +
> relaunch + screenshot + analyse du screenshot (gate visuel) AVANT de passer à
> la suivante. Le screenshot est lu via le tool `Read` et comparé aux maquettes
> B2 (`.superpowers/brainstorm/244-*/content/b2-refined.html`).

---

## Pré-requis (one-time, avant Task 1)

- [ ] **Step 0.1 : Fermer toute instance TestViewer (lock exe)**

Run :
```
powershell -Command "Get-Process Rig.Wpf.Kbis.TestViewer -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800; 'closed'"
```
Expected : `closed` (ou rien si pas lancé). ⚠ `Stop-Process` = action confirmée
règle 7 : demander le "go" utilisateur avant si non déjà donné.

- [ ] **Step 0.2 : Baseline screenshot AVANT redesign (référence de non-régression visuelle)**

Run :
```
cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer" && dotnet build -c Debug --nologo
powershell -Command "Start-Process '.\bin\Debug\net48\Rig.Wpf.Kbis.TestViewer.exe' -WorkingDirectory '.\bin\Debug\net48'; Start-Sleep -Seconds 4"
cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner" && dotnet run --no-build -c Debug -- --inspect-testviewer-kbis-legacy
```
Lire le PNG produit (`Desktop\JsonRapture\screenshots\testviewer-kbis-legacy-inspect-*.png`) via `Read`. Noter l'état "avant" (référence). Fermer le TestViewer (Step 0.1).

---

## Task 1 : Extraire Palette.xaml + Controls.xaml, merger dans App.xaml (bascule couleurs B2, layout inchangé)

But : tout le visuel passe en couleurs B2 SANS toucher aux styles ni au layout,
en remappant les anciennes clés palette. Checkpoint à faible risque.

**Files:**
- Create: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Palette.xaml`
- Create: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml`
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/App.xaml` (remplace `Application.Resources` inline par `MergedDictionaries`)

- [ ] **Step 1.1 : Créer `Resources/Palette.xaml`**

Contenu COMPLET (tokens B2 + anciennes clés remappées sur B2 pour héritage zéro-édition) :

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- ════ B2 tokens (nouveaux, sémantiques) ════ -->
    <SolidColorBrush x:Key="RailBg"          Color="#152C4E"/>
    <SolidColorBrush x:Key="RailBgActive"    Color="#1E3E6E"/>
    <SolidColorBrush x:Key="RailText"        Color="#FFFFFF"/>
    <SolidColorBrush x:Key="Gold"            Color="#E8B53D"/>
    <SolidColorBrush x:Key="Canvas"          Color="#F7F9FC"/>
    <SolidColorBrush x:Key="Surface"         Color="#FFFFFF"/>
    <SolidColorBrush x:Key="Ink"             Color="#152C4E"/>
    <SolidColorBrush x:Key="ConsoleBg"       Color="#0B1120"/>
    <SolidColorBrush x:Key="ConsoleHeaderBg" Color="#152C4E"/>
    <SolidColorBrush x:Key="ConsoleText"     Color="#E2E8F0"/>
    <SolidColorBrush x:Key="ConsoleAccentOk" Color="#34D399"/>
    <SolidColorBrush x:Key="ConsoleAccentRun" Color="#60A5FA"/>
    <SolidColorBrush x:Key="ConsoleMuted"    Color="#475569"/>

    <!-- ════ Anciennes clés REMAPPÉES sur B2 (les styles existants héritent
              du nouveau look sans être édités) ════ -->
    <SolidColorBrush x:Key="BgWindow"      Color="#F7F9FC"/>
    <SolidColorBrush x:Key="BgTopBar"      Color="#152C4E"/>
    <SolidColorBrush x:Key="BgPanel"       Color="#FFFFFF"/>
    <SolidColorBrush x:Key="BgPanelAlt"    Color="#F1F5F9"/>
    <SolidColorBrush x:Key="BgHover"       Color="#F7F9FC"/>
    <SolidColorBrush x:Key="BorderSubtle"  Color="#E6EBF2"/>
    <SolidColorBrush x:Key="BorderStrong"  Color="#D8E0EA"/>
    <SolidColorBrush x:Key="TextPrimary"   Color="#152C4E"/>
    <SolidColorBrush x:Key="TextSecondary" Color="#475569"/>
    <SolidColorBrush x:Key="TextMuted"     Color="#64748B"/>
    <SolidColorBrush x:Key="TextFaint"     Color="#94A3B8"/>
    <SolidColorBrush x:Key="Accent"        Color="#152C4E"/>
    <SolidColorBrush x:Key="AccentHover"   Color="#0F2038"/>
    <SolidColorBrush x:Key="AccentSubtle"  Color="#E7ECF3"/>
    <SolidColorBrush x:Key="OkColor"       Color="#157347"/>
    <SolidColorBrush x:Key="OkSubtle"      Color="#EAF6EF"/>
    <SolidColorBrush x:Key="FailColor"     Color="#B02A37"/>
    <SolidColorBrush x:Key="FailSubtle"    Color="#FBEAEC"/>
    <SolidColorBrush x:Key="SkipColor"     Color="#9A6700"/>
    <SolidColorBrush x:Key="SkipSubtle"    Color="#FBF2DF"/>
    <SolidColorBrush x:Key="UnknownColor"  Color="#CBD5E1"/>
    <SolidColorBrush x:Key="BadgeLegacyBg" Color="#FBF2DF"/>
    <SolidColorBrush x:Key="BadgeLegacyFg" Color="#9A6700"/>
    <SolidColorBrush x:Key="BadgeNativeBg" Color="#EAF6EF"/>
    <SolidColorBrush x:Key="BadgeNativeFg" Color="#157347"/>
    <SolidColorBrush x:Key="BadgeNeutralBg" Color="#F4F6F9"/>
    <SolidColorBrush x:Key="BadgeNeutralFg" Color="#94A3B8"/>

    <DropShadowEffect x:Key="CardShadow" BlurRadius="14" ShadowDepth="2"
                      Direction="270" Color="#152C4E" Opacity="0.05"/>
</ResourceDictionary>
```

- [ ] **Step 1.2 : Créer `Resources/Controls.xaml` en DÉPLAÇANT les styles existants verbatim**

Copier TOUS les `<Style>` actuellement dans `App.xaml` (de `Card` à
`FlatListItem` inclus — Card, CardFlat, PrimaryButton, SecondaryButton,
MiniButton, FilterChip, ModulePill, StatusPill, Eyebrow, MutedText, MonoMuted,
H1, H2, le `Style TargetType="TabControl"`, `Style TargetType="TabItem"`,
FlatList, FlatListItem) tels quels dans :

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- … coller ici les Style existants, INCHANGÉS … -->
</ResourceDictionary>
```
Ne PAS modifier les styles à cette étape (refactor pur).

- [ ] **Step 1.3 : Réécrire `App.xaml` en MergedDictionaries**

Remplacer tout le bloc `<Application.Resources>` par :

```xml
<Application x:Class="Rig.Wpf.Kbis.TestViewer.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Resources/Palette.xaml"/>
                <ResourceDictionary Source="Resources/Controls.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 1.4 : Build**

Run : `cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer" && dotnet build -c Debug --nologo`
Expected : `0 Erreur(s)`, aucun nouveau warning XAML.

- [ ] **Step 1.5 : Relaunch + screenshot + analyse (gate rule 15)**

Run :
```
powershell -Command "Get-Process Rig.Wpf.Kbis.TestViewer -EA SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800"
powershell -Command "Start-Process 'C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48\Rig.Wpf.Kbis.TestViewer.exe' -WorkingDirectory 'C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\bin\Debug\net48'; Start-Sleep -Seconds 4"
cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner" && dotnet run --no-build -c Debug -- --inspect-testviewer-kbis-legacy
```
`Read` le PNG `testviewer-kbis-legacy-inspect-*.png`. Attendu : **mêmes positions/layout qu'avant** (pills horizontales encore là) MAIS couleurs basculées B2 (titres bleu nuit `#152C4E`, accents plus bleu marine que bleu vif, fonds clairs). Si régression de layout ou clé manquante → corriger Palette/Controls, rebuild.

- [ ] **Step 1.6 : Commit** (après "go" utilisateur — règle 6)

```bash
git add Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Palette.xaml Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml Source/Wpf/Rig.Wpf.Kbis.TestViewer/App.xaml
git commit -m "testing(ui): éclate App.xaml en Palette/Controls + tokens B2 (héritage couleurs)"
```

---

## Task 2 : Ajouter les nouveaux styles B2 (RailItem, ConsolePanel, StatusPill variants)

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml` (ajout en fin de dictionnaire)

- [ ] **Step 2.1 : Ajouter `RailItem` (ToggleButton vertical, accent doré actif)**

Ajouter dans `Controls.xaml` :

```xml
<Style TargetType="{x:Type ToggleButton}" x:Key="RailItem">
    <Setter Property="Background"      Value="Transparent"/>
    <Setter Property="Foreground"      Value="{StaticResource RailText}"/>
    <Setter Property="Opacity"         Value="0.55"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Cursor"          Value="Hand"/>
    <Setter Property="HorizontalAlignment" Value="Stretch"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ToggleButton">
                <Grid x:Name="Root" Background="{TemplateBinding Background}">
                    <Border x:Name="Bar" Width="3" HorizontalAlignment="Left"
                            Background="Transparent"/>
                    <ContentPresenter HorizontalAlignment="Center"
                                      VerticalAlignment="Center" Margin="0,11"/>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{StaticResource RailBgActive}"/>
                        <Setter Property="Opacity" Value="0.8"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{StaticResource RailBgActive}"/>
                        <Setter TargetName="Bar"  Property="Background" Value="{StaticResource Gold}"/>
                        <Setter Property="Opacity" Value="1"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Opacity" Value="0.3"/>
                        <Setter Property="Cursor"  Value="Arrow"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 2.2 : Ajouter `ConsolePanel` + `ConsoleHeader` + `ConsoleText`**

```xml
<Style TargetType="Border" x:Key="ConsolePanel">
    <Setter Property="CornerRadius"    Value="12"/>
    <Setter Property="Background"      Value="{StaticResource ConsoleBg}"/>
    <Setter Property="BorderBrush"     Value="{StaticResource RailBg}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="ClipToBounds"    Value="True"/>
</Style>
<Style TargetType="Border" x:Key="ConsoleHeader">
    <Setter Property="Background" Value="{StaticResource ConsoleHeaderBg}"/>
    <Setter Property="Padding"    Value="14,7"/>
</Style>
<Style TargetType="TextBox" x:Key="ConsoleTextBox">
    <Setter Property="Background"       Value="{StaticResource ConsoleBg}"/>
    <Setter Property="Foreground"       Value="{StaticResource ConsoleText}"/>
    <Setter Property="BorderThickness"  Value="0"/>
    <Setter Property="FontFamily"       Value="Consolas"/>
    <Setter Property="FontSize"         Value="12"/>
    <Setter Property="IsReadOnly"       Value="True"/>
    <Setter Property="Padding"          Value="14,10"/>
    <Setter Property="VerticalScrollBarVisibility"   Value="Auto"/>
    <Setter Property="HorizontalScrollBarVisibility" Value="Auto"/>
</Style>
```

- [ ] **Step 2.3 : Ajouter variantes `StatusPill` (Ok/Fail/Skip/Neutral)**

```xml
<Style TargetType="Border" x:Key="StatusPillOk" BasedOn="{StaticResource StatusPill}">
    <Setter Property="Background" Value="{StaticResource OkSubtle}"/>
</Style>
<Style TargetType="Border" x:Key="StatusPillFail" BasedOn="{StaticResource StatusPill}">
    <Setter Property="Background" Value="{StaticResource FailSubtle}"/>
</Style>
<Style TargetType="Border" x:Key="StatusPillSkip" BasedOn="{StaticResource StatusPill}">
    <Setter Property="Background" Value="{StaticResource SkipSubtle}"/>
</Style>
<Style TargetType="Border" x:Key="StatusPillNeutral" BasedOn="{StaticResource StatusPill}">
    <Setter Property="Background" Value="{StaticResource BadgeNeutralBg}"/>
</Style>
```

- [ ] **Step 2.4 : Build** — `dotnet build -c Debug --nologo` → `0 Erreur(s)`. (Styles non encore consommés : pas de changement visuel attendu, juste compilation OK.)

- [ ] **Step 2.5 : Commit** (après "go")
```bash
git add Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml
git commit -m "testing(ui): styles B2 RailItem + ConsolePanel + StatusPill variants"
```

---

## Task 3 : Restyle ciblé des styles existants (raffinements §6 du spec)

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml`

- [ ] **Step 3.1 : `Card` — radius 12**

Dans le style `Card`, remplacer `<Setter Property="CornerRadius" Value="10"/>`
par `<Setter Property="CornerRadius" Value="12"/>`. (L'ombre vient déjà de
`CardShadow` remappé en Task 1.)

- [ ] **Step 3.2 : `TabItem` — soulignement Gold**

Dans le `ControlTemplate` de `TabItem`, trigger `IsSelected=True` :
remplacer `Value="{StaticResource Accent}"` (le `Setter` sur `Bd.BorderBrush`)
par `Value="{StaticResource Gold}"`, et le `Setter` sur
`Cp.TextBlock.Foreground` `Value="{StaticResource Accent}"` par
`Value="{StaticResource Ink}"`.

- [ ] **Step 3.3 : `FlatListItem` — sélection sobre**

Trigger `IsSelected=True` : remplacer
`<Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentSubtle}"/>`
par `<Setter TargetName="Bd" Property="Background" Value="{StaticResource AccentSubtle}"/>`
(déjà remappé `#E7ECF3` en Task 1 — vérifier rendu, pas d'édition si OK).

- [ ] **Step 3.4 : Build + relaunch + screenshot + analyse (rule 15)**

Même procédure que Step 1.5. Attendu : cartes plus arrondies, onglet actif
souligné doré, sélection liste discrète. Toujours layout pills horizontales
(le shell change en Task 4). Comparer au PNG. Itérer si divergence.

- [ ] **Step 3.5 : Commit** (après "go")
```bash
git add Source/Wpf/Rig.Wpf.Kbis.TestViewer/Resources/Controls.xaml
git commit -m "testing(ui): raffinements B2 (card radius, onglet doré, sélection sobre)"
```

---

## Task 4 : Réécrire le shell `MainWindow.xaml` — rail vertical + header

But : remplacer la barre de pills horizontale par le rail vertical B2 +
bandeau header (eyebrow MODULE + titre + pills statut). **Bindings/commands
strictement identiques** (`SelectModuleCommand`, `CommandParameter={Binding}`,
`Modules`, `SelectedModule`, summaries).

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/MainWindow.xaml`

- [ ] **Step 4.1 : Repérer le bloc nav actuel**

Run : `grep -n "Module nav\|ItemsControl ItemsSource=\"{Binding Modules}\"\|ModulePill\|DockPanel.Dock=\"Top\"" "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\MainWindow.xaml"`
Noter les bornes du `<DockPanel>` top-bar contenant l'`ItemsControl` des `Modules` (≈ lignes 30-68 d'après l'état actuel ; vérifier).

- [ ] **Step 4.2 : Restructurer la racine en rail + zone contenu**

Le layout racine devient :
```xml
<DockPanel LastChildFill="True" Background="{StaticResource Canvas}">
  <!-- RAIL gauche -->
  <Border DockPanel.Dock="Left" Width="96" Background="{StaticResource RailBg}">
    <DockPanel LastChildFill="False">
      <TextBlock DockPanel.Dock="Top" Text="RIG" Foreground="{StaticResource Gold}"
                 FontWeight="Bold" FontSize="14" HorizontalAlignment="Center"
                 Margin="0,16,0,18"/>
      <ItemsControl DockPanel.Dock="Top" ItemsSource="{Binding Modules}">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><StackPanel Orientation="Vertical"/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate DataType="{x:Type vm:ModuleNavItem}">
            <ToggleButton Style="{StaticResource RailItem}"
                          IsChecked="{Binding IsActive, Mode=OneWay}"
                          IsEnabled="{Binding IsAvailable}"
                          Command="{Binding DataContext.SelectModuleCommand,
                                    RelativeSource={RelativeSource AncestorType=Window}}"
                          CommandParameter="{Binding}">
              <StackPanel Orientation="Vertical" HorizontalAlignment="Center">
                <TextBlock Text="{Binding Icon}" FontSize="16"
                           HorizontalAlignment="Center"/>
                <TextBlock Text="{Binding Name}" FontSize="10"
                           HorizontalAlignment="Center" Margin="0,3,0,0"/>
              </StackPanel>
            </ToggleButton>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
      <StackPanel DockPanel.Dock="Bottom" Margin="0,0,0,14">
        <TextBlock Text="{Binding GreffeLabel, FallbackValue='9995'}"
                   Foreground="{StaticResource RailText}" Opacity="0.4"
                   FontSize="9" HorizontalAlignment="Center"/>
      </StackPanel>
    </DockPanel>
  </Border>

  <!-- HEADER + contenu : conserver le reste de l'arbre existant ici -->
  <DockPanel LastChildFill="True">
    <Border DockPanel.Dock="Top" Background="{StaticResource Canvas}" Padding="22,16,22,0">
      <Grid>
        <StackPanel>
          <TextBlock Style="{StaticResource Eyebrow}" Text="MODULE"/>
          <TextBlock Style="{StaticResource H1}"
                     Text="{Binding SelectedModule.Name}"/>
        </StackPanel>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <Border Style="{StaticResource StatusPillOk}" Margin="0,0,8,0">
            <TextBlock Foreground="{StaticResource OkColor}" FontWeight="SemiBold"
                       Text="{Binding GlobalSummary, FallbackValue='✓ —'}"/>
          </Border>
        </StackPanel>
      </Grid>
    </Border>
    <!-- ↓↓↓ COLLER ICI l'arbre de contenu existant (les TabControl par module,
            DockPanels IsKbisModule/IsRaptureModule/IsGlobalModule, etc.)
            SANS le modifier ↓↓↓ -->
  </DockPanel>
</DockPanel>
```
⚠ Conserver le `xmlns:vm` existant en tête de `MainWindow.xaml` (déjà présent).
⚠ Ne PAS supprimer/renommer les conteneurs liés à `IsKbisModule` /
`IsRaptureModule` / `IsGlobalModule` ni les `x:Name`/bindings : le driver FlaUI
et les commandes en dépendent.

- [ ] **Step 4.3 : Build** — `dotnet build -c Debug --nologo` → `0 Erreur(s)`.

- [ ] **Step 4.4 : Relaunch + screenshot + analyse (rule 15)**

Procédure Step 1.5. Attendu : **rail vertical sombre à gauche** (RIG doré en
haut, modules empilés icône+label, actif = fond `#1E3E6E` + barre dorée
gauche), header avec eyebrow "MODULE" + grand titre bleu nuit + pill statut à
droite. Comparer pixel-feel à `b2-refined.html`. Itérer si divergence.

- [ ] **Step 4.5 : Non-régression driver FlaUI**

Run : `cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner" && dotnet run --no-build -c Debug -- --drive-testviewer-legacy-kbis`
Expected : le driver retrouve le module KBIS (pill = ToggleButton, `SwitchToModule` via Toggle) + l'onglet Smoke Legacy + le bouton "Run smoke RIG" → smoke 7/7. Si le driver ne switch plus le module : vérifier que `RailItem` reste un `ToggleButton` avec `Command`/`CommandParameter` intacts. Itérer.

- [ ] **Step 4.6 : Commit** (après "go")
```bash
git add Source/Wpf/Rig.Wpf.Kbis.TestViewer/MainWindow.xaml
git commit -m "testing(ui): shell B2 — rail vertical + header (bindings inchangés)"
```

---

## Task 5 : Habiller les zones de logs en ConsolePanel

**Files:**
- Modify: `Source/Wpf/Rig.Wpf.Kbis.TestViewer/MainWindow.xaml`

- [ ] **Step 5.1 : Recenser les TextBox de logs**

Run : `grep -n "FontFamily=\"Consolas\"\|Background=\"#0F172A\"\|RaptureSmokeLastStdout\|LegacyLastFullStdout\|RegressionLastStdout\|RaptureRegressionLastStdout" "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer\MainWindow.xaml"`
Lister chaque `TextBox` de logs (Smoke Import RAPTURE, logs legacy KBIS/Global, stdout xUnit KBIS, stdout xUnit Rapture).

- [ ] **Step 5.2 : Pour CHAQUE TextBox de logs, appliquer le style + cadre**

Pour chaque occurrence, remplacer les setters inline de couleur/police
(`Background="#0F172A"`, `Foreground=…`, `FontFamily="Consolas"`, etc.) par
`Style="{StaticResource ConsoleTextBox}"` et l'envelopper :

```xml
<Border Style="{StaticResource ConsolePanel}">
  <DockPanel LastChildFill="True">
    <Border DockPanel.Dock="Top" Style="{StaticResource ConsoleHeader}">
      <TextBlock Text="LOGS" Foreground="{StaticResource ConsoleText}"
                 FontSize="10" FontWeight="SemiBold"/>
    </Border>
    <!-- TextBox existant, conserver Text={Binding …} + x:Name + events -->
    <TextBox Style="{StaticResource ConsoleTextBox}"
             Text="{Binding RaptureSmokeLastStdout, Mode=OneWay}"/>
  </DockPanel>
</Border>
```
⚠ Conserver pour chaque TextBox son `Text="{Binding …}"` d'origine, son
`x:Name` éventuel et ses handlers (`TextChanged=…` auto-scroll). Adapter le
libellé du header par zone (« LOGS — SMOKE IMPORT », « LOGS — RIG LEGACY »,
« LOGS — DOTNET TEST »).

- [ ] **Step 5.3 : Build** — `dotnet build -c Debug --nologo` → `0 Erreur(s)`.

- [ ] **Step 5.4 : Relaunch + screenshot + analyse (rule 15)**

Procédure Step 1.5, MAIS lancer un run qui produit des logs pour voir la
console peuplée :
```
cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner" && dotnet run --no-build -c Debug -- --drive-testviewer-rapture-import --json rapture-smoke-pc-v5-aud28644-9995.json
```
`Read` le screenshot final. Attendu : console foncée `#0B1120` encadrée
radius 12, header `#152C4E`, texte clair lisible, sur canvas clair. Comparer
maquette. Itérer.

- [ ] **Step 5.5 : Commit** (après "go")
```bash
git add Source/Wpf/Rig.Wpf.Kbis.TestViewer/MainWindow.xaml
git commit -m "testing(ui): consoles de logs encadrées (ConsolePanel B2)"
```

---

## Task 6 : Vérification finale rule 15 (tous modules) + non-régression complète

**Files:** aucun (vérification seule).

- [ ] **Step 6.1 : Build propre**
Run : `cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.TestViewer" && dotnet build -c Debug --nologo`
Expected : `0 Erreur(s)`, `0 Avertissement(s)` nouveau vs baseline.

- [ ] **Step 6.2 : Relaunch + capture des 3 modules**
Fermer, relancer. Via `--inspect-testviewer-kbis-legacy` (KBIS) puis pour
RAPTURE/Global, étendre/adapter l'inspection (ou capture plein écran après
`SwitchToModule`). `Read` chaque screenshot.
Attendu par module : rail B2 + module actif doré, header eyebrow+titre+pill,
contenu cohérent, console encadrée le cas échéant. Comparer à
`.superpowers/brainstorm/244-1779096311/content/b2-refined.html` et
`institutional-variants.html` (B2).

- [ ] **Step 6.3 : Non-régression fonctionnelle**
Run :
```
cd "C:\Code RIG\RigApplication-testing\Source\Wpf\Rig.Wpf.Kbis.SmokeRunner" && dotnet run --no-build -c Debug -- --drive-testviewer-legacy-kbis
dotnet run --no-build -c Debug -- --drive-testviewer-rapture-import --json rapture-smoke-pc-v5-aud28644-9995.json
```
Expected : les deux verts (driver retrouve modules/onglets/boutons ; smokes
internes 7/7 / 8/8). Les 4 scénarios KBIS legacy listés (inspection Step 6.2).

- [ ] **Step 6.4 : Décision rule 15**
Si screenshots conformes maquettes B2 ET drivers verts → fini.
Sinon → identifier l'écart, retourner à la tâche concernée, itérer.
Ne jamais déclarer fini avant ce gate vert (verification-before-completion).

- [ ] **Step 6.5 : Commit final + mémo** (après "go")
```bash
git add docs/superpowers/specs/2026-05-18-rig-testing-redesign-b2-design.md docs/superpowers/plans/2026-05-18-rig-testing-redesign-b2.md
git commit -m "testing(ui): spec + plan redesign B2 Rig Testing"
```

---

## Self-Review (auteur du plan)

**1. Couverture spec :**
- §3 tokens → Task 1.1 (Palette.xaml complet). ✓
- §4 organisation ResourceDictionary → Task 1.1–1.3. ✓
- §5 shell rail/header/tab → Task 4. ✓
- §6 inventaire restyles : Card/TabItem/FlatListItem → Task 3 ; RailItem/ConsolePanel/StatusPill → Task 2 ; PrimaryButton→Ink, FilterChip→Ink, Eyebrow/H* → couverts par remap clés Task 1 (Accent→#152C4E, TextPrimary→Ink) sans édition. ✓
- §6 ConsolePanel sur zones de logs → Task 5. ✓
- §7 non-régression driver/scénarios → Task 4.5, 6.3. ✓
- §9 vérification rule 15 → Step *.5 + Task 6. ✓
- §8 non-goals (pas de frameless, pas de sur-mesure panneau/motion) → respecté (aucune tâche n'y touche). ✓

**2. Placeholders :** XAML complet fourni pour Palette, RailItem, ConsolePanel,
StatusPill, shell, wrapper console. Steps "restyle" donnent le diff exact
(valeur avant→après). Aucun "TBD/TODO". ✓

**3. Cohérence types/clés :** clés palette réutilisées à l'identique
(`Accent`, `BgPanel`…) ; nouvelles clés (`RailItem`, `ConsolePanel`,
`ConsoleTextBox`, `ConsoleHeader`, `StatusPillOk/Fail/Skip/Neutral`, `Ink`,
`Gold`, `RailBg`, `RailBgActive`, `RailText`, `Canvas`, `Surface`,
`ConsoleBg/HeaderBg/Text`) définies en Task 1–2 avant usage Task 4–5. ✓
`vm:ModuleNavItem` : type existant déjà bindé dans le MainWindow actuel. ✓

Aucun écart résiduel.
