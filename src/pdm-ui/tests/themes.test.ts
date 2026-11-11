import { afterEach, describe, expect, it, vi } from 'vitest'
import { applyPdmTheme, isPdmTheme, pdmThemes, resolvePdmColorScheme, watchPdmSystemTheme } from '../src/themes'
afterEach(() => { vi.unstubAllGlobals(); document.documentElement.removeAttribute('data-pdm-color-scheme'); document.documentElement.removeAttribute('data-pdm-theme') })
describe('明暗主题', () => {
  it('只提供系统、深色、浅色，兼容已有账号标识', () => {
    expect(pdmThemes.map(item => item.label)).toEqual(['系统','深色','浅色'])
    expect(resolvePdmColorScheme('c', false)).toBe('dark')
    expect(resolvePdmColorScheme('g', true)).toBe('light')
    expect(resolvePdmColorScheme('a', true)).toBe('dark')
    expect(resolvePdmColorScheme('a', false)).toBe('light')
    expect(isPdmTheme('p')).toBe(false)
  })
  it('系统变更只更新系统模式，手动明暗选择不被覆盖，卸载后停止监听', () => {
    const listeners = new Set<() => void>()
    const media = { matches:false, addEventListener:vi.fn((_name, listener)=>listeners.add(listener)), removeEventListener:vi.fn((_name, listener)=>listeners.delete(listener)) }
    vi.stubGlobal('matchMedia', () => media)
    let theme:'a'|'c'|'g'='a'
    const stop=watchPdmSystemTheme(()=>theme)
    applyPdmTheme(theme)
    expect(document.documentElement.dataset.pdmColorScheme).toBe('light')
    media.matches=true; listeners.forEach(listener=>listener())
    expect(document.documentElement.dataset.pdmColorScheme).toBe('dark')
    theme='g'; applyPdmTheme(theme); listeners.forEach(listener=>listener())
    expect(document.documentElement.dataset.pdmColorScheme).toBe('light')
    stop(); expect(listeners.size).toBe(0)
  })
})
