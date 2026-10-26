import { useEffect, useId, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { formatRelative } from '@/lib/format'
import { Button, IconButton } from '../common/Button'
import { Icon, type IconName } from '../common/Icon'
import styles from './overlays.module.css'

export interface AppNotification {
  id: string
  title: string
  message: string
  level: 'Info' | 'Success' | 'Warning' | 'Error'
  link?: string
  createdAt: string
  read: boolean
}

interface NotificationCenterProps {
  notifications: AppNotification[]
  onMarkAllRead: () => void
  onOpenItem?: (id: string) => void
}

const levelIcon: Record<AppNotification['level'], IconName> = { Info: 'info', Success: 'success', Warning: 'alert', Error: 'error' }

/** Bell button with an unread count and a non-modal popover listing notifications. */
export function NotificationCenter({ notifications, onMarkAllRead, onOpenItem }: NotificationCenterProps) {
  const [open, setOpen] = useState(false)
  const panelId = useId()
  const rootRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const unread = notifications.filter((n) => !n.read).length

  useEffect(() => {
    if (!open) return
    const onDoc = (e: MouseEvent) => !rootRef.current?.contains(e.target as Node) && setOpen(false)
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false)
        triggerRef.current?.focus()
      }
    }
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <div ref={rootRef} className={styles.menuRoot}>
      <IconButton
        ref={triggerRef}
        icon="bell"
        label={unread ? `Notifications, ${unread} unread` : 'Notifications'}
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((o) => !o)}
      />
      {unread > 0 && <span className={styles.notifBadge} aria-hidden="true">{unread > 9 ? '9+' : unread}</span>}
      {open && (
        <section id={panelId} className={styles.notifPanel} aria-label="Notifications">
          <header className={styles.notifHeader}>
            <h2 className={styles.notifTitle}>Notifications</h2>
            {unread > 0 && <Button size="sm" variant="ghost" onClick={onMarkAllRead}>Mark all read</Button>}
          </header>
          {notifications.length === 0 ? (
            <p className={styles.notifEmpty}>You're all caught up.</p>
          ) : (
            <ul className={styles.notifList}>
              {notifications.map((n) => {
                const body = (
                  <>
                    <Icon name={levelIcon[n.level]} size={16} className={styles[`notif${n.level}`]} />
                    <span className={styles.notifText}>
                      <span className={styles.notifItemTitle}>{n.title}</span>
                      <span className={styles.notifMessage}>{n.message}</span>
                      <span className={styles.notifTime}>{formatRelative(n.createdAt)}</span>
                    </span>
                    {!n.read && <span className={styles.notifUnreadDot}><span className="mx-visually-hidden">Unread</span></span>}
                  </>
                )
                return (
                  <li key={n.id}>
                    {n.link ? (
                      <Link className={styles.notifItem} to={n.link} onClick={() => { onOpenItem?.(n.id); setOpen(false) }}>{body}</Link>
                    ) : (
                      <div className={styles.notifItem}>{body}</div>
                    )}
                  </li>
                )
              })}
            </ul>
          )}
        </section>
      )}
    </div>
  )
}
