import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { axeViolations } from '@/test/axe'
import { DataTable, type Column } from './DataTable'
import { ProgressBar } from './Metrics'
import { Pagination } from './Pagination'
import type { SortState } from './SortableHeader'
import { StatusBadge } from './StatusBadge'

interface Row { id: string; name: string; size: number }
const rows: Row[] = [{ id: '1', name: 'alpha', size: 2 }, { id: '2', name: 'beta', size: 1 }]
const columns: Column<Row>[] = [
  { id: 'name', header: 'Name', cell: (r) => r.name, sortKey: 'name' },
  { id: 'size', header: 'Size', cell: (r) => r.size, align: 'end', hideable: true },
]

describe('DataTable', () => {
  it('renders an accessible table with caption and sortable headers', async () => {
    function Harness() {
      const [sort, setSort] = useState<SortState>({ key: 'name', direction: 'asc' })
      return <DataTable caption="Mailboxes" columns={columns} rows={rows} rowKey={(r) => r.id} sort={sort} onSortChange={setSort} />
    }
    const { container } = render(<Harness />)
    const table = screen.getByRole('table', { name: 'Mailboxes' })
    const nameHeader = within(table).getByRole('columnheader', { name: /Name/ })
    expect(nameHeader).toHaveAttribute('aria-sort', 'ascending')
    await userEvent.click(within(nameHeader).getByRole('button'))
    expect(nameHeader).toHaveAttribute('aria-sort', 'descending')
    expect(await axeViolations(container)).toEqual([])
  })

  it('hides columns and activates rows from the keyboard', async () => {
    const onRowClick = vi.fn()
    render(<DataTable caption="T" columns={columns} rows={rows} rowKey={(r) => r.id} hiddenColumns={['size']} onRowClick={onRowClick} rowLabel={(r) => `Open ${r.name}`} />)
    expect(screen.queryByRole('columnheader', { name: 'Size' })).not.toBeInTheDocument()
    const row = screen.getByRole('row', { name: 'Open beta' })
    row.focus()
    await userEvent.keyboard('{Enter}')
    expect(onRowClick).toHaveBeenCalledWith(rows[1])
  })

  it('shows empty and error states', () => {
    const { rerender } = render(<DataTable caption="T" columns={columns} rows={[]} rowKey={(r) => r.id} />)
    expect(screen.getByText('Nothing to show')).toBeInTheDocument()
    rerender(<DataTable caption="T" columns={columns} rows={undefined} rowKey={(r) => r.id} error={new Error('Boom')} onRetry={vi.fn()} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Boom')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})

describe('Pagination', () => {
  it('disables boundaries and reports the range', async () => {
    const onPage = vi.fn()
    render(<Pagination page={1} pageSize={25} totalCount={60} onPageChange={onPage} />)
    expect(screen.getByRole('navigation', { name: 'Pagination' })).toHaveTextContent('1–25 of 60')
    expect(screen.getByRole('button', { name: 'Previous page' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Next page' }))
    expect(onPage).toHaveBeenCalledWith(2)
  })
})

describe('ProgressBar / StatusBadge', () => {
  it('exposes progress values and humanized status text', () => {
    render(<><ProgressBar label="Storage" value={45} max={50} valueText="45 GB of 50 GB" /><StatusBadge status="InvalidCredentials" /></>)
    const bar = screen.getByRole('progressbar', { name: 'Storage' })
    expect(bar).toHaveAttribute('aria-valuetext', '45 GB of 50 GB')
    expect(screen.getByText('Invalid credentials')).toBeInTheDocument()
  })
})
