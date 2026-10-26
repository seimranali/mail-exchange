import type { WebmailAddress, WebmailMessage } from '@/services/webmail'

export function displayName(a: WebmailAddress | null | undefined): string {
  if (!a) return '(unknown sender)'
  return a.name?.trim() || a.address
}

export function formatAddress(a: WebmailAddress): string {
  return a.name ? `${a.name.replace(/[",]/g, '')} <${a.address}>` : a.address
}

export function initials(a: WebmailAddress | null | undefined): string {
  const source = (a?.name?.trim() || a?.address.split('@')[0] || displayName(a)).replace(/[^\p{L}\p{N} ]/gu, ' ').trim()
  const parts = source.split(/\s+/).filter(Boolean)
  const first = parts[0]
  const last = parts[parts.length - 1]
  if (!first || !last) return '?'
  return (parts.length > 1 ? first.charAt(0) + last.charAt(0) : first.slice(0, 2)).toUpperCase()
}

/** Stable color tone (0-5) per address, so a sender always gets the same avatar color. */
export function toneFor(address: string | undefined): number {
  let h = 0
  for (const c of address ?? '') h = (h * 31 + c.charCodeAt(0)) >>> 0
  return h % 6
}

/** "14:05" today, "Oct 25" this year, "25/10/2025" otherwise. */
export function listDate(value: string, now = new Date()): string {
  const d = new Date(value)
  if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  if (d.getFullYear() === now.getFullYear()) return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  return d.toLocaleDateString()
}

export function fullDate(value: string): string {
  return new Date(value).toLocaleString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

const BLOCKED_TAGS = ['script', 'style', 'iframe', 'object', 'embed', 'form', 'input', 'button', 'textarea', 'select', 'link', 'meta', 'base', 'frame', 'frameset', 'svg', 'math']

/**
 * Strips active content from untrusted mail HTML before it is placed into the composer (our own
 * document). The reading pane never needs this: it renders mail in a sandboxed frame.
 */
export function sanitizeHtml(html: string): string {
  const doc = new DOMParser().parseFromString(html, 'text/html')
  doc.querySelectorAll(BLOCKED_TAGS.join(',')).forEach((el) => el.remove())
  doc.body.querySelectorAll('*').forEach((el) => {
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase()
      const value = attr.value.trim().toLowerCase()
      if (name.startsWith('on') || name === 'srcdoc' || name === 'formaction') el.removeAttribute(attr.name)
      else if ((name === 'href' || name === 'src' || name === 'xlink:href') && (value.startsWith('javascript:') || value.startsWith('vbscript:') || (value.startsWith('data:') && !value.startsWith('data:image/'))))
        el.removeAttribute(attr.name)
    }
  })
  return doc.body.innerHTML
}

function bodyAsHtml(m: WebmailMessage): string {
  if (m.html) return sanitizeHtml(m.html)
  return escapeHtml(m.text ?? '').replace(/\n/g, '<br>')
}

export function quoteForReply(m: WebmailMessage): string {
  return `<br><br><div>On ${escapeHtml(fullDate(m.date))}, ${escapeHtml(m.from ? formatAddress(m.from) : 'someone')} wrote:</div><blockquote>${bodyAsHtml(m)}</blockquote>`
}

export function quoteForForward(m: WebmailMessage): string {
  const lines = [
    `From: ${m.from ? formatAddress(m.from) : ''}`,
    `Date: ${fullDate(m.date)}`,
    `Subject: ${m.subject ?? ''}`,
    `To: ${m.to.map(formatAddress).join(', ')}`,
    ...(m.cc.length ? [`Cc: ${m.cc.map(formatAddress).join(', ')}`] : []),
  ]
  return `<br><br><div>---------- Forwarded message ----------<br>${lines.map(escapeHtml).join('<br>')}</div><br>${bodyAsHtml(m)}`
}

export function prefixedSubject(prefix: 'Re' | 'Fwd', subject: string | null): string {
  const s = subject?.trim() ?? ''
  return new RegExp(`^${prefix}:`, 'i').test(s) ? s : `${prefix}: ${s}`
}

/**
 * Document shown in the sandboxed reading frame. Remote images stay blocked (a CSP in the frame)
 * until the user allows them: they reveal when and where a message was opened.
 */
export function frameDocument(html: string, allowRemote: boolean): string {
  const csp = allowRemote
    ? "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline' https:; font-src https: data:"
    : "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:"
  return `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${csp}"><base target="_blank"><style>
html,body{margin:0;padding:0;background:#fff;color:#1f2937;font:14px/1.5 Arial,Helvetica,sans-serif;overflow-wrap:anywhere}
body{padding:4px 2px}img{max-width:100%;height:auto}table{max-width:100%}pre{white-space:pre-wrap}
blockquote{margin:8px 0 0;padding-left:12px;border-left:3px solid #cbd5e1;color:#475569}a{color:#1d4ed8}
</style></head><body>${html}</body></html>`
}

export function hasRemoteImages(html: string | null): boolean {
  return !!html && /<img[^>]+src\s*=\s*["']?\s*https?:/i.test(html) || /url\(\s*["']?https?:/i.test(html ?? '')
}
