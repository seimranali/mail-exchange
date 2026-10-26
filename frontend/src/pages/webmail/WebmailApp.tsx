import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Navigate, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { IconButton } from '@/components/common/Button'
import { Icon, type IconName } from '@/components/common/Icon'
import { ErrorState, LoadingState } from '@/components/data-display/States'
import { DropdownMenu } from '@/components/overlays/DropdownMenu'
import { useToast } from '@/components/overlays/Toast'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { useTheme } from '@/hooks/useTheme'
import { cx } from '@/lib/cx'
import { formatBytes, percent } from '@/lib/format'
import { errorMessage } from '@/services/apiClient'
import { setWebmailSignedOutHandler, webmailApi, webmailToken, type WebmailFolderName, type WebmailListItem, type WebmailMessage } from '@/services/webmail'
import { Avatar } from './Avatar'
import { Composer, type ComposeMode, type ComposeRequest } from './Composer'
import { MessageView } from './MessageView'
import { displayName, listDate } from './mailUtils'
import styles from './webmail.module.css'

const folders: { id: WebmailFolderName; label: string; icon: IconName }[] = [
  { id: 'Inbox', label: 'Inbox', icon: 'inbox' },
  { id: 'Sent', label: 'Sent', icon: 'send' },
  { id: 'Drafts', label: 'Drafts', icon: 'draft' },
  { id: 'Archive', label: 'Archive', icon: 'archive' },
  { id: 'Junk', label: 'Spam', icon: 'alert' },
  { id: 'Trash', label: 'Trash', icon: 'trash' },
]

const PAGE_SIZE = 50
const REFRESH_MS = 30_000

function toFolder(value: string | undefined): WebmailFolderName {
  return folders.find((f) => f.id.toLowerCase() === value?.toLowerCase())?.id ?? 'Inbox'
}

export default function WebmailApp() {
  const params = useParams()
  const folder = toFolder(params.folder)
  const [searchParams, setSearchParams] = useSearchParams()
  const selectedId = searchParams.get('id')
  const navigate = useNavigate()
  const toast = useToast()
  const queryClient = useQueryClient()
  const { theme, setTheme } = useTheme()

  const [search, setSearch] = useState('')
  const debouncedSearch = useDebouncedValue(search.trim(), 300)
  const [page, setPage] = useState(1)
  const [checked, setChecked] = useState<Set<string>>(new Set())
  const [sidebarOpen, setSidebarOpen] = useState(false)
  const [compose, setCompose] = useState<ComposeRequest | null>(null)
  const [signedIn, setSignedIn] = useState(() => !!webmailToken.get())

  useEffect(() => {
    setWebmailSignedOutHandler(() => setSignedIn(false))
    return () => setWebmailSignedOutHandler(null)
  }, [])

  useEffect(() => {
    setPage(1)
    setChecked(new Set())
  }, [folder, debouncedSearch])

  const profile = useQuery({ queryKey: ['webmail', 'me'], queryFn: webmailApi.me, enabled: signedIn, staleTime: 60_000 })
  const folderCounts = useQuery({ queryKey: ['webmail', 'folders'], queryFn: webmailApi.folders, enabled: signedIn, refetchInterval: REFRESH_MS })
  const list = useQuery({
    queryKey: ['webmail', 'list', folder, debouncedSearch, page],
    queryFn: () => webmailApi.list({ folder, search: debouncedSearch || undefined, page, pageSize: PAGE_SIZE }),
    enabled: signedIn,
    refetchInterval: REFRESH_MS,
    placeholderData: keepPreviousData,
  })
  const message = useQuery({
    queryKey: ['webmail', 'message', selectedId],
    queryFn: () => webmailApi.get(selectedId!),
    enabled: signedIn && !!selectedId,
    staleTime: Infinity,
  })

  const inboxUnread = folderCounts.data?.find((f) => f.folder === 'Inbox')?.unread ?? 0
  useEffect(() => {
    document.title = inboxUnread > 0 ? `Inbox (${inboxUnread}) · Mail` : 'Mail'
  }, [inboxUnread])

  // Opening a message marks it read on the server: reflect that in the list and counts.
  useEffect(() => {
    if (!message.data) return
    queryClient.setQueriesData<{ items: WebmailListItem[] }>({ queryKey: ['webmail', 'list'] }, (old) =>
      old ? { ...old, items: old.items.map((i) => (i.id === message.data.id ? { ...i, isRead: true } : i)) } : old)
    void queryClient.invalidateQueries({ queryKey: ['webmail', 'folders'] })
  }, [message.data, queryClient])

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['webmail'], predicate: (q) => q.queryKey[1] !== 'message' })

  const select = (id: string | null) => {
    const next = new URLSearchParams(searchParams)
    if (id) next.set('id', id)
    else next.delete('id')
    setSearchParams(next)
  }

  const openFolder = (id: WebmailFolderName) => {
    setSidebarOpen(false)
    setSearch('')
    navigate(id === 'Inbox' ? '/mail' : `/mail/${id.toLowerCase()}`)
  }

  const items = useMemo(() => list.data?.items ?? [], [list.data])

  /** After moving/deleting the open message, open the next one (or close the reader). */
  const advanceFrom = (ids: string[]) => {
    if (!selectedId || !ids.includes(selectedId)) return
    const index = items.findIndex((i) => i.id === selectedId)
    const next = items.slice(index + 1).find((i) => !ids.includes(i.id)) ?? items.slice(0, index).reverse().find((i) => !ids.includes(i.id))
    select(window.matchMedia('(max-width: 899px)').matches ? null : next?.id ?? null)
  }

  const action = useMutation({
    mutationFn: async (a: { kind: 'move'; ids: string[]; folder: WebmailFolderName } | { kind: 'delete'; ids: string[] } | { kind: 'flags'; ids: string[]; isRead?: boolean; isFlagged?: boolean }) => {
      if (a.kind === 'move') await webmailApi.move(a.ids, a.folder)
      else if (a.kind === 'delete') await webmailApi.remove(a.ids)
      else await webmailApi.setFlags(a.ids, { isRead: a.isRead, isFlagged: a.isFlagged })
      return a
    },
    onMutate: (a) => {
      if (a.kind !== 'flags') advanceFrom(a.ids)
    },
    onSuccess: (a) => {
      setChecked(new Set())
      const n = a.ids.length === 1 ? 'Conversation' : `${a.ids.length} conversations`
      if (a.kind === 'move') toast.success(`${n} moved to ${folders.find((f) => f.id === a.folder)?.label ?? a.folder}`)
      if (a.kind === 'delete') toast.success(folder === 'Trash' || folder === 'Junk' ? `${n} deleted forever` : `${n} moved to Trash`)
      if (a.kind === 'flags') a.ids.forEach((id) => queryClient.setQueryData<WebmailMessage>(['webmail', 'message', id], (old) =>
        old ? { ...old, isRead: a.isRead ?? old.isRead, isFlagged: a.isFlagged ?? old.isFlagged } : old))
      void refresh()
    },
    onError: (e) => toast.error('Action failed', errorMessage(e)),
  })

  if (!signedIn) return <Navigate to="/mail/login" replace />

  const signOut = () => {
    webmailToken.set(null)
    queryClient.removeQueries({ queryKey: ['webmail'] })
    setSignedIn(false)
  }

  const openCompose = (mode: ComposeMode, original?: WebmailMessage) => setCompose({ key: Date.now(), mode, original })

  const checkedIds = [...checked]
  const allChecked = items.length > 0 && items.every((i) => checked.has(i.id))
  const toggleAll = () => setChecked(allChecked ? new Set() : new Set(items.map((i) => i.id)))
  const toggle = (id: string) => setChecked((prev) => {
    const next = new Set(prev)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    return next
  })

  const total = list.data?.total ?? 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const folderInfo = folders.find((f) => f.id === folder)!
  const usage = profile.data ? percent(profile.data.usedBytes, profile.data.quotaBytes) : 0
  const showRecipient = folder === 'Sent' || folder === 'Drafts'

  return (
    <div className={cx(styles.shell, selectedId ? styles.hasSelection : styles.noSelection)}>
      <header className={styles.topbar}>
        <IconButton className={styles.menuButton} icon="menu" label="Folders" onClick={() => setSidebarOpen(true)} />
        <div className={styles.brand}>
          <img src="/mx-icon.svg" alt="" width={28} height={28} />
          <span>Mail</span>
        </div>
        <div className={styles.search} role="search">
          <Icon name="search" size={18} />
          <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={`Search ${folderInfo.label.toLowerCase()}`} aria-label="Search mail" />
        </div>
        <div className={styles.topActions}>
          <IconButton icon={theme === 'dark' ? 'sun' : 'moon'} label={theme === 'dark' ? 'Light theme' : 'Dark theme'}
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} />
          <div className={styles.account}>
            <Avatar address={profile.data ? { name: profile.data.displayName, address: profile.data.address } : null} />
            <div className={styles.accountText}>
              <span className={styles.accountName}>{profile.data?.displayName}</span>
              <span className={styles.accountAddress}>{profile.data?.address}</span>
            </div>
            <DropdownMenu label="Account" iconOnly icon="chevronDown" variant="ghost"
              items={[{ id: 'signout', label: 'Sign out', icon: 'logout', onSelect: signOut }]} />
          </div>
        </div>
      </header>

      <div className={cx(styles.scrim, sidebarOpen && styles.scrimVisible)} onClick={() => setSidebarOpen(false)} aria-hidden="true" />
      <nav className={cx(styles.sidebar, sidebarOpen && styles.sidebarOpen)} aria-label="Folders">
        <button type="button" className={styles.compose} onClick={() => { setSidebarOpen(false); openCompose('new') }} title="Compose">
          <Icon name="edit" size={20} />
          <span className={styles.composeLabel}>Compose</span>
        </button>
        <ul className={styles.folders}>
          {folders.map((f) => {
            const count = folderCounts.data?.find((c) => c.folder === f.id)
            const badge = f.id === 'Drafts' ? count?.total : f.id === 'Inbox' || f.id === 'Junk' ? count?.unread : 0
            return (
              <li key={f.id}>
                <button type="button" className={cx(styles.folder, folder === f.id && styles.folderActive)} onClick={() => openFolder(f.id)}
                  aria-current={folder === f.id ? 'page' : undefined} title={f.label}>
                  <Icon name={f.icon} size={18} />
                  <span className={styles.folderName}>{f.label}</span>
                  {!!badge && <span className={styles.folderCount} aria-label={`${badge} unread`}>{badge}</span>}
                </button>
              </li>
            )
          })}
        </ul>
        {profile.data && (
          <div className={styles.storage}>
            <div className={styles.storageBar} role="progressbar" aria-valuenow={usage} aria-valuemin={0} aria-valuemax={100} aria-label="Storage used">
              <span style={{ width: `${Math.max(usage, 1)}%` }} />
            </div>
            {formatBytes(profile.data.usedBytes)} of {formatBytes(profile.data.quotaBytes)} used
          </div>
        )}
      </nav>

      <section className={styles.listPane} aria-label={`${folderInfo.label} messages`}>
        <div className={styles.listHeader}>
          <input type="checkbox" className={styles.checkbox} checked={allChecked} onChange={toggleAll} aria-label="Select all" disabled={items.length === 0} />
          {checked.size > 0 ? (
            <>
              <span className={styles.listTitle}>{checked.size} selected</span>
              <IconButton size="sm" icon="mailOpen" label="Mark as read" onClick={() => action.mutate({ kind: 'flags', ids: checkedIds, isRead: true })} />
              <IconButton size="sm" icon="mailbox" label="Mark as unread" onClick={() => action.mutate({ kind: 'flags', ids: checkedIds, isRead: false })} />
              {folder !== 'Archive' && <IconButton size="sm" icon="archive" label="Archive" onClick={() => action.mutate({ kind: 'move', ids: checkedIds, folder: 'Archive' })} />}
              <IconButton size="sm" icon="trash" label="Delete" onClick={() => action.mutate({ kind: 'delete', ids: checkedIds })} />
            </>
          ) : (
            <>
              <h1 className={styles.listTitle}>
                {debouncedSearch ? 'Search results' : folderInfo.label}
                {total > 0 && <small>{total}</small>}
              </h1>
              <IconButton size="sm" icon="refresh" label="Refresh" onClick={() => void refresh()} loading={list.isFetching && !list.isLoading} />
            </>
          )}
        </div>

        {list.isLoading && <LoadingState label="Loading messages" />}
        {list.error && <ErrorState error={list.error} onRetry={() => list.refetch()} />}
        {list.data && items.length === 0 && (
          <div className={styles.listEmpty}>
            <Icon name={debouncedSearch ? 'search' : folderInfo.icon} size={40} />
            <strong>{debouncedSearch ? 'No messages match your search' : `${folderInfo.label} is empty`}</strong>
            {folder === 'Inbox' && !debouncedSearch && <span>New mail will appear here.</span>}
          </div>
        )}
        {items.length > 0 && (
          <ul className={styles.messages}>
            {items.map((m) => {
              const who = showRecipient ? (m.to.length ? `To: ${m.to.map((t) => displayName(t)).join(', ')}` : '(no recipients)') : displayName(m.from)
              return (
                <li
                  key={m.id}
                  className={cx(styles.item, !m.isRead && styles.itemUnread, selectedId === m.id && styles.itemSelected)}
                  onClick={() => select(m.id)}
                  onKeyDown={(e) => { if (e.key === 'Enter' && e.target === e.currentTarget) select(m.id) }}
                  tabIndex={0}
                  aria-current={selectedId === m.id ? 'true' : undefined}
                >
                  <span className={styles.itemCheck} onClick={(e) => e.stopPropagation()}>
                    <input type="checkbox" className={styles.checkbox} checked={checked.has(m.id)} onChange={() => toggle(m.id)} aria-label={`Select ${m.subject ?? 'message'}`} />
                  </span>
                  <Avatar address={showRecipient ? m.to[0] : m.from} />
                  <div className={styles.itemBody}>
                    <div className={styles.itemTop}>
                      <span className={styles.itemFrom}>
                        {!m.isRead && <span className="mx-visually-hidden">Unread, </span>}
                        {who}
                      </span>
                      <span className={styles.itemIcons}>
                        {m.hasAttachments && <Icon name="paperclip" size={14} label="Has attachments" />}
                        {m.isAnswered && <Icon name="reply" size={14} label="Replied" />}
                        {m.isFlagged && <Icon name="star" size={14} label="Starred" className={styles.starred} />}
                      </span>
                      <time className={styles.itemDate} dateTime={m.date}>{listDate(m.date)}</time>
                    </div>
                    <div className={styles.itemSubject}>{m.subject || '(no subject)'}</div>
                    {m.snippet && <div className={styles.itemSnippet}>{m.snippet}</div>}
                  </div>
                </li>
              )
            })}
          </ul>
        )}
        {total > PAGE_SIZE && (
          <div className={styles.listFooter}>
            <span>{(page - 1) * PAGE_SIZE + 1}–{Math.min(page * PAGE_SIZE, total)} of {total}</span>
            <span>
              <IconButton size="sm" icon="chevronLeft" label="Newer" onClick={() => setPage(page - 1)} disabled={page <= 1} />
              <IconButton size="sm" icon="chevronRight" label="Older" onClick={() => setPage(page + 1)} disabled={page >= pages} />
            </span>
          </div>
        )}
      </section>

      <section className={styles.readerPane} aria-label="Reading pane">
        <MessageView
          key={selectedId ?? 'none'}
          message={selectedId ? message.data : undefined}
          loading={!!selectedId && message.isLoading}
          error={selectedId ? message.error : null}
          onRetry={() => message.refetch()}
          onBack={() => select(null)}
          onCompose={openCompose}
          onMove={(f) => selectedId && action.mutate({ kind: 'move', ids: [selectedId], folder: f })}
          onDelete={() => selectedId && action.mutate({ kind: 'delete', ids: [selectedId] })}
          onToggleFlag={() => message.data && action.mutate({ kind: 'flags', ids: [message.data.id], isFlagged: !message.data.isFlagged })}
          onMarkUnread={() => {
            if (!selectedId) return
            action.mutate({ kind: 'flags', ids: [selectedId], isRead: false })
            select(null)
          }}
        />
      </section>

      {compose && profile.data && (
        <Composer
          key={compose.key}
          request={compose}
          profile={profile.data}
          onClose={() => setCompose(null)}
          onSent={() => {
            setCompose(null)
            toast.success('Message sent')
            void refresh()
          }}
        />
      )}
    </div>
  )
}
