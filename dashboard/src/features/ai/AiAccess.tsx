import { createContext, useContext, useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { AiAccess, AiFeature, PlatformAuditEntry, PlatformOrganization } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, Status } from '../../components/ui'
import { useQuery } from '../../lib/hooks'
import { dateTime } from '../../lib/format'

export const AI_FEATURES: { id: AiFeature; label: string; description: string }[] = [
  { id: 'experiment_explain', label: 'Experiment interpretation', description: 'Plain-language reading of A/B test results with a rule-based decision' },
  { id: 'insight_explain', label: 'Chart explanations', description: 'Explain cards on trends, funnels and retention' },
  { id: 'query', label: 'Questions in plain language', description: 'Turn a question into a trend or funnel' },
  { id: 'digest', label: 'Weekly strategy note', description: 'A short note on the week with experiment ideas' },
]

export const AiFeaturesContext = createContext<{ features: AiFeature[]; reload: () => void }>({ features: [], reload: () => {} })

export function useAiFeature(feature: AiFeature) {
  return useContext(AiFeaturesContext).features.includes(feature)
}

function FeatureChecks({ allowed, chosen, disabled, onChange, idPrefix }: {
  allowed: AiFeature[]; chosen: AiFeature[]; disabled: boolean; onChange: (next: AiFeature[]) => void; idPrefix: string
}) {
  return (
    <div className="ai-features">
      {AI_FEATURES.map((f) => {
        const available = allowed.includes(f.id)
        const id = `${idPrefix}-${f.id}`
        return (
          <label key={f.id} htmlFor={id} aria-label={f.label} className={`ai-feature${available ? '' : ' unavailable'}`} data-ai-feature={f.id}>
            <input id={id} type="checkbox" checked={available && chosen.includes(f.id)} disabled={disabled || !available}
              onChange={(e) => onChange(e.target.checked ? [...chosen, f.id] : chosen.filter((x) => x !== f.id))} />
            <span>
              <span className="strong">{f.label}</span>
              <span className="muted small">{available ? f.description : 'Not included for this organization'}</span>
            </span>
          </label>
        )
      })}
    </div>
  )
}

export function AiSettingsCard({ canManage }: { canManage: boolean }) {
  const onChanged = useContext(AiFeaturesContext).reload
  const [version, setVersion] = useState(0)
  const access = useQuery((signal) => api.aiAccess(signal), [version])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const save = async (body: { enabled?: boolean; features?: AiFeature[] }) => {
    setBusy(true)
    setError(undefined)
    try {
      await api.setAiAccess(body)
      setVersion((v) => v + 1)
      onChanged()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  const a: AiAccess | undefined = access.data
  return (
    <Card testId="ai-settings" title={<><Icon name="sparkles" />AI features</>}
      meta={a?.model ? `Model: ${a.model} · applies to every project of this organization` : 'Applies to every project of this organization'}>
      <Status state={access}>
        {a && (!a.model
          ? <p className="editor-hint">No AI provider is configured on the server (Ai__ApiKey).</p>
          : !a.platform_enabled
            ? <p className="editor-hint">AI features are not included for this organization. The platform team can turn them on.</p>
            : (
              <div className="stack">
                <label className="switch" htmlFor="ai-master">
                  <input id="ai-master" type="checkbox" checked={a.enabled} disabled={!canManage || busy} onChange={(e) => void save({ enabled: e.target.checked })} />
                  <span className="switch-track" aria-hidden />
                  <span>Use AI in this organization. Only aggregated numbers and event and property names are sent to the model, never persons.</span>
                </label>
                {a.enabled && (
                  <FeatureChecks idPrefix="ai-org" allowed={a.allowed} chosen={a.features} disabled={!canManage || busy} onChange={(features) => void save({ features })} />
                )}
                {!canManage && <p className="footnote">Owners and admins can change these settings.</p>}
              </div>
            ))}
        {error && <p className="form-message error" role="alert">{error}</p>}
      </Status>
    </Card>
  )
}

function OrganizationRow({ org, onSaved, canEdit }: { org: PlatformOrganization; onSaved: () => void; canEdit: boolean }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const save = async (body: { enabled?: boolean; features?: AiFeature[] }) => {
    setBusy(true)
    setError(undefined)
    try {
      await api.setPlatformAi(org.id, body)
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }
  return (
    <div className="platform-org" data-organization={org.id}>
      <div className="platform-org-head">
        <div>
          <span className="strong">{org.name}</span>
          <span className="muted small"> · {org.projects} project{org.projects === 1 ? '' : 's'} · {org.members} member{org.members === 1 ? '' : 's'}</span>
        </div>
        <span className="spacer" />
        {org.ai_enabled && (org.customer_enabled
          ? <Badge tone="good">{org.effective.length} of {org.ai_features.length} in use</Badge>
          : <Badge tone="muted">Turned off by the customer</Badge>)}
        <label className="switch" htmlFor={`platform-ai-${org.id}`}>
          <input id={`platform-ai-${org.id}`} type="checkbox" checked={org.ai_enabled} disabled={busy || !canEdit}
            onChange={(e) => void save({ enabled: e.target.checked, ...(e.target.checked && org.ai_features.length === 0 ? { features: AI_FEATURES.map((f) => f.id) } : {}) })} />
          <span className="switch-track" aria-hidden />
          AI
        </label>
      </div>
      {org.ai_enabled && (
        <FeatureChecks idPrefix={`platform-${org.id}`} allowed={AI_FEATURES.map((f) => f.id)} chosen={org.ai_features} disabled={busy || !canEdit}
          onChange={(features) => void save({ features })} />
      )}
      {error && <p className="form-message error" role="alert">{error}</p>}
    </div>
  )
}

const describeState = (s: PlatformAuditEntry['after']) =>
  s.enabled ? `AI on: ${s.features.length ? s.features.map((f) => AI_FEATURES.find((x) => x.id === f)?.label ?? f).join(', ') : 'no features'}` : 'AI off'

function AuditLog({ version }: { version: number }) {
  const audit = useQuery((signal) => api.platformAudit(signal), [version])
  return (
    <Card testId="platform-audit" title={<><Icon name="calendar" />Recent changes</>} meta="Who changed which customer's AI settings; kept permanently">
      <Status state={audit} empty={audit.data?.entries.length === 0} emptyTitle="No changes yet" emptyText="Changes made on this page are listed here.">
        <ul className="audit-list">
          {audit.data?.entries.map((e) => (
            <li key={e.id} data-audit-id={e.id}>
              <span className="muted small">{dateTime(e.created_at)}</span>
              <span><span className="strong">{e.actor_email}</span> changed <span className="strong">{e.organization_name}</span></span>
              <span className="muted small">{describeState(e.before)} → {describeState(e.after)}</span>
            </li>
          ))}
        </ul>
      </Status>
    </Card>
  )
}

export function PlatformPage({ canEdit }: { canEdit: boolean }) {
  const [version, setVersion] = useState(0)
  const orgs = useQuery((signal) => api.platformOrganizations(signal), [version])
  return (
    <div className="stack">
      <Card testId="platform-ai" title={<><Icon name="sparkles" />AI features per customer</>}
        meta={orgs.data?.model ? `Model: ${orgs.data.model} · turn AI on for an organization and choose its features; the customer can switch them off again` : 'No AI provider is configured on the server'}>
        <Status state={orgs} empty={orgs.data?.organizations.length === 0}>
          <div className="platform-orgs">
            {orgs.data?.organizations.map((o) => <OrganizationRow key={o.id} org={o} canEdit={canEdit} onSaved={() => setVersion((v) => v + 1)} />)}
          </div>
          {!canEdit && <p className="footnote">You have read-only platform access (Platform__ViewerEmails).</p>}
        </Status>
      </Card>
      <AuditLog version={version} />
    </div>
  )
}
