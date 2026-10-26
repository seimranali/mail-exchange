import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { ColumnSelector } from '@/components/data-display/ColumnSelector'
import { DataTable, type Column } from '@/components/data-display/DataTable'
import { FilterPanel } from '@/components/data-display/FilterPanel'
import { ProgressBar } from '@/components/data-display/Metrics'
import { Pagination } from '@/components/data-display/Pagination'
import type { SortState } from '@/components/data-display/SortableHeader'
import { EmptyState, ErrorState, LoadingState } from '@/components/data-display/States'
import { MessageList } from '@/components/mail-administration/MessageList'
import { mailboxesApi, systemApi } from '@/services/endpoints'
import { useQuery } from '@tanstack/react-query'
import { StatusBadge } from '@/components/data-display/StatusBadge'
import { ToggleSwitch } from '@/components/forms/Choice'
import { FormField } from '@/components/forms/FormField'
import { Select } from '@/components/forms/Select'
import { NumberInput, PasswordInput, SearchInput, TextInput } from '@/components/forms/TextInput'
import { MailboxCard } from '@/components/mail-administration/MailboxCard'
import { ConfirmationDialog } from '@/components/overlays/ConfirmationDialog'
import { Drawer, Modal } from '@/components/overlays/Dialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { useDebouncedValue } from '@/hooks/useDebouncedValue'
import { ContentContainer, PageHeader } from '@/layouts/PageLayout'
import { formatBytes, formatRelative, humanize } from '@/lib/format'
import { zodResolver } from '@/lib/zodResolver'
import { useAddAlias, useCreateMailbox, useDeleteMailbox, useDomainOptions, useMailboxes, useUpdateMailbox } from '@/modules/mailboxes'
import type { Mailbox, MailboxStatus, MailboxType } from '@/types/api'
import { Avatar } from './webmail/Avatar'
import styles from './pages.module.css'

const GB = 1024 ** 3

const statusOptions = (['Active', 'Disabled', 'Provisioning'] as const).map((s) => ({ value: s, label: s }))
const typeOptions = (['User', 'Shared', 'Resource'] as const).map((t) => ({ value: t, label: humanize(t) }))

export default function MailboxesPage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'MailAdmin')

  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<MailboxStatus | ''>('')
  const [type, setType] = useState<MailboxType | ''>('')
  const [sort, setSort] = useState<SortState>({ key: 'emailAddress', direction: 'asc' })
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(25)
  const [hidden, setHidden] = useState<string[]>([])
  const [creating, setCreating] = useState(false)
  const [selected, setSelected] = useState<Mailbox | null>(null)
  const debouncedSearch = useDebouncedValue(search)

  const query = useMailboxes({
    search: debouncedSearch, status: status || undefined, type: type || undefined,
    sort: sort.key, desc: sort.direction === 'desc', page, pageSize,
  })

  const resetPage = <T,>(setter: (v: T) => void) => (v: T) => {
    setter(v)
    setPage(1)
  }

  const columns: Column<Mailbox>[] = [
    {
      id: 'mailbox', header: 'Mailbox', sortKey: 'emailAddress', cell: (m) => (
        <div className={styles.identityCell}>
          <Avatar address={{ name: m.displayName, address: m.emailAddress }} />
          <div className={styles.cellStack}>
            <span className={styles.cellPrimary}>{m.displayName}</span>
            <span className={styles.cellSecondary}>{m.emailAddress}</span>
          </div>
        </div>
      ),
    },
    { id: 'type', header: 'Type', cell: (m) => humanize(m.mailboxType), hideable: true, hideOnMobile: true },
    { id: 'status', header: 'Status', cell: (m) => <StatusBadge status={m.status} /> },
    {
      id: 'signin', header: 'Sign-in', hideable: true, hideOnMobile: true,
      cell: (m) => <StatusBadge status={m.hasClientPassword ? 'Enabled' : 'No password'} tone={m.hasClientPassword ? 'success' : 'neutral'} dot={false} />,
    },
    {
      id: 'usage', header: 'Storage', sortKey: 'usedBytes', hideable: true, hideOnMobile: true, cell: (m) => (
        <div className={styles.usageCell}>
          <ProgressBar label={`Storage for ${m.emailAddress}`} value={m.usedBytes} max={m.quotaBytes} valueText={`${formatBytes(m.usedBytes)} of ${formatBytes(m.quotaBytes)}`} />
          <span className={styles.cellSecondary}>{formatBytes(m.usedBytes)} / {formatBytes(m.quotaBytes)}</span>
        </div>
      ),
    },
    { id: 'aliases', header: 'Aliases', cell: (m) => m.aliases.length || '—', hideable: true, hideOnMobile: true, align: 'end' },
    { id: 'lastLogin', header: 'Last sign-in', cell: (m) => formatRelative(m.lastLoginAt), hideable: true, hideOnMobile: true },
    { id: 'created', header: 'Created', sortKey: 'createdAt', cell: (m) => formatRelative(m.createdAt), hideable: true, hideOnMobile: true },
  ]

  const activeFilters = [debouncedSearch, status, type].filter(Boolean).length

  return (
    <ContentContainer>
      <PageHeader
        title="Mailboxes"
        description="User, shared and resource mailboxes across your domains."
        actions={
          <>
            <Button icon="external" onClick={() => window.open('/mail/login', '_blank', 'noopener')}>Open webmail</Button>
            {canManage && <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New mailbox</Button>}
          </>
        }
      />
      <DataTable
        caption="Mailboxes"
        columns={columns}
        rows={query.data?.items}
        rowKey={(m) => m.id}
        loading={query.isFetching}
        error={query.error}
        onRetry={() => query.refetch()}
        sort={sort}
        onSortChange={(s) => { setSort(s); setPage(1) }}
        hiddenColumns={hidden}
        onRowClick={setSelected}
        rowLabel={(m) => `${m.displayName}, ${m.emailAddress}, ${m.status}. Open details`}
        empty={
          <EmptyState icon="mailbox" title={activeFilters ? 'No mailboxes match these filters' : 'No mailboxes yet'}
            description={activeFilters ? 'Try a different search or clear the filters.' : 'Create the first mailbox for one of your domains.'}
            action={canManage && !activeFilters ? <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>New mailbox</Button> : undefined} />
        }
        toolbar={
          <>
            <FilterPanel activeCount={activeFilters} onClear={() => { setSearch(''); setStatus(''); setType(''); setPage(1) }}>
              <SearchInput className={styles.toolbarSearch} value={search} onValueChange={resetPage(setSearch)} placeholder="Search name or address" inputSize="sm" />
              <Select className={styles.toolbarFilter} inputSize="sm" aria-label="Filter by status" placeholder="All statuses" options={statusOptions}
                value={status} onChange={(e) => resetPage(setStatus)(e.target.value as MailboxStatus | '')} />
              <Select className={styles.toolbarFilter} inputSize="sm" aria-label="Filter by type" placeholder="All types" options={typeOptions}
                value={type} onChange={(e) => resetPage(setType)(e.target.value as MailboxType | '')} />
            </FilterPanel>
            <ColumnSelector columns={columns} hidden={hidden} onChange={setHidden} />
          </>
        }
        footer={query.data && query.data.totalCount > 0 && (
          <Pagination page={page} pageSize={pageSize} totalCount={query.data.totalCount} onPageChange={setPage} onPageSizeChange={(s) => { setPageSize(s); setPage(1) }} />
        )}
      />

      <CreateMailboxModal open={creating} onClose={() => setCreating(false)} />
      <MailboxDrawer
        mailbox={selected ? query.data?.items.find((m) => m.id === selected.id) ?? selected : null}
        canManage={canManage}
        onClose={() => setSelected(null)}
      />
    </ContentContainer>
  )
}

/* ---- Create ---------------------------------------------------------- */

const createSchema = z.object({
  displayName: z.string().trim().min(1, 'Display name is required.').max(200),
  localPart: z.string().trim().toLowerCase().regex(/^[a-z0-9]([a-z0-9._+-]{0,62}[a-z0-9])?$/, 'Use letters, digits, dots, hyphens or underscores.'),
  domainId: z.string().min(1, 'Choose a domain.'),
  mailboxType: z.enum(['User', 'Shared', 'Resource']),
  quotaGb: z.coerce.number<number>().int('Whole numbers only.').min(1, 'At least 1 GB.').max(1024, 'At most 1024 GB.'),
})
type CreateForm = z.infer<typeof createSchema>

function CreateMailboxModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const domains = useDomainOptions()
  const { register, handleSubmit, reset, watch, formState: { errors } } = useForm<CreateForm>({
    resolver: zodResolver(createSchema),
    defaultValues: { mailboxType: 'User', quotaGb: 50, domainId: '' },
  })
  const close = () => { reset(); onClose() }
  const create = useCreateMailbox(close)
  const domainName = domains.data?.find((d) => d.value === watch('domainId'))?.label

  return (
    <Modal open={open} onClose={close} title="New mailbox" description="The mailbox is provisioned on the assigned mail server." dismissible={!create.isPending}
      footer={
        <>
          <Button onClick={close} disabled={create.isPending}>Cancel</Button>
          <Button variant="primary" type="submit" form="create-mailbox" loading={create.isPending}>Create mailbox</Button>
        </>
      }>
      <form id="create-mailbox" noValidate className={styles.formStack}
        onSubmit={handleSubmit((v) => create.mutate({ displayName: v.displayName, localPart: v.localPart, domainId: v.domainId, mailboxType: v.mailboxType, quotaBytes: v.quotaGb * GB }))}>
        <FormField label="Display name" error={errors.displayName?.message} required>
          <TextInput {...register('displayName')} autoComplete="off" />
        </FormField>
        <div className={styles.formGrid}>
          <FormField label="Address" error={errors.localPart?.message} required hint={domainName ? `@${domainName}` : undefined}>
            <TextInput {...register('localPart')} autoComplete="off" spellCheck={false} />
          </FormField>
          <FormField label="Domain" error={errors.domainId?.message} required>
            <Select {...register('domainId')} placeholder={domains.isLoading ? 'Loading…' : 'Choose a domain'} options={domains.data ?? []} />
          </FormField>
        </div>
        <div className={styles.formGrid}>
          <FormField label="Type" error={errors.mailboxType?.message}>
            <Select {...register('mailboxType')} options={typeOptions} />
          </FormField>
          <FormField label="Quota" error={errors.quotaGb?.message} required>
            <NumberInput {...register('quotaGb')} min={1} max={1024} suffix={<span className={styles.suffixText}>GB</span>} />
          </FormField>
        </div>
      </form>
    </Modal>
  )
}

/* ---- Recent messages ------------------------------------------------- */

function RecentMessages({ mailboxId }: { mailboxId: string }) {
  const messages = useQuery({ queryKey: ['mailboxes', mailboxId, 'messages'], queryFn: () => mailboxesApi.messages(mailboxId) })
  return (
    <section className={styles.formStack} aria-labelledby="recent-messages">
      <h3 id="recent-messages" className={styles.sectionTitle}>Recent messages</h3>
      <p className={styles.muted}>Delivery and authentication details only. Message content is not shown, and viewing this list is recorded in the audit log.</p>
      {messages.isLoading && <LoadingState label="Loading messages" />}
      {messages.error && <ErrorState error={messages.error} onRetry={() => messages.refetch()} />}
      {messages.data && messages.data.items.length === 0 && <p className={styles.muted}>No messages received yet.</p>}
      {messages.data && messages.data.items.length > 0 && <MessageList messages={messages.data.items} />}
    </section>
  )
}

/* ---- Mail app access ------------------------------------------------- */

// Shown to admins so they can hand users their client settings.
/** The mail server's public host name (from its certificate), falling back to this site's host. */
function useMailHost(): string {
  const certificate = useQuery({ queryKey: ['system', 'mail-certificate'], queryFn: systemApi.mailCertificate, staleTime: 5 * 60_000 })
  return certificate.data?.hostname ?? window.location.hostname
}

const passwordSchema = z.object({
  password: z.string().min(12, 'Use at least 12 characters.').max(256),
  confirm: z.string(),
}).refine((v) => v.password === v.confirm, { path: ['confirm'], message: 'Passwords do not match.' })

function MailAppAccess({ mailbox }: { mailbox: Mailbox }) {
  const [confirmDisable, setConfirmDisable] = useState(false)
  const MAIL_HOST = useMailHost()
  const { register, handleSubmit, reset, formState: { errors } } = useForm<z.infer<typeof passwordSchema>>({ resolver: zodResolver(passwordSchema) })
  const setPassword = useApiMutation({
    mutationFn: (password: string | null) => mailboxesApi.setClientPassword(mailbox.id, password),
    invalidate: [['mailboxes']],
    successMessage: (_, p) => (p ? 'Mailbox password set' : 'Sign-in disabled'),
    errorTitle: 'Could not update sign-in',
    onSuccess: () => { reset(); setConfirmDisable(false) },
  })

  return (
    <section className={styles.formStack} aria-labelledby="mail-app-access">
      <h3 id="mail-app-access" className={styles.sectionTitle}>Sign-in: webmail and mail apps</h3>
      <p className={styles.muted}>
        {mailbox.hasClientPassword
          ? 'This mailbox can sign in to webmail and to mail apps (Outlook, Apple Mail, phones). Setting a new password signs out other devices at their next connection.'
          : 'This mailbox cannot sign in to webmail or mail apps until a password is set. Incoming mail is still delivered.'}
      </p>
      {mailbox.hasClientPassword && (
        <dl className={styles.settingsList}>
          <dt>Webmail</dt><dd><a href="/mail/login" target="_blank" rel="noreferrer">{window.location.host}/mail</a></dd>
          <dt>Username</dt><dd><code>{mailbox.emailAddress}</code></dd>
          <dt>Outgoing (SMTP)</dt><dd><code>{MAIL_HOST}</code> port 465 (SSL/TLS) or 587 (STARTTLS)</dd>
          <dt>Incoming (IMAP)</dt><dd><code>{MAIL_HOST}</code> port 993 (SSL/TLS) or 143 (STARTTLS)</dd>
        </dl>
      )}
      <form noValidate className={styles.formStack} onSubmit={handleSubmit((v) => setPassword.mutate(v.password))}>
        <div className={styles.formGrid}>
          <FormField label={mailbox.hasClientPassword ? 'New password' : 'Password'} error={errors.password?.message}>
            <PasswordInput {...register('password')} autoComplete="new-password" />
          </FormField>
          <FormField label="Confirm password" error={errors.confirm?.message}>
            <PasswordInput {...register('confirm')} autoComplete="new-password" />
          </FormField>
        </div>
        <div className={styles.formActions}>
          {mailbox.hasClientPassword && <Button variant="ghost" onClick={() => setConfirmDisable(true)}>Disable sign-in</Button>}
          <Button type="submit" loading={setPassword.isPending && setPassword.variables !== null}>{mailbox.hasClientPassword ? 'Change password' : 'Set password'}</Button>
        </div>
      </form>
      <ConfirmationDialog open={confirmDisable} title="Disable sign-in?" destructive confirmLabel="Disable" loading={setPassword.isPending}
        message={<>Webmail and mail apps signed in as <strong>{mailbox.emailAddress}</strong> will no longer be able to send or read mail. Incoming mail is still delivered.</>}
        onConfirm={() => setPassword.mutate(null)} onCancel={() => setConfirmDisable(false)} />
    </section>
  )
}

/* ---- Send test message ----------------------------------------------- */

const testSchema = z.object({ to: z.email('Enter a valid email address.') })

function SendTestMessage({ mailboxId }: { mailboxId: string }) {
  const { register, handleSubmit, reset, formState: { errors } } = useForm<z.infer<typeof testSchema>>({ resolver: zodResolver(testSchema) })
  const send = useApiMutation({
    mutationFn: (to: string) => mailboxesApi.sendTest(mailboxId, to),
    invalidate: [['mail-queue']],
    successMessage: (_, to) => `Test message queued for ${to}. Follow it in the Mail queue.`,
    errorTitle: 'Could not send test message',
    onSuccess: () => reset(),
  })
  return (
    <form noValidate className={styles.formStack} onSubmit={handleSubmit((v) => send.mutate(v.to))}>
      <h3 className={styles.sectionTitle}>Send test message</h3>
      <div className={styles.inlineGroup}>
        <FormField label="Recipient" error={errors.to?.message} hint="Verifies outbound delivery and DKIM signing from this address.">
          <TextInput type="email" {...register('to')} spellCheck={false} />
        </FormField>
        <Button type="submit" icon="arrowRight" loading={send.isPending}>Send</Button>
      </div>
    </form>
  )
}

/* ---- Detail / edit --------------------------------------------------- */

const editSchema = z.object({
  displayName: z.string().trim().min(1, 'Display name is required.').max(200),
  quotaGb: z.coerce.number<number>().min(1, 'At least 1 GB.').max(1024),
})
type EditForm = z.infer<typeof editSchema>

function MailboxDrawer({ mailbox, canManage, onClose }: { mailbox: Mailbox | null; canManage: boolean; onClose: () => void }) {
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [aliasLocal, setAliasLocal] = useState('')
  const [enabled, setEnabled] = useState(true)
  const { register, handleSubmit, reset, formState: { errors, isDirty } } = useForm<EditForm>({ resolver: zodResolver(editSchema) })
  const update = useUpdateMailbox()
  const remove = useDeleteMailbox(() => { setConfirmDelete(false); onClose() })
  const addAlias = useAddAlias(() => setAliasLocal(''))
  const [lastId, setLastId] = useState<string | null>(null)

  // Reset the form whenever a different mailbox is opened.
  if (mailbox && mailbox.id !== lastId) {
    setLastId(mailbox.id)
    reset({ displayName: mailbox.displayName, quotaGb: Math.round(mailbox.quotaBytes / GB) })
    setEnabled(mailbox.status !== 'Disabled')
  }

  if (!mailbox) return <Drawer open={false} onClose={onClose} title="" />
  const domainId = mailbox.domainId
  const domainName = mailbox.emailAddress.split('@')[1]

  return (
    <>
      <Drawer open onClose={onClose} title={mailbox.displayName} description={mailbox.emailAddress}
        footer={canManage && (
          <>
            <Button variant="ghost" icon="trash" onClick={() => setConfirmDelete(true)}>Delete</Button>
            <Button variant="primary" type="submit" form="edit-mailbox" loading={update.isPending}
              disabled={!isDirty && enabled === (mailbox.status !== 'Disabled')}>Save changes</Button>
          </>
        )}>
        <div className={styles.formStack}>
          <MailboxCard mailbox={mailbox} />
          {canManage && <RecentMessages mailboxId={mailbox.id} />}
          {canManage && mailbox.status === 'Active' && <MailAppAccess mailbox={mailbox} />}
          {canManage && mailbox.status === 'Active' && <SendTestMessage mailboxId={mailbox.id} />}
          {canManage && (
            <>
              <form id="edit-mailbox" noValidate className={styles.formStack}
                onSubmit={handleSubmit((v) => update.mutate({ id: mailbox.id, body: { displayName: v.displayName, quotaBytes: v.quotaGb * GB, enabled } }))}>
                <h3 className={styles.sectionTitle}>Settings</h3>
                <FormField label="Display name" error={errors.displayName?.message} required>
                  <TextInput {...register('displayName')} />
                </FormField>
                <FormField label="Quota" error={errors.quotaGb?.message} required>
                  <NumberInput {...register('quotaGb')} suffix={<span className={styles.suffixText}>GB</span>} />
                </FormField>
                <ToggleSwitch label="Mailbox enabled" description="Disabled mailboxes cannot sign in or receive mail." checked={enabled}
                  onChange={setEnabled} disabled={mailbox.status === 'Provisioning'} />
              </form>
              <form noValidate className={styles.formStack} onSubmit={(e) => { e.preventDefault(); if (aliasLocal.trim()) addAlias.mutate({ id: mailbox.id, localPart: aliasLocal, domainId }) }}>
                <h3 className={styles.sectionTitle}>Add alias</h3>
                <div className={styles.inlineGroup}>
                  <FormField label="Alias address" hint={`Delivers to this mailbox · @${domainName}`}>
                    <TextInput value={aliasLocal} onChange={(e) => setAliasLocal(e.target.value)} spellCheck={false} />
                  </FormField>
                  <Button type="submit" loading={addAlias.isPending} disabled={!aliasLocal.trim()}>Add</Button>
                </div>
              </form>
            </>
          )}
        </div>
      </Drawer>
      <ConfirmationDialog
        open={confirmDelete}
        title="Delete mailbox?"
        message={<>The mailbox <strong>{mailbox.emailAddress}</strong> and its aliases will be removed from the mail server. Mailboxes under legal hold cannot be deleted.</>}
        confirmLabel="Delete mailbox"
        destructive
        loading={remove.isPending}
        onConfirm={() => remove.mutate(mailbox.id)}
        onCancel={() => setConfirmDelete(false)}
      />
    </>
  )
}
