import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent, ref } from 'vue'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { installPanelHeaderActions } from '../src/panelHeaderActions'
import PanelActions from '../src/components/PanelActions.vue'
import RawDrawerPanel from '../src/components/RawDrawerPanel.vue'

let wrapper: ReturnType<typeof mount>
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })

describe('panel header actions', () => {
  it('moves custom drawer footer buttons before the original close control', async () => {
    const saved = ref(0)
    const host = defineComponent({ components: { RawDrawerPanel, PanelActions }, setup() { return { saved } }, template: '<RawDrawerPanel class="legacy-panel" role="dialog"><template #header="{setActionTarget}"><header><div class="pdm-panel-topbar"><h3>批量编辑</h3><div class="pdm-panel-header-actions" :ref="setActionTarget" /></div><button class="pdm-icon-button">×</button></header></template><p>内容</p><PanelActions><footer><button @click="saved++">确认保存（{{ saved }}）</button></footer></PanelActions></RawDrawerPanel>' })
    wrapper = mount(host, { attachTo: document.body })
    await flushPromises()
    const header = document.body.querySelector('.legacy-panel > header')!
    expect(header.textContent).toContain('确认保存')
    expect(document.body.querySelector('.legacy-panel > footer')).toBeNull()
    ;(header.querySelector('.pdm-panel-header-actions button') as HTMLButtonElement).click()
    expect(saved.value).toBe(1)
    await flushPromises()
    expect(header.textContent).toContain('确认保存（1）')
  })
  for (const kind of ['drawer', 'dialog']) {
    it(`moves ${kind} actions into the header and preserves clicks and disabled state`, async () => {
      const open = ref(true), pending = ref(false), saved = ref(0)
      const host = defineComponent({
        setup() { return { open, pending, saved } },
        template: `<el-${kind} v-model="open" title="操作面板"><input aria-label="业务输入"><p>{{ saved }}</p><template #footer><el-button @click="open=false">取消</el-button><el-button :disabled="pending" @click="saved++">保存</el-button></template></el-${kind}>`,
      })
      wrapper = mount(host, { attachTo: document.body, global: { stubs: { transition: false }, plugins: [ElementPlus, { install: installPanelHeaderActions }] } })
      await flushPromises()
      const header = document.body.querySelector(`.el-${kind}__header`)!
      expect(header.textContent).toContain('操作面板')
      expect(header.textContent).toContain('取消')
      expect(header.textContent).toContain('保存')
      expect(document.body.querySelector(`.el-${kind}__footer`)).toBeNull()
      const save = Array.from(header.querySelectorAll('button')).find(button => button.textContent?.trim() === '保存')!
      save.click(); await flushPromises()
      expect(saved.value).toBe(1)
      pending.value = true; await flushPromises()
      expect(save.disabled).toBe(true)
      const cancel = Array.from(header.querySelectorAll('button')).find(button => button.textContent?.trim() === '取消')!
      cancel.click(); await flushPromises()
      expect(open.value).toBe(false)
    })
  }
  it('preserves a custom header and the native close handler', async () => {
    const open = ref(true)
    const host = defineComponent({ setup() { return { open } }, template: '<el-drawer v-model="open"><template #header="{close}"><strong>程序模板详情</strong><button @click="close">自定义关闭</button></template><p>内容</p><template #footer><button>下载批准版本</button></template></el-drawer>' })
    wrapper = mount(host, { attachTo: document.body, global: { stubs: { transition: false }, plugins: [ElementPlus, { install: installPanelHeaderActions }] } })
    await flushPromises()
    const header = document.body.querySelector('.el-drawer__header')!
    expect(header.textContent).toContain('程序模板详情')
    expect(header.textContent).toContain('下载批准版本')
    ;(Array.from(header.querySelectorAll('button')).find(button => button.textContent === '自定义关闭')!).click()
    await flushPromises()
    await vi.waitFor(() => expect(open.value).toBe(false))
  })
  it('moves nested component actions and keeps an external submit button attached to its form', async () => {
    const saved = ref(0)
    const child = defineComponent({ components: { PanelActions }, setup() { return { saved } }, template: '<form id="header-submit-test" @submit.prevent="saved++"><input value="已填写" required><PanelActions><button type="submit" form="header-submit-test">创建草稿</button></PanelActions></form>' })
    const host = defineComponent({ components: { Child: child }, template: '<el-drawer :model-value="true" title="发布"><Child /></el-drawer>' })
    wrapper = mount(host, { attachTo: document.body, global: { plugins: [ElementPlus, { install: installPanelHeaderActions }] } })
    await flushPromises()
    const button = document.body.querySelector<HTMLButtonElement>('.el-drawer__header button[form="header-submit-test"]')!
    expect(button).not.toBeNull()
    expect(button.form?.id).toBe('header-submit-test')
    button.click(); await flushPromises()
    expect(saved.value).toBe(1)
    wrapper.unmount()
    wrapper = mount(child, { attachTo: document.body })
    await flushPromises()
    expect(document.body.querySelector('form > button[form="header-submit-test"]')).not.toBeNull()
  })
})
