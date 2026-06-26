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

> **Pour la boucle de test `--loop` :** builder TestViewer en **Release** au moins une fois
> (`dotnet build Rig.Wpf.Kbis.TestViewer\Rig.Wpf.Kbis.TestViewer.csproj -c Release`). Le mode
> `--loop` lit les scénarios depuis la sortie **Release** de TestViewer (chemin codé en dur,
> `LoopRun.cs:232`) — un build Debug seul donne **0 scénario**.

## 🤖 Skill Claude Code — `/rig-loop`

Le repo embarque une skill Claude Code (`.claude/skills/rig-loop/`) qui pilote la
boucle de test à ta place (cadrage → écriture des scénarios → run → triage des
échecs → proposition). Elle appelle le CLI `SmokeRunner --loop` sous le capot.

**Installe-la en global** pour l'avoir dans *toutes* tes sessions Claude Code (pas
seulement quand ton cwd est dans ce repo) :

```powershell
# copie la skill dans ton dossier perso Claude Code (~/.claude/skills)
Copy-Item -Recurse -Force .claude\skills\rig-loop "$env:USERPROFILE\.claude\skills\rig-loop"
```

Ensuite, dans n'importe quelle session Claude Code : tape `/rig-loop` et décris ce
que tu veux tester. (Sans cette copie, la skill reste dispo **uniquement** quand tu
travailles à l'intérieur du repo `RIG-TV` — c'est une skill projet par défaut.)

> Sans Claude Code : le même travail se fait à la main via le CLI
> `SmokeRunner --loop` — voir `Rig.Wpf.Kbis.SmokeRunner\loop\LOOP.md`.

## ⚠️ Données / sécurité
- Repo **PRIVÉ** : `Rig.Rapture.Tests/Fixtures/*.json` contiennent des **données réelles d'affaires (RGPD)**
  → **à anonymiser avant tout partage / passage en public**.
- Aucun secret du monolithe ici (les credentials en clair restent côté Azure, jamais poussés sur GitHub).
- RIG lui-même (`C:\rig\`) reste externe ; le harnais le pilote, ne l'embarque pas.
