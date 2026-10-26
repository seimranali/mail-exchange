import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { DataTable, type Column } from '@/components/data-display/DataTable'
import { StatusBadge } from '@/components/data-display/StatusBadge'
import { ToggleSwitch } from '@/components/forms/Choice'
import { FormField } from '@/components/forms/FormField'
import { MultiSelect } from '@/components/forms/MultiSelect'
import { Select } from '@/components/forms/Select'
import { NumberInput, TextInput } from '@/components/forms/TextInput'
import { ConfirmationDialog } from '@/components/overlays/ConfirmationDialog'
import { Modal } from '@/components/overlays/Dialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { formatDate, humanize } from '@/lib/format'
import { zodResolver } from '@/lib/zodResolver'
import { complianceApi, mailboxesApi } from '@/services/endpoints'
import type { LegalHold, RetentionPolicy } from '@/types/api'
import styles from './pages.module.css'

export default function CompliancePage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'ComplianceOfficer')
  const [editing, setEditing] = useState<RetentionPolicy | 'new' | null>(null)
  const [creatingHold, setCreatingHold] = useState(false)
  const [releasing, setReleasing] = useState<LegalHold | null>(null)

  const policies = useQuery({ queryKey: ['compliance', 'retention'], queryFn: complianceApi.retentionPolicies })
  const holds = useQuery({ queryKey: ['compliance', 'holds'], queryFn: complianceApi.legalHolds })
  const release = useApiMutation({
    mutationFn: (id: string) => complianceApi.releaseLegalHold(id),
    invalidate: [['compliance', 'holds']],
    successMessage: 'Legal hold released',
    onSuccess: () => setReleasing(null),
  })

  const policyColumns: Column<RetentionPolicy>[] = [
    { id: 'name', header: 'Policy', cell: (p) => (
      <div className={styles.cellStack}>
        <span className={styles.cellPrimary}>{p.name}</span>
        {p.description && <span className={styles.cellSecondary}>{p.description}</span>}
      </div>
    ) },
    { id: 'period', header: 'Period', cell: (p) => `${p.retentionDays.toLocaleString()} days` },
    { id: 'action', header: 'Then', cell: (p) => humanize(p.action) },
    { id: 'scope', header: 'Scope', cell: (p) => (p.appliesToAllMailboxes ? 'All mailboxes' : 'Selected'), hideOnMobile: true },
    { id: 'state', header: 'State', cell: (p) => <StatusBadge status={p.isEnabled ? 'Active' : 'Disabled'} /> },
    ...(canManage ? [{ id: 'edit', header: 'Actions', align: 'end' as const, cell: (p: RetentionPolicy) => (
      <Button size="sm" variant="ghost" icon="edit" onClick={() => setEditing(p)} aria-label={`Edit ${p.name}`}>Edit</Button>
    ) }] : []),
  ]

  const holdColumns: Column<LegalHold>[] = [
    { id: 'name', header: 'Hold', cell: (h) => (
      <div className={styles.cellStack}>
        <span className={styles.cellPrimary}>{h.name}</span>
        <span className={styles.cellSecondary}>{h.caseReference}</span>
      </div>
    ) },
    { id: 'mailboxes', header: 'Mailboxes', cell: (h) => h.mailboxCount, align: 'end' },
    { id: 'started', header: 'Started', cell: (h) => formatDate(h.startsAt), hideOnMobile: true },
    { id: 'status', header: 'Status', cell: (h) => <StatusBadge status={h.status} tone={h.status === 'Active' ? 'accent' : 'neutral'} /> },
    ...(canManage ? [{ id: 'release', header: 'Actions', align: 'end' as const, cell: (h: LegalHold) => h.status === 'Active' && (
      <Button size="sm" variant="ghost" onClick={() => setReleasing(h)}>Release</Button>
    ) }] : []),
  ]

  return (
    <ContentContainer>
      <PageHeader title="Compliance" description="Retention policies and legal holds. Mailboxes under hold cannot be deleted and their data is preserved." />
      <section className={styles.section} aria-labelledby="retention-heading">
        <div className={styles.inlineGroup} style={{ alignItems: 'center' }}>
          <h2 id="retention-heading" className={styles.sectionTitle}>Retention policies</h2>
          {canManage && <Button icon="plus" size="sm" onClick={() => setEditing('new')}>New policy</Button>}
        </div>
        <DataTable caption="Retention policies" columns={policyColumns} rows={policies.data} rowKey={(p) => p.id} loading={policies.isFetching} error={policies.error} onRetry={() => policies.refetch()} />
      </section>
      <section className={styles.section} aria-labelledby="holds-heading">
        <div className={styles.inlineGroup} style={{ alignItems: 'center' }}>
          <h2 id="holds-heading" className={styles.sectionTitle}>Legal holds</h2>
          {canManage && <Button icon="plus" size="sm" onClick={() => setCreatingHold(true)}>New legal hold</Button>}
        </div>
        <DataTable caption="Legal holds" columns={holdColumns} rows={holds.data} rowKey={(h) => h.id} loading={holds.isFetching} error={holds.error} onRetry={() => holds.refetch()} />
      </section>

      {editing && <RetentionPolicyModal policy={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      <LegalHoldModal open={creatingHold} onClose={() => setCreatingHold(false)} />
      <ConfirmationDialog open={!!releasing} title="Release legal hold?" confirmLabel="Release hold" loading={release.isPending}
        message={<>Releasing <strong>{releasing?.caseReference}</strong> lets retention policies apply to its mailboxes again. This is recorded in the audit log.</>}
        onConfirm={() => releasing && release.mutate(releasing.id)} onCancel={() => setReleasing(null)} />
    </ContentContainer>
  )
}

const policySchema = z.object({
  name: z.string().trim().min(1, 'Name is required.').max(200),
  description: z.string().max(1000),
  retentionDays: z.coerce.number<number>().int().min(1, 'At least 1 day.').max(36500, 'At most 100 years.'),
  action: z.enum(['Retain', 'Delete', 'Archive']),
  appliesToAllMailboxes: z.boolean(),
  isEnabled: z.boolean(),
})
type PolicyForm = z.infer<typeof policySchema>

function RetentionPolicyModal({ policy, onClose }: { policy: RetentionPolicy | null; onClose: () => void }) {
  const { register, handleSubmit, control, formState: { errors } } = useForm<PolicyForm>({
    resolver: zodResolver(policySchema),
    defaultValues: policy
      ? { ...policy, description: policy.description ?? '' }
      : { name: '', description: '', retentionDays: 365, action: 'Retain', appliesToAllMailboxes: true, isEnabled: true },
  })
  const save = useApiMutation({
    mutationFn: (v: PolicyForm) => {
      const body = { ...v, description: v.description || null }
      return policy ? complianceApi.updateRetentionPolicy(policy.id, body) : complianceApi.createRetentionPolicy(body)
    },
    invalidate: [['compliance', 'retention']],
    successMessage: policy ? 'Policy updated' : 'Policy created',
    errorTitle: 'Could not save policy',
    onSuccess: onClose,
  })
  return (
    <Modal open onClose={onClose} title={policy ? 'Edit retention policy' : 'New retention policy'} dismissible={!save.isPending}
      footer={<><Button onClick={onClose} disabled={save.isPending}>Cancel</Button><Button variant="primary" type="submit" form="policy-form" loading={save.isPending}>Save policy</Button></>}>
      <form id="policy-form" noValidate className={styles.formStack} onSubmit={handleSubmit((v) => save.mutate(v))}>
        <FormField label="Name" error={errors.name?.message} required><TextInput {...register('name')} /></FormField>
        <FormField label="Description" error={errors.description?.message}><TextInput {...register('description')} /></FormField>
        <div className={styles.formGrid}>
          <FormField label="Retention period" error={errors.retentionDays?.message} required>
            <NumberInput {...register('retentionDays')} suffix={<span className={styles.suffixText}>days</span>} />
          </FormField>
          <FormField label="After the period" error={errors.action?.message}>
            <Select {...register('action')} options={[{ value: 'Retain', label: 'Keep (retain)' }, { value: 'Archive', label: 'Archive' }, { value: 'Delete', label: 'Delete' }]} />
          </FormField>
        </div>
        <Controller control={control} name="appliesToAllMailboxes" render={({ field }) => (
          <ToggleSwitch label="Apply to all mailboxes" checked={field.value} onChange={field.onChange} />
        )} />
        <Controller control={control} name="isEnabled" render={({ field }) => (
          <ToggleSwitch label="Policy enabled" checked={field.value} onChange={field.onChange} />
        )} />
      </form>
    </Modal>
  )
}

const holdSchema = z.object({
  name: z.string().trim().min(1, 'Name is required.'),
  caseReference: z.string().trim().min(1, 'Case reference is required.'),
  description: z.string(),
  mailboxIds: z.array(z.string()).min(1, 'Select at least one mailbox.'),
})
type HoldForm = z.infer<typeof holdSchema>

function LegalHoldModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const mailboxes = useQuery({ queryKey: ['mailboxes', 'all-for-hold'], queryFn: () => mailboxesApi.list({ pageSize: 200 }), enabled: open })
  const { register, handleSubmit, control, reset, formState: { errors } } = useForm<HoldForm>({
    resolver: zodResolver(holdSchema), defaultValues: { name: '', caseReference: '', description: '', mailboxIds: [] },
  })
  const close = () => { reset(); onClose() }
  const create = useApiMutation({
    mutationFn: (v: HoldForm) => complianceApi.createLegalHold({ ...v, description: v.description || null }),
    invalidate: [['compliance', 'holds']],
    successMessage: 'Legal hold created',
    errorTitle: 'Could not create legal hold',
    onSuccess: close,
  })
  return (
    <Modal open={open} onClose={close} title="New legal hold" size="lg" dismissible={!create.isPending}
      description="Preserves all content in the selected mailboxes until the hold is released."
      footer={<><Button onClick={close} disabled={create.isPending}>Cancel</Button><Button variant="primary" type="submit" form="hold-form" loading={create.isPending}>Create hold</Button></>}>
      <form id="hold-form" noValidate className={styles.formStack} onSubmit={handleSubmit((v) => create.mutate(v))}>
        <div className={styles.formGrid}>
          <FormField label="Name" error={errors.name?.message} required><TextInput {...register('name')} /></FormField>
          <FormField label="Case reference" error={errors.caseReference?.message} required><TextInput {...register('caseReference')} placeholder="CASE-2026-001" /></FormField>
        </div>
        <FormField label="Description" error={errors.description?.message}><TextInput {...register('description')} /></FormField>
        <FormField label="Mailboxes" error={errors.mailboxIds?.message} required hint="Type to filter, use arrow keys and Enter to select.">
          <Controller control={control} name="mailboxIds" render={({ field }) => (
            <MultiSelect value={field.value} onChange={field.onChange} placeholder={mailboxes.isLoading ? 'Loading mailboxes…' : 'Select mailboxes'}
              options={(mailboxes.data?.items ?? []).map((m) => ({ value: m.id, label: `${m.displayName} (${m.emailAddress})` }))} />
          )} />
        </FormField>
      </form>
    </Modal>
  )
}
