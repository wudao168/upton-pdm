import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProjectPermissionSettings from '../src/components/ProjectPermissionSettings.vue'
import { getProjectPermissionSettings, updateProjectPermissionSettings } from '../src/api'
import type { ProjectPermissionSettings as Settings } from '../src/types'
import { permissionDirectory } from './projectPermissionFixture'

vi.mock('../src/api', () => ({ getProjectPermissionSettings: vi.fn(), updateProjectPermissionSettings: vi.fn() }))
const settings: Settings = { ...permissionDirectory, rules: { MainManager: [], ChildManager: [], MainDesigner: [], Engineer: ['bom.electrical.edit'], Other: [] } }

describe('ProjectPermissionSettings', () => {
  afterEach(() => { document.body.innerHTML = ''; vi.clearAllMocks() })

  it('后端目录新增模块后自动显示，并可给新岗位授权而不改原规则', async () => {
    const future = { code: 'project.change.create', name: '创建项目变更', module: '项目变更', sensitive: false, scope: 'project' as const, projectAuthorization: 'position' as const, action: 'create' as const }
    vi.mocked(getProjectPermissionSettings).mockResolvedValue({ ...settings, permissions: [...settings.permissions!, future] })
    vi.mocked(updateProjectPermissionSettings).mockImplementation(async value => ({ ...value, positions: [...settings.positions!, ...value.customPositions!], permissions: [...settings.permissions!, future] }))
    const wrapper = mount(ProjectPermissionSettings, { props: { token: 'token', canEdit: true } })
    await flushPromises()
    await wrapper.get('select[aria-label="功能模块"]').setValue('项目变更')
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(5)
    await wrapper.findAll('button').find(button => button.text() === '新增岗位')!.trigger('click')
    const name = wrapper.get('input[aria-label$="岗位名称"]')
    await name.setValue('变更复核')
    await wrapper.get('select[aria-label="变更复核岗位范围"]').setValue('subtree')
    await wrapper.get('input[aria-label^="变更复核：创建项目变更"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text() === '保存并立即生效')!.trigger('click')
    await flushPromises()
    const payload = vi.mocked(updateProjectPermissionSettings).mock.calls[0]![0]
    const position = payload.customPositions![0]!
    expect(position.key).toMatch(/^custom-[a-f0-9]{32}$/)
    expect(position.scope).toBe('subtree')
    expect(payload.rules[position.key]).toEqual([future.code])
    for (const [key, rules] of Object.entries(settings.rules)) expect(payload.rules[key]).toEqual(rules)
    wrapper.unmount()
  })

  it('目录标为角色控制的功能只展示控制来源，不产生岗位授权框', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue({ ...settings, permissions: [{ code: 'project.create', name: '创建主项目', module: '项目管理', sensitive: false, projectAuthorization: 'role' }] })
    const wrapper = mount(ProjectPermissionSettings, { props: { token: 'token', canEdit: true } })
    await flushPromises()
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(0)
    expect(wrapper.text()).toContain('由角色权限及业务规则控制')
    wrapper.unmount()
  })

  it('矩阵完整显示原配置，跨岗位保存修改并保留预算和其他岗位', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue(settings)
    let finishSave!: (value: Settings) => void
    vi.mocked(updateProjectPermissionSettings).mockImplementation(() => new Promise(resolve => { finishSave = resolve }))
    const wrapper = mount(ProjectPermissionSettings, { attachTo: document.body, props: { token: 'token', canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(55)
    expect(wrapper.findAll('input:checked')).toHaveLength(1)
    expect(wrapper.get('input[aria-label^="执行工程师／电气硬件：电气BOM"]').element).toHaveProperty('checked', true)
    expect(wrapper.text()).toContain('岗位未勾选不表示该人员一定无权查看')
    expect(updateProjectPermissionSettings).not.toHaveBeenCalled()
    const checkbox = wrapper.get('input[aria-label^="主项目经理／协同项目经理：编辑项目信息"]')
    await checkbox.setValue(true)
    await wrapper.get('input[aria-label^="主设：维护质量计划"]').setValue(true)
    await flushPromises()
    expect(wrapper.text()).toContain('有 2 项权限或岗位定义修改未保存')
    const save = wrapper.findAll('button').find(button => button.text() === '保存并立即生效')!
    await save.trigger('click'); await save.trigger('click')
    await flushPromises()
    expect(updateProjectPermissionSettings).toHaveBeenCalledTimes(1)
    const saved = { rules: { ...settings.rules, MainManager: ['project.edit'], MainDesigner: ['validation-plan.edit'] } }
    expect(updateProjectPermissionSettings).toHaveBeenCalledWith({ ...saved, customPositions: [], schemaVersion: 2 }, 'token')
    expect(checkbox.attributes('disabled')).toBeDefined()
    finishSave(saved)
    await flushPromises()
    expect(wrapper.text()).not.toContain('修改未保存')
    expect(settings.rules.MainManager).toEqual([])
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
    const checkbox = wrapper.get('input[aria-label^="主项目经理／协同项目经理：查看项目预算"]').element as HTMLInputElement
    expect(checkbox.checked).toBe(false)
    expect(checkbox.disabled).toBe(true)
    expect(wrapper.text()).toContain('当前账号仅可查看项目岗位授权规则')
    await wrapper.setProps({ token: '' })
    await flushPromises()
    expect(wrapper.findAll('input').every(input => input.attributes('disabled') !== undefined)).toBe(true)
    expect(wrapper.text()).toContain('未加载')
    wrapper.unmount()
  })

  it('撤销只恢复未保存草稿，不修改原始规则或发起保存', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue(settings)
    const wrapper = mount(ProjectPermissionSettings, { props: { token: 'token', canEdit: true } })
    await flushPromises()
    await wrapper.get('input[aria-label^="其他人员：编辑项目信息"]').setValue(true)
    await wrapper.get('input[aria-label^="执行工程师／电气硬件：电气BOM"]').setValue(false)
    await wrapper.findAll('button').find(button => button.text() === '撤销未保存修改')!.trigger('click')
    expect(wrapper.get('input[aria-label^="其他人员：编辑项目信息"]').element).toHaveProperty('checked', false)
    expect(wrapper.get('input[aria-label^="执行工程师／电气硬件：电气BOM"]').element).toHaveProperty('checked', true)
    expect(settings.rules.Engineer).toEqual(['bom.electrical.edit'])
    expect(updateProjectPermissionSettings).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('保存失败时保留草稿，重试仍提交完整规则', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue(settings)
    vi.mocked(updateProjectPermissionSettings).mockRejectedValueOnce(new Error('保存失败，请稍后重试。')).mockResolvedValueOnce(settings)
    const wrapper = mount(ProjectPermissionSettings, { props: { token: 'token', canEdit: true } })
    await flushPromises()
    const checkbox = wrapper.get('input[aria-label^="子项目负责人：维护项目计划"]')
    await checkbox.setValue(true)
    const save = wrapper.findAll('button').find(button => button.text() === '保存并立即生效')!
    await save.trigger('click')
    await flushPromises()
    expect(checkbox.element).toHaveProperty('checked', true)
    expect(save.attributes('disabled')).toBeUndefined()
    await save.trigger('click')
    await flushPromises()
    expect(updateProjectPermissionSettings).toHaveBeenNthCalledWith(2, { rules: { ...settings.rules, ChildManager: ['project.plan.edit'] }, customPositions: [], schemaVersion: 2 }, 'token')
    wrapper.unmount()
  })

  it('会话切换后忽略旧保存响应，不覆盖新会话规则', async () => {
    vi.mocked(getProjectPermissionSettings).mockResolvedValue(settings)
    let finishSave!: (value: Settings) => void
    vi.mocked(updateProjectPermissionSettings).mockImplementation(() => new Promise(resolve => { finishSave = resolve }))
    const wrapper = mount(ProjectPermissionSettings, { props: { token: 'old', canEdit: true } })
    await flushPromises()
    await wrapper.get('input[aria-label^="主设：编辑项目信息"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text() === '保存并立即生效')!.trigger('click')
    await wrapper.setProps({ token: 'new', canEdit: false })
    await flushPromises()
    finishSave({ rules: { ...settings.rules, MainDesigner: ['project.edit'] } })
    await flushPromises()
    expect(wrapper.get('input[aria-label^="主设：编辑项目信息"]').element).toHaveProperty('checked', false)
    expect(wrapper.findAll('input').every(input => input.attributes('disabled') !== undefined)).toBe(true)
    wrapper.unmount()
  })
})
