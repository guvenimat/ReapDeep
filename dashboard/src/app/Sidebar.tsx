import type { Me, Project } from '../api/types'
import { Icon, Logo } from '../components/icons'
import { initials } from '../lib/format'
import { TABS, type Tab } from './route'
import { useTheme } from './theme'

const GROUPS: { id: (typeof TABS)[number]['group']; label?: string }[] = [
  { id: 'analyze', label: 'Analyze' },
  { id: 'data', label: 'Data' },
  { id: 'admin' },
]

export function Sidebar({ me, project, active, status, statusError, onNavigate, onProjectChange, onLogout }: {
  me: Me; project: Project; active: Tab; status: 'ok' | 'down' | 'checking'; statusError?: string
  onNavigate: (tab: Tab) => void; onProjectChange: (id: number) => void; onLogout: () => void
}) {
  const { theme, toggle } = useTheme()
  return (
    <aside className="sidebar">
      <div className="sidebar-brand">
        <Logo />
        <span className="brand-name">ReapDeep</span>
      </div>

      <div className="project-switch">
        <span className="project-org">{project.organization_name}</span>
        {me.projects.length > 1 ? (
          <div className="select-wrap">
            <select value={project.id} onChange={(e) => onProjectChange(Number(e.target.value))} aria-label="Project" data-testid="project-select">
              {me.projects.map((p) => (
                <option key={p.id} value={p.id}>{p.organization_id === project.organization_id ? p.name : `${p.organization_name} / ${p.name}`}</option>
              ))}
            </select>
            <Icon name="chevron-down" size={14} />
          </div>
        ) : (
          <span className="project-name" data-testid="project-name">{project.name}</span>
        )}
      </div>

      <nav className="nav" aria-label="Sections">
        {GROUPS.map((g) => (
          <div className="nav-group" key={g.id}>
            {g.label && <div className="nav-label">{g.label}</div>}
            {TABS.filter((t) => t.group === g.id).map((t) => (
              <a key={t.id} href={`#/${t.id}`} className={`nav-item${active === t.id ? ' active' : ''}`}
                aria-current={active === t.id ? 'page' : undefined}
                onClick={(e) => { e.preventDefault(); onNavigate(t.id) }}>
                <Icon name={t.icon} size={17} />
                <span>{t.label}</span>
              </a>
            ))}
          </div>
        ))}
      </nav>

      <div className="sidebar-foot">
        <div className="foot-row">
          <span className={`status ${status}`} data-testid="connection-status" title={statusError}>
            <span className="status-dot" aria-hidden />
            {status === 'ok' ? 'Connected' : status === 'down' ? 'API unreachable' : 'Checking'}
          </span>
          <button type="button" className="icon-button" onClick={toggle} aria-label={theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            title={theme === 'dark' ? 'Light theme' : 'Dark theme'}>
            <Icon name={theme === 'dark' ? 'sun' : 'moon'} />
          </button>
        </div>
        <div className="user-card">
          <span className="avatar" aria-hidden>{initials(me.user.name || me.user.email)}</span>
          <div className="user-text">
            <span className="user-name">{me.user.name || me.user.email.split('@')[0]}</span>
            <span className="user-email" data-testid="user-email">{me.user.email}</span>
          </div>
          <button type="button" className="icon-button" onClick={onLogout} data-testid="logout" aria-label="Sign out" title="Sign out">
            <Icon name="logout" />
          </button>
        </div>
      </div>
    </aside>
  )
}
