import { useCallback, useEffect, useState } from 'react'
import { describeAuthError, listPasskeys, registerPasskey, removePasskey, signOut, type PasskeyInfo } from './auth'

export default function Account({ onClose }: { onClose: () => void }) {
  const [keys, setKeys] = useState<PasskeyInfo[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(() => listPasskeys().then(setKeys, e => setError(describeAuthError(e))), [])
  useEffect(() => { void refresh() }, [refresh])
  useEffect(() => {
    const k = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    addEventListener('keydown', k)
    return () => removeEventListener('keydown', k)
  }, [onClose])

  const act = async (fn: () => Promise<void>) => {
    setBusy(true); setError(null)
    try { await fn(); await refresh() } catch (e) { setError(describeAuthError(e)) } finally { setBusy(false) }
  }

  const fmt = (s: string | null) => (s ? new Date(s).toLocaleString() : 'never')

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal" role="dialog" aria-label="Account" onClick={e => e.stopPropagation()}>
        <div className="modal-head"><strong>Passkeys</strong><button onClick={onClose} aria-label="Close">×</button></div>
        {keys === null && !error && <p className="muted">Loading…</p>}
        <ul className="keys">
          {keys?.map(k => (
            <li key={k.id}>
              <div><div>{k.name}</div><div className="muted small">Added {fmt(k.createdAt)} · last used {fmt(k.lastUsedAt)}</div></div>
              <button disabled={busy || keys.length < 2} title={keys.length < 2 ? "You can't remove your only passkey" : 'Remove'}
                      onClick={() => { if (confirm(`Remove passkey "${k.name}"?`)) void act(() => removePasskey(k.id)) }}>Remove</button>
            </li>
          ))}
        </ul>
        <form className="add-key" onSubmit={e => { e.preventDefault(); void act(async () => { await registerPasskey(name.trim() || `Passkey ${(keys?.length ?? 0) + 1}`); setName('') }) }}>
          <input value={name} placeholder="Name for new passkey (e.g. iPhone)" maxLength={50} onChange={e => setName(e.target.value)} />
          <button type="submit" disabled={busy}>Add passkey</button>
        </form>
        {error && <p className="error-note" role="alert">{error}</p>}
        <div className="modal-foot">
          <button className="primary" onClick={() => void signOut().then(() => location.reload())}>Sign out</button>
        </div>
      </div>
    </div>
  )
}
