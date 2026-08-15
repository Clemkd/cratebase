import { useId, useState, type FormEvent } from 'react'
import { KeyRound, RotateCw, ShieldCheck, Trash2, UserPlus, X } from 'lucide-react'
import {
  api,
  describeFailure,
  session,
  validationErrors,
  type Collection,
  type Identity,
  type RecordValue,
} from '../api'
import { useRecords } from '../hooks/useRecords'
import { formatCount, formatDateTime, plural } from '../lib/format'
import { PageActions } from '../layout/Page'
import {
  Badge,
  Button,
  Card,
  ConfirmDialog,
  Dialog,
  ErrorBlock,
  Field,
  Input,
  TBody,
  THead,
  Table,
  TableSkeleton,
  Td,
  Th,
  Tooltip,
  Tr,
  useToast,
} from '../ui'

/** Longueur minimale imposée par le moteur. Répétée ici pour l'annoncer avant l'échec. */
const MIN_PASSWORD = 8

interface PasswordFormProps {
  title: string
  description: string
  submitLabel: string
  withEmail: boolean
  busy: boolean
  errors: Record<string, string[]>
  /**
   * ⚠️ L'adresse est **absente** de la charge quand le formulaire ne la demande pas.
   *
   * L'envoyer vide reviendrait à demander au moteur d'effacer l'adresse du compte : il refuse, et
   * un changement de mot de passe parfaitement valide échoue en « l'adresse est obligatoire ».
   */
  onSubmit: (values: { email?: string; password: string; password_confirm: string }) => void
  onClose: () => void
}

/**
 * Formulaire de mot de passe, partagé par la création et le changement.
 *
 * La confirmation est soumise au serveur plutôt que vérifiée ici seulement : c'est le moteur qui
 * porte la règle, et deux vérifications divergentes finiraient par accepter d'un côté ce que
 * l'autre refuse. La comparaison locale ne sert qu'à éviter un aller-retour.
 */
function PasswordForm({
  title,
  description,
  submitLabel,
  withEmail,
  busy,
  errors,
  onSubmit,
  onClose,
}: PasswordFormProps) {
  const formId = useId()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')

  const mismatch = confirm !== '' && confirm !== password
  const incomplete = password.length < MIN_PASSWORD || (withEmail && email.trim() === '')

  const submit = (event: FormEvent) => {
    event.preventDefault()

    if (mismatch || incomplete) return

    onSubmit(
      withEmail
        ? { email: email.trim(), password, password_confirm: confirm }
        : { password, password_confirm: confirm },
    )
  }

  const first = (key: string) => errors[key]?.[0]

  return (
    <Dialog
      open
      onClose={onClose}
      width="sm"
      title={title}
      description={description}
      footer={
        <>
          <Button
            variant="outline"
            icon={<X size={15} aria-hidden="true" />}
            onClick={onClose}
            disabled={busy}
          >
            Annuler
          </Button>
          {/* `form` relie le bouton au formulaire alors qu'il est rendu dans le pied de la modale,
              hors de celui-ci : c'est ce qui fait qu'« Entrée » dans un champ et le clic sur le
              bouton empruntent le même chemin de validation. */}
          <Button
            type="submit"
            form={formId}
            variant="primary"
            icon={
              withEmail ? (
                <UserPlus size={15} aria-hidden="true" />
              ) : (
                <KeyRound size={15} aria-hidden="true" />
              )
            }
            loading={busy}
            disabled={mismatch || incomplete}
          >
            {submitLabel}
          </Button>
        </>
      }
    >
      <form id={formId} onSubmit={submit} className="space-y-4">
        {withEmail && (
          <Field label="Adresse de courriel" error={first('email')} required>
            <Input
              type="email"
              value={email}
              autoComplete="off"
              spellCheck={false}
              onChange={(event) => setEmail(event.target.value)}
            />
          </Field>
        )}

        <Field
          label="Mot de passe"
          error={first('password')}
          hint={`${MIN_PASSWORD} caractères au minimum.`}
          required
        >
          <Input
            type="password"
            value={password}
            autoComplete="new-password"
            onChange={(event) => setPassword(event.target.value)}
          />
        </Field>

        <Field
          label="Confirmation"
          error={mismatch ? 'La confirmation ne correspond pas au mot de passe.' : first('password_confirm')}
          required
        >
          <Input
            type="password"
            value={confirm}
            autoComplete="new-password"
            onChange={(event) => setConfirm(event.target.value)}
          />
        </Field>

      </form>
    </Dialog>
  )
}

/**
 * Comptes super-admins.
 *
 * Écran distinct du navigateur de comptes ordinaire, et pas un cas particulier de celui-ci : un
 * super-admin **passe outre toutes les règles d'accès**, donc ses rôles et ses permissions
 * ne décident de rien. Les afficher — et pire, les rendre modifiables — laisserait croire à un
 * réglage de droits qui n'existe pas à ce niveau.
 */
export function Superusers({
  collection,
  identity,
  onSignedOut,
}: {
  collection: Collection
  identity: Identity
  /** Appelé quand l'administrateur change son propre mot de passe : sa session vient de tomber. */
  onSignedOut: () => void
}) {
  const toast = useToast()

  const [page, setPage] = useState(1)
  const [creating, setCreating] = useState(false)
  const [changing, setChanging] = useState<RecordValue | null>(null)
  const [removing, setRemoving] = useState<RecordValue | null>(null)
  const [busy, setBusy] = useState(false)
  const [errors, setErrors] = useState<Record<string, string[]>>({})

  const { result, loading, error, reload } = useRecords(collection.name, {
    page,
    perPage: 25,
    filter: '',
    sort: '-created',
  })

  const items = result?.items ?? []

  const create = async (values: { email?: string; password: string; password_confirm: string }) => {
    setBusy(true)
    setErrors({})

    try {
      await api.records.create(collection.name, values)
      toast.success('Super-admin créé.')
      setCreating(false)
      await reload()
    } catch (failure) {
      setErrors(validationErrors(failure))
      toast.error(describeFailure(failure))
    } finally {
      setBusy(false)
    }
  }

  const changePassword = async (
    account: RecordValue,
    values: { email?: string; password: string; password_confirm: string },
  ) => {
    setBusy(true)
    setErrors({})

    const mine = String(account.id) === identity.id

    try {
      await api.records.update(collection.name, String(account.id), values)
      setChanging(null)

      if (!mine) {
        toast.success('Mot de passe changé. Les sessions de ce compte sont fermées.')
        await reload()
        return
      }

      // Changer son propre mot de passe révoque ses propres jetons : la console tient une session
      // qui ne vaut plus rien. On la referme tout de suite plutôt que d'attendre le premier 401,
      // qui tomberait au milieu d'un autre écran sans que rien n'explique pourquoi.
      toast.success('Mot de passe changé. Reconnectez-vous.')

      try {
        await api.auth.logout()
      } catch {
        // Le jeton est déjà révoqué côté serveur : l'échec est le résultat attendu.
      }

      session.token = null
      onSignedOut()
    } catch (failure) {
      setErrors(validationErrors(failure))
      toast.error(describeFailure(failure))
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!removing) return

    setBusy(true)

    try {
      await api.records.remove(collection.name, String(removing.id))
      toast.success('Super-admin supprimé.')
      setRemoving(null)
      await reload()
    } catch (failure) {
      // Le refus du dernier compte remonte ici en 409 : le message du moteur dit pourquoi, et il
      // vaut mieux que n'importe quelle reformulation locale.
      toast.error(describeFailure(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Recharger">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Recharger la liste"
            onClick={() => void reload()}
          >
            <RotateCw size={16} aria-hidden="true" />
          </Button>
        </Tooltip>

        <Button
          variant="primary"
          icon={<UserPlus size={15} aria-hidden="true" />}
          onClick={() => {
            setErrors({})
            setCreating(true)
          }}
        >
          Nouveau super-admin
        </Button>
      </PageActions>

      <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3">
        <ShieldCheck size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-brand" />
        <p className="text-xs text-ink-muted">
          Un super-admin passe outre <strong className="text-ink">toutes</strong> les règles
          d'accès : ni rôle ni permission ne s'applique à lui. Le dernier compte ne peut pas être
          supprimé — sans lui, plus personne ne pourrait administrer l'instance.
        </p>
      </div>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={4} />}

      {items.length > 0 && (
        <Card className="overflow-hidden">
          <Table bare caption="Comptes super-admins">
            <THead>
              <tr>
                <Th>Adresse</Th>
                <Th className="w-40">Créé le</Th>
                <Th className="w-40">Modifié le</Th>
                <Th className="w-28">
                  <span className="sr-only">Actions</span>
                </Th>
              </tr>
            </THead>

            <TBody>
              {items.map((account) => {
                const id = String(account.id ?? '')
                const mine = id === identity.id

                return (
                  <Tr key={id}>
                    <Td>
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="font-medium text-ink">{String(account.email ?? '—')}</span>
                        {/* Repérer son propre compte évite la fausse manœuvre la plus coûteuse de
                            cet écran : se supprimer, ou se déconnecter sans l'avoir voulu. */}
                        {mine && <Badge tone="brand">votre compte</Badge>}
                      </span>
                      <span className="block font-mono text-xs text-ink-faint">{id}</span>
                    </Td>

                    <Td className="text-xs text-ink-muted">{formatDateTime(account.created)}</Td>
                    <Td className="text-xs text-ink-muted">{formatDateTime(account.updated)}</Td>

                    <Td>
                      <span className="flex items-center gap-1">
                        <Tooltip content="Changer le mot de passe">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8"
                            aria-label={`Changer le mot de passe de ${String(account.email ?? id)}`}
                            onClick={() => {
                              setErrors({})
                              setChanging(account)
                            }}
                          >
                            <KeyRound size={15} aria-hidden="true" />
                          </Button>
                        </Tooltip>

                        <Tooltip content="Supprimer">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8 hover:text-danger"
                            aria-label={`Supprimer ${String(account.email ?? id)}`}
                            onClick={() => setRemoving(account)}
                          >
                            <Trash2 size={15} aria-hidden="true" />
                          </Button>
                        </Tooltip>
                      </span>
                    </Td>
                  </Tr>
                )
              })}
            </TBody>
          </Table>

          {result && result.totalPages > 1 && (
            <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border-subtle bg-surface-sunken px-4 py-2.5 text-xs text-ink-muted">
              <span className="tabular-nums">
                {formatCount(result.totalItems)} {plural(result.totalItems, 'compte', 'comptes')}
              </span>

              <div className="flex items-center gap-2">
                <Button size="sm" disabled={result.page <= 1} onClick={() => setPage(result.page - 1)}>
                  Précédent
                </Button>
                <Button
                  size="sm"
                  disabled={result.page >= result.totalPages}
                  onClick={() => setPage(result.page + 1)}
                >
                  Suivant
                </Button>
              </div>
            </div>
          )}
        </Card>
      )}

      {creating && (
        <PasswordForm
          withEmail
          title="Nouveau super-admin"
          description="Le compte peut administrer toute l'instance dès sa création."
          submitLabel="Créer le compte"
          busy={busy}
          errors={errors}
          onSubmit={(values) => void create(values)}
          onClose={() => setCreating(false)}
        />
      )}

      {changing && (
        <PasswordForm
          withEmail={false}
          title="Changer le mot de passe"
          description={
            String(changing.id) === identity.id
              ? "C'est votre compte : vos sessions seront fermées et vous devrez vous reconnecter."
              : `Les sessions ouvertes de ${String(changing.email ?? '')} seront fermées.`
          }
          submitLabel="Changer le mot de passe"
          busy={busy}
          errors={errors}
          onSubmit={(values) => void changePassword(changing, values)}
          onClose={() => setChanging(null)}
        />
      )}

      <ConfirmDialog
        open={removing !== null}
        busy={busy}
        title="Supprimer ce super-admin ?"
        message={
          <>
            Le compte <strong className="text-ink">{String(removing?.email ?? '')}</strong> et ses
            sessions seront détruits. L'opération est définitive.
            {String(removing?.id ?? '') === identity.id && (
              <strong className="mt-2 block text-danger">
                C'est le compte avec lequel vous êtes connecté.
              </strong>
            )}
          </>
        }
        confirmLabel="Supprimer le compte"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
        onConfirm={() => void remove()}
        onClose={() => setRemoving(null)}
      />
    </div>
  )
}
