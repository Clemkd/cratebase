import type { Collection } from '../api'
import { Button, Dialog } from '../ui'

interface Entry {
  syntax: string
  description: string
}

const OPERATORS: Entry[] = [
  { syntax: '=', description: 'égal' },
  { syntax: '!=', description: 'différent' },
  { syntax: '>  >=  <  <=', description: 'comparaison, y compris sur les dates' },
  { syntax: '~', description: 'contient — insensible à la casse' },
  { syntax: '!~', description: 'ne contient pas' },
  { syntax: '?=  ?~  ?>  …', description: "sur un champ multi-valué : « au moins un élément » au lieu de « tous »" },
  { syntax: '&&  ||', description: 'et logique, ou logique — parenthèses admises' },
  { syntax: '// commentaire', description: 'commentaire de fin de ligne' },
]

const MODIFIERS: Entry[] = [
  { syntax: ':isset', description: 'le champ a-t-il été soumis ? — réservé aux chemins @request.*' },
  { syntax: ':length', description: "nombre d'éléments d'un champ multi-valué" },
  { syntax: ':each', description: "la condition s'applique à chaque élément" },
  { syntax: ':lower', description: 'comparaison en minuscules' },
  { syntax: ':changed', description: 'le champ a-t-il été soumis et modifié ?' },
]

const REQUEST: Entry[] = [
  { syntax: '@request.auth.id', description: "identifiant de l'appelant — chaîne vide si anonyme" },
  { syntax: '@request.auth.collectionName', description: "collection d'authentification de l'appelant" },
  { syntax: '@request.auth.<champ>', description: "n'importe quel champ du compte connecté" },
  { syntax: '@request.method', description: 'verbe HTTP' },
  { syntax: '@request.context', description: 'default, oauth2, otp, password, realtime, protectedFile' },
  { syntax: '@request.body.<champ>', description: 'valeur soumise, après valeurs par défaut et crochets' },
  { syntax: '@request.query.<clé>', description: 'paramètre de requête' },
  { syntax: '@request.headers.x_forwarded_for', description: 'en-tête, en minuscules et tirets remplacés par des soulignés' },
]

const MACROS: Entry[] = [
  { syntax: '@now  @yesterday  @tomorrow', description: 'instants relatifs, normalisés en UTC' },
  { syntax: '@todayStart  @todayEnd', description: 'bornes du jour courant' },
  { syntax: '@monthStart  @monthEnd', description: 'bornes du mois courant' },
  { syntax: '@yearStart  @yearEnd', description: 'bornes de l’année courante' },
  { syntax: '@second @minute @hour @day @month @year', description: 'composantes numériques de l’instant courant' },
]

function Section({ title, entries }: { title: string; entries: Entry[] }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-semibold tracking-wide text-ink-muted uppercase">{title}</h3>
      <dl className="divide-y divide-border-subtle rounded-[var(--radius-card)] border border-border-subtle">
        {entries.map((entry) => (
          <div key={entry.syntax} className="flex flex-col gap-1 px-4 py-2.5 sm:flex-row sm:gap-4">
            <dt className="shrink-0 font-mono text-xs text-brand sm:w-56">{entry.syntax}</dt>
            <dd className="text-xs text-ink-muted">{entry.description}</dd>
          </div>
        ))}
      </dl>
    </section>
  )
}

/** Aide contextuelle sur la syntaxe de filtre, avec des exemples bâtis sur la collection ouverte. */
export function FilterHelp({
  open,
  collection,
  onClose,
  onUseExample,
}: {
  open: boolean
  collection: Collection
  onClose: () => void
  onUseExample: (expression: string) => void
}) {
  const sample = collection.fields.find((field) => !field.hidden && !field.isSystem)
  const textual = collection.fields.find(
    (field) => !field.hidden && !field.isSystem && (field.type === 'Text' || field.type === 'Email'),
  )

  const examples = [
    `created >= @todayStart`,
    textual ? `${textual.name} ~ 'exemple'` : `id != ''`,
    sample?.multiple ? `${sample.name}:length > 0` : `created < @monthStart && created >= @yearStart`,
    `@request.auth.id != ''`,
  ]

  return (
    <Dialog
      open={open}
      onClose={onClose}
      width="lg"
      title="Syntaxe des filtres"
      description="Le même langage sert au paramètre ?filter= et aux règles d'accès."
      footer={
        <Button variant="outline" onClick={onClose}>
          Fermer
        </Button>
      }
    >
      <div className="space-y-6">
        <section>
          <h3 className="mb-2 text-xs font-semibold tracking-wide text-ink-muted uppercase">
            Exemples — cliquer pour appliquer
          </h3>
          <div className="flex flex-wrap gap-2">
            {examples.map((example) => (
              <button
                key={example}
                type="button"
                onClick={() => {
                  onUseExample(example)
                  onClose()
                }}
                className="rounded-[var(--radius-control)] border border-border-subtle bg-surface-sunken px-2.5 py-1.5 font-mono text-xs text-ink transition-colors hover:border-brand hover:text-brand"
              >
                {example}
              </button>
            ))}
          </div>
        </section>

        <Section title="Opérateurs" entries={OPERATORS} />
        <Section title="Modificateurs de chemin" entries={MODIFIERS} />
        <Section title="Méta-champs de requête" entries={REQUEST} />
        <Section title="Macros de date" entries={MACROS} />

        <p className="text-xs text-ink-muted">
          Un identifiant absent du schéma est refusé par une erreur 400, jamais interpolé : c'est la
          frontière d'injection du moteur. Les macros sont résolues côté application et deviennent
          des paramètres, donc elles rendent la même valeur sur SQLite et sur PostgreSQL.
        </p>
      </div>
    </Dialog>
  )
}
