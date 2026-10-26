import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { Button, IconButton } from '@/components/common/Button'
import { Icon } from '@/components/common/Icon'
import { cx } from '@/lib/cx'
import { formatBytes } from '@/lib/format'
import { errorMessage } from '@/services/apiClient'
import { webmailApi, type WebmailAddress, type WebmailMessage, type WebmailProfile } from '@/services/webmail'
import { formatAddress, prefixedSubject, quoteForForward, quoteForReply } from './mailUtils'
import styles from './webmail.module.css'

export type ComposeMode = 'new' | 'reply' | 'replyAll' | 'forward'
export interface ComposeRequest {
  key: number
  mode: ComposeMode
  original?: WebmailMessage
  to?: string
}

interface ComposerProps {
  request: ComposeRequest
  profile: WebmailProfile
  onClose: () => void
  onSent: () => void
}

const MAX_ATTACHMENTS_BYTES = 25 * 1024 * 1024

function initialFields(request: ComposeRequest, profile: WebmailProfile) {
  const me = new Set([profile.address, ...profile.aliases].map((a) => a.toLowerCase()))
  const notMe = (list: WebmailAddress[]) => list.filter((a) => !me.has(a.address.toLowerCase()))
  const m = request.original
  if (!m || request.mode === 'new') return { to: request.to ?? '', cc: '', subject: '', body: '' }
  if (request.mode === 'forward') return { to: '', cc: '', subject: prefixedSubject('Fwd', m.subject), body: quoteForForward(m) }

  const replyTarget = m.replyTo.length ? m.replyTo : m.from ? [m.from] : []
  // Replying to your own sent message goes to its original recipients.
  const to = m.from && me.has(m.from.address.toLowerCase()) ? m.to : replyTarget
  const cc = request.mode === 'replyAll' ? notMe([...m.to, ...m.cc]).filter((a) => !to.some((t) => t.address.toLowerCase() === a.address.toLowerCase())) : []
  return { to: to.map(formatAddress).join(', '), cc: cc.map(formatAddress).join(', '), subject: prefixedSubject('Re', m.subject), body: quoteForReply(m) }
}

/** Floating compose window: To/Cc/Bcc, rich text, attachments. Full screen on phones. */
export function Composer({ request, profile, onClose, onSent }: ComposerProps) {
  const initial = initialFields(request, profile)
  const [to, setTo] = useState(initial.to)
  const [cc, setCc] = useState(initial.cc)
  const [bcc, setBcc] = useState('')
  const [showCc, setShowCc] = useState(!!initial.cc)
  const [subject, setSubject] = useState(initial.subject)
  const [from, setFrom] = useState(profile.address)
  const [files, setFiles] = useState<File[]>([])
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [size, setSize] = useState<'normal' | 'expanded' | 'minimized'>('normal')
  const editorRef = useRef<HTMLDivElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const titleId = useId()

  useEffect(() => {
    const editor = editorRef.current
    if (!editor) return
    editor.innerHTML = initial.body
    if (request.mode === 'reply' || request.mode === 'replyAll') {
      editor.focus()
      const range = document.createRange()
      range.setStart(editor, 0)
      range.collapse(true)
      window.getSelection()?.removeAllRanges()
      window.getSelection()?.addRange(range)
    }
    // Initialise once per compose request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [request.key])

  const format = (command: string, value?: string) => {
    editorRef.current?.focus()
    document.execCommand(command, false, value)
  }

  const addLink = () => {
    const url = window.prompt('Link address (https://…)')
    if (url && /^(https?:|mailto:)/i.test(url.trim())) format('createLink', url.trim())
  }

  const addFiles = (list: FileList | null) => {
    if (!list) return
    const next = [...files, ...Array.from(list)]
    if (next.reduce((n, f) => n + f.size, 0) > MAX_ATTACHMENTS_BYTES) {
      setError(`Attachments can be up to ${formatBytes(MAX_ATTACHMENTS_BYTES)} in total.`)
      return
    }
    setError(null)
    setFiles(next)
  }

  const send = async (e: FormEvent) => {
    e.preventDefault()
    if (!to.trim() && !cc.trim() && !bcc.trim()) {
      setError('Add at least one recipient.')
      return
    }
    const editor = editorRef.current
    const html = editor?.innerHTML ?? ''
    const text = editor?.innerText ?? ''
    if (!subject.trim() && !text.trim() && !window.confirm('Send this message without a subject or text?')) return
    setSending(true)
    setError(null)
    try {
      await webmailApi.send({
        to, cc, bcc, subject, text,
        html: `<div style="font-family:Arial,Helvetica,sans-serif;font-size:14px">${html}</div>`,
        from: from !== profile.address ? from : undefined,
        inReplyTo: request.mode === 'reply' || request.mode === 'replyAll' ? request.original?.id : undefined,
        attachments: files,
      })
      onSent()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSending(false)
    }
  }

  const close = () => {
    const dirty = files.length > 0 || (editorRef.current?.innerText.trim().length ?? 0) > 0 && request.mode === 'new'
    if (dirty && !window.confirm('Discard this message?')) return
    onClose()
  }

  const title = subject.trim() || (request.mode === 'new' ? 'New message' : request.mode === 'forward' ? 'Forward' : 'Reply')

  return (
    <section
      className={cx(styles.composer, size === 'expanded' && styles.composerExpanded, size === 'minimized' && styles.composerMinimized)}
      role="dialog"
      aria-labelledby={titleId}
      onKeyDown={(e) => {
        if (e.key === 'Escape') close()
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) void send(e)
      }}
    >
      <header className={styles.composerHeader}>
        <h2 id={titleId} className={styles.composerTitle}>{title}</h2>
        <IconButton size="sm" icon="minimize" label={size === 'minimized' ? 'Restore' : 'Minimize'} onClick={() => setSize(size === 'minimized' ? 'normal' : 'minimized')} />
        <IconButton size="sm" icon="maximize" label={size === 'expanded' ? 'Exit full size' : 'Full size'} onClick={() => setSize(size === 'expanded' ? 'normal' : 'expanded')} />
        <IconButton size="sm" icon="close" label="Close" onClick={close} />
      </header>
      <form className={styles.composerForm} onSubmit={send} hidden={size === 'minimized'}>
        {profile.aliases.length > 0 && (
          <div className={styles.field}>
            <label htmlFor="compose-from">From</label>
            <select id="compose-from" value={from} onChange={(e) => setFrom(e.target.value)}>
              {[profile.address, ...profile.aliases].map((a) => <option key={a} value={a}>{profile.displayName} &lt;{a}&gt;</option>)}
            </select>
          </div>
        )}
        <div className={styles.field}>
          <label htmlFor="compose-to">To</label>
          <input id="compose-to" value={to} onChange={(e) => setTo(e.target.value)} autoFocus={request.mode === 'new' || request.mode === 'forward'}
            placeholder="name@example.com, …" autoComplete="email" spellCheck={false} />
          {!showCc && <button type="button" className={styles.ccToggle} onClick={() => setShowCc(true)}>Cc Bcc</button>}
        </div>
        {showCc && (
          <>
            <div className={styles.field}>
              <label htmlFor="compose-cc">Cc</label>
              <input id="compose-cc" value={cc} onChange={(e) => setCc(e.target.value)} spellCheck={false} />
            </div>
            <div className={styles.field}>
              <label htmlFor="compose-bcc">Bcc</label>
              <input id="compose-bcc" value={bcc} onChange={(e) => setBcc(e.target.value)} spellCheck={false} />
            </div>
          </>
        )}
        <div className={styles.field}>
          <label htmlFor="compose-subject">Subject</label>
          <input id="compose-subject" value={subject} onChange={(e) => setSubject(e.target.value)} maxLength={500} />
        </div>
        <div
          ref={editorRef}
          className={styles.editor}
          contentEditable
          role="textbox"
          aria-multiline="true"
          aria-label="Message"
          data-placeholder="Write your message…"
          suppressContentEditableWarning
        />
        {files.length > 0 && (
          <div className={styles.files}>
            {files.map((f, i) => (
              <span key={`${f.name}-${i}`} className={styles.fileChip}>
                <Icon name="paperclip" size={13} />
                <span>{f.name}</span> ({formatBytes(f.size)})
                <IconButton size="sm" icon="close" label={`Remove ${f.name}`} onClick={() => setFiles(files.filter((_, j) => j !== i))} />
              </span>
            ))}
          </div>
        )}
        {error && <div className={styles.composerError} role="alert">{error}</div>}
        <footer className={styles.composerFooter}>
          <Button type="submit" variant="primary" icon="send" loading={sending}>Send</Button>
          <span className={styles.toolbarSpacer} />
          <button type="button" className={styles.formatButton} onClick={() => format('bold')} aria-label="Bold" title="Bold (Ctrl+B)"><b>B</b></button>
          <button type="button" className={styles.formatButton} onClick={() => format('italic')} aria-label="Italic" title="Italic (Ctrl+I)"><i>I</i></button>
          <button type="button" className={styles.formatButton} onClick={() => format('underline')} aria-label="Underline" title="Underline (Ctrl+U)"><u>U</u></button>
          <button type="button" className={styles.formatButton} onClick={() => format('insertUnorderedList')} aria-label="Bulleted list" title="Bulleted list"><Icon name="list" size={16} /></button>
          <button type="button" className={styles.formatButton} onClick={addLink} aria-label="Insert link" title="Insert link"><Icon name="link" size={16} /></button>
          <button type="button" className={styles.formatButton} onClick={() => fileRef.current?.click()} aria-label="Attach files" title="Attach files"><Icon name="paperclip" size={16} /></button>
          <input ref={fileRef} type="file" multiple hidden onChange={(e) => { addFiles(e.target.files); e.target.value = '' }} />
          <IconButton size="sm" icon="trash" label="Discard" onClick={close} />
        </footer>
      </form>
    </section>
  )
}
