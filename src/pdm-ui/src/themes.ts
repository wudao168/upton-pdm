// 保留账号和客户端已有主题标识，替换其界面含义，避免丢失跨端偏好。
export const pdmThemes = [
  { value: 'a', label: '系统', color: '#8d8d8d' },
  { value: 'c', label: '深色', color: '#171717' },
  { value: 'g', label: '浅色', color: '#fcfcfc' },
] as const
export type PdmTheme = typeof pdmThemes[number]['value']
export type PdmColorScheme = 'dark' | 'light'
export function isPdmTheme(value: unknown): value is PdmTheme { return pdmThemes.some(theme => theme.value === value) }
export function resolvePdmColorScheme(theme: PdmTheme, systemDark = typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches): PdmColorScheme {
  return theme === 'c' || theme === 'a' && systemDark ? 'dark' : 'light'
}
export function applyPdmTheme(theme: PdmTheme) {
  const root = document.documentElement
  const scheme = resolvePdmColorScheme(theme)
  root.dataset.pdmTheme = theme
  root.dataset.pdmColorScheme = scheme
  root.style.colorScheme = scheme
}
export function watchPdmSystemTheme(readTheme: () => PdmTheme) {
  if (typeof window.matchMedia !== 'function') return () => {}
  const media = window.matchMedia('(prefers-color-scheme: dark)')
  const update = () => { if (readTheme() === 'a') applyPdmTheme('a') }
  media.addEventListener?.('change', update)
  return () => media.removeEventListener?.('change', update)
}
