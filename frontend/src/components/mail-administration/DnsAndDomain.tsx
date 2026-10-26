import { cx } from '@/lib/cx'
import { daysUntil, formatDate } from '@/lib/format'
import type { Domain, MailCertificate } from '@/types/api'
import { Button } from '../common/Button'
import { Card } from '../common/Card'
import { Icon } from '../common/Icon'
import { StatusBadge } from '../data-display/StatusBadge'
import styles from './mail.module.css'

interface DnsRecord {
  type: string
  configured: boolean
  purpose: string
}

/** Pass/fail list of the DNS records mail delivery depends on. Status is conveyed by text, not just color. */
export function DNSRecordStatus({ records }: { records: DnsRecord[] }) {
  return (
    <ul className={styles.dnsList} aria-label="DNS records">
      {records.map((r) => (
        <li key={r.type} className={styles.dnsItem}>
          <span className={cx(styles.dnsIcon, r.configured ? styles.ok : styles.bad)}>
            <Icon name={r.configured ? 'check' : 'close'} size={13} />
          </span>
          <span className={styles.dnsType}>{r.type}</span>
          <span className={styles.dnsPurpose}>{r.purpose}</span>
          <span className={cx(styles.dnsState, r.configured ? styles.okText : styles.badText)}>
            {r.configured ? 'Configured' : 'Missing'}
          </span>
        </li>
      ))}
    </ul>
  )
}

export function dnsRecordsFor(domain: Domain): DnsRecord[] {
  return [
    { type: 'MX', configured: domain.mxConfigured, purpose: 'Routes inbound mail' },
    { type: 'SPF', configured: domain.spfConfigured, purpose: 'Authorizes sending servers' },
    { type: 'DKIM', configured: domain.dkimConfigured, purpose: 'Signs outgoing mail' },
    { type: 'DMARC', configured: domain.dmarcConfigured, purpose: 'Policy for failed checks' },
  ]
}

interface DomainStatusCardProps {
  domain: Domain
  onVerify?: () => void
  onDelete?: () => void
  onGenerateDkim?: () => void
  verifying?: boolean
  generatingDkim?: boolean
}

export function DomainStatusCard({ domain, onVerify, onDelete, onGenerateDkim, verifying, generatingDkim }: DomainStatusCardProps) {
  const records = dnsRecordsFor(domain)
  const healthy = records.filter((r) => r.configured).length
  return (
    <Card
      as="article"
      title={
        <span className={styles.titleRow}>
          {domain.domainName}
          {domain.isDefault && <StatusBadge status="Default" tone="accent" dot={false} />}
        </span>
      }
      description={`${domain.mailboxCount} mailbox${domain.mailboxCount === 1 ? '' : 'es'} · ${healthy}/${records.length} DNS records configured`}
      actions={<StatusBadge status={domain.verificationStatus} />}
      footer={
        (onVerify || onDelete) && (
          <div className={styles.cardFooterActions}>
            {onDelete && <Button size="sm" variant="ghost" icon="trash" onClick={onDelete}>Remove</Button>}
            {onVerify && <Button size="sm" icon="refresh" onClick={onVerify} loading={verifying}>Check DNS</Button>}
          </div>
        )
      }
    >
      <DNSRecordStatus records={records} />
      {domain.verificationStatus !== 'Verified' && (
        <div className={styles.verifyHint}>
          <p>Add this TXT record at your DNS provider to prove ownership:</p>
          <code className={styles.code}>mx-verification={domain.verificationToken}</code>
        </div>
      )}
      <div className={styles.dkimBlock}>
        <div className={styles.dkimHeader}>
          <h3 className={styles.dkimTitle}>DKIM signing</h3>
          {onGenerateDkim && (
            <Button size="sm" variant={domain.dkimSelector ? 'ghost' : 'subtle'} icon={domain.dkimSelector ? 'refresh' : 'lock'} onClick={onGenerateDkim} loading={generatingDkim}>
              {domain.dkimSelector ? 'Rotate key' : 'Generate key'}
            </Button>
          )}
        </div>
        {domain.dkimSelector && domain.dkimPublicKey ? (
          <>
            <p className={styles.dkimText}>Outgoing mail is signed. Publish this TXT record so receivers can verify it:</p>
            <dl className={styles.dnsRecord}>
              <dt>Name</dt>
              <dd><code>{domain.dkimSelector}._domainkey.{domain.domainName}</code></dd>
              <dt>Value</dt>
              <dd><code className={styles.code}>v=DKIM1; k=rsa; p={domain.dkimPublicKey}</code></dd>
            </dl>
          </>
        ) : (
          <p className={styles.dkimText}>No signing key. Mail sent from this domain is unsigned and more likely to be marked as spam.</p>
        )}
      </div>
    </Card>
  )
}

/** The mail server's own TLS certificate (SMTP/IMAP), with issuer, expiry and automatic renewal. */
export function MailCertificateDetails({ certificate }: { certificate: MailCertificate }) {
  const days = daysUntil(certificate.notAfter)
  const tone = !certificate.issued || days === null ? 'warning' : days < 0 ? 'danger' : days <= 7 ? 'danger' : days <= 20 ? 'warning' : 'success'
  const label = !certificate.issued ? 'Being issued' : days !== null && days < 0 ? 'Expired' : 'Valid'
  return (
    <div className={styles.mailCert}>
      <CertificateStatusCard name={certificate.hostname ?? 'Mail server'} expiresAt={certificate.issued ? certificate.notAfter : null} toneOverride={tone} labelOverride={label} />
      {certificate.issued ? (
        <dl className={styles.dnsRecord}>
          <dt>Issuer</dt><dd>{certificate.issuer}</dd>
          <dt>Valid</dt><dd>{formatDate(certificate.notBefore)} – {formatDate(certificate.notAfter)}</dd>
          <dt>Renewal</dt>
          <dd>{certificate.autoRenew ? `Automatic, from ${formatDate(certificate.renewsAt)}` : 'Manual (automatic renewal is off)'}</dd>
          <dt>Protects</dt><dd>Ports {certificate.ports.join(', ')} (IMAP and SMTP)</dd>
        </dl>
      ) : (
        <p className={styles.dkimText}>
          The mail server requests a certificate from Let's Encrypt automatically. If this persists, check that {certificate.hostname} points to this server (DNS only).
        </p>
      )}
      <p className={styles.dkimText}>One certificate for the server host name covers every hosted domain; mail apps must use {certificate.hostname} as their server.</p>
    </div>
  )
}

interface CertificateStatusCardProps {
  name: string
  expiresAt: string | null
  toneOverride?: 'success' | 'warning' | 'danger' | 'neutral'
  labelOverride?: string
}

export function CertificateStatusCard({ name, expiresAt, toneOverride, labelOverride }: CertificateStatusCardProps) {
  const days = daysUntil(expiresAt)
  const tone = toneOverride ?? (days === null ? 'neutral' : days < 0 ? 'danger' : days <= 14 ? 'danger' : days <= 30 ? 'warning' : 'success')
  const label = labelOverride ?? (days === null ? 'No certificate' : days < 0 ? 'Expired' : days <= 30 ? `Expires in ${days} day${days === 1 ? '' : 's'}` : 'Valid')
  return (
    <div className={styles.certCard}>
      <span className={cx(styles.certIcon, styles[tone])}><Icon name="certificate" size={20} /></span>
      <div className={styles.certBody}>
        <div className={styles.certName}>{name}</div>
        <div className={styles.certMeta}>{expiresAt ? `TLS certificate · expires ${formatDate(expiresAt)}` : 'TLS certificate not issued'}</div>
      </div>
      <StatusBadge status={label} tone={tone} />
    </div>
  )
}
