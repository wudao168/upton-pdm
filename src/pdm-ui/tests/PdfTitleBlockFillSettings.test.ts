import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import PdfTitleBlockFillSettings from '../src/components/PdfTitleBlockFillSettings.vue'
import type { PdmSystemSettings } from '../src/types'

const settings = { vaultRoot: 'D:\\Vault', releaseRoot: 'D:\\Release' } as PdmSystemSettings
describe('PDF 属性自动填写设置', () => {
  it('defaults all 19 fields off and saves independent fields while preserving other settings', async () => {
    const onSaveSettings = vi.fn(async input => input)
    const wrapper = mount(PdfTitleBlockFillSettings, { props: { settings, administrator: true, pending: false, onSaveSettings } })
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(19)
    expect(wrapper.findAll('input:checked')).toHaveLength(0)
    await wrapper.get('[aria-label="自动填写易损件"]').setValue(true)
    await wrapper.get('[aria-label="自动填写物料编码"]').setValue(true)
    await wrapper.get('[aria-label="自动填写重量"]').setValue(true)
    await wrapper.get('[aria-label="自动填写设计日期"]').setValue(true)
    await wrapper.get('button').trigger('click')
    await flushPromises()
    expect(onSaveSettings).toHaveBeenCalledWith({ ...settings, pdfTitleBlockFill: { enabledFields: ['wearPart', 'materialCode', 'weight', 'designedOn'] } })
    expect(wrapper.get('[aria-label="自动填写设计人"]').element).toHaveProperty('checked', false)
  })
  it('shows persisted choices and prevents non-administrator changes', async () => {
    const onSaveSettings = vi.fn()
    const wrapper = mount(PdfTitleBlockFillSettings, { props: { settings: { ...settings, pdfTitleBlockFill: { enabledFields: ['quantity'] } }, administrator: false, pending: false, onSaveSettings } })
    expect(wrapper.get('[aria-label="自动填写本次下发数量"]').element).toHaveProperty('checked', true)
    expect(wrapper.get('fieldset').attributes('disabled')).toBeDefined()
    expect(wrapper.get('button').attributes('disabled')).toBeDefined()
    await wrapper.get('button').trigger('click')
    expect(onSaveSettings).not.toHaveBeenCalled()
  })
})
