import * as monaco from 'monaco-editor'
import EditorWorker from 'monaco-editor/editor/editor.worker?worker'
import { loader } from '@monaco-editor/react'

// Bundle Monaco locally (no CDN). Only the core worker: syntax highlighting needs nothing more.
self.MonacoEnvironment = { getWorker: () => new EditorWorker() }
loader.config({ monaco })

const extToLang = new Map<string, string>()
for (const l of monaco.languages.getLanguages())
  for (const e of l.extensions ?? []) if (!extToLang.has(e)) extToLang.set(e, l.id)

export function languageFor(path: string): string {
  const i = path.lastIndexOf('.')
  return i < 0 ? 'plaintext' : (extToLang.get(path.slice(i).toLowerCase()) ?? 'plaintext')
}
