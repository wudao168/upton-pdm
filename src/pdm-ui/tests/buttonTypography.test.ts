import { afterEach, expect, it, vi } from 'vitest'
import { createApp } from 'vue'
import { installButtonTypography } from '../src/buttonTypography'
let dispose: () => void
const marker = 'pdm-button-font-80x30'
function button(width: number, height: number, role?: string) {
  const element = document.createElement('button')
  Object.defineProperties(element, { offsetWidth: { configurable: true, value: width }, offsetHeight: { configurable: true, value: height } })
  if (role) element.setAttribute('role', role)
  document.body.append(element)
  return element
}
function install() {
  const app = createApp({})
  vi.spyOn(app, 'onUnmount').mockImplementation(callback => { dispose = callback; return app })
  installButtonTypography(app)
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0))
afterEach(() => { dispose?.(); document.body.innerHTML = ''; vi.restoreAllMocks() })
it('matches only 80 by 30 ordinary buttons', () => {
  const target = button(80, 30)
  const others = [button(80, 32), button(60, 30), button(90, 30), button(80, 30, 'tab')]
  install()
  expect(target.classList.contains(marker)).toBe(true)
  expect(others.every(item => !item.classList.contains(marker))).toBe(true)
})
it('covers lazy dialogs and restores typography after Vue replaces classes', async () => {
  install()
  const target = button(80, 30)
  await settle()
  expect(target.classList.contains(marker)).toBe(true)
  target.className = 'el-button is-disabled'
  await settle()
  expect(target.classList.contains(marker)).toBe(true)
  expect(target.classList.contains('is-disabled')).toBe(true)
  Object.defineProperty(target, 'offsetWidth', { value: 90 })
  target.style.width = '90px'
  await settle()
  expect(target.classList.contains(marker)).toBe(false)
})