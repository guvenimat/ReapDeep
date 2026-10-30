import type { ReactNode, SVGProps } from 'react'

export type IconName =
  | 'dashboards' | 'trends' | 'funnels' | 'retention' | 'paths' | 'events' | 'persons' | 'settings'
  | 'plus' | 'close' | 'chevron-left' | 'chevron-right' | 'chevron-down' | 'search' | 'refresh' | 'logout'
  | 'sun' | 'moon' | 'trash' | 'edit' | 'copy' | 'key' | 'users' | 'lock' | 'calendar' | 'bookmark' | 'external' | 'menu'
  | 'catalog' | 'upload' | 'sparkles' | 'lifecycle' | 'rfm'

const PATHS: Record<IconName, ReactNode> = {
  dashboards: <><rect x="3.5" y="3.5" width="7" height="9" rx="1.5" /><rect x="13.5" y="3.5" width="7" height="5" rx="1.5" /><rect x="13.5" y="11.5" width="7" height="9" rx="1.5" /><rect x="3.5" y="15.5" width="7" height="5" rx="1.5" /></>,
  trends: <><path d="M3.5 20.5h17" /><path d="M4.5 15.5l4.5-5 4 3.5 6.5-8" /><path d="M15.5 6h4v4" /></>,
  funnels: <><path d="M3.5 4.5h17l-6.5 8v6l-4 2v-8z" /></>,
  retention: <><rect x="3.5" y="4.5" width="17" height="16" rx="2" /><path d="M3.5 9.5h17M9.5 9.5v11M15 9.5v11" /></>,
  paths: <><circle cx="6" cy="6" r="2.5" /><circle cx="18" cy="18" r="2.5" /><circle cx="18" cy="6" r="2.5" /><path d="M6 8.5v3a3 3 0 0 0 3 3h6.5M8.5 6h7" /></>,
  events: <><path d="M3.5 12h3.5l2.5-7 5 14 2.5-7h3.5" /></>,
  persons: <><circle cx="12" cy="8" r="4" /><path d="M4.5 20.5c1.2-3.6 4-5.5 7.5-5.5s6.3 1.9 7.5 5.5" /></>,
  settings: <><path d="M4 7h10M18 7h2M4 17h4M12 17h8" /><circle cx="16" cy="7" r="2.25" /><circle cx="10" cy="17" r="2.25" /></>,
  plus: <path d="M12 5v14M5 12h14" />,
  close: <path d="M6 6l12 12M18 6L6 18" />,
  'chevron-left': <path d="M14.5 6l-6 6 6 6" />,
  'chevron-right': <path d="M9.5 6l6 6-6 6" />,
  'chevron-down': <path d="M6 9.5l6 6 6-6" />,
  search: <><circle cx="11" cy="11" r="6.5" /><path d="M16 16l4.5 4.5" /></>,
  refresh: <><path d="M19.5 12a7.5 7.5 0 1 1-2.2-5.3" /><path d="M19.5 4.5v4h-4" /></>,
  logout: <><path d="M14 4.5h4a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2h-4" /><path d="M10 16l-4-4 4-4M6 12h10" /></>,
  sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4" /></>,
  moon: <path d="M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z" />,
  trash: <><path d="M4.5 7h15M10 7V4.5h4V7M6.5 7l1 13h9l1-13" /></>,
  edit: <><path d="M4.5 19.5l1-4 10.5-10.5a2.1 2.1 0 0 1 3 3L8.5 18.5z" /><path d="M14.5 6.5l3 3" /></>,
  copy: <><rect x="8.5" y="8.5" width="11" height="11" rx="2" /><path d="M15.5 8.5v-2a2 2 0 0 0-2-2h-7a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h2" /></>,
  key: <><circle cx="8" cy="15" r="4" /><path d="M11 12l8.5-8.5M16 7l2.5 2.5M14 9l1.5 1.5" /></>,
  users: <><circle cx="9" cy="8.5" r="3.5" /><path d="M3 19.5c.9-3 3.2-4.5 6-4.5s5.1 1.5 6 4.5" /><path d="M15.5 5.2a3.5 3.5 0 0 1 0 6.6M17.5 15.3c1.7.6 2.9 2 3.5 4.2" /></>,
  lock: <><rect x="5" y="10.5" width="14" height="10" rx="2" /><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3" /></>,
  calendar: <><rect x="3.5" y="5" width="17" height="15.5" rx="2" /><path d="M3.5 10h17M8 3v4M16 3v4" /></>,
  bookmark: <path d="M6.5 3.5h11v17l-5.5-4-5.5 4z" />,
  external: <><path d="M13.5 4.5h6v6M19.5 4.5l-8 8" /><path d="M17.5 13.5v5a2 2 0 0 1-2 2h-10a2 2 0 0 1-2-2v-10a2 2 0 0 1 2-2h5" /></>,
  menu: <path d="M4 7h16M4 12h16M4 17h16" />,
  catalog: <><path d="M5.5 8h13l-1 12.5h-11z" /><path d="M9 10.5V7a3 3 0 0 1 6 0v3.5" /></>,
  upload: <><path d="M12 15.5V4.5M7.5 9l4.5-4.5L16.5 9" /><path d="M4.5 15.5v3a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3" /></>,
  lifecycle: <><path d="M3.5 12h17" /><path d="M6.5 12V6.5M10.5 12V4.5M14.5 12V7.5M18.5 12V9" /><path d="M8.5 12v4.5M16.5 12v6" /></>,
  rfm: <><rect x="3.5" y="3.5" width="7" height="7" rx="1.5" /><rect x="13.5" y="3.5" width="7" height="7" rx="1.5" /><rect x="3.5" y="13.5" width="7" height="7" rx="1.5" /><path d="M14 17h6M17 14v6" /></>,
  sparkles: <><path d="M10 3.5l1.6 4.4 4.4 1.6-4.4 1.6L10 15.5l-1.6-4.4-4.4-1.6 4.4-1.6z" /><path d="M17.5 14l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z" /></>,
}

export function Icon({ name, size = 16, ...rest }: { name: IconName; size?: number } & SVGProps<SVGSVGElement>) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75}
      strokeLinecap="round" strokeLinejoin="round" aria-hidden className="icon-svg" {...rest}>
      {PATHS[name]}
    </svg>
  )
}

export function Logo({ size = 28 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden className="logo">
      <defs>
        <linearGradient id="logo-grad" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="var(--brand-from)" />
          <stop offset="1" stopColor="var(--brand-to)" />
        </linearGradient>
      </defs>
      <rect width="32" height="32" rx="9" fill="url(#logo-grad)" />
      <path d="M9 21.5l4.5-6 3.5 3 6-8.5" fill="none" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="23" cy="10" r="2" fill="#fff" />
    </svg>
  )
}
