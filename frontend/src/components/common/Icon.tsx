import type { SVGProps } from 'react'

// Original stroke icons on a 24px grid. Decorative by default (aria-hidden); pass `label` to expose.
const paths = {
  dashboard: 'M3 13h8V3H3zM13 21h8V11h-8zM3 21h8v-6H3zM13 3v6h8V3z',
  mailbox: 'M3 6.5h18v11H3z M3.5 7l8.5 6.5L20.5 7',
  domain: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M3 12h18 M12 3c2.5 2.6 3.7 5.6 3.7 9s-1.2 6.4-3.7 9c-2.5-2.6-3.7-5.6-3.7-9S9.5 5.6 12 3z',
  queue: 'M4 6h16 M4 12h16 M4 18h10',
  shield: 'M12 3 4.5 6v5.5c0 4.6 3.1 8.3 7.5 9.5 4.4-1.2 7.5-4.9 7.5-9.5V6z',
  quarantine: 'M5 4h14v16H5z M9 9l6 6 M15 9l-6 6',
  compliance: 'M8 3h8l4 4v14H4V3h4 M14 3v5h5 M8 13l2.5 2.5L16 10',
  audit: 'M12 8v4l2.5 2.5 M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z',
  organization: 'M4 21V5l8-2v18 M12 21V9l8 2v10 M3 21h18 M7.5 8h1 M7.5 12h1 M7.5 16h1 M15.5 14h1 M15.5 17h1',
  settings: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19.4 13.5a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-2.7 1.1v.3a2 2 0 1 1-4 0v-.2a1.6 1.6 0 0 0-2.8-1.1l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0-1.1-2.7h-.3a2 2 0 1 1 0-4h.2a1.6 1.6 0 0 0 1.1-2.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3h.1a1.6 1.6 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.2a1.6 1.6 0 0 0 2.7 1.1l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8v.1a1.6 1.6 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.2a1.6 1.6 0 0 0-1.4 1z',
  server: 'M4 4h16v6H4z M4 14h16v6H4z M8 7h.01 M8 17h.01',
  bell: 'M6 9a6 6 0 1 1 12 0c0 6 2.5 7.5 2.5 7.5h-17S6 15 6 9z M10 20a2 2 0 0 0 4 0',
  search: 'M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z M20 20l-4-4',
  plus: 'M12 5v14 M5 12h14',
  close: 'M6 6l12 12 M18 6 6 18',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  chevronDown: 'M6 9l6 6 6-6',
  chevronUp: 'M6 15l6-6 6 6',
  chevronLeft: 'M15 6l-6 6 6 6',
  chevronRight: 'M9 6l6 6-6 6',
  sort: 'M8 9l4-4 4 4 M8 15l4 4 4-4',
  menu: 'M4 7h16 M4 12h16 M4 17h16',
  more: 'M5 12h.01 M12 12h.01 M19 12h.01',
  sun: 'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4',
  moon: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z',
  monitor: 'M3 4h18v12H3z M8 20h8 M12 16v4',
  alert: 'M12 3 2 20h20z M12 10v4 M12 17h.01',
  info: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M12 11v5 M12 8h.01',
  success: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M8 12l3 3 5-5',
  error: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M15 9l-6 6 M9 9l6 6',
  lock: 'M5 11h14v10H5z M8 11V7a4 4 0 1 1 8 0v4',
  logout: 'M9 21H5V3h4 M16 17l5-5-5-5 M21 12H9',
  refresh: 'M20 11a8 8 0 0 0-14.9-3.5L4 9 M4 4v5h5 M4 13a8 8 0 0 0 14.9 3.5L20 15 M20 20v-5h-5',
  trash: 'M4 7h16 M10 11v6 M14 11v6 M6 7l1 14h10l1-14 M9 7V4h6v3',
  edit: 'M4 20h4L19 9l-4-4L4 16z M13.5 6.5l4 4',
  columns: 'M4 4h16v16H4z M10 4v16 M15 4v16',
  filter: 'M4 5h16l-6 8v6l-4-2v-4z',
  certificate: 'M4 4h16v11H4z M8 8h8 M8 11h5 M15 15l-1.5 6 2.5-1.5 2.5 1.5L17 15',
  calendar: 'M4 5h16v16H4z M4 10h16 M8 3v4 M16 3v4',
  user: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M4 21a8 8 0 0 1 16 0',
  arrowRight: 'M5 12h14 M13 6l6 6-6 6',
  external: 'M14 4h6v6 M20 4l-9 9 M18 14v6H4V6h6',
  inbox: 'M3 13l3-8h12l3 8v6H3z M3 13h5l1.5 2.5h5L16 13h5',
  send: 'M21 3 10 14 M21 3l-7 18-4-7-7-4z',
  draft: 'M6 3h9l4 4v14H6z M14 3v5h5 M9 13h6 M9 17h4',
  archive: 'M3 4h18v4H3z M5 8v12h14V8 M10 12h4',
  star: 'M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8-5.2-2.7-5.2 2.7 1-5.8-4.3-4.1 5.9-.9z',
  reply: 'M10 8 5 12l5 4 M5 12h9a5 5 0 0 1 5 5v1',
  replyAll: 'M11 8l-5 4 5 4 M7 8l-5 4 5 4 M6 12h8a5 5 0 0 1 5 5v1',
  forward: 'M14 8l5 4-5 4 M19 12h-9a5 5 0 0 0-5 5v1',
  paperclip: 'M20 11.5l-8.2 8.2a5 5 0 0 1-7-7L13 4.5a3.3 3.3 0 0 1 4.7 4.7l-8.2 8.2a1.7 1.7 0 0 1-2.4-2.4l7.6-7.6',
  download: 'M12 4v11 M7 10l5 5 5-5 M4 20h16',
  image: 'M4 5h16v14H4z M4 16l5-5 4 4 2-2 5 5 M15.5 9.5h.01',
  link: 'M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1 M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1',
  list: 'M9 6h11 M9 12h11 M9 18h11 M4.5 6h.01 M4.5 12h.01 M4.5 18h.01',
  mailOpen: 'M3 10l9-6 9 6v10H3z M3 10l9 6 9-6',
  minimize: 'M5 19h14',
  maximize: 'M4 4h16v16H4z',
} as const

export type IconName = keyof typeof paths

interface IconProps extends Omit<SVGProps<SVGSVGElement>, 'name'> {
  name: IconName
  size?: number
  label?: string
}

export function Icon({ name, size = 18, label, ...rest }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
      focusable="false"
      {...rest}
    >
      <path d={paths[name]} />
    </svg>
  )
}
