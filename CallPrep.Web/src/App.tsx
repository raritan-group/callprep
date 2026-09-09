import { useEffect, useRef, useState } from 'react'
import './App.css'

type Msg = { role: 'user' | 'assistant'; text: string; tools?: string[]; ms?: number }
type Ev =
  | { type: 'text'; text: string }
  | { type: 'tool'; name: string; rows: number; ms: number }
  | { type: 'done'; ms: number; input_tokens: number; output_tokens: number; session_id: string }
  | { type: 'error'; text: string }

type Me = { login: string; displayName: string | null; role: 'rep' | 'manager' | 'admin'; salesrepId: string | null; salesrepName: string | null; scope: string }
type AccessRow = { login: string; display_name: string | null; role: string; salesrep_id: string | null; enabled: boolean; notes: string | null; updated_at?: string; updated_by?: string | null }
type Rep = { salesrep_id: string; salesrep_name: string; customers: number }
type TodayRow = { customer_id: string; customer_name: string; detail: string; dollars: string; question: string }
type TodaySection = { key: string; title: string; blurb: string; count: number; headline: string; rows: TodayRow[] }
type Today = { scope: string; generated_at: string; ms: number; sections: TodaySection[] }

const SUGGESTIONS = [
  'What is Buist not buying that similar contractors are?',
  'Snapshot of Buist before my call',
  'Any open quotes with Buist I should follow up on?',
  'Why did we lose quotes with Buist this year?',
]
const REP_SUGGESTIONS = [
  'Which of my accounts have open quotes worth following up this week?',
  'Who in my book has gone quiet in the last 90 days?',
]

/** Admin-only: who may use Call Prep and what they can see. Saved rows go through /api/admin/users; the database re-checks the admin role. */
function AccessPanel({ me, onClose }: { me: Me; onClose: () => void }) {
  const [rows, setRows] = useState<AccessRow[]>([])
  const [reps, setReps] = useState<Rep[]>([])
  const [err, setErr] = useState('')
  const [saving, setSaving] = useState('')
  const blank = (): AccessRow => ({ login: '', display_name: '', role: 'rep', salesrep_id: null, enabled: true, notes: '' })
  const [draft, setDraft] = useState<AccessRow>(blank())
  async function load() {
    setErr('')
    const r = await fetch('/api/admin/users')
    if (!r.ok) { setErr(`HTTP ${r.status}`); return }
    const j = await r.json(); setRows(j.users); setReps(j.salesreps)
  }
  useEffect(() => { load() }, [])
  async function save(row: AccessRow) {
    setSaving(row.login); setErr('')
    const r = await fetch('/api/admin/users', { method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ login: row.login, displayName: row.display_name, role: row.role, salesrepId: row.role === 'rep' ? row.salesrep_id : null, enabled: row.enabled, notes: row.notes }) })
    setSaving('')
    if (!r.ok) { const j = await r.json().catch(() => ({})); setErr(j.error || `HTTP ${r.status}`); return }
    if (row === draft) setDraft(blank())
    await load()
  }
  const edit = (i: number, patch: Partial<AccessRow>) => setRows(rs => rs.map((r, j) => j === i ? { ...r, ...patch } : r))
  // plain render function, not a nested component: a nested component would remount on every keystroke and drop focus
  const row = (r: AccessRow, onChange: (p: Partial<AccessRow>) => void, onSave: () => void, isNew?: boolean) => (
    <tr key={isNew ? '__new' : r.login} className={r.enabled ? '' : 'off'}>
      <td>{isNew ? <input value={r.login} placeholder="domain account" onChange={e => onChange({ login: e.target.value })} /> : <code>{r.login}</code>}</td>
      <td><input value={r.display_name ?? ''} placeholder="name" onChange={e => onChange({ display_name: e.target.value })} /></td>
      <td>
        <select value={r.role} onChange={e => onChange({ role: e.target.value })} disabled={r.login === me.login}>
          <option value="rep">rep (own accounts)</option><option value="manager">manager (all)</option><option value="admin">admin (all + access)</option>
        </select>
      </td>
      <td>
        {r.role === 'rep' ? (
          <select value={r.salesrep_id ?? ''} onChange={e => onChange({ salesrep_id: e.target.value || null })}>
            <option value="">choose rep…</option>
            {reps.map(p => <option key={p.salesrep_id} value={p.salesrep_id}>{p.salesrep_name} ({p.customers})</option>)}
          </select>
        ) : <span className="muted">all accounts</span>}
      </td>
      <td><input type="checkbox" checked={r.enabled} disabled={r.login === me.login} onChange={e => onChange({ enabled: e.target.checked })} /></td>
      <td><input value={r.notes ?? ''} placeholder="notes" onChange={e => onChange({ notes: e.target.value })} /></td>
      <td><button className="ghost" disabled={saving === r.login || (isNew && !r.login.trim())} onClick={onSave}>{isNew ? 'Add' : 'Save'}</button></td>
    </tr>
  )
  return (
    <div className="panel">
      <div className="panel-head">
        <strong>Access</strong>
        <span className="muted">Sign-in is the person's Windows account. A rep sees only customers assigned to their P21 rep id; managers and admins see all. Disable instead of deleting so the audit trail keeps its name.</span>
        <button className="ghost" onClick={onClose}>Close</button>
      </div>
      {err && <div className="err">⚠ {err}</div>}
      <div className="tablewrap">
        <table>
          <thead><tr><th>Account</th><th>Name</th><th>Role</th><th>Sees</th><th>On</th><th>Notes</th><th></th></tr></thead>
          <tbody>
            {rows.filter(r => r.login !== 'callprep-service').map(r => row(r, p => edit(rows.indexOf(r), p), () => save(r)))}
            {row(draft, p => setDraft(d => ({ ...d, ...p })), () => save(draft), true)}
          </tbody>
        </table>
      </div>
    </div>
  )
}

/** The week's list: hard-coded triggers over the rep's book (005_rep_list.sql). First glance = five numbers; one section's rows at a time.
 *  Each row carries the question that opens the chat. */
function TodayPanel({ today, loading, onAsk, onRefresh }: { today: Today | null; loading: boolean; onAsk: (q: string) => void; onRefresh: () => void }) {
  const [picked, setPicked] = useState<string | null>(null)
  if (loading && !today) return <div className="today"><p className="muted">Building your list…</p></div>
  if (!today) return null
  const live = today.sections.filter(s => s.count > 0)
  const current = live.find(s => s.key === picked) ?? live[0]
  if (!current) return <div className="today"><p className="muted">Nothing on the list this week. Ask about a customer below.</p></div>
  return (
    <div className="today">
      <div className="today-head">
        <div><strong>This week</strong> <span className="muted">· {today.scope} · as of {today.generated_at}</span></div>
        <button className="ghost" onClick={onRefresh} disabled={loading}>{loading ? 'Refreshing…' : 'Refresh'}</button>
      </div>
      <div className="tiles">
        {live.map(s => (
          <button key={s.key} className={`tile ${s.key === current.key ? 'on' : ''}`} onClick={() => setPicked(s.key)} title={s.blurb}>
            <div className="tile-n">{s.count}</div>
            <div className="tile-t">{s.title}</div>
            <div className="tile-h">{s.headline}</div>
          </button>
        ))}
      </div>
      <section className="today-sec">
        <p className="muted blurb">{current.blurb}{current.count > current.rows.length ? ` Showing the top ${current.rows.length} of ${current.count}.` : ''}</p>
        <ul>
          {current.rows.map((r, i) => (
            <li key={`${current.key}-${r.customer_id}-${i}`} className="today-row">
              <div className="today-txt">
                <div className="today-name">{r.customer_name} <span className="today-dollars">{r.dollars}</span></div>
                <div className="today-detail">{r.detail}</div>
              </div>
              <button className="chip ask" title={r.question} onClick={() => onAsk(r.question)}>Prep</button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}

// Local speech-to-text: capture mic as 16 kHz mono 16-bit WAV in the browser, POST to /api/transcribe (whisper on our server).
// Nothing is sent to a third-party speech service.
class Recorder {
  private ctx: AudioContext | null = null
  private stream: MediaStream | null = null
  private node: ScriptProcessorNode | null = null
  private chunks: Float32Array[] = []
  private rate = 48000
  private analyser: AnalyserNode | null = null
  /** 0..1 RMS level of the last frame, for the meter */
  level(): number {
    if (!this.analyser) return 0
    const buf = new Float32Array(this.analyser.fftSize)
    this.analyser.getFloatTimeDomainData(buf)
    let sum = 0; for (let i = 0; i < buf.length; i++) sum += buf[i] * buf[i]
    return Math.min(1, Math.sqrt(sum / buf.length) * 4)
  }
  async start() {
    this.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } })
    this.ctx = new AudioContext()
    this.rate = this.ctx.sampleRate
    const src = this.ctx.createMediaStreamSource(this.stream)
    this.analyser = this.ctx.createAnalyser(); this.analyser.fftSize = 1024; src.connect(this.analyser)
    this.node = this.ctx.createScriptProcessor(4096, 1, 1)
    this.chunks = []
    this.node.onaudioprocess = e => { this.chunks.push(new Float32Array(e.inputBuffer.getChannelData(0))) }
    src.connect(this.node); this.node.connect(this.ctx.destination)
  }
  async stop(): Promise<Blob> {
    this.node?.disconnect(); this.stream?.getTracks().forEach(t => t.stop()); await this.ctx?.close()
    const total = this.chunks.reduce((n, c) => n + c.length, 0)
    const pcm = new Float32Array(total); let o = 0
    for (const c of this.chunks) { pcm.set(c, o); o += c.length }
    // downsample to 16 kHz by averaging
    const ratio = this.rate / 16000, outLen = Math.floor(pcm.length / ratio), out = new Int16Array(outLen)
    for (let i = 0; i < outLen; i++) {
      const a = Math.floor(i * ratio), b = Math.min(pcm.length, Math.floor((i + 1) * ratio)); let sum = 0
      for (let j = a; j < b; j++) sum += pcm[j]
      const v = Math.max(-1, Math.min(1, sum / Math.max(1, b - a)))
      out[i] = v < 0 ? v * 0x8000 : v * 0x7fff
    }
    const buf = new ArrayBuffer(44 + out.length * 2), dv = new DataView(buf)
    const str = (p: number, t: string) => { for (let i = 0; i < t.length; i++) dv.setUint8(p + i, t.charCodeAt(i)) }
    str(0, 'RIFF'); dv.setUint32(4, 36 + out.length * 2, true); str(8, 'WAVE'); str(12, 'fmt '); dv.setUint32(16, 16, true)
    dv.setUint16(20, 1, true); dv.setUint16(22, 1, true); dv.setUint32(24, 16000, true); dv.setUint32(28, 32000, true)
    dv.setUint16(32, 2, true); dv.setUint16(34, 16, true); str(36, 'data'); dv.setUint32(40, out.length * 2, true)
    new Int16Array(buf, 44).set(out)
    return new Blob([buf], { type: 'audio/wav' })
  }
}

export default function App() {
  const [msgs, setMsgs] = useState<Msg[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [listening, setListening] = useState(false)
  const [level, setLevel] = useState(0)
  const [transcribing, setTranscribing] = useState(false)
  const [status, setStatus] = useState<string>('')
  const [health, setHealth] = useState<string>('')
  const session = useRef<string>(crypto.randomUUID().replace(/-/g, ''))
  const rec = useRef<Recorder | null>(null)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const canListen = !!navigator.mediaDevices?.getUserMedia
  const [me, setMe] = useState<Me | null>(null)
  const [auth, setAuth] = useState<'loading' | 'ok' | 'signin' | 'denied' | 'down'>('loading')
  const [deniedLogin, setDeniedLogin] = useState('')
  const [showAccess, setShowAccess] = useState(false)
  const [today, setToday] = useState<Today | null>(null)
  const [todayLoading, setTodayLoading] = useState(false)
  const [showToday, setShowToday] = useState(true)

  useEffect(() => {
    // Who am I? The browser answers the Negotiate challenge with the Windows login on a domain PC; nothing to type.
    fetch('/api/me').then(async r => {
      if (r.status === 401) {
        // No session: go straight to Microsoft. Show the manual button only if we just came back from a sign-in
        // attempt that still has no session (avoids a redirect loop when the registration/redirect URI is wrong).
        let last = 0
        try { last = Number(sessionStorage.getItem('cp_signin_at') || 0) } catch { /* storage blocked */ }
        if (Date.now() - last > 60_000) {
          try { sessionStorage.setItem('cp_signin_at', String(Date.now())) } catch { /* ignore */ }
          location.replace(`/signin?returnUrl=${encodeURIComponent(location.pathname)}`)
          return
        }
        setAuth('signin'); return
      }
      try { sessionStorage.removeItem('cp_signin_at') } catch { /* ignore */ }
      if (r.status === 403) { const j = await r.json().catch(() => ({})); setDeniedLogin(j.login ?? ''); setAuth('denied'); return }
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      setMe(await r.json()); setAuth('ok'); loadToday(false)
    }).catch(() => setAuth('down'))
    fetch('/api/health').then(r => r.json()).then(h => setHealth(`sales current to ${h.sales_current_to} · ${h.model}`)).catch(() => setHealth('API not reachable'))
  }, [])
  async function loadToday(refresh: boolean) {
    setTodayLoading(true)
    try {
      const r = await fetch(`/api/today${refresh ? '?refresh=true' : ''}`)
      if (r.ok) setToday(await r.json())
    } catch { /* the list is optional; the chat still works */ }
    finally { setTodayLoading(false) }
  }
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }) }, [msgs, status])
  useEffect(() => {
    if (!listening) { setLevel(0); return }
    let raf = 0
    const tick = () => { setLevel(rec.current?.level() ?? 0); raf = requestAnimationFrame(tick) }
    raf = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(raf)
  }, [listening])
  /** Stop the answer in flight (typo, wrong customer). The server drops the question from the conversation; the text
   *  goes back into the box so it can be fixed and re-sent. */
  const stop = () => abort.current?.abort()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      if (listening) { rec.current?.stop(); setListening(false) }
      else if (busy) stop()
    }
    window.addEventListener('keydown', onKey); return () => window.removeEventListener('keydown', onKey)
  }, [listening, busy])


  async function ask(q: string) {
    const question = q.trim()
    if (!question || busy) return
    setInput('')
    setStatus('')
    setBusy(true)
    setMsgs(m => [...m, { role: 'user', text: question }, { role: 'assistant', text: '', tools: [] }])
    const t0 = performance.now()
    const ctl = new AbortController()
    abort.current = ctl
    try {
      const res = await fetch('/api/chat', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sessionId: session.current, message: question }),
        signal: ctl.signal,
      })
      if (res.status === 401) { location.href = `/signin?returnUrl=${encodeURIComponent(location.pathname)}`; return }
      if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`)
      const reader = res.body.getReader()
      const dec = new TextDecoder()
      let buf = ''
      for (;;) {
        const { value, done } = await reader.read()
        if (done) break
        buf += dec.decode(value, { stream: true })
        let i: number
        while ((i = buf.indexOf('\n\n')) >= 0) {
          const chunk = buf.slice(0, i); buf = buf.slice(i + 2)
          const line = chunk.split('\n').find(l => l.startsWith('data: '))
          if (!line) continue
          const ev = JSON.parse(line.slice(6)) as Ev
          if (ev.type === 'tool') {
            setStatus(`${ev.name} · ${ev.rows} rows · ${ev.ms} ms`)
            setMsgs(m => { const c = [...m]; const last = { ...c[c.length - 1] }; last.tools = [...(last.tools ?? []), ev.name]; c[c.length - 1] = last; return c })
          } else if (ev.type === 'text') {
            setMsgs(m => { const c = [...m]; const last = { ...c[c.length - 1] }; last.text += ev.text; c[c.length - 1] = last; return c })
          } else if (ev.type === 'done') {
            setMsgs(m => { const c = [...m]; const last = { ...c[c.length - 1] }; last.ms = ev.ms; c[c.length - 1] = last; return c })
            setStatus('')
          } else if (ev.type === 'error') {
            setMsgs(m => { const c = [...m]; const last = { ...c[c.length - 1] }; last.text += `\n⚠ ${ev.text}`; c[c.length - 1] = last; return c })
          }
        }
      }
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        // stopped on purpose: remove the question and the empty answer, hand the text back for editing
        setMsgs(m => m.slice(0, -2))
        setInput(question)
      } else {
        setMsgs(m => { const c = [...m]; const last = { ...c[c.length - 1] }; last.text += `\n⚠ ${(e as Error).message}`; c[c.length - 1] = last; return c })
      }
    } finally {
      abort.current = null
      setBusy(false); setStatus('')
      if (!msgs.length) console.debug('first answer', Math.round(performance.now() - t0), 'ms')
    }
  }

  async function toggleMic() {
    if (listening) {
      setListening(false)
      const blob = await rec.current!.stop()
      setTranscribing(true)
      try {
        const r = await fetch('/api/transcribe', { method: 'POST', headers: { 'Content-Type': 'audio/wav', 'X-Session': session.current }, body: blob })
        const j = await r.json()
        // Transcript goes into the box for review; nothing is sent until Ask is pressed (Paul, 9/9: names get misheard)
        if (j.text) { setInput(j.text); setStatus('Check the text, then press Ask'); setTimeout(() => inputRef.current?.focus(), 0) }
        else setStatus("Didn't catch that, try again")
      } catch { setStatus('transcription failed') }
      finally { setTranscribing(false) }
      return
    }
    rec.current = new Recorder()
    await rec.current.start()
    setListening(true)
  }

  async function reset() {
    await fetch('/api/reset', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sessionId: session.current, message: '' }) })
    session.current = crypto.randomUUID().replace(/-/g, '')
    setMsgs([]); setShowToday(true)
  }

  if (auth !== 'ok' || !me) {
    return (
      <div className="app">
        <header><div className="brand">Call Prep</div><div className="health">{health}</div></header>
        <main>
          <div className="empty">
            {auth === 'loading' && <p>Signing you in…</p>}
            {auth === 'signin' && (
              <>
                <p>Sign-in didn't complete. Try again with your Raritan Group Microsoft account; if it keeps bouncing back here, tell IT.</p>
                <div className="chips"><a className="chip primary" href={`/signin?returnUrl=${encodeURIComponent(location.pathname)}`}>Sign in with Microsoft</a></div>
              </>
            )}
            {auth === 'denied' && (
              <>
                <p>Your account{deniedLogin && <> (<code>{deniedLogin}</code>)</>} is signed in but not set up for Call Prep yet. Ask IT to add you.</p>
                <div className="chips"><a className="chip" href="/signout">Sign out</a></div>
              </>
            )}
            {auth === 'down' && <p>The Call Prep service is not reachable right now.</p>}
          </div>
        </main>
      </div>
    )
  }

  const seesAll = me.role !== 'rep'
  return (
    <div className="app">
      <header>
        <div className="brand">Call Prep</div>
        <div className="health">
          <div className="who">{me.displayName || me.login} · {seesAll ? 'all accounts' : `${me.salesrepName || 'rep ' + me.salesrepId}'s accounts`}</div>
          {health}
        </div>
        {me.role === 'admin' && <button className="ghost" onClick={() => setShowAccess(s => !s)}>{showAccess ? 'Hide access' : 'Access'}</button>}
        <button className="ghost" onClick={() => setShowToday(t => !t)}>{showToday ? 'Hide list' : 'This week'}</button>
        <button className="ghost" onClick={reset} disabled={busy}>New conversation</button>
        <a className="ghost" href="/signout" title={me.login}>Sign out</a>
      </header>

      {showAccess && <AccessPanel me={me} onClose={() => setShowAccess(false)} />}

      <main>
        {showToday && <TodayPanel today={today} loading={todayLoading} onAsk={q => { setShowToday(false); ask(q) }} onRefresh={() => loadToday(true)} />}
        {msgs.length === 0 && (
          <div className="empty">
            <p>Ask about a customer before a call. Tap the mic or type.</p>
            {!seesAll && <p className="muted">You see the accounts assigned to you in P21. Similar-customer comparisons still use everyone's buying patterns, with other reps' figures held back.</p>}
            <div className="chips">
              {(seesAll ? SUGGESTIONS : REP_SUGGESTIONS).map(s => <button key={s} className="chip" onClick={() => ask(s)}>{s}</button>)}
            </div>
          </div>
        )}
        {msgs.map((m, i) => (
          <div key={i} className={`msg ${m.role}`}>
            {m.role === 'assistant' && m.tools && m.tools.length > 0 && (
              <div className="tools">{m.tools.map((t, j) => <span key={j} className="tag">{t}</span>)}</div>
            )}
            <div className="bubble">{m.text || (m.role === 'assistant' && busy && i === msgs.length - 1 ? <span className="thinking">{status || 'working…'}</span> : null)}</div>
            {m.ms !== undefined && <div className="meta">{(m.ms / 1000).toFixed(1)} s</div>}
          </div>
        ))}
        <div ref={bottom} />
      </main>

      <footer>
        {status && !busy && <div className="hint">{status}</div>}
        <form onSubmit={e => { e.preventDefault(); ask(input) }}>
          {canListen && (
            <button type="button" className={`mic ${listening ? 'on' : ''} ${transcribing ? 'busy' : ''}`} onClick={toggleMic} disabled={busy || transcribing}
              title={listening ? 'Stop (Esc to cancel)' : 'Speak'} aria-label={listening ? 'Stop recording' : 'Start recording'}
              style={listening ? { ['--lvl' as string]: level } : undefined}>
              {listening && <span className="ring" />}
              {transcribing ? (
                <svg viewBox="0 0 24 24" width="22" height="22" className="spin"><circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" strokeWidth="2.5" strokeDasharray="42 14" strokeLinecap="round"/></svg>
              ) : listening ? (
                <svg viewBox="0 0 24 24" width="22" height="22"><rect x="6" y="6" width="12" height="12" rx="2" fill="currentColor"/></svg>
              ) : (
                <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                  <rect x="9" y="3" width="6" height="11" rx="3" fill="currentColor" stroke="none"/>
                  <path d="M5 11a7 7 0 0 0 14 0"/><path d="M12 18v3"/><path d="M8.5 21h7"/>
                </svg>
              )}
            </button>
          )}
          {listening && (
            <div className="meter" aria-hidden="true">
              {Array.from({ length: 12 }, (_, i) => <span key={i} style={{ height: `${20 + Math.max(0, Math.min(1, level * 1.6 - i * 0.08)) * 80}%` }} />)}
            </div>
          )}
          <input ref={inputRef} value={input} onChange={e => { setInput(e.target.value); if (status) setStatus('') }} placeholder={listening ? 'Listening… tap the square when done' : transcribing ? 'Transcribing…' : 'Ask about a customer…'} disabled={busy} autoFocus />
          {/* distinct keys: React must replace the node, not patch it. Patching turned the just-clicked Stop button into the
              submit button before the browser ran the click's default action, which re-submitted the restored text. */}
          {busy
            ? <button key="stop" type="button" className="stop" onMouseDown={e => e.preventDefault()} onClick={e => { e.preventDefault(); stop() }} title="Stop this answer (Esc)">Stop</button>
            : <button key="ask" type="submit" disabled={!input.trim()}>Ask</button>}
        </form>
      </footer>
    </div>
  )
}
