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

/** Minimum length enforced by the engine. Repeated here to announce it before the failure. */
const MIN_PASSWORD = 8

interface PasswordFormProps {
  title: string
  description: string
  submitLabel: string
  withEmail: boolean
  busy: boolean
  errors: Record<string, string[]>
  /**
   * ⚠️ The address is **absent** from the payload when the form doesn't ask for it.
   *
   * Sending it empty would amount to asking the engine to clear the account's address: it
   * refuses, and a perfectly valid password change fails with "address is required".
   */
  onSubmit: (values: { email?: string; password: string; password_confirm: string }) => void
  onClose: () => void
}

/**
 * Password form, shared by creation and changing.
 *
 * The confirmation is submitted to the server rather than checked only here: it's the engine
 * that owns the rule, and two diverging checks would eventually accept on one side what the
 * other refuses. The local comparison only serves to avoid a round trip.
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
            Cancel
          </Button>
          {/* `form` links the button to the form even though it's rendered in the modal's
              footer, outside of it: that's what makes "Enter" in a field and clicking the
              button take the same validation path. */}
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
          <Field label="Email address" error={first('email')} required>
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
          label="Password"
          error={first('password')}
          hint={`At least ${MIN_PASSWORD} characters.`}
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
          error={mismatch ? "The confirmation doesn't match the password." : first('password_confirm')}
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
 * Superuser accounts.
 *
 * A screen distinct from the ordinary account browser, not a special case of it: a superuser
 * **bypasses every access rule**, so their roles and permissions decide nothing. Displaying them
 * — and worse, making them editable — would suggest a rights setting that doesn't exist at this
 * level.
 */
export function Superusers({
  collection,
  identity,
  onSignedOut,
}: {
  collection: Collection
  identity: Identity
  /** Called when the admin changes their own password: their session has just dropped. */
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
      toast.success('Superuser created.')
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
        toast.success('Password changed. This account\'s sessions are closed.')
        await reload()
        return
      }

      // Changing your own password revokes your own tokens: the console holds a session that's
      // no longer valid. It's closed right away rather than waiting for the first 401, which
      // would land in the middle of another screen with nothing explaining why.
      toast.success('Password changed. Please sign in again.')

      try {
        await api.auth.logout()
      } catch {
        // The token is already revoked server-side: the failure is the expected outcome.
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
      toast.success('Superuser deleted.')
      setRemoving(null)
      await reload()
    } catch (failure) {
      // The refusal to delete the last account surfaces here as a 409: the engine's message
      // says why, and it's better than any local rewording.
      toast.error(describeFailure(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <PageActions>
        <Tooltip content="Reload">
          <Button
            variant="ghost"
            size="icon"
            aria-label="Reload list"
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
          New superuser
        </Button>
      </PageActions>

      <div className="flex items-start gap-2.5 rounded-[var(--radius-card)] border border-border-subtle bg-surface-sunken px-4 py-3">
        <ShieldCheck size={16} aria-hidden="true" className="mt-0.5 shrink-0 text-brand" />
        <p className="text-xs text-ink-muted">
          A superuser bypasses <strong className="text-ink">every</strong> access rule: neither
          role nor permission applies to them. The last account can't be deleted — without it, no
          one could administer the instance anymore.
        </p>
      </div>

      {error && <ErrorBlock message={error} onRetry={() => void reload()} />}

      {loading && result === null && <TableSkeleton columns={4} />}

      {items.length > 0 && (
        <Card className="overflow-hidden">
          <Table bare caption="Superuser accounts">
            <THead>
              <tr>
                <Th>Address</Th>
                <Th className="w-40">Created</Th>
                <Th className="w-40">Updated</Th>
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
                        {/* Marking your own account avoids the costliest mistake on this
                            screen: deleting yourself, or signing out without meaning to. */}
                        {mine && <Badge tone="brand">your account</Badge>}
                      </span>
                      <span className="block font-mono text-xs text-ink-faint">{id}</span>
                    </Td>

                    <Td className="text-xs text-ink-muted">{formatDateTime(account.created)}</Td>
                    <Td className="text-xs text-ink-muted">{formatDateTime(account.updated)}</Td>

                    <Td>
                      <span className="flex items-center gap-1">
                        <Tooltip content="Change password">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8"
                            aria-label={`Change password for ${String(account.email ?? id)}`}
                            onClick={() => {
                              setErrors({})
                              setChanging(account)
                            }}
                          >
                            <KeyRound size={15} aria-hidden="true" />
                          </Button>
                        </Tooltip>

                        <Tooltip content="Delete">
                          <Button
                            variant="ghost"
                            size="icon"
                            className="size-8 hover:text-danger"
                            aria-label={`Delete ${String(account.email ?? id)}`}
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
                {formatCount(result.totalItems)} {plural(result.totalItems, 'account', 'accounts')}
              </span>

              <div className="flex items-center gap-2">
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
            </div>
          )}
        </Card>
      )}

      {creating && (
        <PasswordForm
          withEmail
          title="New superuser"
          description="The account can administer the whole instance as soon as it's created."
          submitLabel="Create account"
          busy={busy}
          errors={errors}
          onSubmit={(values) => void create(values)}
          onClose={() => setCreating(false)}
        />
      )}

      {changing && (
        <PasswordForm
          withEmail={false}
          title="Change password"
          description={
            String(changing.id) === identity.id
              ? 'This is your account: your sessions will be closed and you\'ll need to sign in again.'
              : `The open sessions of ${String(changing.email ?? '')} will be closed.`
          }
          submitLabel="Change password"
          busy={busy}
          errors={errors}
          onSubmit={(values) => void changePassword(changing, values)}
          onClose={() => setChanging(null)}
        />
      )}

      <ConfirmDialog
        open={removing !== null}
        busy={busy}
        title="Delete this superuser?"
        message={
          <>
            The account <strong className="text-ink">{String(removing?.email ?? '')}</strong> and
            its sessions will be destroyed. This operation is permanent.
            {String(removing?.id ?? '') === identity.id && (
              <strong className="mt-2 block text-danger">
                This is the account you're currently signed in with.
              </strong>
            )}
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
