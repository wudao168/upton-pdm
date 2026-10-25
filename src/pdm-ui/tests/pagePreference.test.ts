import { mount } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { afterEach, describe, expect, it } from 'vitest'
import { pagePreferenceScopeKey, rememberedTab } from '../src/pagePreference'

afterEach(() => { window.localStorage.clear(); window.sessionStorage.clear() })
const Page = defineComponent({ setup() { const tab = rememberedTab('example', 'first', ['first','second']); return () => h('button',{ onClick: () => { tab.value = tab.value === 'first' ? 'second' : 'first' } },tab.value) } })
describe('页面页签记忆', () => {
  it('刷新后恢复页签，账号和公司之间互不影响', async () => {
    const scope=ref(JSON.stringify(['alice','company']))
    const options={global:{provide:{[pagePreferenceScopeKey as symbol]:scope}}}
    let wrapper=mount(Page,options)
    await wrapper.get('button').trigger('click');expect(wrapper.text()).toBe('second');wrapper.unmount()
    window.localStorage.setItem(`upton-pdm-page-tab:v1:${scope.value}:example`,'first')
    wrapper=mount(Page,options);expect(wrapper.text()).toBe('second')
    scope.value=JSON.stringify(['bob','company']);await wrapper.vm.$nextTick();expect(wrapper.text()).toBe('first')
    scope.value=JSON.stringify(['alice','other-company']);await wrapper.vm.$nextTick();expect(wrapper.text()).toBe('first')
    scope.value=JSON.stringify(['alice','company']);await wrapper.vm.$nextTick();expect(wrapper.text()).toBe('second')
    wrapper.unmount()
  })
  it('忽略不再支持的旧页签', () => {
    const scope=ref(JSON.stringify(['alice','company']))
    window.localStorage.setItem(`upton-pdm-page-tab:v1:${scope.value}:example`,'removed-tab')
    const wrapper=mount(Page,{global:{provide:{[pagePreferenceScopeKey as symbol]:scope}}})
    expect(wrapper.text()).toBe('first');wrapper.unmount()
  })
})
