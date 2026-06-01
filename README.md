# RIG-TV — RIG Testing Viewer

Harnais de test du RIG (pilote WPF **TestViewer** + driver console **SmokeRunner** + tests **xUnit**),
**extrait du monolithe RIG** (`Source/Wpf`, auto-contenu, aucune référence vers le legacy `Source/RIG`).

## Contenu
- `Rig.Wpf.Kbis.TestViewer` — app WPF pilote (rail modules : KBIS / Alertes / DCADEMAT / RAPTURE…).
- `Rig.Wpf.Kbis.SmokeRunner` — driver console (pilote RigClientAccueil via UIA/FlaUI, HDESK isolé).
- `Rig.Rapture.Tests` + autres `*.Tests` — logique pure (parser/diff/regex) en xUnit.
- `Rig.Wpf.Core` / `Mvvm` / `RigMetier*` / `Shell` / `Processus.*` — socle WPF.
- Outils : `kbis-harness.ps1`, `ensure-fresh.ps1`, `render-kbis-pdf.ps1`, `CLAUDE.md` (guide harnais).

## Build (Debug par défaut)
```powershell
dotnet build Rig.Wpf.Kbis.TestViewer\Rig.Wpf.Kbis.TestViewer.csproj -c Debug
dotnet build Rig.Wpf.Kbis.SmokeRunner\Rig.Wpf.Kbis.SmokeRunner.csproj -c Debug
```

## ⚠️ Données / sécurité
- Repo **PRIVÉ** : `Rig.Rapture.Tests/Fixtures/*.json` contiennent des **données réelles d'affaires (RGPD)**
  → **à anonymiser avant tout partage / passage en public**.
- Aucun secret du monolithe ici (les credentials en clair restent côté Azure, jamais poussés sur GitHub).
- RIG lui-même (`C:\rig\`) reste externe ; le harnais le pilote, ne l'embarque pas.
