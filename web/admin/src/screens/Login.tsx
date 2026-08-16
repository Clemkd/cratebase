import { useState, type FormEvent } from 'react'
import { LogIn, Moon, Sun } from 'lucide-react'
import { api, describeFailure } from '../api'
import { useTheme } from '../hooks/useTheme'
import { AuthLayout } from '../layout/AuthLayout'
import { Button, ErrorBlock, Field, Input, Tooltip } from '../ui'

export function Login({ onAuthenticated }: { onAuthenticated: () => void }) {
  const [identity, setIdentity] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const { resolved, setPreference } = useTheme()

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.auth.login(identity, password)
      onAuthenticated()
    } catch (failure) {
      // The server doesn't distinguish "unknown account" from "wrong password", and the
      // interface must not reintroduce the distinction: it would tell an attacker which
      // addresses exist.
      setError(describeFailure(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthLayout
      title="Sign in"
      description="Admin console."
      footer="Reserved for superusers. The session expires when the tab closes."
      aside={
        <Tooltip content={resolved === 'dark' ? 'Switch to light' : 'Switch to dark'}>
          <Button
            variant="ghost"
            size="icon"
            aria-label={resolved === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            onClick={() => setPreference(resolved === 'dark' ? 'light' : 'dark')}
          >
            {resolved === 'dark' ? (
              <Sun size={16} aria-hidden="true" />
            ) : (
              <Moon size={16} aria-hidden="true" />
            )}
          </Button>
        </Tooltip>
      }
    >
      {error && (
        <div className="mb-4">
          <ErrorBlock message={error} />
        </div>
      )}

      <form onSubmit={submit} noValidate className="space-y-4">
        <Field label="Email address" required>
          <Input
            type="email"
            autoComplete="username"
            autoFocus
            required
            disabled={busy}
            value={identity}
            onChange={(event) => setIdentity(event.target.value)}
          />
        </Field>

        <Field label="Password" required>
          <Input
            type="password"
            autoComplete="current-password"
            required
            disabled={busy}
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </Field>

        <Button
          type="submit"
          variant="primary"
          className="w-full"
          icon={<LogIn size={16} aria-hidden="true" />}
          loading={busy}
          disabled={identity === '' || password === ''}
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    </AuthLayout>
  )
}
