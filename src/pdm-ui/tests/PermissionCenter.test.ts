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

  it('保存作废图档时只添加权限中心可以配置的前置权限', async () => {
    const directory = fixture()
    directory.permissions.push(
      { code: 'document.view', name: '查看图档', module: '项目图档', sensitive: false, scope: 'project', dependencies: ['project.view'] },
      { code: 'document.obsolete', name: '作废图档', module: '项目图档', sensitive: true, scope: 'project', dependencies: ['document.view', 'release.manage'] },
    )
    vi.mocked(api.getAuthorization).mockResolvedValue(directory)
    vi.mocked(api.saveAuthorization).mockImplementation(async settings => ({ ...settings, revision: 6 }))
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockImplementation(async () => 'confirm' as never)
    const wrapper = mount(PermissionCenter, { props: { token: 'token', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      const navigation = wrapper.get('[aria-label="权限分类"]')
      await navigation.findAll('button').find(button => button.text().startsWith('项目中心'))!.trigger('click')
      await navigation.findAll('button').find(button => button.text().startsWith('图档'))!.trigger('click')
      await wrapper.get('[aria-label="授权作废图档"]').setValue(true)
      expect((wrapper.get('[aria-label="授权查看图档"]').element as HTMLInputElement).checked).toBe(true)
      await wrapper.findAll('button').find(button => button.text() === '保存生效')!.trigger('click')
      await flushPromises()
      const [saved] = vi.mocked(api.saveAuthorization).mock.calls[0]!
      expect(saved.templates[0]!.grants.map(grant => grant.permission)).toEqual(['project.view', 'document.obsolete', 'document.view'])
      expect(wrapper.text()).toContain('配置版本 6')
    } finally { wrapper.unmount(); confirm.mockRestore() }
  })

  it('按侧栏顺序及子页分类，跨分类搜索保留待保存授权', async () => {
    const directory = fixture()
    directory.permissions.push(
      { code: 'stock.view', name: '查看常备物料', module: '常备物料', sensitive: false },
      { code: 'project.expense.view', name: '查看费用申请', module: '费用申请', sensitive: true, scope: 'project' },
      { code: 'project.budget.view', name: '查看项目预算', module: '项目预算', sensitive: true, scope: 'project' },
      { code: 'future.view', name: '查看新功能', module: '新功能', sensitive: false },
    )
    vi.mocked(api.getAuthorization).mockResolvedValue(directory)
    const wrapper = mount(PermissionCenter, { props: { token: 'token', companyId: 'company' }, global: { plugins: [ElementPlus] } })
    try {
      await flushPromises()
      const navigation = wrapper.get('[aria-label="权限分类"]')
      const category = (label: string) => navigation.findAll('button').find(button => button.get('span').text() === label)!
      expect(navigation.findAll('.authorization-category-entry > span').map(label => label.text())).toEqual(['项目总览', '项目中心', '标准物料', '其他'])
      await category('项目中心').trigger('click')
      await category('预算').trigger('click')
      expect(wrapper.get('[aria-label="权限设置"] h3').text()).toContain('项目中心 → 预算')
      expect(wrapper.find('[aria-label="授权查看常备物料"]').exists()).toBe(false)
      await wrapper.get('[aria-label="授权查看费用申请"]').setValue(true)
      expect(category('预算').text()).toContain('1/2')
      const search = wrapper.get('[aria-label="搜索权限"]')
      await search.setValue('标准物料 → 常备件')
      expect(wrapper.find('[aria-label="授权查看常备物料"]').exists()).toBe(true)
      await search.setValue('费用申请')
      expect((wrapper.get('[aria-label="授权查看费用申请"]').element as HTMLInputElement).checked).toBe(true)
      await search.setValue('')
      expect(category('预算').attributes('aria-pressed')).toBe('true')
      await category('其他').trigger('click')
      expect(wrapper.find('[aria-label="授权查看新功能"]').exists()).toBe(true)
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
