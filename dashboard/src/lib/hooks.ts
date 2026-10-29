import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { errorMessage } from '../api/client'

export interface QueryState<T> { data?: T; error?: string; loading: boolean }

export function useQuery<T>(fn: (signal: AbortSignal) => Promise<T>, deps: unknown[]): QueryState<T> {
  const [state, setState] = useState<QueryState<T>>({ loading: true })
  useEffect(() => {
    const ctl = new AbortController()
    setState((s) => ({ data: s.data, loading: true }))
    fn(ctl.signal)
      .then((data) => !ctl.signal.aborted && setState({ data, loading: false }))
      .catch((e: Error) => !ctl.signal.aborted && setState({ error: e.message, loading: false }))
    return () => ctl.abort()
  }, deps)
  return state
}

export function useDebounced<T>(value: T, ms = 400): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return v
}

export function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true)
    setError(undefined)
    try {
      await fn()
      return true
    } catch (err) {
      setError(errorMessage(err))
      return false
    } finally {
      setBusy(false)
    }
  }
  return { busy, error, setError, run }
}

export function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => setWidth(Math.floor(entry.contentRect.width)))
    ro.observe(el)
    setWidth(el.clientWidth)
    return () => ro.disconnect()
  }, [])
  return [ref, width] as const
}
