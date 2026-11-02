import { mount } from '@vue/test-utils'
import { defineComponent, ref } from 'vue'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import SearchField from '../src/components/SearchField.vue'

describe('统一搜索外观', () => {
  it('保留实时筛选，点击按钮重新应用当前输入且不提交外围表单', async () => {
    const submit = vi.fn(), search = vi.fn()
    const Host = defineComponent({ components: { SearchField }, setup() { return { query: ref(''), submit, search } }, template: '<form @submit.prevent="submit"><SearchField @search="search"><input v-model="query" aria-label="搜索名称"></SearchField><output>{{ query }}</output></form>' })
    const wrapper = mount(Host, { attachTo: document.body })
    await wrapper.get('input').setValue('轴承')
    expect(wrapper.get('output').text()).toBe('轴承')
    await wrapper.get('button').trigger('click')
    expect(search).toHaveBeenCalledOnce()
    expect(submit).not.toHaveBeenCalled()
    expect(document.activeElement).toBe(wrapper.get('input').element)
    wrapper.unmount()
  })
  it('保留 Element Plus 清空、回车查询及原查询按钮，点击只执行一次', async () => {
    const search = vi.fn()
    const Host = defineComponent({ components: { SearchField }, setup() { return { query: ref('轴承'), search } }, template: '<SearchField><el-input v-model="query" clearable aria-label="搜索料品" @keyup.enter="search" /><template #action><el-button @click="search">搜索</el-button></template></SearchField>' })
    const wrapper = mount(Host, { global: { plugins: [ElementPlus] } })
    expect(wrapper.findAll('button')).toHaveLength(1)
    await wrapper.get('input').trigger('keyup', { key: 'Enter' })
    expect(search).toHaveBeenCalledTimes(1)
    await wrapper.get('button').trigger('click')
    expect(search).toHaveBeenCalledTimes(2)
    await wrapper.get('.el-input').trigger('mouseenter')
    await wrapper.get('.el-input__clear').trigger('click')
    expect((wrapper.get('input').element as HTMLInputElement).value).toBe('')
    wrapper.unmount()
  })
  it('禁用时不触发搜索', async () => {
    const wrapper = mount(SearchField, { props: { disabled: true }, slots: { default: '<input disabled>' } })
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('search')).toBeUndefined()
    wrapper.unmount()
  })
  it('保留非相邻查询按钮的位置时不生成重复按钮', () => {
    const wrapper = mount(SearchField, { props: { showAction: false }, slots: { default: '<input aria-label="搜索标准物料">' } })
    expect(wrapper.find('button').exists()).toBe(false)
    expect(wrapper.get('input').attributes('aria-label')).toBe('搜索标准物料')
    wrapper.unmount()
  })
})
