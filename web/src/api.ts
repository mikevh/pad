export interface Entry {
  name: string
  path: string
  isFolder: boolean
  size: number | null
  modified: string | null
  eTag: string | null
  contentType: string | null
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message)
  }
}

const enc = (p: string) => p.split('/').map(encodeURIComponent).join('/')
export const fileUrl = (path: string) => `/api/files/${enc(path)}`

async function fail(res: Response): Promise<never> {
  // Session expired or signed out elsewhere: reload, which lands on the sign-in screen.
  if (res.status === 401) location.reload()
  let code = 'error', message = res.statusText
  try {
    const j = await res.json()
    code = j.error ?? code
    message = j.message ?? message
  } catch { /* not json */ }
  throw new ApiError(res.status, code, message)
}

export async function listTree(prefix: string): Promise<Entry[]> {
  const res = await fetch(`/api/tree?prefix=${encodeURIComponent(prefix)}`)
  if (!res.ok) return fail(res)
  return (await res.json()).entries
}

export async function readText(path: string): Promise<{ text: string; etag: string }> {
  const res = await fetch(fileUrl(path))
  if (!res.ok) return fail(res)
  return { text: await res.text(), etag: res.headers.get('ETag') ?? '' }
}

/** `etag` = only overwrite if unchanged; `null` = create only; `'*'` = overwrite unconditionally. */
export async function writeText(path: string, text: string, etag: string | null | '*'): Promise<string> {
  const headers: Record<string, string> = { 'Content-Type': 'application/octet-stream' }
  if (etag === null) headers['If-None-Match'] = '*'
  else if (etag !== '*') headers['If-Match'] = etag
  const res = await fetch(fileUrl(path), { method: 'PUT', headers, body: text })
  if (!res.ok) return fail(res)
  return (await res.json()).etag
}

export async function createFolder(path: string): Promise<void> {
  const res = await fetch(`/api/folders/${enc(path)}`, { method: 'POST' })
  if (!res.ok) return fail(res)
}

export const downloadUrl = (path: string) => `${fileUrl(path)}?download=1`

export async function deletePath(path: string, isFolder: boolean): Promise<void> {
  const url = isFolder ? `/api/folders/${enc(path)}` : fileUrl(path)
  const res = await fetch(url, { method: 'DELETE' })
  if (!res.ok && res.status !== 404) return fail(res)
}

export async function movePath(from: string, to: string): Promise<void> {
  const res = await fetch('/api/move', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ from, to }),
  })
  if (!res.ok) return fail(res)
}

/** Upload raw bytes. `overwrite=false` fails with 409 `already_exists` if the file is there. */
export async function uploadFile(path: string, file: Blob, overwrite: boolean): Promise<void> {
  const headers: Record<string, string> = {}
  if (!overwrite) headers['If-None-Match'] = '*'
  const res = await fetch(fileUrl(path), { method: 'PUT', headers, body: file })
  if (!res.ok) return fail(res)
}

export async function listAllPaths(): Promise<string[]> {
  const res = await fetch('/api/paths')
  if (!res.ok) return fail(res)
  return (await res.json()).files.map((f: { path: string }) => f.path)
}
