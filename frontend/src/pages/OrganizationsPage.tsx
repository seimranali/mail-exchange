import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { DataTable, type Column } from '@/components/data-display/DataTable'
import { Pagination } from '@/components/data-display/Pagination'
import { StatusBadge } from '@/components/data-display/StatusBadge'
import { FormField } from '@/components/forms/FormField'
import { Select } from '@/components/forms/Select'
import { NumberInput, SearchInput, TextInput } from '@/components/forms/TextInput'
import { Modal } from '@/components/overlays/Dialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { formatDate } from '@/lib/format'
import { zodResolver } from '@/lib/zodResolver'
import { organizationsApi } from '@/services/endpoints'
import type { Organization } from '@/types/api'
import styles from './pages.module.css'

export default function OrganizationsPage() {
  const { hasRole } = useAuth()
  const isPlatformAdmin = hasRole('PlatformAdmin')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [creating, setCreating] = useState(false)
  const debounced = useDebouncedValue(search)
  const orgs = useQuery({
    queryKey: ['organizations', debounced, page],
    queryFn: () => organizationsApi.list({ search: debounced, page, pageSize: 25 }),
    placeholderData: keepPreviousData,
  })

  const columns: Column<Organization>[] = [
    { id: 'name', header: 'Organization', cell: (o) => (
      <div className={styles.cellStack}>
        <span className={styles.cellPrimary}>{o.name}</span>
        <span className={styles.cellSecondary}>{o.slug}</span>
      </div>
    ) },
    { id: 'status', header: 'Status', cell: (o) => <StatusBadge status={o.status} /> },
    { id: 'domains', header: 'Domains', cell: (o) => o.domainCount, align: 'end' },
    { id: 'mailboxes', header: 'Mailboxes', cell: (o) => o.mailboxCount.toLocaleString(), align: 'end' },
    { id: 'created', header: 'Created', cell: (o) => formatDate(o.createdAt), hideOnMobile: true },
  ]

  return (
    <ContentContainer>
      <PageHeader title="Organizations"
        description={isPlatformAdmin ? 'All tenants on this installation.' : 'Your organization.'}
        actions={isPlatformAdmin && <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New organization</Button>} />
      <DataTable caption="Organizations" columns={columns} rows={orgs.data?.items} rowKey={(o) => o.id} loading={orgs.isFetching} error={orgs.error} onRetry={() => orgs.refetch()}
        toolbar={isPlatformAdmin && <SearchInput className={styles.toolbarSearch} inputSize="sm" value={search} onValueChange={(v) => { setSearch(v); setPage(1) }} placeholder="Search organizations" />}
        footer={orgs.data && orgs.data.totalCount > 25 && <Pagination page={page} pageSize={25} totalCount={orgs.data.totalCount} onPageChange={setPage} />} />
      {isPlatformAdmin && <CreateOrganizationModal open={creating} onClose={() => setCreating(false)} />}
    </ContentContainer>
  )
}

const schema = z.object({
  name: z.string().trim().min(1, 'Name is required.').max(200),
  slug: z.string().trim().toLowerCase().regex(/^[a-z0-9][a-z0-9-]{1,62}$/, '2–63 lowercase letters, digits or hyphens.'),
  plan: z.enum(['Starter', 'Business', 'Enterprise']),
  mailboxLimit: z.coerce.number<number>().int().min(1).max(1_000_000),
})
type OrgForm = z.infer<typeof schema>

function CreateOrganizationModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { register, handleSubmit, reset, setValue, getFieldState, formState: { errors } } = useForm<OrgForm>({
    resolver: zodResolver(schema), defaultValues: { name: '', slug: '', plan: 'Starter', mailboxLimit: 50 },
  })
  const close = () => { reset(); onClose() }
  const create = useApiMutation({
    mutationFn: (v: OrgForm) => organizationsApi.create(v),
    invalidate: [['organizations']],
    successMessage: (o) => `${o.name} created`,
    errorTitle: 'Could not create organization',
    onSuccess: close,
  })
  return (
    <Modal open={open} onClose={close} title="New organization" dismissible={!create.isPending}
      footer={<><Button onClick={close} disabled={create.isPending}>Cancel</Button><Button variant="primary" type="submit" form="org-form" loading={create.isPending}>Create</Button></>}>
      <form id="org-form" noValidate className={styles.formStack} onSubmit={handleSubmit((v) => create.mutate(v))}>
        <FormField label="Name" error={errors.name?.message} required>
          <TextInput {...register('name', {
            onChange: (e) => {
              // Suggest a slug until the user edits it themselves.
              if (!getFieldState('slug').isDirty) setValue('slug', String(e.target.value).toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 63))
            },
          })} />
        </FormField>
        <FormField label="Slug" error={errors.slug?.message} required hint="Short unique identifier used in URLs and logs.">
          <TextInput {...register('slug')} spellCheck={false} />
        </FormField>
        <div className={styles.formGrid}>
          <FormField label="Plan"><Select {...register('plan')} options={['Starter', 'Business', 'Enterprise'].map((p) => ({ value: p, label: p }))} /></FormField>
          <FormField label="Mailbox limit" error={errors.mailboxLimit?.message} required><NumberInput {...register('mailboxLimit')} /></FormField>
        </div>
      </form>
    </Modal>
  )
}
