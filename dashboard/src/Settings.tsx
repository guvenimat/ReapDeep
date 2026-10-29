import { useState, type FormEvent } from 'react'
import { api, type Me, type Project, type Role } from './api'
import { dateTime } from './format'
import { Status, useQuery } from './ui'

const ROLES: Role[] = ['owner', 'admin', 'member']

function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const run = async (fn: () => Promise<void>) => {
    setBusy(true)
    setError(undefined)
    try {
      await fn()
      return true
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      return false
    } finally {
      setBusy(false)
    }
  }
  return { busy, error, run }
}

export function Settings({ me, project, onAccessChanged }: { me: Me; project: Project; onAccessChanged: () => void }) {
  const canManage = project.role === 'owner' || project.role === 'admin'
  return (
    <div className="settings">
      <Account />
      <Members me={me} project={project} canManage={canManage} onAccessChanged={onAccessChanged} />
      {canManage && <ApiKeys />}
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
      await action.run(async () => { throw new Error('The new passwords do not match') })
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
    <form className="card settings-form" onSubmit={submit} data-testid="settings-account">
      <div className="card-head"><h2>Change password</h2><span className="muted small">Signs out your other sessions</span></div>
      <label className="field"><span>Current password</span>
        <input type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} name="current" />
      </label>
      <label className="field"><span>New password (at least 8 characters)</span>
        <input type="password" autoComplete="new-password" required minLength={8} value={next} onChange={(e) => setNext(e.target.value)} name="next" />
      </label>
      <label className="field"><span>Repeat new password</span>
        <input type="password" autoComplete="new-password" required minLength={8} value={confirm} onChange={(e) => setConfirm(e.target.value)} name="confirm" />
      </label>
      {action.error && <div className="login-error" role="alert">{action.error}</div>}
      {done && <div className="small good" role="status">Password changed.</div>}
      <div><button type="submit" className="primary" disabled={action.busy}>Change password</button></div>
    </form>
  )
}

function Members({ me, project, canManage, onAccessChanged }: { me: Me; project: Project; canManage: boolean; onAccessChanged: () => void }) {
  const [version, setVersion] = useState(0)
  const list = useQuery((signal) => api.members(signal), [version])
  const action = useAction()
  const [form, setForm] = useState({ email: '', name: '', password: '', role: 'member' as Role })
  const isOwner = project.role === 'owner'
  const refresh = (self: boolean) => {
    setVersion((v) => v + 1)
    if (self) onAccessChanged()
  }

  const add = async (e: FormEvent) => {
    e.preventDefault()
    if (await action.run(async () => { await api.addMember({ ...form, name: form.name || undefined, password: form.password || undefined }) })) {
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
    <div className="card">
      <div className="card-head"><h2>Members of {project.organization_name}</h2><span className="muted small">Members see every project of the organization</span></div>
      <Status state={list}>
        <div className="table-wrap">
          <table data-testid="members-table">
            <thead><tr><th>Email</th><th>Name</th><th>Role</th><th>Added</th><th /></tr></thead>
            <tbody>
              {list.data?.members.map((m) => {
                const editable = canManage && (isOwner || m.role !== 'owner')
                return (
                  <tr key={m.user_id}>
                    <td>{m.email}{m.user_id === me.user.id && <span className="muted small"> (you)</span>}</td>
                    <td>{m.name}</td>
                    <td>
                      {editable ? (
                        <select value={m.role} onChange={(e) => changeRole(m.user_id, e.target.value as Role)} aria-label={`Role of ${m.email}`} disabled={action.busy}>
                          {ROLES.filter((r) => isOwner || r !== 'owner').map((r) => <option key={r} value={r}>{r}</option>)}
                        </select>
                      ) : m.role}
                    </td>
                    <td className="nowrap tabular">{dateTime(m.created_at)}</td>
                    <td className="num">
                      {(editable || m.user_id === me.user.id) && (
                        <button className="link danger-link" onClick={() => remove(m.user_id, m.email)} disabled={action.busy}>
                          {m.user_id === me.user.id ? 'Leave' : 'Remove'}
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
      {action.error && <div className="login-error" role="alert">{action.error}</div>}
      {canManage && (
        <form className="row settings-inline" onSubmit={add} data-testid="add-member">
          <input type="email" required placeholder="Email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} aria-label="Email" />
          <input placeholder="Name (optional)" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} aria-label="Name" />
          <input type="password" autoComplete="new-password" placeholder="Temporary password (new users)" value={form.password}
            onChange={(e) => setForm({ ...form, password: e.target.value })} aria-label="Temporary password" />
          <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })} aria-label="Role">
            {ROLES.filter((r) => isOwner || r !== 'owner').map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
          <button type="submit" className="primary" disabled={action.busy}>Add member</button>
        </form>
      )}
    </div>
  )
}

function ApiKeys() {
  const [version, setVersion] = useState(0)
  const list = useQuery((signal) => api.apiKeys(signal), [version])
  const action = useAction()
  const [name, setName] = useState('')
  const [created, setCreated] = useState<{ name: string; key: string }>()

  const create = async (e: FormEvent) => {
    e.preventDefault()
    await action.run(async () => {
      const res = await api.createApiKey(name)
      setCreated({ name: res.api_key.name, key: res.key })
      setName('')
      setVersion((v) => v + 1)
    })
  }
  const revoke = (id: number, keyName: string) => {
    if (!window.confirm(`Revoke "${keyName}"? Scripts using it stop working immediately.`)) return
    void action.run(() => api.revokeApiKey(id).then(() => setVersion((v) => v + 1)))
  }

  return (
    <div className="card">
      <div className="card-head"><h2>API keys</h2><span className="muted small">Read-only keys for this project (Authorization: Bearer ...)</span></div>
      {created && (
        <div className="new-key" role="status" data-testid="new-api-key">
          <div className="small">New key <span className="strong">{created.name}</span>. Copy it now; it is not shown again.</div>
          <div className="row">
            <code className="mono">{created.key}</code>
            <button onClick={() => navigator.clipboard?.writeText(created.key)}>Copy</button>
            <button className="icon" onClick={() => setCreated(undefined)} aria-label="Dismiss">×</button>
          </div>
        </div>
      )}
      <Status state={list}>
        <div className="table-wrap">
          <table data-testid="api-keys-table">
            <thead><tr><th>Name</th><th>Key</th><th>Created</th><th>By</th><th>Status</th><th /></tr></thead>
            <tbody>
              {list.data?.api_keys.map((k) => (
                <tr key={k.id} className={k.revoked_at ? 'muted' : undefined}>
                  <td>{k.name}</td>
                  <td className="mono">{k.prefix}...</td>
                  <td className="nowrap tabular">{dateTime(k.created_at)}</td>
                  <td className="muted">{k.created_by ?? '-'}</td>
                  <td className="nowrap">{k.revoked_at ? `Revoked ${dateTime(k.revoked_at)}` : 'Active'}</td>
                  <td className="num">
                    {!k.revoked_at && <button className="link danger-link" onClick={() => revoke(k.id, k.name)} disabled={action.busy}>Revoke</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Status>
      {action.error && <div className="login-error" role="alert">{action.error}</div>}
      <form className="row settings-inline" onSubmit={create} data-testid="create-api-key">
        <input required maxLength={100} placeholder="Key name, e.g. reporting script" value={name} onChange={(e) => setName(e.target.value)} aria-label="Key name" />
        <button type="submit" className="primary" disabled={action.busy}>Create key</button>
      </form>
    </div>
  )
}
