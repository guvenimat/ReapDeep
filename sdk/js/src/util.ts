declare const __VERSION__: string
export const VERSION = __VERSION__

export function uuidv7(): string {
  const b = new Uint8Array(16)
  crypto.getRandomValues(b)
  const ms = Date.now()
  for (let i = 0; i < 6; i++) b[i] = Math.floor(ms / 2 ** (8 * (5 - i))) & 0xff
  b[6] = (b[6] & 0x0f) | 0x70
  b[8] = (b[8] & 0x3f) | 0x80
  const h = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('')
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`
}

export function createStorage(key: string) {
  let memory: string | null = null
  return {
    get<T>(): T | null {
      try {
        const v = localStorage.getItem(key)
        return v ? (JSON.parse(v) as T) : null
      } catch {
        return memory ? (JSON.parse(memory) as T) : null
      }
    },
    set(value: unknown): void {
      const s = JSON.stringify(value)
      try {
        localStorage.setItem(key, s)
      } catch {
        memory = s
      }
    },
  }
}
