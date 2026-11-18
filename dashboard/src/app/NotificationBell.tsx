import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { NotificationItem } from '../api/types'
import { Icon } from '../components/icons'
import { dateTime } from '../lib/format'

const POLL_MS = 60_000

export function NotificationBell({ projectId }: { projectId: number }) {
  const [items, setItems] = useState<NotificationItem[]>([])
  const [unread, setUnread] = useState(0)
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let stop = false
    const load = () => api.notifications().then((r) => { if (!stop) { setItems(r.notifications); setUnread(r.unread) } }, () => {})
    void load()
    const timer = setInterval(load, POLL_MS)
    return () => { stop = true; clearInterval(timer) }
  }, [projectId])

  useEffect(() => {
    if (!open) return
    const close = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [open])

  const toggle = () => {
    const next = !open
    setOpen(next)
    if (next && unread > 0) void api.markNotificationsRead().then(() => setUnread(0), () => {})
  }

  return (
    <div className="bell" ref={box}>
      <button type="button" className="icon-button bell-button" onClick={toggle} aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'}
        aria-expanded={open} data-unread={unread}>
        <Icon name="bell" />
        {unread > 0 && <span className="bell-count">{unread > 99 ? '99+' : unread}</span>}
      </button>
      {open && (
        <div className="bell-panel" role="dialog" aria-label="Notifications">
          <div className="bell-head">Notifications</div>
          {items.length === 0
            ? <p className="editor-hint">Nothing yet. Alerts that fire or resolve and scheduled strategy notes show up here.</p>
            : (
              <ul className="bell-list">
                {items.map((n) => (
                  <li key={n.id} className={n.unread ? 'unread' : undefined} data-kind={n.kind}>
                    <a href={n.link || '#/'} onClick={() => setOpen(false)}>
                      <span className="strong">{n.title}</span>
                      {n.body && <span className="muted small bell-body">{n.body}</span>}
                      <span className="muted small">{dateTime(n.created_at)}</span>
                    </a>
                  </li>
                ))}
              </ul>
            )}
        </div>
      )}
    </div>
  )
}
