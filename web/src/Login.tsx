import { useState } from 'react'
import { describeAuthError, registerPasskey, signIn, type AuthStatus } from './auth'

export default function Login({ status, onDone }: { status: AuthStatus; onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [token, setToken] = useState('')
  const [name, setName] = useState('')

  const run = async (fn: () => Promise<void>) => {
    setBusy(true); setError(null)
    try { await fn(); onDone() } catch (e) { setError(describeAuthError(e)) } finally { setBusy(false) }
  }

  const supported = typeof window.PublicKeyCredential !== 'undefined'

  return (
    <div className="login">
      <div className="login-card">
        <h1>pad</h1>
        {!supported && <p className="error-note">This browser doesn't support passkeys.</p>}

        {supported && status.registered && (
          <>
            <p className="muted">Sign in with your passkey.</p>
            <button className="primary" disabled={busy} onClick={() => run(signIn)}>{busy ? 'Waiting for passkey…' : 'Sign in with passkey'}</button>
          </>
        )}

        {supported && !status.registered && status.setupAvailable && (
          <form onSubmit={e => { e.preventDefault(); void run(() => registerPasskey(name.trim() || 'First passkey', token)) }}>
            <p className="muted">First-time setup. Enter the setup token, then create a passkey on this device.</p>
            <label>Setup token
              <input type="password" autoComplete="off" value={token} onChange={e => setToken(e.target.value)} required />
            </label>
            <label>Device name <span className="muted">(optional)</span>
              <input type="text" value={name} placeholder="e.g. MacBook" onChange={e => setName(e.target.value)} maxLength={50} />
            </label>
            <button className="primary" type="submit" disabled={busy || !token}>{busy ? 'Waiting for passkey…' : 'Create passkey'}</button>
          </form>
        )}

        {supported && !status.registered && !status.setupAvailable && (
          <p className="error-note">Setup is disabled. Set the <code>Auth__SetupToken</code> app setting to register the first passkey.</p>
        )}

        {error && <p className="error-note" role="alert">{error}</p>}
      </div>
    </div>
  )
}
