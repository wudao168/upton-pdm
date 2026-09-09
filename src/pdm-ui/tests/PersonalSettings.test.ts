import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import AppHeader from '../src/components/AppHeader.vue'
import LoginView from '../src/components/LoginView.vue'

afterEach(() => {
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

describe('CRM-aligned personal settings', () => {
  it.each([
    ['Engineer', '机械工程师'],
    ['ElectricalEngineer', '电气工程师'],
    ['CommissioningEngineer', '调试工程师'],
    ['HardwareEngineer', '硬件工程师'],
    ['MechanicalManager', '机械经理'],
    ['TechnicalAssistant', '技术助理'],
    ['BusinessUnitManager', '事业部经理'],
    ['ProcessReviewer', '标准化工程师'],
    ['ProjectManager', '项目经理'],
    ['SupplyChain', '供应链'],
    ['ProcurementSpecialist', '采购专员'],
    ['ProcurementManager', '采购经理'],
    ['ProductionManager', '生产经理'],
    ['ProductionAssistant', '生产助理'],
    ['MachiningSupervisor', '机加主管'],
    ['MachiningOperator', '机加人员'],
    ['AssemblySupervisor', '装配主管'],
    ['AssemblyFitter', '装配钳工'],
    ['ElectricalSupervisor', '电工主管'],
    ['AssemblyElectrician', '装配电工'],
    ['PlanningManager', '计划管理'],
    ['ProductionViewer', '生产物料员'],
    ['Approver', '标准化主管'],
    ['Administrator', '系统管理员'],
    ['platform_admin', '平台管理员'],
    ['developer', '开发者'],
  ])('shows role %s as the Chinese name %s inside personal settings only', async (role, expectedName) => {
    const wrapper = mount(AppHeader, {
      attachTo: document.body,
      props: { online: true, userName: '测试用户', role },
      global: { plugins: [ElementPlus] },
    })

    expect(wrapper.find('.pdm-user-role').exists()).toBe(false)
    expect(wrapper.get('.pdm-user-profile-trigger').text()).toBe('测试用户')
    await wrapper.get('.pdm-user-profile-trigger').trigger('click')
    await flushPromises()
    const roleField = Array.from(document.body.querySelectorAll('.el-form-item')).find(field => field.querySelector('label')?.textContent === '角色')
    expect(roleField?.querySelector('input')?.value).toBe(expectedName)
    expect(roleField?.querySelector('input')?.disabled).toBe(true)
    wrapper.unmount()
  })

  it('shows the same profile and password functions when clicking the user name', async () => {
    const wrapper = mount(AppHeader, {
      attachTo: document.body,
      global: { plugins: [ElementPlus] },
      props: {
        online: true,
        userName: '系统管理员',
        username: 'admin',
        role: 'Administrator',
        profile: { username: 'admin', displayName: '系统管理员', gender: 'unspecified' },
        onSaveProfile: vi.fn(),
        onChangePassword: vi.fn(),
      },
    })

    await wrapper.get('.pdm-user-profile-trigger').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('个人设置')
    expect(document.body.textContent).toContain('个人资料')
    expect(document.body.textContent).toContain('修改密码')
    expect(document.body.textContent).toContain('昵称')
    expect(document.body.textContent).toContain('移动电话')
    wrapper.unmount()
  })

  it('opens the CRM-style account and name reset request', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('true', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetchMock)
    const wrapper = mount(LoginView, {
      attachTo: document.body,
      global: { plugins: [ElementPlus] },
      props: { pending: false, error: '', online: true },
    })

    await wrapper.get('.pdm-login-forgot').trigger('click')
    await flushPromises()
    const inputs = Array.from(document.body.querySelectorAll<HTMLInputElement>('.el-dialog input'))
    expect(document.body.textContent).toContain('申请重置密码')
    expect(inputs).toHaveLength(2)
    inputs[0].value = 'admin'
    inputs[0].dispatchEvent(new Event('input'))
    inputs[1].value = '系统管理员'
    inputs[1].dispatchEvent(new Event('input'))
    const send = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.el-dialog button')).find(button => button.textContent?.includes('发送申请'))
    send?.click()
    await flushPromises()

    const [url, request] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/api/auth/password-reset-request')
    expect(JSON.parse(String(request.body))).toEqual({ username: 'admin', displayName: '系统管理员' })
    wrapper.unmount()
  })
})
