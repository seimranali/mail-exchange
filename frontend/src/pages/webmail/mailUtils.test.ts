import { describe, expect, it } from 'vitest'
import type { WebmailMessage } from '@/services/webmail'
import { frameDocument, hasRemoteImages, initials, prefixedSubject, quoteForReply, sanitizeHtml } from './mailUtils'

const message = (overrides: Partial<WebmailMessage> = {}): WebmailMessage => ({
  id: '1', folder: 'Inbox', from: { name: 'Ana Lima', address: 'ana@example.com' }, to: [{ name: null, address: 'me@example.com' }], cc: [], replyTo: [],
  subject: 'Hello', date: '2026-10-25T10:00:00Z', html: null, text: 'Line 1\nLine <2>', attachments: [], isRead: true, isFlagged: false, isAnswered: false,
  messageId: null, receivedOverTls: true, spf: 'Pass', dkim: 'Pass', dmarc: 'Pass', spamScore: null, ...overrides,
})

describe('sanitizeHtml', () => {
  it('removes scripts, event handlers and javascript links', () => {
    const out = sanitizeHtml('<p onclick="x()">Hi<script>alert(1)</script><img src="x" onerror="alert(1)"><a href="javascript:alert(1)">l</a><iframe src="https://e"></iframe></p>')
    expect(out).not.toMatch(/script|onclick|onerror|javascript:|iframe/i)
    expect(out).toContain('Hi')
  })

  it('keeps ordinary formatting and links', () => {
    expect(sanitizeHtml('<b>bold</b> <a href="https://example.com">x</a>')).toBe('<b>bold</b> <a href="https://example.com">x</a>')
  })
})

describe('compose helpers', () => {
  it('prefixes subjects once', () => {
    expect(prefixedSubject('Re', 'Hello')).toBe('Re: Hello')
    expect(prefixedSubject('Re', 'RE: Hello')).toBe('RE: Hello')
    expect(prefixedSubject('Fwd', null)).toBe('Fwd: ')
  })

  it('quotes plain text safely', () => {
    const html = quoteForReply(message())
    expect(html).toContain('Ana Lima &lt;ana@example.com&gt; wrote:')
    expect(html).toContain('Line 1<br>Line &lt;2&gt;')
  })

  it('builds initials', () => {
    expect(initials({ name: 'Ana Lima', address: 'ana@example.com' })).toBe('AL')
    expect(initials({ name: null, address: 'support@example.com' })).toBe('SU')
    expect(initials(null)).toBe('US') // "(unknown sender)"
  })
})

describe('reading frame', () => {
  it('blocks remote content until allowed', () => {
    expect(frameDocument('<p>x</p>', false)).toContain("img-src data:;")
    expect(frameDocument('<p>x</p>', true)).toContain('img-src data: https: http:')
    expect(hasRemoteImages('<img src="https://tracker.example/p.gif">')).toBe(true)
    expect(hasRemoteImages('<img src="data:image/png;base64,AAA">')).toBe(false)
  })
})
