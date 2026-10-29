import { useCallback, useState } from 'react'

export type Theme = 'dark' | 'light'
const THEME_STORAGE = 'reapdeep.theme'

export function initialTheme(): Theme {
  try {
    return localStorage.getItem(THEME_STORAGE) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

export function useTheme() {
  const [theme, setTheme] = useState<Theme>(() => (document.documentElement.dataset.theme as Theme) ?? initialTheme())
  const toggle = useCallback(() => {
    setTheme((current) => {
      const next: Theme = current === 'dark' ? 'light' : 'dark'
      document.documentElement.dataset.theme = next
      try {
        localStorage.setItem(THEME_STORAGE, next)
      } catch {
      }
      return next
    })
  }, [])
  return { theme, toggle }
}
