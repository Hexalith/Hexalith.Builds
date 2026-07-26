---
name: audit-hexalith-ci-cd
description: Audite les actifs CI/CD partagés de Hexalith.Builds et recherche des améliorations vérifiables.
argument-hint: "[périmètre ou contrainte facultative ; par défaut : audit complet en lecture seule]"
agent: agent
---

# Audit CI/CD de Hexalith.Builds

Agis comme un auditeur senior DevSecOps spécialisé dans GitHub Actions, .NET,
NuGet, npm, Dapr, conteneurs OCI et sécurité de la chaîne
d'approvisionnement logicielle.

Ta mission est d'évaluer si le dépôt courant constitue un module de gestion des
builds fiable et conforme pour les différents modules Hexalith. Recherche aussi
les améliorations qui ne sont pas encore exigées par les règles locales, mais
qui apporteraient un gain concret de sécurité, de reproductibilité, de
maintenabilité, de performance ou de traçabilité.

L'audit est **en lecture seule par défaut**. Ne modifie aucun fichier, ne mets à
jour aucune dépendance, ne crée ni branche ni commit, et ne déclenche aucune
publication ou aucun déploiement. Si l'utilisateur demande explicitement des
corrections, termine d'abord l'audit, propose un plan priorisé, puis limite les
changements au périmètre autorisé.

## Référentiel local obligatoire

Avant toute analyse :

1. Lis et applique [AGENTS.md](../../AGENTS.md), notamment la procédure de
   localisation de `hexalith-llm-instructions.md`. N'initialise aucun sous-module
   imbriqué.
2. Lis les règles et contrats propres au dépôt :
   [DEVELOPMENT.md](../../DEVELOPMENT.md),
   [README.md](../../README.md) et
   [ci-cd-standards.md](../workflows/ci-cd-standards.md).
3. Inspecte les fichiers de configuration qui font autorité, y compris
   `.editorconfig`, `.gitattributes`, `.gitmodules`, `global.json`, les fichiers
   MSBuild, le catalogue central de packages, `package.json`, le lockfile npm,
   la configuration semantic-release et Dependabot.
4. Considère les règles locales explicites comme le contrat Hexalith. Lorsqu'une
   recommandation externe est plus stricte ou contredit une exception locale
   documentée, ne masque pas le conflit : qualifie-le comme risque accepté à
   réexaminer ou comme décision d'architecture nécessaire, avec ses compromis.

## Méthode d'audit

### 1. Établir l'état et le périmètre

- Détermine la racine Git et travaille depuis le dépôt propriétaire.
- Relève la branche, le commit audité, l'état du working tree et les versions
  d'outils disponibles. Ne touche pas aux changements existants.
- Utilise de préférence `git ls-files` pour inventorier les actifs suivis et
  éviter que `node_modules`, les artefacts ou des fichiers locaux polluent
  l'analyse.
- Inventorie et classe au minimum :
  - workflows appelants et réutilisables sous `.github/workflows` ;
  - actions composites sous `Github/*/action.yml` ;
  - scripts Bash, PowerShell, Python et JavaScript exécutés par ces actions ;
  - configuration de build, test, pack, versionnement et publication ;
  - contrôles de sécurité, tests de contrats, fixtures et documentation ;
  - interfaces exposées aux dépôts consommateurs : inputs, secrets, outputs,
    valeurs par défaut, permissions et hypothèses de structure.
- Construis un graphe synthétique « déclencheur → workflow appelant → workflow
  réutilisable/action → script → artefact ou destination ». Repère les actifs
  orphelins, dupliqués, obsolètes ou incompatibles entre eux.
- Distingue toujours :
  1. ce qui est prouvé par les fichiers du dépôt ;
  2. ce qui est prouvé par un test ou une commande exécutée ;
  3. ce qui dépend de paramètres GitHub distants ;
  4. ce qui reste inconnu.

### 2. Rechercher les bonnes pratiques actuelles

Consulte des sources primaires et actuelles. Priorise la documentation officielle
GitHub, Microsoft/.NET/NuGet, Dapr et Azure, puis les spécifications OpenSSF et
SLSA. Vérifie la date et la version applicables ; ne te fonde pas uniquement sur
ta mémoire.

Utilise notamment ces points de départ, sans considérer la liste exhaustive :

- <https://docs.github.com/en/actions/reference/security/secure-use>
- <https://docs.github.com/en/actions/concepts/workflows-and-actions/reusing-workflow-configurations>
- <https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/control-deployments>
- <https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations>
- <https://docs.github.com/en/code-security/concepts/supply-chain-security/supply-chain-security>
- <https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files>
- <https://github.com/ossf/scorecard/blob/main/docs/checks.md>
- <https://slsa.dev/spec/v1.2/requirements>

Pour chaque recommandation externe retenue, donne le lien direct vers la page
qui la justifie et la date de consultation. Recherche aussi les avis de sécurité,
versions maintenues et notes de migration des actions et outils réellement
utilisés. N'affirme pas qu'une version est à jour sans l'avoir vérifié auprès de
la source amont.

### 3. Contrôler les workflows et actions

Vérifie au minimum les dimensions suivantes.

#### Déclencheurs, orchestration et contrats réutilisables

- Couverture des événements attendus : pull request, push sur `main`, exécution
  planifiée pour les contrôles périodiques et déclenchement manuel des releases.
- Absence de déclencheur privilégié dangereux combiné à du code non fiable,
  notamment `pull_request_target` ou `workflow_run` avec checkout ou artefacts
  contrôlés par une pull request.
- Effet des filtres de branches et de chemins sur les checks requis ; absence de
  succès trompeur ou de check bloqué parce qu'il a été ignoré.
- `workflow_call` : types, champs requis, valeurs par défaut, validation des
  entrées, secrets explicitement déclarés, outputs et compatibilité ascendante.
- Sémantique exacte des workflows réutilisables : contexte du dépôt appelant,
  résolution des actions locales, propagation des permissions et des secrets,
  limites d'imbrication et comportement lors d'un rerun.
- Timeouts, concurrence, annulation, retries bornés, gestion des échecs et
  prévention des courses entre releases ou déploiements.

#### Permissions, secrets et entrées non fiables

- `permissions: {}` ou lecture minimale par défaut, puis élévation uniquement au
  niveau du job qui en a besoin.
- Scopes minimaux de `GITHUB_TOKEN`, impossibilité pour les jobs de validation
  d'écrire, et absence de permission héritée inutile.
- Secrets passés explicitement ; absence de `secrets: inherit` pour les chemins
  de publication ; séparation entre validation de code non fiable et accès aux
  secrets.
- Environnements protégés, reviewers, politique de branche et concurrence pour
  les publications ou déploiements.
- Préférence pour OIDC et des jetons courts lorsque le fournisseur le permet, au
  lieu d'identifiants cloud permanents.
- Aucune interpolation directe d'un contexte contrôlable par l'utilisateur dans
  un script. Passe ces valeurs par variables d'environnement ou arguments,
  valide-les par allowlist et cite correctement les expansions shell.
- Pas de secret, contexte GitHub complet, jeton, URL signée ou donnée sensible
  dans les logs, artefacts, caches ou messages d'erreur. Vérifie également les
  transformations de secrets qui pourraient contourner le masquage.

#### Dépendances et chaîne d'approvisionnement

- Toute action ou tout workflow externe est épinglé conformément au contrat
  Hexalith et au niveau de risque : SHA complet immuable pour le tiers ; SHA
  revu pour toute publication ; commentaire de version vérifiable sur la même
  ligne lorsque Dependabot doit le maintenir.
- Le SHA appartient bien au dépôt amont attendu, correspond à la version
  documentée et n'est associé à aucun avis de sécurité connu.
- Dependabot couvre tous les écosystèmes effectivement présents
  (`github-actions`, `nuget`, `npm`, et autres le cas échéant), sans supposer que
  les réglages distants sont activés.
- `npm ci`, lockfile cohérent, provenance/signatures lorsque supportées,
  `NuGetAudit` actif, versions centralisées et exceptions bornées.
- Reproductibilité de la restauration .NET. Évalue l'intérêt de lockfiles NuGet
  et de `--locked-mode` selon la nature des projets ; n'impose pas un lockfile de
  bibliothèque sans analyser son utilité réelle pour le graphe consommateur.
- Origine, intégrité et traçabilité des packages NuGet, outils, scripts
  téléchargés et images de base.
- SBOM, signatures, attestations de provenance, SLSA, digests OCI et releases
  immuables. Classe ces éléments en non-conformité seulement s'ils sont exigés
  par le contrat ; sinon présente-les comme améliorations chiffrées et
  priorisées.

#### Build, tests et qualité .NET

- SDK sélectionné depuis `global.json`, versions supportées et cohérence avec
  les valeurs par défaut des actions partagées.
- En CI/CD : restore, build, tests, pack et publish en `Release`, warnings as
  errors, artefacts reproductibles, et aucune dépendance involontaire à des
  sorties Debug ou à des `ProjectReference` entre modules Hexalith lorsque le
  contrat impose des packages NuGet.
- Restore explicite suivi de `--no-restore`, build suivi de `--no-build` lorsque
  cela reste correct ; absence de rebuilds inutiles ou de tests exécutés sur des
  binaires différents de ceux qui sont publiés.
- Tests exécutés projet par projet, avec preuve qu'au moins un test pertinent a
  réellement tourné. Détecte les filtres qui ne sélectionnent rien, les skips
  généralisés et les jobs advisory qui devraient être bloquants.
- Couverture, tests d'intégration Dapr/Aspire, tests de contrats des workflows,
  publication des TRX/rapports avec `if: always()`, noms d'artefacts uniques et
  rétention proportionnée au besoin.
- Cache NuGet limité au global-packages folder, clé incluant OS et tous les
  fichiers qui définissent réellement le graphe de dépendances, absence de
  collision ou de restauration trop large et aucun contenu sensible.
- Cohérence entre `.slnx`, projets, catalogue central, analyseurs, formatage,
  documentation XML, scripts de validation et comportement de la CI.

#### Releases, packages et conteneurs

- La release est intentionnelle, sérialisée et protégée ; elle prouve le commit
  exact de `main` et le succès de la CI pour ce SHA avant toute approbation puis
  à chaque frontière d'écriture sensible.
- Le code de release exécuté est immuable et son identité est vérifiée de bout en
  bout. Recherche les écarts TOCTOU entre validation, tag, package, image et
  publication.
- Permissions d'écriture limitées au job de release ; environnement protégé et
  secrets nommés explicitement ; aucune publication depuis une pull request.
- Semantic-release, Conventional Commits, titre de pull request/squash et
  changelog forment un contrat cohérent, testé et fermé en cas d'entrée invalide.
- Inventaire des packages attendu déclaré indépendamment des artefacts produits,
  validation des noms/versions/hashes avant écriture, collision traitée en
  échec, et aucun `--skip-duplicate` qui masquerait une divergence.
- Images non-root, minimales et multi-architecture lorsque requis ; publication
  et déploiement par digest lorsque possible ; absence de tag mutable `latest`
  comme identité d'autorité ; validation du registre et preuve des digests
  enfants d'un manifest multi-plateforme.
- Authentification registre/cloud à privilèges minimaux, entrées Azure/OCI
  strictement validées et quotées, et absence de mise à jour de sous-module ou
  de branche distante pendant une publication.

#### Maintenabilité et expérience des consommateurs

- Une seule source d'autorité pour les règles partagées ; pas de logique copiée
  qui dérive entre actions historiques et workflows modernes.
- Documentation exacte des inputs, defaults, secrets, permissions, prérequis,
  exemples d'appel et politiques GitHub externes au dépôt.
- Tests de contrat négatifs et positifs pour les comportements de sécurité et
  les chemins de publication, avec échecs fermés et messages diagnostiques
  exploitables.
- Politique de dépréciation et de compatibilité pour les interfaces consommées
  à `@main` ; identification des changements susceptibles de casser tous les
  modules Hexalith.
- Rapport valeur/coût pour les optimisations : temps de pipeline, cache,
  parallélisme, duplication, bruit des alertes et durée de conservation.

### 4. Vérifier la configuration GitHub distante

Si `gh` est disponible et authentifié avec des droits suffisants, effectue des
requêtes **en lecture seule** pour vérifier :

- rulesets ou protection de `main`, reviews et checks obligatoires ;
- protection de l'environnement `production`, reviewers et branches autorisées ;
- permissions par défaut de `GITHUB_TOKEN`, possibilité pour Actions de créer ou
  approuver des pull requests et politique d'autorisation/SHA des actions ;
- dependency graph, Dependabot alerts/updates, secret scanning, push protection,
  code scanning/CodeQL et releases immuables ;
- statut récent des workflows essentiels et cohérence des checks déclarés avec
  les protections réelles.

N'utilise jamais une absence dans les fichiers pour conclure qu'un réglage
distant est absent. Si l'accès, le plan GitHub ou les permissions ne permettent
pas la vérification, inscris précisément **Non vérifié**, avec la commande/API à
exécuter et l'autorisation nécessaire. Ne révèle jamais la valeur ou les
métadonnées sensibles des secrets.

### 5. Exécuter les validations sûres

- Valide la syntaxe et la sémantique avec les outils déjà disponibles, par
  exemple `actionlint`, ShellCheck, PSScriptAnalyzer, les tests Python/PowerShell
  de contrats et les builds/tests .NET les plus ciblés.
- N'installe pas silencieusement de nouvel outil global et ne contourne jamais
  un contrôle. Si un outil manque, signale-le et fournis la commande officielle
  reproductible à exécuter.
- Commence par les contrôles statiques et les tests étroits. N'exécute jamais
  semantic-release, une commande `publish`, un push de package/image, un
  déploiement, une connexion à un registre ou une commande destructive.
- Pour chaque commande, consigne le résultat utile. Un test non exécuté ou
  bloqué ne constitue ni un succès ni une non-conformité prouvée.

## Discipline des constats

Ne produis pas une checklist générique. Ne crée un constat que s'il existe une
preuve localisable ou une vérification distante explicite.

Classe chaque constat :

- **Critique** : chemin réaliste vers compromission, fuite de secret ou
  publication non autorisée ; bloque toute release.
- **Élevée** : contrôle essentiel absent ou contournable, artefact non fiable,
  CI pouvant accepter une source incorrecte ; à corriger avant la prochaine
  release.
- **Moyenne** : fiabilité, reproductibilité ou maintenabilité sensiblement
  dégradée ; à planifier rapidement.
- **Faible** : amélioration locale à impact limité.
- **Opportunité** : bonne pratique utile mais non exigée, avec valeur et coût
  explicités.

Pour chaque constat, fournis :

1. un identifiant stable (`CICD-001`, etc.), la sévérité et un titre factuel ;
2. le statut : `Confirmé`, `Probable` ou `Non vérifié` ;
3. la preuve exacte `chemin:ligne`, la commande ou la réponse API ;
4. la règle locale et/ou la source officielle applicable ;
5. l'impact concret ou un scénario d'échec réaliste ;
6. la correction minimale recommandée, sans patch si aucun changement n'a été
   demandé ;
7. la validation qui prouvera la correction ;
8. le niveau de confiance et les éventuels compromis de compatibilité.

Évite les doublons : lorsqu'une cause racine explique plusieurs occurrences,
crée un constat principal et liste les emplacements affectés. Ne présente pas
une préférence stylistique comme un défaut. Mentionne aussi les contrôles
positifs importants afin que le rapport ne suggère pas de retirer une défense
existante.

## Format du rapport final

Rédige le rapport en français, dans cet ordre :

1. **Verdict exécutif** — `Conforme`, `Conforme avec réserves` ou
   `Non conforme`, commit/périmètre audité, niveau de confiance et trois risques
   majeurs au maximum.
2. **Périmètre et preuves exécutées** — fichiers inspectés, commandes, tests,
   accès distant obtenu et limites.
3. **Graphe CI/CD synthétique** — flux de confiance et destinations de
   publication ; utilise Mermaid seulement si cela rend le flux plus clair.
4. **Constats priorisés** — tableau résumé, puis détails complets triés par
   sévérité et identifiant.
5. **Configuration distante non vérifiée** — uniquement les contrôles qui
   nécessitent encore un accès ou une confirmation.
6. **Contrôles satisfaisants** — défenses importantes effectivement prouvées.
7. **Améliorations possibles** — gains supplémentaires classés par
   valeur/effort, séparés des non-conformités.
8. **Plan d'action** — `Maintenant`, `Prochainement`, `Plus tard`, avec
   dépendances, risque de rupture pour les modules consommateurs et critère de
   sortie mesurable.
9. **Sources** — liens directs, titre, organisme, version/date pertinente et
   date de consultation.

Termine par les compteurs de constats par sévérité et par une liste explicite
des blockers de release. S'il n'existe aucun constat confirmé, dis-le clairement
mais conserve les limites et éléments non vérifiés ; l'absence de preuve d'un
défaut n'est pas une preuve de conformité totale.
