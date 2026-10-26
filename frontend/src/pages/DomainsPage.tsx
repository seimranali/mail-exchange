import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { useAuth } from '@/app/AuthProvider'
import { Button } from '@/components/common/Button'
import { Card } from '@/components/common/Card'
import { EmptyState, ErrorState, LoadingState } from '@/components/data-display/States'
import { Checkbox } from '@/components/forms/Choice'
import { FormField } from '@/components/forms/FormField'
import { TextInput } from '@/components/forms/TextInput'
import { DomainStatusCard, MailCertificateDetails } from '@/components/mail-administration/DnsAndDomain'
import { ConfirmationDialog } from '@/components/overlays/ConfirmationDialog'
import { Modal } from '@/components/overlays/Dialog'
import { useApiMutation } from '@/hooks/useApiMutation'
import { ContentContainer, PageHeader, ResponsiveGrid, SplitPanel } from '@/layouts/PageLayout'
import { zodResolver } from '@/lib/zodResolver'
import { domainsApi, systemApi } from '@/services/endpoints'
import type { Domain } from '@/types/api'
import styles from './pages.module.css'

const schema = z.object({
  domainName: z.string().trim().toLowerCase().regex(/^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/, 'Enter a valid domain, e.g. example.com.'),
  isDefault: z.boolean(),
})
type DomainForm = z.infer<typeof schema>

export default function DomainsPage() {
  const { hasRole } = useAuth()
  const canManage = hasRole('PlatformAdmin', 'OrgAdmin', 'MailAdmin')
  const [adding, setAdding] = useState(false)
  const [toDelete, setToDelete] = useState<Domain | null>(null)
  const domains = useQuery({ queryKey: ['domains', 'list'], queryFn: () => domainsApi.list() })
  const certificate = useQuery({ queryKey: ['system', 'mail-certificate'], queryFn: systemApi.mailCertificate })

  const verify = useApiMutation({
    mutationFn: (id: string) => domainsApi.verify(id),
    invalidate: [['domains'], ['dashboard']],
    successMessage: (d) => d.verificationStatus === 'Verified' ? `${d.domainName} verified` : `${d.domainName}: verification record not found yet`,
    errorTitle: 'DNS check failed',
  })
  const rotateDkim = useApiMutation({
    mutationFn: (id: string) => domainsApi.rotateDkim(id),
    invalidate: [['domains']],
    successMessage: (k) => `DKIM key ${k.selector} created. Publish its DNS record, then check DNS.`,
    errorTitle: 'Could not create DKIM key',
  })
  const remove = useApiMutation({
    mutationFn: (id: string) => domainsApi.remove(id),
    invalidate: [['domains'], ['dashboard']],
    successMessage: 'Domain removed',
    errorTitle: 'Could not remove domain',
    onSuccess: () => setToDelete(null),
  })

  const items = domains.data?.items ?? []

  return (
    <ContentContainer>
      <PageHeader title="Domains" description="Accepted domains, DNS health and TLS certificates."
        actions={canManage && <Button variant="primary" icon="plus" onClick={() => setAdding(true)}>Add domain</Button>} />
      {domains.isLoading && <LoadingState label="Loading domains" />}
      {domains.error && <ErrorState error={domains.error} onRetry={() => domains.refetch()} />}
      {domains.data && items.length === 0 && (
        <EmptyState icon="domain" title="No domains yet" description="Add a domain you own to start creating mailboxes."
          action={canManage && <Button variant="primary" icon="plus" onClick={() => setAdding(true)}>Add domain</Button>} />
      )}
      {items.length > 0 && (
        <SplitPanel
          primary={
            <ResponsiveGrid minColumnWidth="340px">
              {items.map((d) => (
                <DomainStatusCard key={d.id} domain={d}
                  onVerify={canManage ? () => verify.mutate(d.id) : undefined}
                  verifying={verify.isPending && verify.variables === d.id}
                  onGenerateDkim={canManage ? () => rotateDkim.mutate(d.id) : undefined}
                  generatingDkim={rotateDkim.isPending && rotateDkim.variables === d.id}
                  onDelete={canManage ? () => setToDelete(d) : undefined} />
              ))}
            </ResponsiveGrid>
          }
          secondary={
            <Card title="Mail server certificate" description="TLS for IMAP and SMTP (mail apps and other servers)">
              {certificate.isLoading && <LoadingState label="Loading certificate" />}
              {certificate.error && <ErrorState error={certificate.error} onRetry={() => certificate.refetch()} />}
              {certificate.data && <MailCertificateDetails certificate={certificate.data} />}
            </Card>
          }
        />
      )}
      <AddDomainModal open={adding} onClose={() => setAdding(false)} />
      <ConfirmationDialog open={!!toDelete} title="Remove domain?" destructive confirmLabel="Remove domain" loading={remove.isPending}
        message={<>Mail for <strong>{toDelete?.domainName}</strong> will no longer be accepted. Domains with mailboxes cannot be removed.</>}
        onConfirm={() => toDelete && remove.mutate(toDelete.id)} onCancel={() => setToDelete(null)} />
    </ContentContainer>
  )
}

function AddDomainModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { register, handleSubmit, reset, formState: { errors } } = useForm<DomainForm>({ resolver: zodResolver(schema), defaultValues: { isDefault: false } })
  const close = () => { reset(); onClose() }
  const create = useApiMutation({
    mutationFn: (v: DomainForm) => domainsApi.create(v.domainName, v.isDefault),
    invalidate: [['domains'], ['dashboard']],
    successMessage: (d) => `${d.domainName} added. Publish the TXT record to verify ownership.`,
    errorTitle: 'Could not add domain',
    onSuccess: close,
  })
  return (
    <Modal open={open} onClose={close} title="Add domain" dismissible={!create.isPending}
      footer={<><Button onClick={close} disabled={create.isPending}>Cancel</Button><Button variant="primary" type="submit" form="add-domain" loading={create.isPending}>Add domain</Button></>}>
      <form id="add-domain" noValidate className={styles.formStack} onSubmit={handleSubmit((v) => create.mutate(v))}>
        <FormField label="Domain name" error={errors.domainName?.message} required hint="You'll verify ownership with a DNS TXT record.">
          <TextInput {...register('domainName')} placeholder="example.com" spellCheck={false} autoComplete="off" />
        </FormField>
        <Checkbox {...register('isDefault')} label="Make this the default domain" description="New mailboxes use the default domain unless another is chosen." />
      </form>
    </Modal>
  )
}
