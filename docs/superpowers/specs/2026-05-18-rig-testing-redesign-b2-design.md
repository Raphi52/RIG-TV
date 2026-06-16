# Rig Testing — Redesign visuel « B2 Rail latéral »

> Spec de conception. Statut : validé en brainstorming le 2026-05-18.
> Cible : `Source/Wpf/Rig.Wpf.Kbis.TestViewer` (WPF .NET Framework 4.8, x86).
> Suite : plan d'implémentation (writing-plans).

## 1. Objectif & contexte

Rig Testing est l'outil interne de pilotage des tests RIG (smoke FlaUI legacy,
xUnit, scénarios). Il est utilisé au quotidien par le dev **et montré au
supérieur en démo**. Le look actuel (palette Tailwind-slate, accent bleu) est
propre mais générique.

Objectif : un **redesign distinctif orienté « wow démo »**, avec une identité
propre, tout en restant lisible et rapide à l'usage. Direction retenue après
maquettes comparatives : **B2 « Rail latéral »** — rail vertical bleu nuit +
accent doré, canvas clair, console de logs foncée encadrée.

Non-objectif : changer le comportement, la logique de test, les bindings.
Le redesign est **purement visuel**.

## 2. Décisions verrouillées (brainstorming)

| Décision | Choix retenu | Raison |
|---|---|---|
| Direction visuelle | **B2 Rail latéral** (validée sur maquette HF de l'écran réel RAPTURE/Smoke Import) | Identité "logiciel d'État moderne", scale multi-modules, contraste rail/canvas/console net |
| Périmètre | **Design-system (`App.xaml`) + shell (`MainWindow.xaml`)** ; tout panneau hérite | Styles centralisés → cohérence partout pour effort minimal |
| Chrome fenêtre | **Approche 1** : chrome OS Windows conservé, rail interne | ~90 % de l'effet, risque nul ; frameless WPF 4.8 = piège effort/bugs |
| Sur-mesure par panneau + motion | **Hors phase 1** (phase 2 optionnelle, seulement si trivial) | YAGNI ; le gros de l'effet vient du design-system + shell |

## 3. Design tokens (palette B2)

Brushes nommés sémantiques (remplacent la palette Tailwind actuelle ; mêmes
`x:Key` réutilisés là où c'est possible pour éviter les régressions de binding) :

```
Rail
  RailBg            #152C4E      fond du rail
  RailBgActive      #1E3E6E      item module actif
  RailText          #FFFFFF      texte rail (Opacity .55 si inactif)
  Gold              #E8B53D      accent : barre active, filets, soulignements

Surfaces
  Canvas            #F7F9FC      fond zone contenu
  Surface           #FFFFFF      cartes / panneaux
  BorderSubtle      #E6EBF2      bordures cartes / filets

Texte
  Ink               #152C4E      titres (aligné couleur rail = cohérence)
  TextSecondary     #475569
  TextMuted         #64748B
  TextFaint         #94A3B8      eyebrow / inactif

Statuts (ré-harmonisés, contraste AA sur Surface)
  Ok                #157347      OkSubtle  #EAF6EF
  Fail              #B02A37      FailSubtle #FBEAEC
  Skip              #9A6700      SkipSubtle #FBF2DF
  Neutral badge     bg #F4F6F9 / fg #94A3B8

Console (logs)
  ConsoleBg         #0B1120
  ConsoleHeaderBg   #152C4E
  ConsoleText       #E2E8F0
  ConsoleAccentOk   #34D399   ConsoleAccentRun #60A5FA   ConsoleMuted #475569
```

Contrainte typo : pas de letter-spacing (indispo WPF 4.8 sans Typography
custom) → la hiérarchie passe par taille + poids (Bold/SemiBold) uniquement.

## 4. Organisation des ressources

`App.xaml` éclaté pour la maintenabilité (le fichier est réécrit largement) :

```
Rig.Wpf.Kbis.TestViewer/
  App.xaml                       → MergedDictionaries (Palette + Controls)
  Resources/Palette.xaml         → Color/SolidColorBrush + DropShadowEffect
  Resources/Controls.xaml        → tous les Style (Card, boutons, rail, tab…)
```

Règle : **mêmes `x:Key` que l'existant** quand le style existe déjà
(`Card`, `PrimaryButton`, `SecondaryButton`, `MiniButton`, `FilterChip`,
`StatusPill`, `Eyebrow`, `MutedText`, `MonoMuted`, `H1`, `H2`, `FlatList`,
`FlatListItem`, styles `TabControl`/`TabItem` implicites). Les `MainWindow`
et panneaux qui les consomment ne changent pas de référence → zéro
régression de binding/style.

## 5. Shell — `MainWindow.xaml` (Approche 1)

Chrome Windows natif conservé. Layout interne :

```
┌──────┬───────────────────────────────────────────────┐
│ RAIL │ HEADER : eyebrow "MODULE" · Titre module       │
│ 96px │         · pills statut (✓N ✗N ⊘N) à droite     │
│      ├───────────────────────────────────────────────┤
│ RIG  │ TAB STRIP (soulignement Gold sur l'actif)      │
│ 🌐   ├───────────────────────────────────────────────┤
│ 📄   │                                                │
│ ⚖🟡 │  Contenu module (cards / listes / ConsolePanel) │
│ 👤   │  — hérite intégralement du design-system        │
│ 🏛   │                                                │
│ 9995 │                                                │
│ user │                                                │
└──────┴───────────────────────────────────────────────┘
```

**Rail** : `ItemsControl` vertical sur `Modules` (remplace la rangée de pills
horizontale actuelle). `ItemTemplate` = icône + label empilés. Item actif :
fond `RailBgActive` + barre gauche `Gold` (3 px). Inactif : `RailText`
Opacity .55, hover `RailBgActive` Opacity .5. Footer rail : greffe + user
(texte faint). **Command/bindings inchangés** : `SelectModuleCommand`,
`CommandParameter={Binding}`, `IsActive`.

**Header** : `Eyebrow` "MODULE" + `H1` `{Binding SelectedModule.Name}` à
gauche ; `StatusPill` ✓/✗/⊘ liés aux summaries existants à droite.

**Tab strip** : styles `TabControl`/`TabItem` existants, soulignement passe
de `Accent` (bleu) à `Gold`.

**Panneaux** : aucun changement structurel ; ils héritent des styles
restylés. Le `ConsolePanel` (nouveau style, voir §6) habille les zones de
logs (Smoke Import RAPTURE, logs legacy KBIS, stdout xUnit).

## 6. Inventaire des restyles

| Clé / élément | Avant | Après |
|---|---|---|
| `ModulePill` (ToggleButton) | pill horizontale, actif = `Accent` | **`RailItem`** vertical, actif = `RailBgActive` + barre `Gold` |
| `Card` | radius 10, ombre `#0F172A` .06 | radius 12, ombre plus douce, bord `BorderSubtle` |
| `PrimaryButton` | fond `Accent` bleu | fond `Ink`, hover plus sombre |
| `SecondaryButton` / `MiniButton` | inchangé structurellement | bord `BorderSubtle`, hover `Canvas` |
| `FilterChip` actif | `Accent` | `Ink` |
| `TabItem` actif | soulignement `Accent` | soulignement `Gold` |
| `Eyebrow/H1/H2/MutedText/MonoMuted` | couleurs Tailwind | re-mappées `Ink`/`TextMuted`/`TextFaint` |
| `StatusPill` | générique | variantes Ok/Fail/Skip/Neutral (bg subtle + fg) |
| **Nouveau `ConsolePanel`** | — | header `ConsoleHeaderBg` + corps `ConsoleBg`/`ConsoleText`, encadré radius 12 |
| `FlatList/FlatListItem` | highlight `AccentSubtle` | sélection `RailBgActive` Opacity .08, hover `Canvas` |

## 7. Comportement & non-régression

- **Aucune modification** VM / commands / services / bindings / logique de test.
- Le `TestViewerDriver` et les modes `--drive-testviewer-*` /
  `--inspect-testviewer-kbis-legacy` doivent continuer à fonctionner
  (ils ciblent des `Name`/`ControlType`/structure, pas des couleurs ;
  le rail reste des ToggleButton, les tabs des TabItem, les boutons gardent
  leurs libellés "Run smoke RIG"/"Run smoke import").
- Les 4 scénarios KBIS legacy + scénarios RAPTURE/xUnit restent listés et
  runnables à l'identique.

## 8. Contraintes / non-goals

- WPF .NET Framework 4.8, plateforme **x86**, **aucune nouvelle dépendance
  NuGet**.
- Pas de fenêtre frameless (chrome OS conservé).
- Pas de letter-spacing.
- **Hors phase 1** : `DataTemplate` de lignes sur-mesure (listes xUnit /
  Scénarios), animations de transition module/onglet, états vides
  ("lance un test"). Optionnels phase 2, uniquement si triviaux.

## 9. Vérification (protocole CLAUDE.md règle 15)

1. `dotnet build` TestViewer (Debug) — zéro erreur, zéro warning nouveau.
2. Fermer l'instance courante, relancer le binaire reconstruit.
3. Piloter via `--inspect-testviewer-kbis-legacy` + capture par module
   (Global / KBIS / RAPTURE) → screenshots plein écran.
4. **Analyser les screenshots** : le rendu réel correspond-il aux maquettes
   B2 (rail sombre + barre dorée active, canvas clair, console encadrée,
   header eyebrow+titre+pills) ?
5. Non-régression fonctionnelle : les 4 scénarios KBIS legacy listés ;
   `--drive-testviewer-legacy-kbis` et `--drive-testviewer-rapture-import`
   toujours verts (le driver retrouve modules/onglets/boutons).
6. Itérer (retour étape 1) tant que screenshot ≠ maquette OU régression.
   Ne pas déclarer fini avant screenshots conformes + drivers verts.

## 10. Risques & mitigations

| Risque | Mitigation |
|---|---|
| Le driver FlaUI ne retrouve plus un module/onglet/bouton après restyle | Conserver les `Name`/libellés/ControlType ; vérif étape 9.5 obligatoire |
| Régression de binding en éclatant `App.xaml` | Réutiliser les `x:Key` à l'identique ; MergedDictionaries ; build + smoke |
| Rendu réel ≠ maquette (rendu GDI/DPI WPF) | Boucle screenshot rule 15, ajustements ciblés |
| Scope creep vers le sur-mesure par panneau | Phase 1 = design-system + shell uniquement ; phase 2 explicitement différée |

## 11. Découpage indicatif (détaillé dans le plan d'implémentation)

1. Extraire `Palette.xaml` (tokens B2) + `Controls.xaml`, merges App.xaml.
2. Restyler les styles existants (mêmes clés) selon §6.
3. Ajouter `RailItem` + `ConsolePanel` + variantes `StatusPill`.
4. Réécrire le shell `MainWindow.xaml` (rail vertical, header, tab strip).
5. Brancher la console encadrée sur les zones de logs.
6. Build + vérif rule 15 (screenshots par module + drivers).
