import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ElementPlus, { ElMessageBox } from 'element-plus'
import PermissionCenter from '../src/components/PermissionCenter.vue'
import * as api from '../src/authorization'
vi.mock('../src/authorization', () => ({ getAuthorization: vi.fn(), saveAuthorization: vi.fn(), previewAuthorization: vi.fn() }))
const fixture = (): api.AuthorizationDirectory => ({
  settings: { revision: 5, migratedAt: '', positions: [], assignments: [], templates: [{ key: 'reader', name: '项目查阅', description: '', roleCode: null, isActive: true, projectAssignable: true, grants: [{ permission: 'project.view', scopes: ['company'] }] }] },
  permissions: [{ code: 'project.view', name: '查看项目', module: '项目', sensitive: false, scope: 'project', dependencies: [] }, { code: 'project.edit', name: '编辑项目', module: '项目', sensitive: true, scope: 'project', dependencies: ['project.view'] }],
  scopes: { company: '授权范围内全部' }, users: [{ username: 'worker', displayName: '测试工程师', companyId: 'company', role: 'custom', isActive: true }], projects: [], departments: [], canEdit: true,
})
describe('统一权限中心', () => {
  beforeEach(() => { vi.clearAllMocks(); vi.mocked(api.getAuthorization).mockResolvedValue(fixture()) })
  it('撤销前置查看会同步撤销依赖操作，确认后保存', async () => {
    const wrapper = mount(PermissionCenter, { props: { token: 'token', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockImplementation(async () => 'confirm' as never)
    vi.mocked(api.saveAuthorization).mockImplementation(async s => ({ ...s, revision: 6 }))
    try {
      await flushPromises()
      await wrapper.get('[aria-label="授权编辑项目"]').setValue(true)
      expect(wrapper.find('details').text()).toContain('编辑项目')
      await wrapper.get('[aria-label="授权查看项目"]').setValue(false)
      expect((wrapper.get('[aria-label="授权编辑项目"]').element as HTMLInputElement).checked).toBe(false)
      await wrapper.findAll('button').find(b => b.text() === '保存生效')!.trigger('click'); await flushPromises()
      expect(api.saveAuthorization).toHaveBeenCalledWith(expect.objectContaining({ revision: 5, templates: [expect.objectContaining({ grants: [] })] }), 'token')
      expect(wrapper.text()).toContain('配置版本 6')
    } finally { wrapper.unmount(); confirm.mockRestore() }
  })
  it('只读账号不能编辑和保存，检查结果显示来源', async () => {
    vi.mocked(api.getAuthorization).mockResolvedValue({ ...fixture(), canEdit: false })
    vi.mocked(api.previewAuthorization).mockResolvedValue([{ permission: 'project.view', allowed: true, reason: '指定项目授权', sources: ['项目查阅'] }])
    const wrapper = mount(PermissionCenter, { props: { token: 'readonly', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises(); expect(wrapper.get('[aria-label="授权查看项目"]').attributes('disabled')).toBeDefined()
      expect(wrapper.findAll('button').some(b => b.text() === '保存生效')).toBe(false)
      await wrapper.findAll('button').find(b => b.text() === '权限检查')!.trigger('click')
      await wrapper.get('[aria-label="人员列表"] button').trigger('click'); await flushPromises()
      expect(api.previewAuthorization).toHaveBeenCalledWith('worker', '', 'readonly')
      expect(wrapper.text()).toContain('指定项目授权'); expect(wrapper.text()).toContain('项目查阅')
      expect(api.saveAuthorization).not.toHaveBeenCalled()
    } finally { wrapper.unmount() }
  })

  it('切换检查项目保留人员，较早的请求不会覆盖当前结果', async () => {
    const directory = fixture()
    directory.projects = [{ id: 'project-a', code: 'P260841', name: '生产线改造项目', organizationId: 'company' }]
    vi.mocked(api.getAuthorization).mockResolvedValue(directory)
    let resolveCompany!: (rows: api.AuthorizationDecision[]) => void
    vi.mocked(api.previewAuthorization)
      .mockImplementationOnce(() => new Promise(resolve => { resolveCompany = resolve }))
      .mockResolvedValueOnce([{ permission: 'project.view', allowed: true, reason: '命中指定项目', sources: ['项目查阅'] }])
    const wrapper = mount(PermissionCenter, { props: { token: 'token', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      await wrapper.findAll('button').find(b => b.text() === '权限检查')!.trigger('click')
      await wrapper.get('[aria-label="人员列表"] button').trigger('click')
      await wrapper.findAll('[aria-label="检查范围"] button').find(b => b.text().includes('P260841'))!.trigger('click')
      await flushPromises()
      resolveCompany([{ permission: 'project.view', allowed: false, reason: '旧公司范围结果', sources: [] }])
      await flushPromises()
      expect(api.previewAuthorization).toHaveBeenLastCalledWith('worker', 'project-a', 'token')
      expect(wrapper.get('[aria-label="检查结果"]').text()).toContain('命中指定项目')
      expect(wrapper.text()).not.toContain('旧公司范围结果')
    } finally { wrapper.unmount() }
  })
})
