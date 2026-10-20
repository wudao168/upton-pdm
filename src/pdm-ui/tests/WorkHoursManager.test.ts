import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElSelect } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import WorkHoursManager from '../src/components/WorkHoursManager.vue'
import * as api from '../src/api'
import * as calendar from '../src/projectCalendar'
import * as workbook from '../src/procurementWorkbook'
import type { WorkHoursView } from '../src/workHours'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))
vi.mock('../src/globalStatusContent', () => ({ loadGlobalStatusContent: async () => ({ holidayCalendars: [] }) }))
const view = (): WorkHoursView => ({ today: '2026-10-20', startDate: '2026-10-12', username: 'a', displayName: '员工甲', scope: '仅本人',
  people: [{ username: 'a', displayName: '员工甲', department: '设计部', isActive: true }],
  projects: [{ id: 'root', code: 'P700012', name: '设备', rootProjectId: 'root', rootProjectCode: 'P700012', rootProjectName: '设备' }, { id: 'child', code: 'P700012-1', name: '上料单元', rootProjectId: 'root', rootProjectCode: 'P700012', rootProjectName: '设备' }],
  sheets: [{ companyId: 'c', username: 'a', month: '2026-09', rowVersion: 0, entries: [], history: [] }, { companyId: 'c', username: 'a', month: '2026-10', rowVersion: 2, history: [], entries: [{ id: 'e', date: '2026-10-19', projectId: 'child', projectCode: 'P700012-1', projectName: '上料单元', rootProjectId: 'root', rootProjectCode: 'P700012', rootProjectName: '设备', location: '厂内', hours: 8 }] }, { companyId: 'c', username: 'a', month: '2026-11', rowVersion: 0, entries: [], history: [] }] })
// 真实下拉搜索及布局由内网 Playwright 覆盖；jsdom 使用原生控件，避免 Element Plus 的布局观察循环。
const selectStub = defineComponent({ name: 'ElSelect', props: ['modelValue', 'disabled'], emits: ['update:modelValue'], template: '<select :value="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\',$event.target.value)"><slot /></select>' })
const optionStub = defineComponent({ name: 'ElOption', props: ['value','label'], template: '<option :value="value">{{ label }}</option>' })
const mountManager = () => mount(WorkHoursManager, { props: { token: 'token', companyId: 'c', active: true }, global: { plugins: [ElementPlus], stubs: { teleport: true, ElSelect: selectStub, ElOption: optionStub } } })
type Wrapper = ReturnType<typeof mountManager>
const button = (wrapper: Wrapper, text: string) => wrapper.findAll('button').find(x => x.text() === text)!
beforeEach(() => { vi.spyOn(calendar,'chinaToday').mockReturnValue('2026-10-20'); vi.spyOn(api,'readWorkHours').mockImplementation(async () => view()); vi.spyOn(api,'saveWorkHours').mockImplementation(async () => view()) })
afterEach(() => vi.restoreAllMocks())

describe('工时填报与汇总', () => {
  it('日历逐条显示项目号、名称及小时数，同项目不同所在地分别保留且不再显示已填', async () => {
    const data = view()
    const sheet = data.sheets.find(x => x.month === '2026-10')!
    sheet.entries = [
      { ...sheet.entries[0]!, id: 'inside', hours: 3.5 },
      { ...sheet.entries[0]!, id: 'outside', location: '厂外', hours: 4.5 },
    ]
    vi.mocked(api.readWorkHours).mockResolvedValue(data)
    const wrapper = mountManager(); await flushPromises()
    const day = wrapper.get('button[aria-label="2026-10-19 8小时"]')
    expect(day.findAll('.wh-day-project-code').map(row => row.text())).toEqual(['P700012-1','P700012-1'])
    expect(day.findAll('.wh-day-project-name').map(row => row.text())).toEqual(['上料单元','上料单元'])
    expect(day.findAll('.wh-day-record-hours').map(row => row.text())).toEqual(['3.5小时','4.5小时'])
    expect(day.find('.wh-day-status').exists()).toBe(false)
    expect(day.text()).not.toContain('条记录')
    expect(day.get('b').text()).toBe('8 小时')
    expect(day.findAll('.wh-day-record')[1]!.attributes('title')).toContain('厂外')
    await day.trigger('click'); await flushPromises()
    expect(wrapper.findAll('.wh-entry')).toHaveLength(2)
    wrapper.unmount()
  })
  it('默认月历，项目名称自动带出，保存半小时工时并重新读取', async () => {
    const wrapper = mountManager(); await flushPromises()
    expect(wrapper.find('.wh-calendar').exists()).toBe(true)
    expect(wrapper.text()).toContain('员工甲')
    wrapper.findComponent(ElSelect).vm.$emit('update:modelValue','child'); await flushPromises()
    expect((wrapper.get('input[aria-label="自动带出项目名称"]').element as HTMLInputElement).value).toBe('上料单元')
    await wrapper.get('input[aria-label="工时数"]').setValue('4.5')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledWith({ from:'2026-10-20',to:'2026-10-20',entries:[{ date:'2026-10-20',projectId:'child',location:'厂内',hours:4.5 }],versions:[{month:'2026-10',rowVersion:2}] }, 'token')
    expect(api.readWorkHours).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })
  it('周表按日期和所在地提交，删除行只在保存后生效', async () => {
    const wrapper = mountManager(); await flushPromises(); await button(wrapper,'周表').trigger('click'); await flushPromises()
    expect(wrapper.get('.wh-week-table').text()).toContain('每日合计')
    await wrapper.get('input[aria-label="2026-10-20工时"]').setValue('2.5')
    await button(wrapper,'保存本周').trigger('click'); await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledWith(expect.objectContaining({from:'2026-10-19',to:'2026-10-20',entries:expect.arrayContaining([{date:'2026-10-20',projectId:'child',location:'厂内',hours:2.5}])}), 'token')
    await wrapper.get('button[aria-label="移除项目行"]').trigger('click')
    expect(api.saveWorkHours).toHaveBeenCalledTimes(1)
    await button(wrapper,'保存本周').trigger('click'); await flushPromises()
    expect(api.saveWorkHours).toHaveBeenLastCalledWith(expect.objectContaining({ entries: [] }), 'token')
    wrapper.unmount()
  })
  it('未保存或保存失败时保留填写内容，避免切日期丢失', async () => {
    vi.mocked(api.saveWorkHours).mockRejectedValue(new Error('服务器暂时不可用，请重试。'))
    const wrapper = mountManager(); await flushPromises()
    wrapper.findComponent(ElSelect).vm.$emit('update:modelValue','child'); await flushPromises()
    await wrapper.get('input[aria-label="工时数"]').setValue('6.5')
    await wrapper.get('button[aria-label="2026-10-19 8小时"]').trigger('click')
    expect((wrapper.get('input[aria-label="填报日期"]').element as HTMLInputElement).value).toBe('2026-10-20')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect((wrapper.get('input[aria-label="工时数"]').element as HTMLInputElement).value).toBe('6.5')
    await button(wrapper,'汇总工时').trigger('click'); expect(wrapper.find('.wh-calendar').exists()).toBe(true)
    wrapper.unmount()
  })
  it('汇总可切换人员、项目和明细，按当前筛选导出，不重复累计子项目', async () => {
    const download = vi.spyOn(workbook,'downloadProcurementWorkbook').mockImplementation(() => {})
    const wrapper = mountManager(); await flushPromises(); await button(wrapper,'汇总工时').trigger('click'); await flushPromises()
    expect(wrapper.get('.wh-report-summary').text()).toContain('8 小时')
    await button(wrapper,'按项目').trigger('click'); await flushPromises()
    expect(wrapper.get('.wh-report-table tbody').text()).toContain('P700012')
    expect(wrapper.get('.wh-report-table tbody').text()).toContain('8小时')
    await button(wrapper,'导出Excel').trigger('click')
    expect(download).toHaveBeenCalledWith(expect.any(String), expect.any(Array), [['P700012','设备',8,8,0,1,1]], '工时汇总',expect.objectContaining({version:'项目汇总'}))
    await button(wrapper,'填报明细').trigger('click'); expect(wrapper.get('.wh-report-table').text()).toContain('P700012-1')
    await wrapper.get('select[aria-label="汇总所在地"]').setValue('厂外')
    expect(wrapper.get('.wh-report-summary').text()).toContain('0 小时')
    expect(wrapper.get('.wh-report-table').text()).toContain('没有填报记录')
    wrapper.unmount()
  })
})
