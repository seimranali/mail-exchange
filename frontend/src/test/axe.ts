import axe from 'axe-core'

/** Runs axe against a container and returns violations formatted for assertion messages. */
export async function axeViolations(container: Element): Promise<string[]> {
  const results = await axe.run(container, {
    // Color contrast needs real layout/CSS, which jsdom cannot compute; verified in the browser.
    rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
  })
  return results.violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)
}
