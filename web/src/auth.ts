import { startAuthentication, startRegistration } from '@simplewebauthn/browser'

export interface AuthStatus { authenticated: boolean; registered: boolean; setupAvailable: boolean }
export interface PasskeyInfo { id: string; name: string; createdAt: string; lastUsedAt: string | null }

async function post(url: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
}

async function ensureOk(res: Response): Promise<Response> {
  if (res.ok) return res
  let message = res.statusText
  try { message = (await res.json()).message ?? message } catch { /* not json */ }
  throw new Error(res.status === 429 ? 'Too many attempts. Wait a minute and try again.' : message)
}

export async function getStatus(): Promise<AuthStatus> {
  return (await ensureOk(await fetch('/api/auth/status'))).json()
}

/** First passkey needs the setup token; adding more while signed in does not. */
export async function registerPasskey(name: string, setupToken?: string): Promise<void> {
  const optionsJSON = await (await ensureOk(await post('/api/auth/register/options', { setupToken: setupToken ?? null }))).json()
  const credential = await startRegistration({ optionsJSON })
  await ensureOk(await post('/api/auth/register', { name, credential }))
}

export async function signIn(): Promise<void> {
  const optionsJSON = await (await ensureOk(await post('/api/auth/login/options'))).json()
  const credential = await startAuthentication({ optionsJSON })
  await ensureOk(await post('/api/auth/login', credential))
}

export async function signOut(): Promise<void> {
  await post('/api/auth/logout')
}

export async function listPasskeys(): Promise<PasskeyInfo[]> {
  return (await ensureOk(await fetch('/api/auth/credentials'))).json()
}

export async function removePasskey(id: string): Promise<void> {
  await ensureOk(await fetch(`/api/auth/credentials/${encodeURIComponent(id)}`, { method: 'DELETE' }))
}

/** Turn WebAuthn browser errors into something readable. */
export function describeAuthError(e: unknown): string {
  if (e instanceof Error) {
    if (e.name === 'NotAllowedError') return 'The passkey prompt was cancelled or timed out.'
    if (e.name === 'InvalidStateError') return 'This device already has a passkey registered for pad.'
    return e.message
  }
  return String(e)
}
