import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessage as toastMessage } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import AppHeader from '../src/components/AppHeader.vue'
import { resetGlobalStatusContentCacheForTests } from '../src/globalStatusContent'
import { clearGlobalStatus, ElMessage, globalStatus } from '../src/statusMessage'

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
beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('offline test')))
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.useRealTimers()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  resetGlobalStatusContentCacheForTests()
  document.body.innerHTML = ''
})

describe('global operation status', () => {
  it('clears old page status and restores the idle animation without suppressing new messages', async () => {
    const wrapper = header()
    const old = ElMessage.success('旧页面的状态')
    clearGlobalStatus()
    await flushPromises()
    expect(wrapper.find('.pdm-global-status.is-idle').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('旧页面的状态')
    ElMessage.success('新页面的状态')
    old.close()
    await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toBe('新页面的状态')
  })
  it('shows sourced local content instead of the UPTON animation while idle', async () => {
    vi.useFakeTimers()
    const interval = vi.spyOn(globalThis, 'setInterval')
    const wrapper = header()
    expect(wrapper.get('.pdm-global-status').text()).toContain('确认需求，让执行更准确。')
    expect(wrapper.get('.pdm-global-status').text()).toContain('系统原创')
    expect(wrapper.get('.pdm-global-status').element.tagName).toBe('BUTTON')
    expect(wrapper.find('.pdm-global-status__letter').exists()).toBe(false)
    expect(wrapper.findComponent({ name: 'ElPopover' }).exists()).toBe(true)
    const result = ElMessage.success('BOM已保存')
    await flushPromises()
    expect(wrapper.get('.pdm-global-status').text()).toBe('成功BOM已保存')
    result.close()
    await flushPromises()
    expect(wrapper.get('.pdm-global-status').text()).toContain('确认需求，让执行更准确。')
    expect(interval).toHaveBeenCalledWith(expect.any(Function), 600_000)
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
