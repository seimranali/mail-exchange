import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { axeViolations } from '@/test/axe'
import { ToggleSwitch, RadioGroup, Checkbox } from './Choice'
import { FormField } from './FormField'
import { MultiSelect } from './MultiSelect'
import { Select } from './Select'
import { EmailInput, PasswordInput, SearchInput } from './TextInput'

describe('FormField', () => {
  it('associates label, hint and error with the input', () => {
    render(
      <FormField label="Email" hint="Work address" error="Required" required>
        <EmailInput />
      </FormField>,
    )
    const input = screen.getByLabelText(/Email/)
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(input).toHaveAttribute('aria-required', 'true')
    expect(input).toHaveAccessibleDescription('Work address Required')
    expect(screen.getByRole('alert')).toHaveTextContent('Required')
  })

  it('has no axe violations for a composed form', async () => {
    const { container } = render(
      <form>
        <FormField label="Name" hint="Shown to recipients"><EmailInput /></FormField>
        <FormField label="Password" error="Too short"><PasswordInput /></FormField>
        <FormField label="Plan"><Select options={[{ value: 'a', label: 'A' }]} /></FormField>
        <Checkbox label="Default domain" description="Used for new mailboxes" />
        <RadioGroup legend="Theme" name="t" value="light" onChange={() => undefined} options={[{ value: 'light', label: 'Light' }, { value: 'dark', label: 'Dark' }]} />
        <ToggleSwitch label="Enabled" checked onChange={() => undefined} />
      </form>,
    )
    expect(await axeViolations(container)).toEqual([])
  })
})

describe('PasswordInput', () => {
  it('toggles visibility with an accessible pressed state', async () => {
    render(<FormField label="Password"><PasswordInput /></FormField>)
    const input = screen.getByLabelText('Password')
    expect(input).toHaveAttribute('type', 'password')
    await userEvent.click(screen.getByRole('button', { name: 'Show password' }))
    expect(input).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Hide password' })).toHaveAttribute('aria-pressed', 'true')
  })
})

describe('SearchInput', () => {
  it('clears on Escape', async () => {
    const onChange = vi.fn()
    render(<SearchInput value="abc" onValueChange={onChange} placeholder="Search mailboxes" />)
    const input = screen.getByRole('searchbox', { name: 'Search mailboxes' })
    input.focus()
    await userEvent.keyboard('{Escape}')
    expect(onChange).toHaveBeenCalledWith('')
  })
})

describe('ToggleSwitch', () => {
  it('exposes switch role and toggles with keyboard', async () => {
    function Harness() {
      const [on, setOn] = useState(false)
      return <ToggleSwitch label="Require MFA" checked={on} onChange={setOn} />
    }
    render(<Harness />)
    const sw = screen.getByRole('switch', { name: 'Require MFA' })
    expect(sw).toHaveAttribute('aria-checked', 'false')
    sw.focus()
    await userEvent.keyboard(' ')
    expect(sw).toHaveAttribute('aria-checked', 'true')
  })
})

describe('MultiSelect', () => {
  it('selects options with the keyboard and removes chips', async () => {
    function Harness() {
      const [value, setValue] = useState<string[]>([])
      return (
        <FormField label="Mailboxes">
          <MultiSelect value={value} onChange={setValue} options={[{ value: '1', label: 'Alpha' }, { value: '2', label: 'Beta' }]} />
        </FormField>
      )
    }
    render(<Harness />)
    const combo = screen.getByRole('combobox', { name: 'Mailboxes' })
    await userEvent.click(combo)
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    await userEvent.keyboard('{ArrowDown}{Enter}')
    expect(screen.getByRole('option', { name: /Beta/ })).toHaveAttribute('aria-selected', 'true')
    await userEvent.click(screen.getByRole('button', { name: 'Remove Beta' }))
    expect(screen.getByRole('option', { name: /Beta/ })).toHaveAttribute('aria-selected', 'false')
  })
})
