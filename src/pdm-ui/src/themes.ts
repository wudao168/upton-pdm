export const pdmThemes = [
  { value: 'a', label: '海湾蓝', color: '#1D4ED8' },
  { value: 'c', label: '松石青', color: '#0F766E' },
  { value: 'p', label: '鸢尾紫', color: '#6D28D9' },
  { value: 'o', label: '琥珀橙', color: '#A84B08' },
  { value: 'g', label: '石墨灰', color: '#F5F5F7' },
] as const

export type PdmTheme = typeof pdmThemes[number]['value']

export function isPdmTheme(value: unknown): value is PdmTheme {
  return pdmThemes.some(theme => theme.value === value)
}
