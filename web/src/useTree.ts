import { useCallback, useState } from 'react'
import { listTree, type Entry } from './api'
import { isUnder, remapPath } from './files'

/** Lazy-loaded folder tree: children are fetched one level at a time when a folder is first expanded. */
export function useTree() {
  const [children, setChildren] = useState<Record<string, Entry[]>>({})
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [loading, setLoading] = useState<Set<string>>(() => new Set())
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async (path: string) => {
    setLoading(s => new Set(s).add(path))
    try {
      const entries = await listTree(path)
      setChildren(c => ({ ...c, [path]: entries }))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(s => { const n = new Set(s); n.delete(path); return n })
    }
  }, [])

  const expand = useCallback((path: string) => {
    setExpanded(s => new Set(s).add(path))
    if (!(path in children)) void load(path)
  }, [children, load])

  const toggle = useCallback((path: string) => {
    if (expanded.has(path)) setExpanded(s => { const n = new Set(s); n.delete(path); return n })
    else expand(path)
  }, [expanded, expand])

  /** Re-fetch every folder currently loaded. */
  const refreshAll = useCallback(() => {
    for (const p of Object.keys(children)) void load(p)
  }, [children, load])

  /**
   * A folder or file moved/renamed/deleted: drop cached listings under `from`, carry expanded state to `to`
   * (or forget it if `to` is null), and reload what's still visible.
   */
  const remap = useCallback((from: string, to: string | null) => {
    setChildren(c => Object.fromEntries(Object.entries(c).filter(([k]) => !isUnder(k, from))))
    setExpanded(s => {
      const n = new Set<string>()
      for (const p of s) {
        if (!isUnder(p, from)) n.add(p)
        else if (to !== null) n.add(remapPath(p, from, to))
      }
      return n
    })
    if (to !== null) {
      for (const p of expanded) if (isUnder(p, from)) void load(remapPath(p, from, to))
    }
  }, [expanded, load])

  return { children, expanded, loading, error, load, expand, toggle, refreshAll, remap }
}
