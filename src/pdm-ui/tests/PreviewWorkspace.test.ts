import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
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
  it('图档属性以预览区顶部一行横向常驻显示，且不提供折叠', () => {
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem },
    })

    const bar = wrapper.get('.pdm-preview-properties-bar')
    expect(bar.find('.pdm-preview-properties').exists()).toBe(true)
    expect(bar.findAll('.pdm-preview-properties > div')).toHaveLength(8)
    expect(wrapper.find('.pdm-preview-properties-toggle').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="折叠图档属性"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="折叠图档信息卡"]').exists()).toBe(false)
    expect(wrapper.get('.pdm-preview-properties-bar__title').text()).toBe('图档属性')
    expect(wrapper.get('.pdm-preview-properties').attributes('style') ?? '').not.toContain('display: none')
  })

  it('shows the matched BOM material code instead of the document drawing name', () => {
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem },
    })

    const materialNumber = wrapper.get('[aria-label="图档属性"] > div:first-child')
    expect(materialNumber.get('dt').text()).toBe('物料编码')
    expect(materialNumber.get('dd').text()).toBe('01020014733')
    const materialName = wrapper.get('[aria-label="图档属性"] > div:nth-child(2)')
    expect(materialName.get('dd').text()).toBe('导向轴支座')
    expect(wrapper.get('.pdm-preview-properties').element).toBeTruthy()
    expect(wrapper.find('.pdm-web-preview-side-controls').exists()).toBe(false)
    expect(wrapper.get('.pdm-preview-state-content').text()).toContain('加载预览')
  })

  it('审核结论栏容器位于保存批注右侧，页面通过 decision-bar 插槽注入结论栏', () => {
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem, desktopAvailable: true },
      slots: { 'decision-bar': '<div class="stub-decision">审核结论</div>' },
    })

    const row = wrapper.get('.pdm-preview-markup-row')
    const host = row.get('#drawing-review-decision-host')
    // 行内顺序：批注工具 → 保存批注 → 审核结论栏。
    expect(row.find('.pdm-markup-toolbar').exists()).toBe(true)
    expect(host.element.previousElementSibling?.getAttribute('aria-label')).toBe('保存批注')
    expect(host.get('.stub-decision').text()).toBe('审核结论')
    expect(row.find('button[aria-label="更多"]').exists()).toBe(false)
  })

  it('结论栏容器在预览工具条内，页面通过 decision-bar 插槽把结论栏渲染进去', async () => {
    const wrapper = mount(PreviewWorkspace, {
      attachTo: document.body,
      props: { selected, related: [], bomItem, desktopAvailable: false },
      slots: { 'decision-bar': '<div class="stub-decision">审核结论</div>' },
    })
    await nextTick()

    const host = document.getElementById('drawing-review-decision-host')
    expect(host?.closest('.pdm-preview-markup-row')).toBeTruthy()
    expect(host?.querySelector('.stub-decision')?.textContent).toBe('审核结论')
    wrapper.unmount()
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
      '打开最新', '编辑打开', '保存批注',
    ])
    expect(wrapper.find('button[aria-label="使用位置"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="作废图档"]').exists()).toBe(false)
    // 客户端与网页端一致：图档属性行始终常驻显示，不随桌面预览而隐藏。
    expect(wrapper.get('.pdm-preview-properties-bar').find('[aria-label="图档属性"]').exists()).toBe(true)

    window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'dirty' } }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('button[aria-label="保存批注"]').classes()).toContain('is-markup-dirty')

    await wrapper.get('button[aria-label="保存批注"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-save-markup', payload: undefined })
  })

  it('shows a local lightweight image and keeps eDrawings behind an explicit action', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage } },
    })
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem, desktopAvailable: true },
    })

    expect(postMessage).toHaveBeenCalledWith({
      type: 'lightweight-preview-request',
      payload: { documentId: 'document-1' },
    })
    expect(wrapper.emitted('preview')).toBeUndefined()

    window.dispatchEvent(new CustomEvent('pdm-lightweight-preview-status', {
      detail: { documentId: 'document-1', state: 'ready', dataUrl: 'data:image/jpeg;base64,preview' },
    }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.pdm-lightweight-preview-image').attributes('src')).toBe('data:image/jpeg;base64,preview')
    expect(wrapper.get('.pdm-lightweight-preview-stage .pdm-lightweight-preview-controls').text()).toContain('加载交互预览')
    expect(wrapper.text()).toContain('本地轻量预览')

    await wrapper.setProps({ selected: { ...selected, id: 'node-2', documentId: 'document-2', fileName: 'SECOND.SLDPRT' } })
    window.dispatchEvent(new CustomEvent('pdm-lightweight-preview-status', {
      detail: { documentId: 'document-1', state: 'ready', dataUrl: 'data:image/jpeg;base64,stale' },
    }))
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.pdm-lightweight-preview-image').exists()).toBe(false)
    expect(wrapper.emitted('preview')).toBeUndefined()

    const interactionButton = wrapper.findAll('button').find(button => button.text() === '加载交互预览')
    expect(interactionButton).toBeTruthy()
    await interactionButton!.trigger('click')
    expect(wrapper.emitted('preview')?.[0]?.[0]).toMatchObject({ documentId: 'document-2' })
  })

  it('keeps loading the following documents after the first interactive load in the client', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage } },
    })
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem, desktopAvailable: true },
    })

    await wrapper.findAll('button').find(button => button.text() === '加载交互预览')!.trigger('click')
    expect(wrapper.emitted('preview')?.[0]?.[0]).toMatchObject({ documentId: 'document-1' })

    await wrapper.setProps({ selected: { ...selected, id: 'node-2', documentId: 'document-2', fileName: 'SECOND.SLDPRT' } })
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('preview')?.[1]?.[0]).toMatchObject({ documentId: 'document-2' })
    expect(wrapper.findAll('button').some(button => button.text() === '加载交互预览')).toBe(false)

    wrapper.unmount()
  })

  it('suspends the client preview instead of releasing it when the tab is left', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage } },
    })
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem, desktopAvailable: true, active: true },
    })
    postMessage.mockClear()

    await wrapper.setProps({ active: false })
    await wrapper.vm.$nextTick()
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-suspend', payload: undefined })
    expect(postMessage).not.toHaveBeenCalledWith({ type: 'preview-host-hide', payload: undefined })

    wrapper.unmount()
  })

  it('图档属性在顶栏常驻并允许折行，审批与批注工具在第二行', () => {
    const wrapper = mount(PreviewWorkspace, {
      props: { selected, related: [], bomItem },
    })

    const toolbar = wrapper.get('.pdm-preview-toolbar')
    const bar = toolbar.get('.pdm-preview-properties-bar')
    expect(bar.find('.pdm-preview-properties').exists()).toBe(true)
    expect(toolbar.element.firstElementChild).toBe(bar.element)
    expect(wrapper.find('.pdm-preview-document-switcher').exists()).toBe(false)
    expect(wrapper.find('.pdm-related-documents').exists()).toBe(false)
    expect(wrapper.find('.pdm-preview-markup-row #drawing-review-decision-host').exists()).toBe(true)
    expect(wrapper.find('.pdm-preview-markup-row [aria-label="图形批注工具"]').exists()).toBe(true)
    expect(wrapper.find('.pdm-preview-markup-row button[aria-label="保存批注"]').exists()).toBe(true)

    wrapper.unmount()
  })
})
