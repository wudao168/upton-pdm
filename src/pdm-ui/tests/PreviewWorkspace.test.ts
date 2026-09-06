import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import PreviewWorkspace from '../src/components/PreviewWorkspace.vue'
import type { BomItem, DocumentNode } from '../src/types'

const selected: DocumentNode = {
  id: 'node-1',
  documentId: 'document-1',
  drawingNumber: 'SHF20-20211227114217544',
  name: 'SHF20-20211227114217544',
  fileName: 'SHF20-20211227114217544.SLDPRT',
  kind: 'Part',
  configuration: 'Default',
  quantity: 1,
  version: 'W2',
  status: 'Normal',
  children: [],
}

const bomItem: BomItem = {
  id: 'bom-item-1',
  kind: 'Standard',
  sequence: 1,
  drawingNumber: '01020014733',
  name: '导向轴支座',
  quantity: 1,
  unit: '个',
  specification: 'SHF20',
  brand: '美亚特',
  material: '6061',
  revision: 'W2',
  complete: true,
  sourceDocumentId: 'document-1',
}

describe('PreviewWorkspace', () => {
  it('shows the matched BOM material code instead of the document drawing name', () => {
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem },
    })

    const materialNumber = wrapper.get('[aria-label="图档属性"] > div:first-child')
    expect(materialNumber.get('dt').text()).toBe('物料编码')
    expect(materialNumber.get('dd').text()).toBe('01020014733')
    const materialName = wrapper.get('[aria-label="图档属性"] > div:nth-child(2)')
    expect(materialName.get('dd').text()).toBe('导向轴支座')
  })

  it('shows only the active markup actions and hides reference and obsolete actions', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage } },
    })
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem, desktopAvailable: true, canManageLifecycle: true, canEditDocuments: true },
    })

    const markupToolbar = wrapper.get('[aria-label="图形批注工具"]')
    expect(markupToolbar.findAll('button').map(button => button.attributes('aria-label'))).toEqual([
      '引线批注', '云线批注', '框选批注', '手绘批注',
    ])
    expect(wrapper.findAll('.pdm-preview-command').map(button => button.attributes('aria-label'))).toEqual([
      '保存批注', '打开最新', '编辑打开',
    ])
    expect(wrapper.find('button[aria-label="使用位置"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="作废图档"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="图档属性"]').exists()).toBe(false)

    window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'dirty' } }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('button[aria-label="保存批注"]').classes()).toContain('is-markup-dirty')

    await wrapper.get('button[aria-label="保存批注"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-save-markup', payload: undefined })
  })
})
