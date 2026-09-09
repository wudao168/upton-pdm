import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessage as toastMessage } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import AppHeader from '../src/components/AppHeader.vue'
import { ElMessage, globalStatus } from '../src/statusMessage'

const wrappers: ReturnType<typeof mount>[] = []
function header() {
  const wrapper = mount(AppHeader, {
    attachTo: document.body,
    props: { online: true, userName: '工程师', username: 'engineer' },
    global: { plugins: [ElementPlus] },
  })
  wrappers.push(wrapper)
  return wrapper
}
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.useRealTimers()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('global operation status', () => {
  it('shows the animated UPTON mark without visible text or an empty popover while idle', async () => {
    const wrapper = header()
    expect(wrapper.get('.pdm-global-status').text()).toBe('')
    expect(wrapper.get('.pdm-global-status').element.tagName).toBe('DIV')
    expect(wrapper.findAll('.pdm-global-status__letter').map(letter => letter.attributes('data-letter'))).toEqual(['U', 'P', 'T', 'O', 'N'])
    expect(wrapper.findAll('.pdm-global-status__letter .dash')).toHaveLength(4)
    expect(wrapper.findAll('.pdm-global-status__letter .spin')).toHaveLength(1)
    expect(wrapper.findComponent({ name: 'ElPopover' }).exists()).toBe(false)
    const result = ElMessage.success('BOM已保存')
    await flushPromises()
    expect(wrapper.find('.pdm-global-status__loader').exists()).toBe(false)
    expect(wrapper.get('.pdm-global-status').text()).toBe('成功BOM已保存')
    result.close()
    await flushPromises()
    expect(wrapper.get('.pdm-global-status').text()).toBe('')
    expect(wrapper.findAll('.pdm-global-status__letter')).toHaveLength(5)
  })

  it('sits immediately before the date and persists without a toast or timeout', async () => {
    vi.useFakeTimers()
    const toast = vi.spyOn(toastMessage, 'success')
    const wrapper = header()
    const message = 'BOM已保存；CAD来源物料的变更已进入SolidWorks待写回队列'
    ElMessage.success(message)
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.pdm-global-status').element.nextElementSibling?.classList.contains('pdm-header-clock')).toBe(true)
    expect(wrapper.get('[role="status"]').text()).toBe(message)
    expect(toast).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(wrapper.get('[role="status"]').text()).toBe(message)
    await wrapper.setProps({ notificationCount: 2 })
    expect(wrapper.get('[role="status"]').text()).toBe(message)
  })

  it.each(['success', 'warning', 'error', 'info'] as const)('shows %s with the correct color class and full text', async type => {
    const wrapper = header()
    const text = '完整操作结果；'.repeat(30)
    ElMessage[type](text)
    await flushPromises()
    const button = wrapper.get('.pdm-global-status')
    expect(button.classes()).toContain(`is-${type}`)
    expect(button.attributes('title')).toBe(text)
    await button.trigger('click')
    await flushPromises()
    expect(document.querySelector('.pdm-global-status-detail p')?.textContent).toBe(text)
  })

  it('replaces the latest status and does not let an old handle clear a newer result', async () => {
    const wrapper = header()
    const old = ElMessage.success('已保存')
    ElMessage.error('保存失败')
    old.close()
    await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toBe('保存失败')
  })

  it('clears status on account change and logout', async () => {
    const wrapper = header()
    ElMessage.success('当前用户的操作结果')
    await wrapper.setProps({ username: 'another-user' })
    expect(globalStatus.value).toBeUndefined()
    ElMessage.info('另一个用户的状态')
    await wrapper.setProps({ userName: '' })
    expect(globalStatus.value).toBeUndefined()
    expect(wrapper.find('.pdm-global-status').exists()).toBe(false)
  })

  it('retains the existing toast in login or standalone windows without a header', () => {
    const toast = vi.spyOn(toastMessage, 'error').mockReturnValue({ close: vi.fn() })
    ElMessage.error('登录失败')
    expect(toast).toHaveBeenCalledWith('登录失败')
    expect(globalStatus.value).toBeUndefined()
  })
})
