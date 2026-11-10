import { useCallback, useEffect, useState } from 'react'
import App from './App'
import Login from './Login'
import { getStatus, type AuthStatus } from './auth'

export default function Root() {
  const [status, setStatus] = useState<AuthStatus | null>(null)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(() => getStatus().then(setStatus, e => setError(e instanceof Error ? e.message : String(e))), [])
  useEffect(() => { void refresh() }, [refresh])

  if (error) return <div className="login"><div className="login-card"><p className="error-note">{error}</p><button onClick={() => location.reload()}>Retry</button></div></div>
  if (!status) return <div className="login"><div className="login-card muted">Loading…</div></div>
  return status.authenticated ? <App /> : <Login status={status} onDone={() => void refresh()} />
}
