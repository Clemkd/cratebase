import { useEffect, useState } from 'react'
import { KeyRound, Plus, RotateCw, ShieldCheck, Trash2, UserPlus, Users } from 'lucide-react'
import { api, describeFailure, type Collection, type RecordValue } from '../api'
import { useRecords } from '../hooks/useRecords'
import { asStringList } from '../lib/fields'
import { formatCount, formatDateTime, plural } from '../lib/format'
import {
  Badge,
  Button,
  Card,
  Checkbox,
  ConfirmDialog,
  Dialog,
  EmptyState,
  ErrorBlock,
  Field,
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
import { StringListInput } from './FieldControl'
import { RecordEditor } from './RecordEditor'

interface Role {
  name: string
  grants: string[]
}

/**
 * Comptes d'une collection d'authentification.
 *
 * L'attribution des droits passe par `POST .../grants`, jamais par un PATCH ordinaire : rôles et
 * permissions sont des champs système, refusés dans un corps de requête. C'est ce qui empêche un
 * compte de s'accorder ses propres droits dès que la règle de modification est un peu large.
 *
 * D'où deux chemins d'édition bien séparés : le panneau d'enregistrement pour ce qui appartient au
 * compte, une modale de droits pour ce qui appartient au moteur d'autorisation.
 */
export function AccountsBrowser({
  collection,
  collections,
}: {
  collection: Collection
  collections: Collection[]
}) {
  const toast = useToast()

  const [page, setPage] = useState(1)
  const [roles, setRoles] = useState<Role[]>([])
  const [granting, setGranting] = useState<RecordValue | null>(null)
  const [creating, setCreating] = useState(false)
  const [removing, setRemoving] = useState<RecordValue | null>(null)
  const [deleting, setDeleting] = useState(false)

  const { result, loading, error, reload } = useRecords(collection.name, {
    page,
    perPage: 25,
    filter: '',
    sort: '-created',
  })

  useEffect(() => {
    let abandoned = false

    api.records
      .list(api.auth.roles, { perPage: 200, skipTotal: true })
      .then((list) => {
        if (abandoned) return

        setRoles(
          list.items.map((item) => ({
            name: String(item.name ?? ''),
            grants: asStringList(item.grants),
          })),
        )
      })
      .catch(() => {
        // Le catalogue de rôles n'est qu'une aide à la saisie : sans lui, les permissions restent
        // attribuables une à une.
      })

    return () => {
      abandoned = true
    }
  }, [])

  const remove = async () => {
    if (!removing) return

    setDeleting(true)

    try {
      await api.records.remove(collection.name, String(removing.id))
      toast.success('Compte supprimé.')
      await reload()
    } catch (failure) {
      toast.error(describeFailure(failure))
    } finally {
      setDeleting(false)
      setRemoving(null)
    }
  }

  const items = result?.items ?? []

  return (
    <div className="space-y-4">
      <Card className="flex flex-wrap items-center gap-2 px-4 py-3">
        <p className="text-xs text-ink-muted">
          {result
            ? `${formatCount(result.totalItems)} ${plural(result.totalItems, 'compte', 'comptes')}`
            : 'Chargement…'}
        </p>

        <div className="ml-auto flex items-center gap-2">
          <Tooltip content="Recharger">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Recharger la liste"
              onClick={() => void reload()}
            >
              <RotateCw size={16} aria-hidden="true" />
            </Button>
          </Tooltip>

          <Button
            size="sm"
            variant="primary"
            icon={<UserPlus size={15} aria-hidden="true" />}
            onClick={() => setCreating(true)}
          >
            Nouveau compte
          </Button>
        </div>
      </Card>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={5} />}

      {!loading && !error && items.length === 0 && (
        <EmptyState
          icon={<Users size={28} aria-hidden="true" />}
          title="Aucun compte"
          description="Créez un compte, ou laissez vos utilisateurs s'inscrire si la règle de création le permet."
          action={
            <Button
              variant="primary"
              icon={<Plus size={15} aria-hidden="true" />}
              onClick={() => setCreating(true)}
            >
              Créer un compte
            </Button>
          }
        />
      )}

      {items.length > 0 && (
        <Card className="overflow-hidden">
          <Table bare caption={`Comptes de la collection ${collection.name}`}>
            <THead>
              <tr>
                <Th>Adresse</Th>
                <Th>Vérifié</Th>
                <Th>Rôles</Th>
                <Th>Permissions</Th>
                <Th>Créé le</Th>
                <Th className="w-24">
                  <span className="sr-only">Actions</span>
                </Th>
              </tr>
            </THead>

            <TBody>
              {items.map((account) => {
                const accountRoles = asStringList(account.roles)
                const accountPermissions = asStringList(account.permissions)

                return (
                  <Tr key={String(account.id)}>
                    <Td>
                      <span className="font-medium">{String(account.email ?? '—')}</span>
                      <span className="block font-mono text-xs text-ink-faint">
                        {String(account.id ?? '')}
                      </span>
                    </Td>

                    <Td>
                      <Badge tone={account.verified === true ? 'success' : 'neutral'} dot>
                        {account.verified === true ? 'oui' : 'non'}
                      </Badge>
                    </Td>

                    <Td>
                      {accountRoles.length === 0 ? (
                        <span className="text-ink-faint">—</span>
                      ) : (
                        <span className="flex flex-wrap gap-1">
                          {accountRoles.map((role) => (
                            <Badge key={role} tone="brand">
                              {role}
                            </Badge>
                          ))}
                        </span>
                      )}
                    </Td>

                    <Td>
                      {accountPermissions.length === 0 ? (
                        <span className="text-ink-faint">—</span>
                      ) : (
                        <span className="flex flex-wrap gap-1">
                          {accountPermissions.slice(0, 3).map((permission) => (
                            <Badge key={permission} mono>
                              {permission}
                            </Badge>
                          ))}
                          {accountPermissions.length > 3 && (
                            <span className="text-xs text-ink-muted">
                              +{accountPermissions.length - 3}
                            </span>
                          )}
                        </span>
                      )}
                    </Td>

                    <Td className="whitespace-nowrap text-ink-muted">
                      {formatDateTime(account.created)}
                    </Td>

                    <Td>
                      <div className="flex justify-end gap-1">
                        <Tooltip content="Rôles et permissions">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8"
                            aria-label={`Modifier les droits de ${String(account.email ?? account.id)}`}
                            onClick={() => setGranting(account)}
                          >
                            <ShieldCheck size={15} aria-hidden="true" />
                          </Button>
                        </Tooltip>

                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-8 hover:text-danger"
                          aria-label={`Supprimer ${String(account.email ?? account.id)}`}
                          onClick={() => setRemoving(account)}
                        >
                          <Trash2 size={15} aria-hidden="true" />
                        </Button>
                      </div>
                    </Td>
                  </Tr>
                )
              })}
            </TBody>
          </Table>

          {result && result.totalPages > 1 && (
            <div className="flex items-center justify-end gap-2 border-t border-border-subtle bg-surface-sunken px-4 py-2.5 text-xs text-ink-muted">
              <span className="tabular-nums">
                page {result.page} / {result.totalPages}
              </span>
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
          )}
        </Card>
      )}

      {granting && (
        <GrantsDialog
          collection={collection}
          account={granting}
          roles={roles}
          onClose={() => setGranting(null)}
          onSaved={() => {
            setGranting(null)
            void reload()
          }}
        />
      )}

      {creating && (
        <RecordEditor
          open
          collection={collection}
          collections={collections}
          record={null}
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false)
            void reload()
          }}
        />
      )}

      <ConfirmDialog
        open={removing !== null}
        busy={deleting}
        title="Supprimer ce compte ?"
        message={
          <>
            Le compte <strong className="text-ink">{String(removing?.email ?? '')}</strong> et ses
            sessions seront détruits. L'opération est définitive.
          </>
        }
        confirmLabel="Supprimer le compte"
        onConfirm={() => void remove()}
        onClose={() => setRemoving(null)}
      />
    </div>
  )
}

function GrantsDialog({
  collection,
  account,
  roles,
  onClose,
  onSaved,
}: {
  collection: Collection
  account: RecordValue
  roles: Role[]
  onClose: () => void
  onSaved: () => void
}) {
  const toast = useToast()

  const [selectedRoles, setSelectedRoles] = useState<string[]>(() => asStringList(account.roles))
  const [permissions, setPermissions] = useState<string[]>(() => asStringList(account.permissions))
  const [failure, setFailure] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const inherited = roles
    .filter((role) => selectedRoles.includes(role.name))
    .flatMap((role) => role.grants)

  const save = async () => {
    setSaving(true)
    setFailure(null)

    try {
      await api.auth.grant(collection.name, String(account.id), selectedRoles, permissions)
      toast.success('Droits enregistrés. Les sessions ouvertes du compte ont été révoquées.')
      onSaved()
    } catch (error) {
      setFailure(describeFailure(error))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Dialog
      open
      onClose={onClose}
      title="Rôles et permissions"
      description={String(account.email ?? account.id)}
      footer={
        <>
          <Button variant="outline" onClick={onClose} disabled={saving}>
            Annuler
          </Button>
          <Button variant="primary" onClick={() => void save()} loading={saving}>
            Enregistrer les droits
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        {failure && <ErrorBlock message={failure} />}

        <Field label="Rôles" hint="Chaque rôle apporte les permissions qui lui sont attachées.">
          {roles.length === 0 ? (
            <p className="text-xs text-ink-muted">
              Aucun rôle déclaré. La collection <code className="font-mono">_roles</code> est vide.
            </p>
          ) : (
            <div className="space-y-2 rounded-[var(--radius-control)] border border-border-subtle p-3">
              {roles.map((role) => (
                <Checkbox
                  key={role.name}
                  label={role.name}
                  hint={role.grants.join(', ') || 'aucune permission'}
                  checked={selectedRoles.includes(role.name)}
                  onChange={(event) =>
                    setSelectedRoles((current) =>
                      event.target.checked
                        ? [...current, role.name]
                        : current.filter((item) => item !== role.name),
                    )
                  }
                />
              ))}
            </div>
          )}
        </Field>

        <Field
          label="Dérogations individuelles"
          hint="Permissions accordées à ce compte seul, en plus de celles de ses rôles. Le joker « * » est admis."
        >
          <StringListInput
            value={permissions}
            onChange={setPermissions}
            placeholder="ex. : posts.write puis Entrée"
          />
        </Field>

        {inherited.length > 0 && (
          <div className="rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken p-4">
            <p className="mb-2 flex items-center gap-1.5 text-xs font-semibold text-ink">
              <KeyRound size={13} aria-hidden="true" />
              Permissions héritées des rôles
            </p>
            <div className="flex flex-wrap gap-1">
              {[...new Set(inherited)].map((permission) => (
                <Badge key={permission} mono>
                  {permission}
                </Badge>
              ))}
            </div>
          </div>
        )}

        <p className="text-xs text-ink-muted">
          Enregistrer révoque les jetons du compte : un droit retiré cesse d'être utilisable
          immédiatement, y compris sur une session déjà ouverte.
        </p>
      </div>
    </Dialog>
  )
}
