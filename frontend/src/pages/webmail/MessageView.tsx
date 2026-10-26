import { useEffect, useRef, useState } from 'react'
import { Button, IconButton } from '@/components/common/Button'
import { Icon } from '@/components/common/Icon'
import { LoadingState, ErrorState } from '@/components/data-display/States'
import { DropdownMenu } from '@/components/overlays/DropdownMenu'
import { cx } from '@/lib/cx'
import { formatBytes } from '@/lib/format'
import { webmailApi, type WebmailFolderName, type WebmailMessage } from '@/services/webmail'
import { Avatar } from './Avatar'
import { displayName, formatAddress, frameDocument, fullDate, hasRemoteImages } from './mailUtils'
import type { ComposeMode } from './Composer'
import styles from './webmail.module.css'

interface MessageViewProps {
  message: WebmailMessage | undefined
  loading: boolean
  error: unknown
  onRetry: () => void
  onBack: () => void
  onCompose: (mode: ComposeMode, original: WebmailMessage) => void
  onMove: (folder: WebmailFolderName) => void
  onDelete: () => void
  onToggleFlag: () => void
  onMarkUnread: () => void
}

const moveTargets: WebmailFolderName[] = ['Inbox', 'Archive', 'Junk', 'Trash']

/** Sandboxed frame for HTML mail: no scripts, no access to this page, links open in a new tab. */
function HtmlBody({ html, allowRemote }: { html: string; allowRemote: boolean }) {
  const ref = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(200)

  useEffect(() => {
    const frame = ref.current
    if (!frame) return
    let observer: ResizeObserver | undefined
    const measure = () => {
      const doc = frame.contentDocument
      if (!doc?.body) return
      setHeight(Math.max(120, doc.documentElement.scrollHeight + 4))
      observer?.disconnect()
      observer = new ResizeObserver(() => setHeight(Math.max(120, doc.documentElement.scrollHeight + 4)))
      observer.observe(doc.body)
    }
    frame.addEventListener('load', measure)
    return () => {
      frame.removeEventListener('load', measure)
      observer?.disconnect()
    }
  }, [html, allowRemote])

  return (
    <iframe
      ref={ref}
      className={styles.bodyFrame}
      title="Message body"
      // allow-same-origin only lets us measure the height: without allow-scripts nothing in the mail can run.
      sandbox="allow-same-origin allow-popups allow-popups-to-escape-sandbox"
      referrerPolicy="no-referrer"
      srcDoc={frameDocument(html, allowRemote)}
      style={{ height }}
    />
  )
}

function trust(m: WebmailMessage) {
  if (m.folder === 'Sent' || m.folder === 'Drafts') return null
  const authenticated = m.dkim === 'Pass' || m.spf === 'Pass'
  if (m.dmarc === 'Fail' || m.spf === 'Fail' || m.dkim === 'Fail')
    return <span className={cx(styles.trust, styles.trustWarn)} title={`SPF ${m.spf}, DKIM ${m.dkim}, DMARC ${m.dmarc}`}><Icon name="alert" size={13} /> Sender not verified</span>
  if (authenticated)
    return <span className={cx(styles.trust, styles.trustOk)} title={`SPF ${m.spf}, DKIM ${m.dkim}, DMARC ${m.dmarc}`}><Icon name="shield" size={13} /> Verified sender</span>
  return null
}

export function MessageView({ message: m, loading, error, onRetry, onBack, onCompose, onMove, onDelete, onToggleFlag, onMarkUnread }: MessageViewProps) {
  // The parent remounts this view per message (key), so the choice never carries over.
  const [allowRemote, setAllowRemote] = useState(false)

  if (loading) return <div className={styles.placeholder}><LoadingState label="Opening message" /></div>
  if (error) return <div className={styles.placeholder}><ErrorState error={error} onRetry={onRetry} /></div>
  if (!m) {
    return (
      <div className={styles.placeholder}>
        <Icon name="mailOpen" size={48} />
        <p>Select a message to read it.</p>
      </div>
    )
  }

  const remote = hasRemoteImages(m.html)
  return (
    <>
      <div className={styles.readerToolbar}>
        <IconButton className={styles.backButton} icon="chevronLeft" label="Back to list" onClick={onBack} />
        <IconButton icon="archive" label="Archive" onClick={() => onMove('Archive')} disabled={m.folder === 'Archive'} />
        {m.folder === 'Junk'
          ? <IconButton icon="inbox" label="Not spam" onClick={() => onMove('Inbox')} />
          : <IconButton icon="alert" label="Report spam" onClick={() => onMove('Junk')} disabled={m.folder === 'Sent'} />}
        <IconButton icon="trash" label={m.folder === 'Trash' || m.folder === 'Junk' ? 'Delete forever' : 'Delete'} onClick={onDelete} />
        <IconButton icon="mailbox" label="Mark as unread" onClick={onMarkUnread} />
        <DropdownMenu label="Move to" iconOnly icon="more" variant="ghost"
          items={moveTargets.filter((f) => f !== m.folder).map((f) => ({ id: f, label: `Move to ${f}`, onSelect: () => onMove(f) }))} />
        <span className={styles.toolbarSpacer} />
        <IconButton icon="star" label={m.isFlagged ? 'Remove star' : 'Star'} onClick={onToggleFlag} className={m.isFlagged ? styles.starred : undefined} />
      </div>
      <article className={styles.reader} aria-label={m.subject ?? 'Message'}>
        <h1 className={styles.subject}>{m.subject || '(no subject)'}</h1>
        <div className={styles.sender}>
          <Avatar address={m.from} large />
          <div className={styles.senderMain}>
            <div className={styles.senderName}>
              {displayName(m.from)}
              {m.from?.name && <span>&lt;{m.from.address}&gt;</span>}
            </div>
            <div className={styles.recipients}>
              to {m.to.map(formatAddress).join(', ') || 'undisclosed recipients'}
              {m.cc.length > 0 && <> · cc {m.cc.map(formatAddress).join(', ')}</>}
            </div>
          </div>
          <div className={styles.senderMeta}>
            <time dateTime={m.date}>{fullDate(m.date)}</time>
            {trust(m)}
            {m.receivedOverTls && <span className={styles.trust} title="Delivered over an encrypted connection"><Icon name="lock" size={12} /> Encrypted</span>}
          </div>
        </div>

        {m.folder === 'Junk' && (
          <div className={cx(styles.banner, styles.bannerWarn)}>
            <Icon name="alert" size={16} /> This message was marked as spam. Be careful with links and attachments.
          </div>
        )}
        {remote && !allowRemote && (
          <div className={styles.banner}>
            <Icon name="image" size={16} /> Images from the web are hidden to protect your privacy.
            <button type="button" className={styles.linkButton} onClick={() => setAllowRemote(true)}>Show images</button>
          </div>
        )}

        {m.html ? <HtmlBody html={m.html} allowRemote={allowRemote} /> : <div className={styles.textBody}>{m.text || '(empty message)'}</div>}

        {m.attachments.length > 0 && (
          <div className={styles.attachments} aria-label="Attachments">
            {m.attachments.map((a) => (
              <button key={a.index} type="button" className={styles.attachment} onClick={() => void webmailApi.download(m.id, a)} title={`Download ${a.fileName}`}>
                <Icon name="download" size={18} />
                <span className={styles.attachmentText}>
                  <span className={styles.attachmentName}>{a.fileName}</span>
                  <span className={styles.attachmentSize}>{a.size ? formatBytes(a.size) : a.contentType}</span>
                </span>
              </button>
            ))}
          </div>
        )}

        <div className={styles.replyBar}>
          <Button icon="reply" onClick={() => onCompose('reply', m)}>Reply</Button>
          {m.to.length + m.cc.length > 1 && <Button icon="replyAll" onClick={() => onCompose('replyAll', m)}>Reply all</Button>}
          <Button icon="forward" onClick={() => onCompose('forward', m)}>Forward</Button>
        </div>
      </article>
    </>
  )
}
