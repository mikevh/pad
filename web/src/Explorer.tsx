import { useCallback, useEffect, useRef, useState } from 'react'
import type { Entry } from './api'
import { baseName, isUnder, parentOf } from './files'
import ContextMenu, { type MenuItem } from './ContextMenu'
import type { useTree } from './useTree'

type Tree = ReturnType<typeof useTree>
export interface UploadItem { file: File; rel: string }

interface Props {
  tree: Tree
  activePath: string | null
  dirtyPaths: Set<string>
  onOpen: (path: string) => void
  onCreate: (parent: string, kind: 'file' | 'folder', name: string) => Promise<boolean>
  onRename: (path: string, newName: string) => Promise<boolean>
  onMove: (paths: string[], targetFolder: string) => Promise<void>
  onDelete: (paths: string[]) => Promise<void>
  onUpload: (folder: string, items: UploadItem[]) => Promise<void>
  onDownload: (path: string) => void
  onNotice: (msg: string) => void
}

const PAD_MIME = 'application/x-pad-paths'

function InlineInput({ initial, folder, onCommit, onCancel }: { initial: string; folder: boolean; onCommit: (v: string) => void; onCancel: () => void }) {
  const ref = useRef<HTMLInputElement>(null)
  const done = useRef(false)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    el.focus()
    const dot = folder ? -1 : initial.lastIndexOf('.')
    el.setSelectionRange(0, dot > 0 ? dot : initial.length)
  }, [initial, folder])
  const finish = (commit: boolean) => {
    if (done.current) return
    done.current = true
    const v = ref.current?.value.trim() ?? ''
    if (commit && v && v !== initial) onCommit(v)
    else onCancel()
  }
  return (
    <input
      ref={ref} className="inline-input" defaultValue={initial} spellCheck={false} autoCapitalize="off" autoCorrect="off"
      onClick={e => e.stopPropagation()}
      onKeyDown={e => {
        e.stopPropagation()
        if (e.key === 'Enter') finish(true)
        else if (e.key === 'Escape') finish(false)
      }}
      onBlur={() => finish(true)}
    />
  )
}

/** Collect files from a drop, descending into dropped folders. Must be called synchronously inside the drop handler. */
function collectDropped(dt: DataTransfer): Promise<UploadItem[]> {
  const entries = Array.from(dt.items ?? [])
    .map(i => (i.kind === 'file' ? i.webkitGetAsEntry?.() : null))
    .filter((e): e is FileSystemEntry => !!e)
  if (entries.length === 0) return Promise.resolve(Array.from(dt.files).map(file => ({ file, rel: file.name })))

  const out: UploadItem[] = []
  const walk = async (entry: FileSystemEntry, base: string): Promise<void> => {
    if (entry.isFile) {
      const file = await new Promise<File>((res, rej) => (entry as FileSystemFileEntry).file(res, rej))
      out.push({ file, rel: base + entry.name })
    } else if (entry.isDirectory) {
      const reader = (entry as FileSystemDirectoryEntry).createReader()
      for (;;) {
        const batch = await new Promise<FileSystemEntry[]>((res, rej) => reader.readEntries(res, rej))
        if (batch.length === 0) break
        for (const child of batch) await walk(child, base + entry.name + '/')
      }
    }
  }
  return Promise.all(entries.map(e => walk(e, ''))).then(() => out)
}

export default function Explorer(p: Props) {
  const { tree } = p
  const { load } = tree
  const [selection, setSelection] = useState<Set<string>>(() => new Set())
  const [anchor, setAnchor] = useState<string | null>(null)
  const [focused, setFocused] = useState<string | null>(null)
  const [renaming, setRenaming] = useState<string | null>(null)
  const [creating, setCreating] = useState<{ parent: string; kind: 'file' | 'folder' } | null>(null)
  const [menu, setMenu] = useState<{ x: number; y: number; items: MenuItem[] } | null>(null)
  const [dropTarget, setDropTarget] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const uploadTarget = useRef('')
  const hoverTimer = useRef<{ path: string; t: number } | null>(null)
  const longPress = useRef<number | null>(null)

  useEffect(() => { void load('') }, [load])

  const byPath = useCallback(() => {
    const m = new Map<string, Entry>()
    for (const list of Object.values(tree.children)) for (const e of list) m.set(e.path, e)
    return m
  }, [tree.children])

  const visible = useCallback((): string[] => {
    const out: string[] = []
    const walk = (parent: string) => {
      for (const e of tree.children[parent] ?? []) {
        out.push(e.path)
        if (e.isFolder && tree.expanded.has(e.path)) walk(e.path)
      }
    }
    walk('')
    return out
  }, [tree.children, tree.expanded])

  const folderFor = (path: string | null): string => {
    if (path === null) return ''
    const e = byPath().get(path)
    return e?.isFolder ? path : parentOf(path)
  }

  const startCreate = (parent: string, kind: 'file' | 'folder') => {
    if (parent) tree.expand(parent)
    setRenaming(null)
    setCreating({ parent, kind })
  }

  const startUpload = (folder: string) => { uploadTarget.current = folder; fileInput.current?.click() }

  const selectOnly = (path: string) => { setSelection(new Set([path])); setAnchor(path); setFocused(path) }

  const clickRow = (e: React.MouseEvent, entry: Entry) => {
    setFocused(entry.path)
    if (e.metaKey || e.ctrlKey) {
      setSelection(s => { const n = new Set(s); if (n.has(entry.path)) n.delete(entry.path); else n.add(entry.path); return n })
      setAnchor(entry.path)
      return
    }
    if (e.shiftKey && anchor) {
      const v = visible()
      const a = v.indexOf(anchor), b = v.indexOf(entry.path)
      if (a >= 0 && b >= 0) { setSelection(new Set(v.slice(Math.min(a, b), Math.max(a, b) + 1))); return }
    }
    selectOnly(entry.path)
    if (entry.isFolder) tree.toggle(entry.path)
    else p.onOpen(entry.path)
  }

  const confirmDelete = (paths: string[]) => void p.onDelete(paths)

  const openMenu = (x: number, y: number, path: string | null) => {
    let paths: string[]
    if (path === null) paths = []
    else if (selection.has(path)) paths = [...selection]
    else { selectOnly(path); paths = [path] }
    const single = paths.length === 1 ? paths[0] : null
    const entry = single ? byPath().get(single) : undefined
    const target = single ? folderFor(single) : ''
    const items: MenuItem[] = [
      { label: 'New File…', onSelect: () => startCreate(target, 'file') },
      { label: 'New Folder…', onSelect: () => startCreate(target, 'folder') },
      { label: 'Upload Files…', onSelect: () => startUpload(target) },
    ]
    if (paths.length > 0) {
      items.push({ separator: true, label: '', onSelect: () => {} })
      if (entry && !entry.isFolder) items.push({ label: 'Download', onSelect: () => p.onDownload(entry.path) })
      if (single) items.push({ label: 'Rename…', hint: 'F2', onSelect: () => { setCreating(null); setRenaming(single) } })
      items.push({
        label: 'Move to…',
        onSelect: () => {
          const dest = prompt(`Move ${paths.length === 1 ? baseName(paths[0]) : paths.length + ' items'} to folder (blank = root):`, parentOf(paths[0]))
          if (dest !== null) void p.onMove(paths, dest.trim().replace(/^\/+|\/+$/g, ''))
        },
      })
      if (single) items.push({ label: 'Copy Path', onSelect: () => void navigator.clipboard?.writeText(single).then(() => p.onNotice('Path copied')) })
      items.push({ separator: true, label: '', onSelect: () => {} })
      items.push({ label: paths.length > 1 ? `Delete ${paths.length} items` : 'Delete', hint: 'Del', danger: true, onSelect: () => confirmDelete(paths) })
    }
    setMenu({ x, y, items })
  }

  // Long-press opens the menu on touch devices (iOS does not fire contextmenu).
  const touchStart = (e: React.TouchEvent, path: string | null) => {
    const t = e.touches[0]
    const x = t.clientX, y = t.clientY
    longPress.current = window.setTimeout(() => { longPress.current = null; openMenu(x, y, path) }, 550)
  }
  const touchCancel = () => { if (longPress.current) { clearTimeout(longPress.current); longPress.current = null } }

  const onKeyDown = (e: React.KeyboardEvent) => {
    const v = visible()
    const idx = focused ? v.indexOf(focused) : -1
    const move = (to: string) => {
      setFocused(to)
      if (e.shiftKey && anchor) {
        const a = v.indexOf(anchor), b = v.indexOf(to)
        setSelection(new Set(v.slice(Math.min(a, b), Math.max(a, b) + 1)))
      } else { setSelection(new Set([to])); setAnchor(to) }
    }
    const entry = focused ? byPath().get(focused) : undefined
    switch (e.key) {
      case 'ArrowDown': e.preventDefault(); if (v.length) move(v[Math.min(v.length - 1, idx + 1)]); break
      case 'ArrowUp': e.preventDefault(); if (v.length) move(v[Math.max(0, idx - 1)]); break
      case 'ArrowRight':
        if (entry?.isFolder) { e.preventDefault(); if (!tree.expanded.has(entry.path)) tree.expand(entry.path); else if (tree.children[entry.path]?.[0]) move(tree.children[entry.path][0].path) }
        break
      case 'ArrowLeft':
        if (entry) {
          e.preventDefault()
          if (entry.isFolder && tree.expanded.has(entry.path)) tree.toggle(entry.path)
          else if (parentOf(entry.path)) move(parentOf(entry.path))
        }
        break
      case 'Enter':
        if (entry) { e.preventDefault(); if (entry.isFolder) tree.toggle(entry.path); else p.onOpen(entry.path) }
        break
      case 'F2':
        if (focused) { e.preventDefault(); setCreating(null); setRenaming(focused) }
        break
      case 'Delete':
      case 'Backspace':
        if (e.key === 'Delete' || e.metaKey) {
          e.preventDefault()
          const paths = selection.size ? [...selection] : focused ? [focused] : []
          if (paths.length) confirmDelete(paths)
        }
        break
    }
  }

  // ---- drag & drop ----
  const acceptsDrag = (dt: DataTransfer) => dt.types.includes(PAD_MIME) || dt.types.includes('Files')

  const dragOverFolder = (e: React.DragEvent, folder: string) => {
    if (!acceptsDrag(e.dataTransfer)) return
    e.preventDefault()
    e.stopPropagation()
    e.dataTransfer.dropEffect = e.dataTransfer.types.includes(PAD_MIME) ? 'move' : 'copy'
    setDropTarget(folder)
    if (folder && !tree.expanded.has(folder) && hoverTimer.current?.path !== folder) {
      if (hoverTimer.current) clearTimeout(hoverTimer.current.t)
      hoverTimer.current = { path: folder, t: window.setTimeout(() => tree.expand(folder), 700) }
    }
  }

  const endDrag = () => {
    setDropTarget(null)
    if (hoverTimer.current) { clearTimeout(hoverTimer.current.t); hoverTimer.current = null }
  }

  const dropOnFolder = (e: React.DragEvent, folder: string) => {
    if (!acceptsDrag(e.dataTransfer)) return
    e.preventDefault()
    e.stopPropagation()
    const dt = e.dataTransfer
    endDrag()
    const raw = dt.getData(PAD_MIME)
    if (raw) {
      const paths = (JSON.parse(raw) as string[]).filter(path => parentOf(path) !== folder && !isUnder(folder, path))
      if (paths.length) void p.onMove(paths, folder)
    } else {
      void collectDropped(dt).then(items => { if (items.length) void p.onUpload(folder, items) })
    }
  }

  const rowDragStart = (e: React.DragEvent, path: string) => {
    const paths = selection.has(path) ? [...selection] : [path]
    if (!selection.has(path)) selectOnly(path)
    e.dataTransfer.setData(PAD_MIME, JSON.stringify(paths))
    e.dataTransfer.setData('text/plain', paths.join('\n'))
    e.dataTransfer.effectAllowed = 'move'
  }

  // ---- rendering (plain functions, not components, so inline inputs keep focus across renders) ----
  const renderCreateRow = (parent: string, depth: number) =>
    creating && creating.parent === parent ? (
      <div className="row" style={{ paddingLeft: 8 + depth * 14 + (creating.kind === 'file' ? 16 : 0) }} key="__creating">
        <span className="icon">{creating.kind === 'file' ? '📄' : '📁'}</span>
        <InlineInput
          initial="" folder={creating.kind === 'folder'}
          onCancel={() => setCreating(null)}
          onCommit={name => { const c = creating; void p.onCreate(c.parent, c.kind, name).then(ok => { if (ok) setCreating(null) }) }}
        />
      </div>
    ) : null

  const renderLevel = (parent: string, depth: number): React.ReactNode => {
    const entries = tree.children[parent]
    const pad = 12 + depth * 14
    if (!entries) return <>{renderCreateRow(parent, depth)}{tree.loading.has(parent) && <div className="row muted" style={{ paddingLeft: pad }}>Loading…</div>}</>
    return (
      <>
        {renderCreateRow(parent, depth)}
        {entries.length === 0 && parent !== '' && !creating && <div className="row muted" style={{ paddingLeft: pad + 14 }}>(empty)</div>}
        {entries.map(e => renderRow(e, depth))}
      </>
    )
  }

  const renderRow = (e: Entry, depth: number): React.ReactNode => {
    const open = tree.expanded.has(e.path)
    const pad = 8 + depth * 14 + (e.isFolder ? 0 : 16)
    const cls = 'row'
      + (selection.has(e.path) ? ' selected' : '')
      + (focused === e.path ? ' focused' : '')
      + (!e.isFolder && p.activePath === e.path ? ' active' : '')
      + (e.isFolder && dropTarget === e.path ? ' drop-target' : '')
    const label = renaming === e.path
      ? <InlineInput
          initial={e.name} folder={e.isFolder}
          onCancel={() => setRenaming(null)}
          onCommit={name => void p.onRename(e.path, name).then(ok => { if (ok) setRenaming(null) })}
        />
      : <span className="name">{e.name}</span>
    return (
      <div key={e.path}>
        <div
          className={cls} style={{ paddingLeft: pad }} role="treeitem" aria-selected={selection.has(e.path)}
          aria-expanded={e.isFolder ? open : undefined} title={e.path}
          draggable={renaming !== e.path}
          onClick={ev => clickRow(ev, e)}
          onContextMenu={ev => { ev.preventDefault(); ev.stopPropagation(); openMenu(ev.clientX, ev.clientY, e.path) }}
          onTouchStart={ev => touchStart(ev, e.path)} onTouchEnd={touchCancel} onTouchMove={touchCancel}
          onDragStart={ev => rowDragStart(ev, e.path)} onDragEnd={endDrag}
          onDragOver={ev => dragOverFolder(ev, e.isFolder ? e.path : parentOf(e.path))}
          onDrop={ev => dropOnFolder(ev, e.isFolder ? e.path : parentOf(e.path))}
        >
          {e.isFolder && <span className="chev">{open ? '▾' : '▸'}</span>}
          <span className="icon">{e.isFolder ? (open ? '📂' : '📁') : '📄'}</span>
          {label}
          {!e.isFolder && p.dirtyPaths.has(e.path) && <span className="dot" aria-label="unsaved">●</span>}
        </div>
        {e.isFolder && open && renderLevel(e.path, depth + 1)}
      </div>
    )
  }

  const current = folderFor(focused)

  return (
    <div className="explorer">
      <div className="explorer-head">
        <span className="title">EXPLORER</span>
        <span className="actions">
          <button title="New file" aria-label="New file" onClick={() => startCreate(current, 'file')}>＋📄</button>
          <button title="New folder" aria-label="New folder" onClick={() => startCreate(current, 'folder')}>＋📁</button>
          <button title="Upload files" aria-label="Upload files" onClick={() => startUpload(current)}>⬆</button>
          <button title="Refresh" aria-label="Refresh" onClick={tree.refreshAll}>⟳</button>
        </span>
      </div>
      {tree.error && <div className="error-note">{tree.error}</div>}
      <div
        className={'tree' + (dropTarget === '' ? ' drop-root' : '')} role="tree" tabIndex={0} aria-multiselectable
        onKeyDown={onKeyDown}
        onClick={ev => { if (ev.target === ev.currentTarget) { setSelection(new Set()); setFocused(null) } }}
        onContextMenu={ev => { ev.preventDefault(); openMenu(ev.clientX, ev.clientY, null) }}
        onTouchStart={ev => { if (ev.target === ev.currentTarget) touchStart(ev, null) }} onTouchEnd={touchCancel} onTouchMove={touchCancel}
        onDragOver={ev => dragOverFolder(ev, '')} onDrop={ev => dropOnFolder(ev, '')}
        onDragLeave={ev => { if (!ev.currentTarget.contains(ev.relatedTarget as Node | null)) endDrag() }}
      >
        {renderLevel('', 0)}
        {tree.children[''] && tree.children[''].length === 0 && !creating && <div className="row muted">No files yet. Right-click, use ＋, or drop files here.</div>}
      </div>
      <input
        ref={fileInput} type="file" multiple hidden
        onChange={ev => {
          const items = Array.from(ev.target.files ?? []).map(file => ({ file, rel: file.name }))
          ev.target.value = ''
          if (items.length) void p.onUpload(uploadTarget.current, items)
        }}
      />
      {menu && <ContextMenu x={menu.x} y={menu.y} items={menu.items} onClose={() => setMenu(null)} />}
    </div>
  )
}

