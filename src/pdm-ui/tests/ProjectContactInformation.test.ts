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
  vi.clearAllMocks()
  vi.mocked(readProjectContactInformation).mockResolvedValue(fixture())
  vi.mocked(saveProjectContactInformation).mockImplementation(async information => ({ ...fixture(), information: { ...information, rowVersion: information.rowVersion + 1 } }))
})
const create = () => mount(ProjectContactInformation, { props: { projectId: 'project', token: 'token' }, global: { plugins: [ElementPlus] } })

describe('客户地址用途', () => {
  it('用两个选项显示已有用途，保存时仍提交布尔值', async () => {
    const wrapper = create(); await flushPromises()
    const purpose = wrapper.get('select[aria-label="地址用途 1"]')
    expect(purpose.findAll('option').map(option => option.text())).toEqual(['办公地址', '收货地址'])
    expect((purpose.element as HTMLSelectElement).value).toBe('false')
    expect((wrapper.get('select[aria-label="地址用途 2"]').element as HTMLSelectElement).value).toBe('true')
    await purpose.setValue('true')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(saveProjectContactInformation).toHaveBeenLastCalledWith(expect.objectContaining({ rowVersion: 3, entries: expect.arrayContaining([expect.objectContaining({ contact: '王工', isShippingAddress: true })]) }), 'token')
    await purpose.setValue('false')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click'); await flushPromises()
    expect(vi.mocked(saveProjectContactInformation).mock.calls[1]![0].entries![0]!.isShippingAddress).toBe(false)
    wrapper.unmount()
  })
  it('选择收货地址后仍要求填写地址', async () => {
    const view = fixture(); view.information.entries![0]!.address = ''
    vi.mocked(readProjectContactInformation).mockResolvedValue(view)
    const wrapper = create(); await flushPromises()
    await wrapper.get('select[aria-label="地址用途 1"]').setValue('true')
    await wrapper.get('[aria-label="保存客户信息"]').trigger('click')
    expect(wrapper.get('[role="alert"]').text()).toContain('用途为收货地址的记录必须填写地址')
    expect(saveProjectContactInformation).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('只读账号显示完整用途文字且不能修改', async () => {
    vi.mocked(readProjectContactInformation).mockResolvedValue({ ...fixture(), canEdit: false })
    const wrapper = create(); await flushPromises()
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.findAll('tbody tr').map(row => row.findAll('td')[1]!.text())).toEqual(['办公地址', '收货地址'])
    wrapper.unmount()
  })
  it('旧格式联系人和收货地址仍按原标记回显', async () => {
    const view = fixture(); view.information.entries = undefined
    view.information.customerContact = '旧联系人'; view.information.shippingAddress = '旧收货地址'
    vi.mocked(readProjectContactInformation).mockResolvedValue(view)
    const wrapper = create(); await flushPromises()
    expect(wrapper.findAll('select').map(select => (select.element as HTMLSelectElement).value)).toEqual(['false', 'true'])
    wrapper.unmount()
  })
})
