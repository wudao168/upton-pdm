import { mount } from '@vue/test-utils'
import { afterEach, expect, it, vi } from 'vitest'
import OplBoard from '../src/components/OplBoard.vue'
import type { OplRow } from '../src/projectOpl'

let wrapper: ReturnType<typeof mount>
const row = { issue: { id: 'drag-test', number: 1, status: '待处理', description: '拖动验证', owner: '', priority: '高', createdAt: '2026-11-07', dueDate: null }, canEdit: true, canComplete: true } as OplRow
function setup(editable = true) {
  wrapper = mount(OplBoard, { attachTo: document.body, props: { rows: [{ ...row, canEdit: editable, canComplete: editable }], busy: false, movingId: null, name: (s: string) => s, overdue: () => false } })
  Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: vi.fn(() => wrapper.get('[data-status="处理中"]').element) })
}
function pointer(target: EventTarget, type: string, x: number) {
  const event = new Event(type, { bubbles: true, cancelable: true })
  Object.assign(event, { pointerId: 1, isPrimary: true, button: 0, buttons: type === 'pointerup' ? 0 : 1, clientX: x, clientY: 30 })
  target.dispatchEvent(event)
}
function start() { pointer(wrapper.get('article').element, 'pointerdown', 20); pointer(window, 'pointermove', 100) }
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = ''; delete (document as unknown as Record<string, unknown>).elementFromPoint })
it('moves once with pointer events and suppresses the trailing detail click', async () => {
  setup(); start(); expect(document.querySelector('.opl-board-drag-preview')).not.toBeNull()
  pointer(window, 'pointerup', 100); await wrapper.get('article button').trigger('click')
  expect(wrapper.emitted('move')?.map(args => args[1])).toEqual(['处理中'])
  expect(wrapper.emitted('open')).toBeUndefined()
  expect(document.querySelector('.opl-board-drag-preview')).toBeNull()
})
it('cancels on Escape and cleans up the drag preview', () => {
  setup(); start(); window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })); pointer(window, 'pointerup', 100)
  expect(wrapper.emitted('move')).toBeUndefined(); expect(document.querySelector('.opl-board-drag-preview')).toBeNull()
})
it('does not drag without permission, while retaining detail clicks', async () => {
  setup(false); start(); pointer(window, 'pointerup', 100); await wrapper.get('article button').trigger('click')
  expect(wrapper.emitted('move')).toBeUndefined(); expect(wrapper.emitted('open')).toHaveLength(1)
})
it('cancels when saving begins and removes window listeners on unmount', async () => {
  setup(); start(); await wrapper.setProps({ busy: true }); pointer(window, 'pointerup', 100)
  expect(wrapper.emitted('move')).toBeUndefined(); expect(document.querySelector('.opl-board-drag-preview')).toBeNull()
  await wrapper.setProps({ busy: false }); start(); wrapper.unmount(); pointer(window, 'pointerup', 100)
  expect(wrapper.emitted('move')).toBeUndefined(); expect(document.querySelector('.opl-board-drag-preview')).toBeNull()
})
