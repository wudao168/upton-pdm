import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProjectPermissionSettings from '../src/components/ProjectPermissionSettings.vue'
import { getProjectPermissionSettings, updateProjectPermissionSettings } from '../src/api'
import type { ProjectPermissionSettings as Settings } from '../src/types'

vi.mock('../src/api', () => ({ getProjectPermissionSettings: vi.fn(), updateProjectPermissionSettings: vi.fn() }))
const settings: Settings = { rules: { MainManager: [], ChildManager: [], MainDesigner: [], Engineer: ['bom.electrical.edit'], Other: [] } }

describe('ProjectPermissionSettings', () => {
  afterEach(() => { document.body.innerHTML = ''; vi.clearAllMocks() })

  it('未授予预算查看权限显示未授权，并保存所选岗位而保留其他岗位', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue(settings)
    let finishSave!: (value: Settings) => void
    vi.mocked(updateProjectPermissionSettings).mockImplementation(() => new Promise(resolve => { finishSave = resolve }))
    const wrapper = mount(ProjectPermissionSettings, { attachTo: document.body, props: { token: 'token', canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('.el-table__row')[0]!.find('button').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('.pdm-permission-matrix')?.textContent).toContain('未授权')
    expect(document.body.querySelector('.pdm-permission-matrix')?.textContent).not.toContain('只读')
    const checkbox = document.body.querySelector<HTMLInputElement>('input[aria-label="查看项目预算"]')!
    checkbox.click()
    await flushPromises()
    const save = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.el-dialog button')).find(button => button.textContent === '保存并立即生效')!
    save.click(); save.click()
    await flushPromises()
    expect(updateProjectPermissionSettings).toHaveBeenCalledTimes(1)
    expect(updateProjectPermissionSettings).toHaveBeenCalledWith({ rules: { ...settings.rules, MainManager: ['project.budget.view'] } }, 'token')
    expect(checkbox.disabled).toBe(true)
    finishSave(settings)
    await flushPromises()
    wrapper.unmount()
  })

  it('会话切换后忽略旧请求，退出后清除已加载的权限', async () => {
    let finishOld!: (value: Settings) => void
    vi.mocked(getProjectPermissionSettings).mockImplementation(token => token === 'old' ? new Promise(resolve => { finishOld = resolve }) : Promise.resolve(settings))
    const wrapper = mount(ProjectPermissionSettings, { attachTo: document.body, props: { token: 'old', canEdit: true }, global: { plugins: [ElementPlus] } })
    await wrapper.setProps({ token: 'new', canEdit: false })
    await flushPromises()
    finishOld({ rules: { ...settings.rules, MainManager: ['project.budget.view'] } })
    await flushPromises()
    await wrapper.findAll('.el-table__row')[0]!.find('button').trigger('click')
    await flushPromises()
    const checkbox = document.body.querySelector<HTMLInputElement>('input[aria-label="查看项目预算"]')!
    expect(checkbox.checked).toBe(false)
    expect(checkbox.disabled).toBe(true)
    expect(document.body.textContent).toContain('当前账号仅可查看项目权限设置')
    await wrapper.setProps({ token: '' })
    await flushPromises()
    expect(wrapper.findAll('.el-table__row').every(row => row.find('button').attributes('disabled') !== undefined)).toBe(true)
    expect(wrapper.text()).toContain('未加载')
    wrapper.unmount()
  })
})
