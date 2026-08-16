import { useEffect, useState } from 'react'
import { KeyRound, Plus, RotateCw, ShieldCheck, Trash2, UserPlus, Users, X } from 'lucide-react'
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
 * Accounts of an authentication collection.
 *
 * Granting rights goes through `POST .../grants`, never an ordinary PATCH: roles and permissions
 * are system fields, rejected in a request body. That's what stops an account from granting
 * itself its own rights the moment the update rule is even slightly permissive.
 *
 * Hence two clearly separate editing paths: the record panel for what belongs to the account, a
 * rights modal for what belongs to the authorization engine.
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
        // The role catalog is only an input aid: without it, permissions remain assignable one
        // by one.
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
      toast.success('Account deleted.')
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
            ? `${formatCount(result.totalItems)} ${plural(result.totalItems, 'account', 'accounts')}`
            : 'Loading…'}
        </p>

        <div className="ml-auto flex items-center gap-2">
          <Tooltip content="Reload">
            <Button
              variant="ghost"
              size="icon"
              className="size-8"
              aria-label="Reload list"
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
            New account
          </Button>
        </div>
      </Card>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={5} />}

      {!loading && !error && items.length === 0 && (
        <EmptyState
          icon={<Users size={28} aria-hidden="true" />}
          title="No accounts"
          description="Create an account, or let your users sign up if the create rule allows it."
          action={
            <Button
              variant="primary"
              icon={<Plus size={15} aria-hidden="true" />}
              onClick={() => setCreating(true)}
            >
              Create an account
            </Button>
          }
        />
      )}

      {items.length > 0 && (
        <Card className="overflow-hidden">
          <Table bare caption={`Accounts in the ${collection.name} collection`}>
            <THead>
              <tr>
                <Th>Address</Th>
                <Th>Verified</Th>
                <Th>Roles</Th>
                <Th>Permissions</Th>
                <Th>Created</Th>
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
                        {account.verified === true ? 'yes' : 'no'}
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
                        <Tooltip content="Roles and permissions">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8"
                            aria-label={`Edit rights for ${String(account.email ?? account.id)}`}
                            onClick={() => setGranting(account)}
                          >
                            <ShieldCheck size={15} aria-hidden="true" />
                          </Button>
                        </Tooltip>

                        <Button
                          variant="ghost"
                          size="icon"
                          className="size-8 hover:text-danger"
                          aria-label={`Delete ${String(account.email ?? account.id)}`}
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
                Previous
              </Button>
              <Button
                size="sm"
                disabled={result.page >= result.totalPages}
                onClick={() => setPage(result.page + 1)}
              >
                Next
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
        title="Delete this account?"
        message={
          <>
            The account <strong className="text-ink">{String(removing?.email ?? '')}</strong> and
            its sessions will be destroyed. This operation is permanent.
          </>
        }
        confirmLabel="Delete account"
        confirmIcon={<Trash2 size={15} aria-hidden="true" />}
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
      toast.success('Rights saved. The account\'s open sessions have been revoked.')
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
      title="Roles and permissions"
      description={String(account.email ?? account.id)}
      footer={
        <>
          <Button
            variant="outline"
            icon={<X size={15} aria-hidden="true" />}
            onClick={onClose}
            disabled={saving}
          >
            Cancel
          </Button>
          <Button
            variant="primary"
            icon={<ShieldCheck size={15} aria-hidden="true" />}
            onClick={() => void save()}
            loading={saving}
          >
            Save rights
          </Button>
        </>
      }
    >
      <div className="space-y-5">
        {failure && <ErrorBlock message={failure} />}

        <Field label="Roles" hint="Each role brings the permissions attached to it.">
          {roles.length === 0 ? (
            <p className="text-xs text-ink-muted">
              No roles declared. The <code className="font-mono">_roles</code> collection is empty.
            </p>
          ) : (
            <div className="space-y-2 rounded-[var(--radius-control)] border border-border-subtle p-3">
              {roles.map((role) => (
                <Checkbox
                  key={role.name}
                  label={role.name}
                  hint={role.grants.join(', ') || 'no permissions'}
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
          label="Individual overrides"
          hint="Permissions granted to this account alone, in addition to those from its roles. The '*' wildcard is allowed."
        >
          <StringListInput
            value={permissions}
            onChange={setPermissions}
            placeholder="e.g. posts.write then Enter"
          />
        </Field>

        {inherited.length > 0 && (
          <div className="rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken p-4">
            <p className="mb-2 flex items-center gap-1.5 text-xs font-semibold text-ink">
              <KeyRound size={13} aria-hidden="true" />
              Permissions inherited from roles
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
          Saving revokes the account's tokens: a removed right stops being usable immediately,
          including on a session that's already open.
        </p>
      </div>
    </Dialog>
  )
}
