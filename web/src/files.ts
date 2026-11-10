const IMAGE = new Set(['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp'])
const BINARY = new Set(['zip', 'gz', 'tar', '7z', 'exe', 'dll', 'bin', 'mp3', 'mp4', 'mov', 'wav', 'woff', 'woff2', 'ttf', 'otf', 'docx', 'xlsx', 'pptx', 'psd', 'iso', 'dmg'])

export type Kind = 'text' | 'image' | 'pdf' | 'binary'

export function kindOf(path: string): Kind {
  const ext = path.includes('.') ? path.split('.').pop()!.toLowerCase() : ''
  if (IMAGE.has(ext)) return 'image'
  if (ext === 'pdf') return 'pdf'
  if (BINARY.has(ext)) return 'binary'
  return 'text'
}

export const baseName = (p: string) => p.slice(p.lastIndexOf('/') + 1)
export const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')

export const joinPath = (folder: string, name: string) => (folder ? `${folder}/${name}` : name)
export const isUnder = (path: string, folder: string) => path === folder || path.startsWith(folder + '/')
/** Re-root `path` from `from` to `to` (path must be `from` or under it). */
export const remapPath = (path: string, from: string, to: string) => to + path.slice(from.length)
export const MAX_UPLOAD_BYTES = 100 * 1024 * 1024
