import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import Editor from '@monaco-editor/react'
import type { editor } from 'monaco-editor'
import { ApiError, createFolder, deletePath, downloadUrl, fileUrl, movePath, readText, uploadFile, writeText } from './api'
import { baseName, isUnder, joinPath as join, kindOf, MAX_UPLOAD_BYTES, parentOf, remapPath, type Kind } from './files'
import { languageFor } from './monacoSetup'
import { useTree } from './useTree'
import Explorer, { type UploadItem } from './Explorer'
import Account from './Account'
import QuickOpen from './QuickOpen'

interface Tab {
  path: string
  kind: Kind
  status: 'loading' | 'ready' | 'error'
  text: string
  saved: string
  etag: string
  error?: string
  conflict?: boolean
}

const isDirty = (t: Tab) => t.kind === 'text' && t.status === 'ready' && t.text !== t.saved

function useDark() {
  const [dark, setDark] = useState(() => matchMedia('(prefers-color-scheme: dark)').matches)
  useEffect(() => {
    const m = matchMedia('(prefers-color-scheme: dark)')
    const f = (e: MediaQueryListEvent) => setDark(e.matches)
    m.addEventListener('change', f)
    return () => m.removeEventListener('change', f)
  }, [])
  return dark
}

export default function App() {
  const dark = useDark()
  const tree = useTree()
  const [tabs, setTabs] = useState<Tab[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [sidebarOpen, setSidebarOpen] = useState(() => !matchMedia('(max-width: 767px)').matches && localStorage.getItem('pad.sidebar') !== '0')
  const [sidebarWidth, setSidebarWidth] = useState(() => Number(localStorage.getItem('pad.sidebarWidth')) || 280)
  const [saving, setSaving] = useState(false)
  const [accountOpen, setAccountOpen] = useState(false)
  const [quickOpen, setQuickOpen] = useState(false)
  const [cursor, setCursor] = useState({ line: 1, col: 1 })
  const [toast, setToast] = useState<{ msg: string; error: boolean } | null>(null)
  const tabsRef = useRef(tabs)
  tabsRef.current = tabs

  const tab = tabs.find(t => t.path === active) ?? null
  const dirtyPaths = useMemo(() => new Set(tabs.filter(isDirty).map(t => t.path)), [tabs])

  const flash = useCallback((msg: string, error = true) => { setToast({ msg, error }); setTimeout(() => setToast(null), 4000) }, [])
  const patch = useCallback((path: string, p: Partial<Tab>) =>
    setTabs(ts => ts.map(t => (t.path === path ? { ...t, ...p } : t))), [])

  useEffect(() => { if (!matchMedia('(max-width: 767px)').matches) localStorage.setItem('pad.sidebar', sidebarOpen ? '1' : '0') }, [sidebarOpen])
  useEffect(() => { localStorage.setItem('pad.sidebarWidth', String(sidebarWidth)) }, [sidebarWidth])

  const openFile = useCallback(async (path: string) => {
    setActive(path)
    if (matchMedia('(max-width: 767px)').matches) setSidebarOpen(false)
    if (tabsRef.current.some(t => t.path === path)) return
    const kind = kindOf(path)
    if (kind !== 'text') {
      setTabs(ts => [...ts, { path, kind, status: 'ready', text: '', saved: '', etag: '' }])
      return
    }
    setTabs(ts => [...ts, { path, kind, status: 'loading', text: '', saved: '', etag: '' }])
    try {
      const { text, etag } = await readText(path)
      patch(path, { status: 'ready', text, saved: text, etag })
    } catch (e) {
      patch(path, { status: 'error', error: e instanceof Error ? e.message : String(e) })
    }
  }, [patch])

  const closeTab = useCallback((path: string) => {
    const t = tabsRef.current.find(x => x.path === path)
    if (t && isDirty(t) && !confirm(`Discard unsaved changes to ${baseName(path)}?`)) return
    const idx = tabsRef.current.findIndex(x => x.path === path)
    const rest = tabsRef.current.filter(x => x.path !== path)
    setTabs(rest)
    setActive(cur => (cur === path ? (rest[Math.min(idx, rest.length - 1)]?.path ?? null) : cur))
  }, [])

  const save = useCallback(async (path: string, force = false) => {
    const t = tabsRef.current.find(x => x.path === path)
    if (!t || !isDirty(t) && !force) return
    setSaving(true)
    const text = t.text
    try {
      const etag = await writeText(path, text, force ? '*' : t.etag)
      patch(path, { saved: text, etag, conflict: false })
    } catch (e) {
      if (e instanceof ApiError && e.code === 'etag_mismatch') patch(path, { conflict: true })
      else flash(`Save failed: ${e instanceof Error ? e.message : e}`)
    } finally {
      setSaving(false)
    }
  }, [patch, flash])

  const reloadFromServer = useCallback(async (path: string) => {
    try {
      const { text, etag } = await readText(path)
      patch(path, { text, saved: text, etag, conflict: false })
    } catch (e) {
      flash(`Reload failed: ${e instanceof Error ? e.message : e}`)
    }
  }, [patch, flash])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (active) void save(active)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [active, save])

  // Cmd/Ctrl+P (and K as an alias) open quick open. Capture phase beats Monaco's own handlers;
  // preventDefault stops the browser's print dialog. Costs printing from inside pad.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const k = e.key.toLowerCase()
      if ((e.metaKey || e.ctrlKey) && !e.shiftKey && !e.altKey && (k === 'p' || k === 'k')) {
        e.preventDefault()
        e.stopPropagation()
        setQuickOpen(true)
      }
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [])

  useEffect(() => {
    const onUnload = (e: BeforeUnloadEvent) => { if (tabsRef.current.some(isDirty)) e.preventDefault() }
    window.addEventListener('beforeunload', onUnload)
    return () => window.removeEventListener('beforeunload', onUnload)
  }, [])

  const validName = useCallback((name: string) => {
    if (/[\\/]/.test(name) || name === '.' || name === '..' || name === '.padkeep' || /[\u0000-\u001f]/.test(name)) {
      flash(`"${name}" is not a valid name.`)
      return false
    }
    return true
  }, [flash])

  /** Point open tabs at a new path after a rename/move, and re-read each ETag (a copy gets a new one). */
  const retarget = useCallback((from: string, to: string) => {
    const affected = tabsRef.current.filter(t => isUnder(t.path, from)).map(t => remapPath(t.path, from, to))
    setTabs(ts => ts.map(t => (isUnder(t.path, from) ? { ...t, path: remapPath(t.path, from, to) } : t)))
    setActive(a => (a && isUnder(a, from) ? remapPath(a, from, to) : a))
    for (const path of affected) {
      if (kindOf(path) !== 'text') continue
      readText(path).then(({ etag }) => patch(path, { etag }), () => { /* leave stale; save will surface it */ })
    }
  }, [patch])

  const onCreate = useCallback(async (parent: string, kind: 'file' | 'folder', name: string) => {
    if (!validName(name)) return false
    const path = join(parent, name)
    try {
      if (kind === 'file') await writeText(path, '', null)
      else await createFolder(path)
      if (parent) tree.expand(parent)
      await tree.load(parent)
      if (kind === 'file') void openFile(path)
      else tree.expand(path)
      return true
    } catch (e) {
      flash(e instanceof ApiError && e.status === 409 ? `"${name}" already exists.` : `Could not create ${kind}: ${e instanceof Error ? e.message : e}`)
      return false
    }
  }, [validName, tree, openFile, flash])

  const onRename = useCallback(async (path: string, name: string) => {
    if (!validName(name)) return false
    const to = join(parentOf(path), name)
    try {
      await movePath(path, to)
      retarget(path, to)
      tree.remap(path, to)
      await tree.load(parentOf(path))
      return true
    } catch (e) {
      flash(e instanceof ApiError && e.status === 409 ? `"${name}" already exists here.` : `Rename failed: ${e instanceof Error ? e.message : e}`)
      return false
    }
  }, [validName, retarget, tree, flash])

  const onMove = useCallback(async (paths: string[], target: string) => {
    const parents = new Set<string>([target])
    let moved = 0
    for (const path of paths) {
      const to = join(target, baseName(path))
      try {
        await movePath(path, to)
        retarget(path, to)
        tree.remap(path, to)
        parents.add(parentOf(path))
        moved++
      } catch (e) {
        flash(e instanceof ApiError && e.status === 409 ? `"${baseName(path)}" already exists in ${target || 'the root'}.` : `Move failed: ${e instanceof Error ? e.message : e}`)
      }
    }
    if (target) tree.expand(target)
    await Promise.all([...parents].map(p => tree.load(p)))
    if (moved > 1) flash(`Moved ${moved} items.`, false)
  }, [retarget, tree, flash])

  const onDelete = useCallback(async (paths: string[]) => {
    const kinds = paths.map(p => Object.values(tree.children).flat().find(e => e.path === p)?.isFolder)
    const what = paths.length === 1 ? `"${baseName(paths[0])}"` : `${paths.length} items`
    const folderNote = kinds.some(Boolean) ? ' Folders are deleted with everything inside them.' : ''
    const dirty = tabsRef.current.filter(t => isDirty(t) && paths.some(p => isUnder(t.path, p))).length
    const dirtyNote = dirty ? ` ${dirty} open file${dirty > 1 ? 's have' : ' has'} unsaved changes.` : ''
    if (!confirm(`Delete ${what}?${folderNote}${dirtyNote} This cannot be undone.`)) return
    const parents = new Set<string>()
    for (let i = 0; i < paths.length; i++) {
      try {
        await deletePath(paths[i], !!kinds[i])
        parents.add(parentOf(paths[i]))
        tree.remap(paths[i], null)
        const gone = paths[i]
        const remaining = tabsRef.current.filter(t => !isUnder(t.path, gone))
        setTabs(ts => ts.filter(t => !isUnder(t.path, gone)))
        setActive(a => (a && isUnder(a, gone) ? (remaining[remaining.length - 1]?.path ?? null) : a))
      } catch (e) {
        flash(`Delete failed: ${e instanceof Error ? e.message : e}`)
      }
    }
    await Promise.all([...parents].map(p => tree.load(p)))
  }, [tree, flash])

  const onUpload = useCallback(async (folder: string, items: UploadItem[]) => {
    let ok = 0, skipped = 0
    const failed: string[] = []
    flash(`Uploading ${items.length} file${items.length > 1 ? 's' : ''}…`, false)
    for (const { file, rel } of items) {
      const path = join(folder, rel)
      if (file.size > MAX_UPLOAD_BYTES) { failed.push(`${rel} (over 100 MB)`); continue }
      try {
        try {
          await uploadFile(path, file, false)
        } catch (e) {
          if (e instanceof ApiError && e.code === 'already_exists') {
            if (!confirm(`"${rel}" already exists. Overwrite it?`)) { skipped++; continue }
            await uploadFile(path, file, true)
          } else throw e
        }
        ok++
        // An open, clean tab for this file would now be stale.
        const open = tabsRef.current.find(t => t.path === path)
        if (open && !isDirty(open)) void reloadFromServer(path)
      } catch (e) {
        failed.push(`${rel} (${e instanceof Error ? e.message : e})`)
      }
    }
    if (folder) tree.expand(folder)
    tree.refreshAll()
    await tree.load(folder)
    const parts = [`Uploaded ${ok} file${ok === 1 ? '' : 's'}`]
    if (skipped) parts.push(`${skipped} skipped`)
    if (failed.length) parts.push(`${failed.length} failed: ${failed.slice(0, 2).join(', ')}${failed.length > 2 ? '…' : ''}`)
    flash(parts.join(', ') + '.', failed.length > 0)
  }, [flash, tree, reloadFromServer])

  const onDownload = useCallback((path: string) => {
    const a = document.createElement('a')
    a.href = downloadUrl(path)
    a.download = baseName(path)
    document.body.appendChild(a)
    a.click()
    a.remove()
  }, [])

  const openFromQuickOpen = useCallback((path: string) => {
    // Reveal the file in the explorer by expanding each ancestor folder.
    const parts = path.split('/').slice(0, -1)
    parts.forEach((_, i) => tree.expand(parts.slice(0, i + 1).join('/')))
    void openFile(path)
  }, [tree, openFile])

  const startResize = (ev: React.PointerEvent) => {
    ev.preventDefault()
    const move = (e: PointerEvent) => setSidebarWidth(Math.min(600, Math.max(180, e.clientX - 48)))
    const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  const onMount = useCallback((ed: editor.IStandaloneCodeEditor) => {
    ed.onDidChangeCursorPosition(e => setCursor({ line: e.position.lineNumber, col: e.position.column }))
  }, [])

  return (
    <div className={'app' + (sidebarOpen ? ' sidebar-open' : '')}>
      <div className="activity" role="toolbar" aria-label="Activity bar">
        <button className={sidebarOpen ? 'on' : ''} title="Explorer" onClick={() => setSidebarOpen(o => !o)}>🗂</button>
        <button title="Go to file (Cmd/Ctrl+P)" aria-label="Go to file" onClick={() => setQuickOpen(true)}>🔍</button>
        <span className="grow" />
        <button title="Passkeys and sign out" aria-label="Account" onClick={() => setAccountOpen(true)}>🔑</button>
      </div>

      {sidebarOpen && <div className="backdrop" onClick={() => setSidebarOpen(false)} />}
      <aside className="sidebar" style={{ width: sidebarWidth }} hidden={!sidebarOpen}>
        <Explorer
          tree={tree}
          activePath={active}
          dirtyPaths={dirtyPaths}
          onOpen={openFile}
          onCreate={onCreate}
          onRename={onRename}
          onMove={onMove}
          onDelete={onDelete}
          onUpload={onUpload}
          onDownload={onDownload}
          onNotice={m => flash(m, false)}
        />
        <div className="resizer" onPointerDown={startResize} />
      </aside>

      <main className="main">
        <header className="mobilebar">
          <button onClick={() => setSidebarOpen(true)} aria-label="Open explorer">☰</button>
          <span className="mobile-title">{tab ? baseName(tab.path) : 'pad'}</span>
          {tab && isDirty(tab) && <button className="save-btn" onClick={() => void save(tab.path)}>Save</button>}
          <button aria-label="Go to file" onClick={() => setQuickOpen(true)}>🔍</button>
          <button aria-label="Account" onClick={() => setAccountOpen(true)}>🔑</button>
        </header>

        <div className="tabs" role="tablist">
          {tabs.map(t => (
            <div key={t.path} role="tab" aria-selected={t.path === active}
                 className={'tab' + (t.path === active ? ' active' : '')}
                 onClick={() => setActive(t.path)}
                 onAuxClick={e => { if (e.button === 1) closeTab(t.path) }} title={t.path}>
              <span className="tab-name">{baseName(t.path)}</span>
              <button className="close" aria-label={`Close ${baseName(t.path)}`}
                      onClick={e => { e.stopPropagation(); closeTab(t.path) }}>
                {isDirty(t) ? '●' : '×'}
              </button>
            </div>
          ))}
        </div>

        {tab?.conflict && (
          <div className="banner">
            <span>{baseName(tab.path)} changed on the server since you opened it.</span>
            <button onClick={() => void reloadFromServer(tab.path)}>Discard mine &amp; reload</button>
            <button onClick={() => void save(tab.path, true)}>Overwrite</button>
          </div>
        )}

        <div className="editor-area">
          {!tab && <div className="welcome"><h1>pad</h1><p>Open a file from the explorer, or create one with ＋.</p><p className="muted">Ctrl/Cmd+S saves.</p></div>}
          {tab?.status === 'loading' && <div className="welcome muted">Loading…</div>}
          {tab?.status === 'error' && <div className="welcome error-note">Could not open file: {tab.error}</div>}
          {tab?.status === 'ready' && tab.kind === 'text' && (
            <Editor
              path={tab.path}
              language={languageFor(tab.path)}
              value={tab.text}
              theme={dark ? 'vs-dark' : 'vs'}
              onChange={v => patch(tab.path, { text: v ?? '' })}
              onMount={onMount}
              options={{ automaticLayout: true, minimap: { enabled: false }, wordWrap: 'on', fontSize: 14, scrollBeyondLastLine: false, tabSize: 2 }}
            />
          )}
          {tab?.status === 'ready' && tab.kind === 'image' && <div className="viewer"><img src={fileUrl(tab.path)} alt={baseName(tab.path)} /></div>}
          {tab?.status === 'ready' && tab.kind === 'pdf' && <iframe className="pdf" src={fileUrl(tab.path)} title={baseName(tab.path)} />}
          {tab?.status === 'ready' && tab.kind === 'binary' && (
            <div className="welcome"><p>{baseName(tab.path)} can't be previewed.</p><a href={fileUrl(tab.path)} download={baseName(tab.path)}>Download</a></div>
          )}
        </div>

        <footer className="status">
          <span>{tab ? tab.path : 'pad'}</span>
          <span className="spacer" />
          {saving && <span>Saving…</span>}
          {tab && isDirty(tab) && !saving && <span>Unsaved</span>}
          {tab?.status === 'ready' && tab.kind === 'text' && <><span>Ln {cursor.line}, Col {cursor.col}</span><span>{languageFor(tab.path)}</span></>}
        </footer>
      </main>
      {quickOpen && <QuickOpen recent={[...tabs.map(t => t.path)].reverse()} onPick={openFromQuickOpen} onClose={() => setQuickOpen(false)} />}
      {accountOpen && <Account onClose={() => setAccountOpen(false)} />}
      {toast && <div className={'toast' + (toast.error ? ' error' : '')} role="alert">{toast.msg}</div>}
    </div>
  )
}
