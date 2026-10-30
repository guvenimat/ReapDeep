import { useState, type FormEvent } from 'react'
import { api } from '../../api/client'
import type { KeyScope, Me, Project, Role } from '../../api/types'
import { Icon } from '../../components/icons'
import { Badge, Card, FormMessage, Status } from '../../components/ui'
import { dateTime, initials } from '../../lib/format'
import { useAction, useQuery } from '../../lib/hooks'

const ROLES: Role[] = ['owner', 'admin', 'member']

export function Settings({ me, project, onAccessChanged }: { me: Me; project: Project; onAccessChanged: () => void }) {
  const canManage = project.role === 'owner' || project.role === 'admin'
  return (
    <div className="settings">
      <Members me={me} project={project} canManage={canManage} onAccessChanged={onAccessChanged} />
      {canManage && <ApiKeys />}
      <Account />
    </div>
  )
}

function Account() {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [done, setDone] = useState(false)
  const action = useAction()

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setDone(false)
    if (next !== confirm) {
      action.setError('The new passwords do not match')
      return
    }
    if (await action.run(() => api.changePassword(current, next))) {
      setCurrent('')
      setNext('')
      setConfirm('')
      setDone(true)
    }
  }

  return (
    <Card title={<><Icon name="lock" />Change password</>} meta="Signs out your other sessions">
      <form className="settings-form" onSubmit={submit} data-testid="settings-account">
        <label className="field"><span>Current password</span>
          <input type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} name="current" />
        </label>
        <div className="field-pair">
          <label className="field"><span>New password</span>
            <input type="password" autoComplete="new-password" required minLength={8} value={next} onChange={(e) => setNext(e.target.value)} name="next" placeholder="At least 8 characters" />
          </label>
          <label className="field"><span>Repeat new password</span>
            <input type="password" autoComplete="new-password" required minLength={8} value={confirm} onChange={(e) => setConfirm(e.target.value)} name="confirm" />
          </label>
        </div>
        <FormMessage error={action.error} success={done ? 'Password changed.' : undefined} />
        <div><button type="submit" className="primary" disabled={action.busy}>Change password</button></div>
      </form>
    </Card>
  )
}

function Members({ me, project, canManage, onAccessChanged }: { me: Me; project: Project; canManage: boolean; onAccessChanged: () => void }) {
  const [version, setVersion] = useState(0)
  const list = useQuery((signal) => api.members(signal), [version])
  const action = useAction()
  const [form, setForm] = useState({ email: '', name: '', password: '', role: 'member' as Role })
  const isOwner = project.role === 'owner'
  const assignable = ROLES.filter((r) => isOwner || r !== 'owner')
  const refresh = (self: boolean) => {
    setVersion((v) => v + 1)
    if (self) onAccessChanged()
  }

  const add = async (e: FormEvent) => {
    e.preventDefault()
    if (await action.run(() => api.addMember({ ...form, name: form.name || undefined, password: form.password || undefined }))) {
      setForm({ email: '', name: '', password: '', role: 'member' })
      refresh(false)
    }
  }
  const changeRole = (userId: number, role: Role) => action.run(() => api.changeRole(userId, role).then(() => refresh(userId === me.user.id)))
  const remove = (userId: number, email: string) => {
    const self = userId === me.user.id
    if (!window.confirm(self ? `Leave ${project.organization_name}?` : `Remove ${email} from ${project.organization_name}?`)) return
    void action.run(() => api.removeMember(userId).then(() => refresh(self)))
  }

  return (
    <Card title={<><Icon name="users" />Members of {project.organization_name}</>} meta="Members see every project of the organization">
      <Status state={list}>
        <div className="table-wrap">
          <table data-testid="members-table">
            <thead><tr><th>Member</th><th>Role</th><th>Added</th><th /></tr></thead>
            <tbody>
              {list.data?.members.map((m) => {
                const editable = canManage && (isOwner || m.role !== 'owner')
                const self = m.user_id === me.user.id
                return (
                  <tr key={m.user_id}>
                    <td>
                      <div className="person-cell">
                        <span className="avatar sm" aria-hidden>{initials(m.name || m.email)}</span>
                        <div>
                          <div className="strong">{m.name || m.email.split('@')[0]}{self && <span className="muted small"> (you)</span>}</div>
                          <div className="muted small">{m.email}</div>
                        </div>
                      </div>
                    </td>
                    <td>
                      {editable ? (
                        <select value={m.role} onChange={(e) => changeRole(m.user_id, e.target.value as Role)} aria-label={`Role of ${m.email}`} disabled={action.busy}>
                          {assignable.map((r) => <option key={r} value={r}>{r}</option>)}
                        </select>
                      ) : <Badge tone={m.role === 'owner' ? 'accent' : undefined}>{m.role}</Badge>}
                    </td>
                    <td className="nowrap tabular muted">{dateTime(m.created_at)}</td>
                    <td className="num">
                      {(editable || self) && (
                        <button className="link danger-link" onClick={() => remove(m.user_id, m.email)} disabled={action.busy}>
                          {self ? 'Leave' : 'Remove'}
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </Status>
      <FormMessage error={action.error} />
      {canManage && (
        <form className="inline-form" onSubmit={add} data-testid="add-member">
          <div className="editor-title">Invite a member</div>
          <div className="row">
            <input type="email" required placeholder="Email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} aria-label="Email" />
            <input placeholder="Name (optional)" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} aria-label="Name" />
            <input type="password" autoComplete="new-password" placeholder="Temporary password (new users)" value={form.password}
              onChange={(e) => setForm({ ...form, password: e.target.value })} aria-label="Temporary password" />
            <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })} aria-label="Role">
              {assignable.map((r) => <option key={r} value={r}>{r}</option>)}
            </select>
            <button type="submit" className="primary" disabled={action.busy}><Icon name="plus" />Add member</button>
          </div>
        </form>
      )}
    </Card>
  )
}

function ApiKeys() {
  const [version, setVersion] = useState(0)
  const list = useQuery((signal) => api.apiKeys(signal), [version])
  const action = useAction()
  const [name, setName] = useState('')
  const [scope, setScope] = useState<KeyScope>('read')
  const [created, setCreated] = useState<{ name: string; key: string }>()
  const [copied, setCopied] = useState(false)

  const create = async (e: FormEvent) => {
    e.preventDefault()
    await action.run(async () => {
      const res = await api.createApiKey(name, scope)
      setCreated({ name: res.api_key.name, key: res.key })
      setCopied(false)
      setName('')
      setVersion((v) => v + 1)
    })
  }
  const revoke = (id: number, keyName: string) => {
    if (!window.confirm(`Revoke "${keyName}"? Scripts using it stop working immediately.`)) return
    void action.run(() => api.revokeApiKey(id).then(() => setVersion((v) => v + 1)))
  }
  const copy = (key: string) => navigator.clipboard?.writeText(key).then(() => setCopied(true))

  return (
    <Card title={<><Icon name="key" />API keys</>} meta={<>Read keys query analytics; catalog keys only sync products · <span className="mono">Authorization: Bearer …</span></>}>
      {created && (
        <div className="new-key" role="status" data-testid="new-api-key">
          <div className="small">New key <span className="strong">{created.name}</span>. Copy it now; it is not shown again.</div>
          <div className="row">
            <code className="mono">{created.key}</code>
            <button type="button" onClick={() => copy(created.key)}><Icon name="copy" />{copied ? 'Copied' : 'Copy'}</button>
            <button type="button" className="icon-button" onClick={() => setCreated(undefined)} aria-label="Dismiss"><Icon name="close" /></button>
          </div>
        </div>
      )}
      <Status state={list} empty={list.data?.api_keys.length === 0} emptyTitle="No API keys yet" emptyText="Create a read-only key for scripts and reporting tools.">
        <div className="table-wrap">
          <table data-testid="api-keys-table">
            <thead><tr><th>Name</th><th>Key</th><th>Scope</th><th>Created</th><th>By</th><th>Status</th><th /></tr></thead>
            <tbody>
              {list.data?.api_keys.map((k) => (
                <tr key={k.id} className={k.revoked_at ? 'revoked' : undefined}>
                  <td className="strong">{k.name}</td>
                  <td className="mono">{k.prefix}…</td>
                  <td>{k.scope === 'catalog_write' ? <Badge tone="accent">Catalog write</Badge> : <Badge>Read</Badge>}</td>
                  <td className="nowrap tabular muted">{dateTime(k.created_at)}</td>
                  <td className="muted">{k.created_by ?? '-'}</td>
                  <td className="nowrap">{k.revoked_at ? <Badge tone="muted">Revoked {dateTime(k.revoked_at)}</Badge> : <Badge tone="good">Active</Badge>}</td>
                  <td className="num">
                    {!k.revoked_at && <button className="link danger-link" onClick={() => revoke(k.id, k.name)} disabled={action.busy}>Revoke</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
      <FormMessage error={action.error} />
      <form className="inline-form" onSubmit={create} data-testid="create-api-key">
        <div className="row">
          <input required maxLength={100} placeholder="Key name, e.g. reporting script" value={name} onChange={(e) => setName(e.target.value)} aria-label="Key name" />
          <select value={scope} onChange={(e) => setScope(e.target.value as KeyScope)} aria-label="Key scope">
            <option value="read">Read analytics</option>
            <option value="catalog_write">Write catalog</option>
          </select>
          <button type="submit" className="primary" disabled={action.busy}><Icon name="plus" />Create key</button>
        </div>
      </form>
    </Card>
  )
}
