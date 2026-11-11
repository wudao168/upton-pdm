import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProjectContactInformation from '../src/components/ProjectContactInformation.vue'
import { readProjectContactInformation, saveProjectContactInformation } from '../src/api'
import type { ProjectContactInformationView } from '../src/projectContactInformation'

vi.mock('../src/api', () => ({ readProjectContactInformation: vi.fn(), saveProjectContactInformation: vi.fn() }))
const fixture = (): ProjectContactInformationView => ({ customerName: '测试客户', canEdit: true, information: {
  projectId: 'project', customerContact: '', customerPhone: '', shippingAddress: '', shippingContact: '', shippingPhone: '', rowVersion: 3,
  entries: [{ contact: '王工', phone: '123', address: '办公楼', isShippingAddress: false }, { contact: '李工', phone: '456', address: '收货仓库', isShippingAddress: true }],
} })
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(readProjectContactInformation).mockResolvedValue(fixture())
  vi.mocked(saveProjectContactInformation).mockImplementation(async information => ({ ...fixture(), information: { ...information, rowVersion: information.rowVersion + 1 } }))
})
const create = () => mount(ProjectContactInformation, { props: { projectId: 'project', token: 'token' }, global: { plugins: [ElementPlus] } })

describe('客户档案与联系人编辑', () => {
  it('已有记录默认只读，点击编辑仅开放该行，保存后重新锁定', async () => {
    const wrapper = create(); await flushPromises()
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.get('tbody').text()).toContain('王工')
    await wrapper.get('[aria-label="编辑联系人 1"]').trigger('click')
    expect(wrapper.find('input[aria-label="联系人 2"]').exists()).toBe(false)
    const purpose = wrapper.get('select[aria-label="地址用途 1"]')
    expect(purpose.findAll('option').map(option => option.text())).toEqual(['办公地址', '收货地址'])
    await purpose.setValue('true')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(saveProjectContactInformation).toHaveBeenLastCalledWith(expect.objectContaining({ rowVersion: 3, entries: expect.arrayContaining([expect.objectContaining({ contact: '王工', isShippingAddress: true })]) }), 'token')
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.find('[aria-label="编辑联系人 1"]').exists()).toBe(true)
    await wrapper.get('[aria-label="编辑联系人 1"]').trigger('click')
    await wrapper.get('select[aria-label="地址用途 1"]').setValue('false')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(vi.mocked(saveProjectContactInformation).mock.calls[1]![0]).toMatchObject({ rowVersion: 4, entries: [expect.objectContaining({ isShippingAddress: false }), expect.objectContaining({ isShippingAddress: true })] })
    wrapper.unmount()
  })
  it('新增行直接编辑，保存成功后显示只读内容', async () => {
    const wrapper = create(); await flushPromises()
    await wrapper.get('[aria-label="新增"]').trigger('click')
    await wrapper.get('[aria-label="联系人 3"]').setValue('新增联系人')
    expect(wrapper.find('[aria-label="联系人 1"]').exists()).toBe(false)
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.get('tbody').text()).toContain('新增联系人')
    expect(wrapper.find('[aria-label="编辑联系人 3"]').exists()).toBe(true)
    wrapper.unmount()
  })
  it('选择收货地址后仍要求填写地址，校验失败保留编辑状态', async () => {
    const view = fixture(); view.information.entries![0]!.address = ''
    vi.mocked(readProjectContactInformation).mockResolvedValue(view)
    const wrapper = create(); await flushPromises()
    await wrapper.get('[aria-label="编辑联系人 1"]').trigger('click')
    await wrapper.get('select[aria-label="地址用途 1"]').setValue('true')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click')
    expect(wrapper.get('[role="alert"]').text()).toContain('用途为收货地址的记录必须填写地址')
    expect(wrapper.find('[aria-label="联系人 1"]').exists()).toBe(true)
    expect(saveProjectContactInformation).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('保存失败保留输入，重试成功后转为只读', async () => {
    vi.mocked(saveProjectContactInformation).mockRejectedValueOnce(new Error('信息已被修改，请重新读取后重试。'))
    const wrapper = create(); await flushPromises()
    await wrapper.get('[aria-label="编辑联系人 1"]').trigger('click')
    await wrapper.get('[aria-label="联系人 1"]').setValue('修改后的联系人')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('信息已被修改')
    expect((wrapper.get('[aria-label="联系人 1"]').element as HTMLInputElement).value).toBe('修改后的联系人')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.get('tbody').text()).toContain('修改后的联系人')
    wrapper.unmount()
  })
  it('删除前面的行后，后续编辑状态仍属于原记录', async () => {
    const wrapper = create(); await flushPromises()
    await wrapper.get('[aria-label="新增"]').trigger('click')
    await wrapper.get('[aria-label="联系人 3"]').setValue('第三位')
    await wrapper.get('[aria-label="删除联系人 1"]').trigger('click')
    expect(wrapper.find('[aria-label="联系人 1"]').exists()).toBe(false)
    expect((wrapper.get('[aria-label="联系人 2"]').element as HTMLInputElement).value).toBe('第三位')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(vi.mocked(saveProjectContactInformation).mock.calls[0]![0].entries?.map(entry => entry.contact)).toEqual(['李工', '第三位'])
    wrapper.unmount()
  })
  it('只读账号显示用途与档案，不能新增、编辑或删除', async () => {
    vi.mocked(readProjectContactInformation).mockResolvedValue({ ...fixture(), canEdit: false })
    const wrapper = create(); await flushPromises()
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.findAll('tbody button')).toHaveLength(0)
    expect(wrapper.find('[aria-label="新增"]').exists()).toBe(false)
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[1]!.text())).toEqual(['办公地址', '收货地址'])
    wrapper.unmount()
  })
  it('旧格式联系人和收货地址仍按原标记只读回显', async () => {
    const view = fixture(); view.information.entries = undefined
    view.information.customerContact = '旧联系人'; view.information.shippingAddress = '旧收货地址'
    vi.mocked(readProjectContactInformation).mockResolvedValue(view)
    const wrapper = create(); await flushPromises()
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[1]!.text())).toEqual(['办公地址', '收货地址'])
    expect(wrapper.get('tbody').text()).toContain('旧联系人')
    wrapper.unmount()
  })
  it('切换项目清除上一个项目的编辑状态', async () => {
    const wrapper = create(); await flushPromises()
    await wrapper.get('[aria-label="编辑联系人 1"]').trigger('click')
    await wrapper.get('[aria-label="联系人 1"]').setValue('未保存')
    await wrapper.setProps({ projectId: 'another-project' }); await flushPromises()
    expect(readProjectContactInformation).toHaveBeenLastCalledWith('another-project', 'token')
    expect(wrapper.findAll('input,select')).toHaveLength(0)
    expect(wrapper.get('tbody').text()).not.toContain('未保存')
    wrapper.unmount()
  })
  it('展示完整关联客户档案，保存联系人不改写客户档案', async () => {
    const wrapper = create(); await wrapper.setProps({ customerCode: 'C001', customer: { id: 'customer', code: 'C001', name: '完整客户名称', isActive: false, sourceSystem: 'U9C', lastSyncedAt: '2026-11-08T10:30:00+08:00' } }); await flushPromises()
    const details = wrapper.get('[aria-label="客户档案信息"]')
    expect(details.findAll('dt').map(field => field.text())).toEqual(['客户名称', '客户编码', '客户状态', '客户来源', '最近同步'])
    expect(details.text()).toContain('完整客户名称'); expect(details.text()).toContain('C001'); expect(details.text()).toContain('已停用'); expect(details.text()).toContain('U9C')
    expect(details.findAll('input')).toHaveLength(0)
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(vi.mocked(saveProjectContactInformation).mock.calls[0]![0]).not.toHaveProperty('customerCode')
    expect(details.text()).toContain('C001')
    wrapper.unmount()
  })
  it('档案目录未提供时仍显示项目客户名称编码，空联系人保持空状态', async () => {
    vi.mocked(readProjectContactInformation).mockResolvedValue({ ...fixture(), information: { ...fixture().information, entries: [] } })
    const wrapper = create(); await wrapper.setProps({ customerCode: 'C-OLD' }); await flushPromises()
    expect(wrapper.get('[aria-label="客户档案信息"]').text()).toContain('C-OLD')
    expect(wrapper.get('[aria-label="客户档案信息"]').text()).toContain('测试客户')
    expect(wrapper.get('tbody').text()).toContain('暂无联系人及收货地址')
    wrapper.unmount()
  })
})
