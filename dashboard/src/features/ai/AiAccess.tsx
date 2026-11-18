import { createContext, useContext, useState } from 'react'
import { api, errorMessage } from '../../api/client'
import type { AiAccess, AiFeature, PlatformOrganization } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, Status } from '../../components/ui'
import { useQuery } from '../../lib/hooks'

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

function OrganizationRow({ org, onSaved }: { org: PlatformOrganization; onSaved: () => void }) {
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
          <input id={`platform-ai-${org.id}`} type="checkbox" checked={org.ai_enabled} disabled={busy}
            onChange={(e) => void save({ enabled: e.target.checked, ...(e.target.checked && org.ai_features.length === 0 ? { features: AI_FEATURES.map((f) => f.id) } : {}) })} />
          <span className="switch-track" aria-hidden />
          AI
        </label>
      </div>
      {org.ai_enabled && (
        <FeatureChecks idPrefix={`platform-${org.id}`} allowed={AI_FEATURES.map((f) => f.id)} chosen={org.ai_features} disabled={busy}
          onChange={(features) => void save({ features })} />
      )}
      {error && <p className="form-message error" role="alert">{error}</p>}
    </div>
  )
}

export function PlatformPage() {
  const [version, setVersion] = useState(0)
  const orgs = useQuery((signal) => api.platformOrganizations(signal), [version])
  return (
    <div className="stack">
      <Card testId="platform-ai" title={<><Icon name="sparkles" />AI features per customer</>}
        meta={orgs.data?.model ? `Model: ${orgs.data.model} · turn AI on for an organization and choose its features; the customer can switch them off again` : 'No AI provider is configured on the server'}>
        <Status state={orgs} empty={orgs.data?.organizations.length === 0}>
          <div className="platform-orgs">
            {orgs.data?.organizations.map((o) => <OrganizationRow key={o.id} org={o} onSaved={() => setVersion((v) => v + 1)} />)}
          </div>
        </Status>
      </Card>
    </div>
  )
}
