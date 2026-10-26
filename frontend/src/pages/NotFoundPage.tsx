import { Link } from 'react-router-dom'
import { EmptyState } from '@/components/data-display/States'
import { PageHeader } from '@/layouts/PageLayout'

export function NotFoundPage() {
  return (
    <>
      <PageHeader title="Page not found" />
      <EmptyState icon="search" title="We couldn't find that page" description="The address may be mistyped, or the page may have moved." action={<Link to="/">Go to dashboard</Link>} />
    </>
  )
}
