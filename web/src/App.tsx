import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError, type IncidentDetail, type IncidentSummary, type Status } from './api'

function useHash() {
  const [hash, setHash] = useState(location.hash)
  useEffect(() => {
    const on = () => setHash(location.hash)
    addEventListener('hashchange', on)
    return () => removeEventListener('hashchange', on)
  }, [])
  return hash
}

const time = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit' })

function since(iso: string) {
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000))
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.round(s / 60)}m`
  if (s < 86400) return `${Math.round(s / 3600)}h`
  return `${Math.round(s / 86400)}d`
}

export function App() {
  const [user, setUser] = useState<string | null | undefined>(undefined)
  useEffect(() => {
    api.me().then((m) => setUser(m.name)).catch(() => setUser(null))
  }, [])
  if (user === undefined) return null
  if (user === null) return <Login onDone={setUser} />
  return <Console user={user} onLogout={() => api.logout().finally(() => setUser(null))} />
}

function Login({ onDone }: { onDone: (name: string) => void }) {
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string>()
  async function submit(e: FormEvent) {
    e.preventDefault()
    try {
      onDone((await api.login(name, password)).name)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Sign-in failed')
    }
  }
  return (
    <main className="login">
      <form onSubmit={submit} className="card">
        <h1><span className="mark" />ReapOps</h1>
        <p className="muted">Operations console</p>
        <label>Your name<input value={name} onChange={(e) => setName(e.target.value)} autoComplete="name" required /></label>
        <label>Console password<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required /></label>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="primary" type="submit">Sign in</button>
      </form>
    </main>
  )
}

function Console({ user, onLogout }: { user: string; onLogout: () => void }) {
  const hash = useHash()
  const id = /^#\/incidents\/([0-9a-f-]{36})$/.exec(hash)?.[1]
  return (
    <div className="shell">
      <header>
        <a href="#/" className="brand"><span className="mark" />ReapOps</a>
        <span className="muted">Signed in as {user}</span>
        <button className="ghost" onClick={onLogout}>Sign out</button>
      </header>
      <main>{id ? <Detail id={id} /> : <List />}</main>
    </div>
  )
}

const filters: { value: string; label: string }[] = [
  { value: '', label: 'All' },
  { value: 'open', label: 'Open' },
  { value: 'acknowledged', label: 'Acknowledged' },
  { value: 'resolved', label: 'Resolved' },
]

function List() {
  const [status, setStatus] = useState('')
  const [items, setItems] = useState<IncidentSummary[]>()
  const load = useCallback(() => api.incidents(status).then((r) => setItems(r.incidents)).catch(() => setItems([])), [status])
  useEffect(() => {
    load()
    const t = setInterval(load, 15000)
    return () => clearInterval(t)
  }, [load])
  const active = items?.filter((i) => i.status !== 'resolved') ?? []
  return (
    <>
      <div className="summary">
        <Stat label="Active" value={active.length} tone={active.length ? 'warn' : 'ok'} />
        <Stat label="Critical" value={active.filter((i) => i.severity === 'critical').length} tone={active.some((i) => i.severity === 'critical') ? 'bad' : 'ok'} />
        <Stat label="Acknowledged" value={active.filter((i) => i.status === 'acknowledged').length} />
      </div>
      <div className="toolbar">
        <h2>Incidents</h2>
        <div className="tabs" role="tablist">
          {filters.map((f) => (
            <button key={f.value} role="tab" aria-selected={status === f.value} className={status === f.value ? 'on' : ''} onClick={() => setStatus(f.value)}>{f.label}</button>
          ))}
        </div>
      </div>
      {items === undefined ? <p className="muted">Loading…</p> : items.length === 0 ? (
        <div className="empty card"><strong>All quiet.</strong><span className="muted">No incidents match this filter.</span></div>
      ) : (
        <table className="card">
          <thead><tr><th>Severity</th><th>Incident</th><th>Service</th><th>Status</th><th>Opened</th><th>Last seen</th></tr></thead>
          <tbody>
            {items.map((i) => (
              <tr key={i.id} onClick={() => (location.hash = `#/incidents/${i.id}`)} data-testid="incident-row">
                <td><Sev s={i.severity} /></td>
                <td className="title">{i.title}<small>{i.fingerprint}</small></td>
                <td>{i.service}</td>
                <td><span className={`status ${i.status}`}>{i.status}</span></td>
                <td title={time(i.opened_at)}>{since(i.opened_at)} ago</td>
                <td title={time(i.last_seen_at)}>{since(i.last_seen_at)} ago</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'warn' | 'bad' }) {
  return <div className={`stat card ${tone ?? ''}`}><span>{label}</span><strong>{value}</strong></div>
}

function Sev({ s }: { s: IncidentSummary['severity'] }) {
  return <span className={`sev ${s}`}>{s}</span>
}

function Detail({ id }: { id: string }) {
  const [d, setD] = useState<IncidentDetail | null>()
  const [error, setError] = useState<string>()
  const load = useCallback(() => api.incident(id).then(setD).catch(() => setD(null)), [id])
  useEffect(() => {
    load()
    const t = setInterval(load, 15000)
    return () => clearInterval(t)
  }, [load])
  async function act(action: (id: string) => Promise<void>) {
    setError(undefined)
    try {
      await action(id)
      await load()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'The action failed')
    }
  }
  if (d === undefined) return <p className="muted">Loading…</p>
  if (d === null) return <p>Incident not found. <a href="#/">Back to incidents</a></p>
  const s = d.summary
  const status: Status = s.status
  return (
    <div className="detail">
      <a href="#/" className="back">← Incidents</a>
      <div className="detail-head card">
        <div>
          <div className="row"><Sev s={s.severity} /><span className={`status ${status}`}>{status}</span><code>{s.fingerprint}</code></div>
          <h2>{s.title}</h2>
          <p className="muted">Opened {time(s.opened_at)} · last seen {time(s.last_seen_at)}{s.resolved_at ? ` · resolved ${time(s.resolved_at)}` : ''}</p>
          {d.acknowledged_by && <p className="muted">Acknowledged by {d.acknowledged_by}</p>}
          {d.resolution && <p className="muted">Resolution: {d.resolution}</p>}
        </div>
        {status !== 'resolved' && (
          <div className="actions">
            {status === 'open' && <button className="primary" onClick={() => act(api.acknowledge)} data-testid="acknowledge">Acknowledge</button>}
            <button className="ghost" onClick={() => act(api.resolve)} data-testid="resolve">Resolve</button>
          </div>
        )}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="grid">
        <section className="card">
          <h3>Evidence</h3>
          <dl>
            {d.evidence.map((e) => (
              <div key={e.label}>
                <dt>{e.label}</dt>
                <dd>{e.value}{e.query && <code className="query">{e.query}</code>}</dd>
              </div>
            ))}
          </dl>
        </section>
        <section className="card">
          <h3>Timeline</h3>
          <ol className="timeline">
            {d.timeline.map((t, n) => (
              <li key={n} className={t.kind}><time>{time(t.at)}</time><span>{label(t)}</span></li>
            ))}
          </ol>
        </section>
      </div>
      <p className="muted small">ReapOps only detects and notifies in this release. Diagnoses and proposed fixes come with the agent.</p>
    </div>
  )
}

function label(t: { kind: string; detail: string }) {
  if (t.kind.startsWith('notification_')) {
    const decision = t.kind.slice('notification_'.length)
    const what = `${t.detail} notification`
    return decision === 'send' ? `Sent the ${what}` : decision === 'maintenance' ? `Held the ${what} (maintenance window)` : `Held the ${what} (rate limit)`
  }
  return t.detail
}
