import { DropdownMenu } from '../overlays/DropdownMenu'
import type { Column } from './DataTable'

interface ColumnSelectorProps<T> {
  columns: Column<T>[]
  hidden: string[]
  onChange: (hidden: string[]) => void
}

export function ColumnSelector<T>({ columns, hidden, onChange }: ColumnSelectorProps<T>) {
  const hideable = columns.filter((c) => c.hideable)
  return (
    <DropdownMenu
      label="Columns"
      icon="columns"
      items={hideable.map((c) => ({
        id: c.id,
        label: c.header,
        checked: !hidden.includes(c.id),
        onSelect: () => onChange(hidden.includes(c.id) ? hidden.filter((h) => h !== c.id) : [...hidden, c.id]),
        keepOpen: true,
      }))}
    />
  )
}
