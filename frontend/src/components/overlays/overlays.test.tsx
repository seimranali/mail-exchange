import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { axeViolations } from '@/test/axe'
import { ConfirmationDialog } from './ConfirmationDialog'
import { Modal } from './Dialog'
import { DropdownMenu } from './DropdownMenu'
import { ToastProvider, useToast } from './Toast'

describe('DropdownMenu', () => {
  it('follows the menu button keyboard pattern', async () => {
    const onEdit = vi.fn()
    render(
      <DropdownMenu label="Actions" items={[
        { id: 'edit', label: 'Edit', onSelect: onEdit },
        { id: 'delete', label: 'Delete', onSelect: vi.fn() },
      ]} />,
    )
    const trigger = screen.getByRole('button', { name: 'Actions' })
    expect(trigger).toHaveAttribute('aria-haspopup', 'menu')
    trigger.focus()
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('menu')).toBeInTheDocument()
    expect(screen.getByRole('menuitem', { name: 'Edit' })).toHaveFocus()
    await userEvent.keyboard('{ArrowDown}')
    expect(screen.getByRole('menuitem', { name: 'Delete' })).toHaveFocus()
    await userEvent.keyboard('{Home}{Enter}')
    expect(onEdit).toHaveBeenCalledOnce()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  it('closes on Escape and returns focus', async () => {
    render(<DropdownMenu label="Actions" items={[{ id: 'a', label: 'A', onSelect: vi.fn() }]} />)
    const trigger = screen.getByRole('button', { name: 'Actions' })
    await userEvent.click(trigger)
    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })
})

describe('Modal', () => {
  it('is labelled by its title and has no axe violations', async () => {
    const { baseElement } = render(
      <Modal open onClose={vi.fn()} title="New mailbox" description="Provisioned on the mail server">
        <p>Body</p>
      </Modal>,
    )
    const dialog = screen.getByRole('dialog', { name: 'New mailbox' })
    expect(dialog).toHaveAccessibleDescription('Provisioned on the mail server')
    expect(await axeViolations(baseElement)).toEqual([])
  })

  it('restores focus to the trigger when closed', async () => {
    function Harness() {
      const [open, setOpen] = useState(false)
      return (
        <>
          <button onClick={() => setOpen(true)}>Open</button>
          <Modal open={open} onClose={() => setOpen(false)} title="Dialog">content</Modal>
        </>
      )
    }
    render(<Harness />)
    const trigger = screen.getByRole('button', { name: 'Open' })
    await userEvent.click(trigger)
    await userEvent.click(screen.getByRole('button', { name: 'Close' }))
    await waitFor(() => expect(trigger).toHaveFocus())
  })
})

describe('ConfirmationDialog', () => {
  it('uses alertdialog and puts focus on the safe choice', () => {
    render(<ConfirmationDialog open title="Delete mailbox?" message="Gone forever" destructive onConfirm={vi.fn()} onCancel={vi.fn()} />)
    expect(screen.getByRole('alertdialog', { name: 'Delete mailbox?' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
  })
})

describe('Toast', () => {
  it('announces via a live region and errors use role=alert', async () => {
    function Trigger() {
      const toast = useToast()
      return <button onClick={() => toast.error('Could not save', 'Server unavailable')}>Fail</button>
    }
    render(<ToastProvider><Trigger /></ToastProvider>)
    await userEvent.click(screen.getByRole('button', { name: 'Fail' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Could not save')
    expect(screen.getByRole('region', { name: 'Notifications' })).toHaveAttribute('aria-live', 'polite')
  })
})
