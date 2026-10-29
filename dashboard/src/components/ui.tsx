import type { ReactNode } from 'react'
import type { QueryState } from '../lib/hooks'
import { Icon, type IconName } from './icons'

export function Card({ title, meta, actions, children, className, testId }: {
  title?: ReactNode; meta?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string; testId?: string
}) {
  return (
    <section className={`card${className ? ` ${className}` : ''}`} data-testid={testId}>
      {(title || meta || actions) && (
        <header className="card-head">
          <div className="card-title">
            {title && <h2>{title}</h2>}
            {meta && <span className="card-meta">{meta}</span>}
          </div>
          {actions && <div className="card-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  )
}

export function Segmented<T extends string | number>({ value, options, onChange, label, size }: {
  value: T; options: { id: T; label: ReactNode }[]; onChange: (v: T) => void; label: string; size?: 'sm'
}) {
  return (
    <div className={`segmented${size ? ` ${size}` : ''}`} role="group" aria-label={label}>
      {options.map((o) => (
        <button key={String(o.id)} type="button" className={value === o.id ? 'active' : ''} aria-pressed={value === o.id} onClick={() => onChange(o.id)}>
          {o.label}
        </button>
      ))}
    </div>
  )
}

export function IconButton({ icon, label, onClick, disabled, tone }: { icon: IconName; label: string; onClick: () => void; disabled?: boolean; tone?: 'danger' }) {
  return (
    <button type="button" className={`icon-button${tone ? ` ${tone}` : ''}`} onClick={onClick} disabled={disabled} aria-label={label} title={label}>
      <Icon name={icon} />
    </button>
  )
}

export function StatTile({ label, value, hint, swatch, testId }: { label: ReactNode; value: ReactNode; hint?: ReactNode; swatch?: string; testId?: string }) {
  return (
    <div className="stat">
      <div className="stat-label">{swatch && <span className="swatch" style={{ background: swatch }} />}{label}</div>
      <div className="stat-value" data-testid={testId}>{value}</div>
      {hint && <div className="stat-hint">{hint}</div>}
    </div>
  )
}

export function EmptyState({ icon = 'search', title, children }: { icon?: IconName; title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <span className="empty-icon"><Icon name={icon} size={20} /></span>
      <div className="empty-title">{title}</div>
      {children && <div className="empty-text">{children}</div>}
    </div>
  )
}

export function Status({ state, empty, emptyTitle, emptyText, children }: {
  state: QueryState<unknown>; empty?: boolean; emptyTitle?: string; emptyText?: string; children: ReactNode
}) {
  if (state.error) return <div className="notice error" role="alert"><Icon name="close" /> {state.error}</div>
  if (!state.data) return <Skeleton />
  return (
    <div className={`status-body${state.loading ? ' stale' : ''}`}>
      {empty ? <EmptyState title={emptyTitle ?? 'No data in this range'}>{emptyText ?? 'Try a longer date range or fewer filters.'}</EmptyState> : children}
    </div>
  )
}

function Skeleton({ rows = 3 }: { rows?: number }) {
  return (
    <div className="skeleton" aria-busy="true" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => <span key={i} style={{ width: `${92 - i * 18}%` }} />)}
    </div>
  )
}

export function Badge({ children, tone }: { children: ReactNode; tone?: 'good' | 'muted' | 'accent' | 'danger' }) {
  return <span className={`badge${tone ? ` ${tone}` : ''}`}>{children}</span>
}

export function JsonView({ value }: { value: unknown }) {
  return <pre className="json">{JSON.stringify(value, null, 2)}</pre>
}

export function FormMessage({ error, success }: { error?: string; success?: string }) {
  if (error) return <div className="form-message error" role="alert">{error}</div>
  if (success) return <div className="form-message good" role="status">{success}</div>
  return null
}
