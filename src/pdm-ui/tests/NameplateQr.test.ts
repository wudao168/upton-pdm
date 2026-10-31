import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import NameplateQr from '../src/components/NameplateQr.vue'
import { createNameplateQr } from '../src/productionDrawingQr'

vi.mock('../src/productionDrawingQr', () => ({ createNameplateQr: vi.fn() }))
beforeEach(() => { vi.mocked(createNameplateQr).mockReset() })

it('切换设备时忽略旧二维码生成结果', async () => {
  let finish!: (value: { content: string; src: string }) => void
  vi.mocked(createNameplateQr).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  vi.mocked(createNameplateQr).mockResolvedValueOnce({ content: '70000047', src: 'data:image/svg+xml,new' })
  const wrapper = mount(NameplateQr, { props: { serialNumber: '70000046' } })
  await flushPromises()
  await wrapper.setProps({ serialNumber: '70000047' }); await flushPromises()
  finish({ content: '70000046', src: 'data:image/svg+xml,old' }); await flushPromises()
  expect(wrapper.get('img').attributes('title')).toBe('70000047')
  expect(wrapper.get('img').attributes('src')).toBe('data:image/svg+xml,new')
  wrapper.unmount()
})

it('生成失败时显示中文提示，重试仍使用同一序列号', async () => {
  vi.mocked(createNameplateQr).mockRejectedValueOnce(new Error('wasm fetch ECONNREFUSED'))
  const wrapper = mount(NameplateQr, { props: { serialNumber: '70000046' } })
  await flushPromises()
  expect(wrapper.get('[role=alert]').text()).toContain('铭牌二维码生成失败')
  expect(wrapper.text()).not.toContain('ECONNREFUSED')
  vi.mocked(createNameplateQr).mockResolvedValueOnce({ content: '70000046', src: 'data:image/svg+xml,qr' })
  await wrapper.get('button').trigger('click'); await flushPromises()
  expect(createNameplateQr).toHaveBeenLastCalledWith('70000046')
  expect(wrapper.get('img').attributes('title')).toBe('70000046')
  wrapper.unmount()
})
