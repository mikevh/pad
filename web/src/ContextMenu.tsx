import { useEffect, useLayoutEffect, useRef, useState } from 'react'

export interface MenuItem {
  label: string
  onSelect: () => void
  danger?: boolean
  separator?: boolean
  hint?: string
}

export default function ContextMenu({ x, y, items, onClose }: { x: number; y: number; items: MenuItem[]; onClose: () => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ x, y })

  useLayoutEffect(() => {
    const r = ref.current?.getBoundingClientRect()
    if (!r) return
    setPos({ x: Math.max(4, Math.min(x, innerWidth - r.width - 4)), y: Math.max(4, Math.min(y, innerHeight - r.height - 4)) })
  }, [x, y])

  useEffect(() => {
    const away = (e: Event) => { if (!ref.current?.contains(e.target as Node)) onClose() }
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    addEventListener('pointerdown', away, true)
    addEventListener('keydown', key)
    addEventListener('blur', onClose)
    addEventListener('resize', onClose)
    return () => {
      removeEventListener('pointerdown', away, true)
      removeEventListener('keydown', key)
      removeEventListener('blur', onClose)
      removeEventListener('resize', onClose)
    }
  }, [onClose])

  return (
    <div className="ctxmenu" ref={ref} style={{ left: pos.x, top: pos.y }} role="menu" onContextMenu={e => e.preventDefault()}>
      {items.map((it, i) => it.separator
        ? <div key={i} className="sep" role="separator" />
        : (
          <button key={i} role="menuitem" className={it.danger ? 'danger' : ''} onClick={() => { onClose(); it.onSelect() }}>
            <span>{it.label}</span>{it.hint && <span className="hint">{it.hint}</span>}
          </button>
        ))}
    </div>
  )
}
