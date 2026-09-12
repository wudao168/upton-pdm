import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, expect, it, vi } from 'vitest'
import BomHeaderMaterialDirectory from '../src/components/BomHeaderMaterialDirectory.vue'

const api = vi.hoisted(() => ({ listBomHeaderMaterialDirectory: vi.fn() }))
vi.mock('../src/api', () => api)
beforeEach(() => {
  vi.clearAllMocks()
  api.listBomHeaderMaterialDirectory.mockResolvedValue([
    { materialId: 'legacy', projectId: 'p1', projectCode: 'P700004', subprojectCode: 'P700004-1', projectName: '项目1', kind: 'Standard', materialCode: 'PDM-PENDING-legacy', materialName: '标准件BOM', automaticStatus: 'NotRequested', automaticMessage: '历史表头尚未进入自动流程', isArchived: false },
    { materialId: 'done', projectId: 'p2', projectCode: 'P700005', subprojectCode: '—', projectName: '项目2', kind: 'Master', materialCode: '03020000009', materialName: '项目主BOM', automaticStatus: 'Completed', automaticMessage: 'U9C正式料号已回查确认', isArchived: false },
    { materialId: 'failed', projectId: 'p3', projectCode: 'P700006', subprojectCode: '—', projectName: '项目3', kind: 'Electrical', materialCode: '01000003', materialName: '电气BOM', automaticStatus: 'Failed', automaticMessage: '自动处理失败：缺少分类映射', isArchived: false },
  ])
})
it('separates header states, keeps refresh read-only, filters and navigates to the linked project', async () => {
  const wrapper = mount(BomHeaderMaterialDirectory, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
  await flushPromises()
  expect(wrapper.text()).toContain('待发布／核对')
  expect(wrapper.text()).toContain('已完成')
  expect(wrapper.text()).toContain('自动处理失败：缺少分类映射')
  const columns = wrapper.findAllComponents({ name: 'ElTableColumn' })
  expect(columns.filter((_, index) => [0, 1, 2, 3, 6, 8].includes(index)).map(column => column.props('width'))).toEqual(['100', '100', '150', '100', '100', '150'])
  expect(columns.filter((_, index) => [4, 5, 7].includes(index)).map(column => column.props('minWidth'))).toEqual(['1', '1', '1'])
  expect(columns.slice(0, 8).every(column => column.props('showOverflowTooltip'))).toBe(true)
  const buttons = wrapper.findAll('button').map(b => b.text())
  expect(buttons).not.toContain('批准')
  expect(buttons).not.toContain('删除')
  expect(buttons).not.toContain('重试')
  const navigate = vi.fn()
  window.addEventListener('pdm-open-project', navigate)
  await wrapper.findAll('button').find(b => b.text() === '查看对应BOM')!.trigger('click')
  expect(navigate.mock.calls[0][0].detail).toEqual({ projectId: 'p1', tab: 'bom' })
  window.removeEventListener('pdm-open-project', navigate)
  await wrapper.find('input').setValue('03020000009')
  expect(wrapper.findAll('tbody tr')).toHaveLength(1)
  await wrapper.findAll('button').find(b => b.text() === '刷新')!.trigger('click')
  await flushPromises()
  expect(api.listBomHeaderMaterialDirectory).toHaveBeenCalledTimes(2)
  wrapper.unmount()
})
it('keeps load errors visible inside the header directory', async () => {
  api.listBomHeaderMaterialDirectory.mockRejectedValue(new Error('目录读取失败，请刷新重试'))
  const wrapper = mount(BomHeaderMaterialDirectory, { props: { token: 'token' }, global: { plugins: [ElementPlus] } })
  await flushPromises()
  expect(wrapper.find('.el-alert').text()).toContain('目录读取失败，请刷新重试')
  wrapper.unmount()
})
