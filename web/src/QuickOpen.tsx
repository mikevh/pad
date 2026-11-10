import { useEffect, useMemo, useRef, useState } from 'react'
import { listAllPaths } from './api'
import { baseName } from './files'
import { rank } from './fuzzy'

function Highlighted({ text, indices, offset = 0 }: { text: string; indices: Set<number>; offset?: number }) {
  return <>{Array.from(text).map((ch, i) => (indices.has(i + offset) ? <mark key={i}>{ch}</mark> : ch))}</>
}

export default function QuickOpen({ recent, onPick, onClose }: { recent: string[]; onPick: (path: string) => void; onClose: () => void }) {
  const [paths, setPaths] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [index, setIndex] = useState(0)
  const listRef = useRef<HTMLUListElement>(null)

  useEffect(() => { listAllPaths().then(setPaths, e => setError(e instanceof Error ? e.message : String(e))) }, [])

  const results = useMemo(() => rank(query, paths ?? [], recent), [query, paths, recent])
  useEffect(() => setIndex(0), [query])
  useEffect(() => { listRef.current?.children[index]?.scrollIntoView({ block: 'nearest' }) }, [index])

  const pick = (i: number) => { const m = results[i]; if (m) { onClose(); onPick(m.path) } }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { e.preventDefault(); onClose() }
    else if (e.key === 'ArrowDown') { e.preventDefault(); setIndex(i => Math.min(results.length - 1, i + 1)) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setIndex(i => Math.max(0, i - 1)) }
    else if (e.key === 'Enter') { e.preventDefault(); pick(index) }
  }

  return (
    <div className="modal-backdrop quick" onClick={onClose}>
      <div className="quickopen" role="dialog" aria-label="Quick open" onClick={e => e.stopPropagation()} onKeyDown={onKeyDown}>
        <input
          autoFocus value={query} onChange={e => setQuery(e.target.value)} placeholder="Go to file…"
          spellCheck={false} autoCapitalize="off" autoCorrect="off" aria-label="File name"
        />
        {error && <div className="error-note">{error}</div>}
        {!error && paths === null && <div className="muted pad">Loading…</div>}
        {paths && results.length === 0 && <div className="muted pad">{paths.length === 0 ? 'No files yet.' : 'No matching files.'}</div>}
        <ul ref={listRef} role="listbox">
          {results.map((m, i) => {
            const name = baseName(m.path)
            const dir = m.path.slice(0, m.path.length - name.length - (m.path.includes('/') ? 1 : 0))
            const hit = new Set(m.indices)
            return (
              <li key={m.path} role="option" aria-selected={i === index} className={i === index ? 'on' : ''}
                  onMouseMove={() => setIndex(i)} onClick={() => pick(i)}>
                <span className="qo-name"><Highlighted text={name} indices={hit} offset={m.path.length - name.length} /></span>
                {dir && <span className="qo-dir"><Highlighted text={dir} indices={hit} /></span>}
              </li>
            )
          })}
        </ul>
      </div>
    </div>
  )
}
