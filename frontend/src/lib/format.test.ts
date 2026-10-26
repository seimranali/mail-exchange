import { describe, expect, it } from 'vitest'
import { daysUntil, formatBytes, humanize, percent } from './format'

describe('format helpers', () => {
  it('formats bytes with binary units', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(50 * 1024 ** 3)).toBe('50.0 GB')
  })

  it('humanizes PascalCase enum values', () => {
    expect(humanize('SecurityAdmin')).toBe('Security admin')
    expect(humanize('mailbox.created')).toBe('Mailbox created')
  })

  it('clamps percentages and handles zero totals', () => {
    expect(percent(5, 0)).toBe(0)
    expect(percent(150, 100)).toBe(100)
  })

  it('computes days until a date', () => {
    const now = Date.UTC(2026, 0, 1)
    expect(daysUntil(new Date(now + 3 * 86_400_000).toISOString(), now)).toBe(3)
    expect(daysUntil(null)).toBeNull()
  })
})
