import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElDatePicker, ElMessageBox, ElSelect } from 'element-plus'
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
const treeSelectStub = defineComponent({name:'ElTreeSelect',props:['modelValue','data'],emits:['update:modelValue'],template:'<div data-testid="department-tree" />'})
const mountManager = () => mount(WorkHoursManager, { props: { token: 'token', companyId: 'c', active: true }, global: { plugins: [ElementPlus], stubs: { teleport: true, ElSelect: selectStub, ElOption: optionStub, ElTreeSelect: treeSelectStub } } })
type Wrapper = ReturnType<typeof mountManager>
const button = (wrapper: Wrapper, text: string) => wrapper.findAll('button').find(x => x.text() === text)!
beforeEach(() => { vi.spyOn(calendar,'chinaToday').mockReturnValue('2026-10-20'); vi.spyOn(api,'readWorkHours').mockImplementation(async () => view()); vi.spyOn(api,'saveWorkHours').mockImplementation(async () => view()) })
afterEach(() => vi.restoreAllMocks())

describe('工时填报与汇总', () => {
  async function calendarMenu(w: Wrapper, date: string, action: string) {
    await w.get(`[data-work-hours-date="${date}"]`).trigger('contextmenu',{clientX:300,clientY:300});await flushPromises()
    await w.get('.wh-calendar-menu').findAll('button').find(x=>x.text()===action)!.trigger('click');await flushPromises()
  }
  const transfer = () => ({ setData: vi.fn(), effectAllowed: '', dropEffect: '' })

  it('拖拽整日工时先核对再复制，源日期保留', async()=>{
    const w=mountManager();await flushPromises();const data=transfer()
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('dragstart',{dataTransfer:data})
    expect(data.setData).toHaveBeenCalledWith('application/x-uplm-work-hours','copy')
    await w.get('[data-work-hours-date="2026-10-20"]').trigger('dragover',{dataTransfer:data})
    await w.get('[data-work-hours-date="2026-10-20"]').trigger('drop',{dataTransfer:data});await flushPromises()
    expect(w.get('.wh-copy-row').text()).toContain('P700012-1');expect(api.saveWorkHours).not.toHaveBeenCalled()
    await button(w,'保存复制工时').trigger('click');await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledWith(expect.objectContaining({from:'2026-10-20',to:'2026-10-20',entries:[{date:'2026-10-20',projectId:'child',location:'厂内',hours:8}]}),'token')
    expect(w.get('[data-work-hours-date="2026-10-19"]').text()).toContain('8 小时');w.unmount()
  })

  it('空白日、未来日期、同日及外来拖入不产生复制或保存', async()=>{
    const w=mountManager();await flushPromises();const data=transfer()
    expect(w.get('[data-work-hours-date="2026-10-18"]').attributes('draggable')).toBe('false')
    await w.get('[data-work-hours-date="2026-10-20"]').trigger('drop',{dataTransfer:data});await flushPromises();expect(w.find('.wh-copy-row').exists()).toBe(false)
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('dragstart',{dataTransfer:data})
    await w.get('[data-work-hours-date="2026-10-21"]').trigger('drop',{dataTransfer:data});await flushPromises();expect(w.find('.wh-copy-row').exists()).toBe(false)
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('dragstart',{dataTransfer:data})
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('drop',{dataTransfer:data});await flushPromises();expect(w.find('.wh-copy-row').exists()).toBe(false)
    expect(api.saveWorkHours).not.toHaveBeenCalled();w.unmount()
  })

  it('右键复制和粘贴沿用整日核对及合并规则', async()=>{
    const w=mountManager();await flushPromises()
    await calendarMenu(w,'2026-10-19','复制');expect(w.find('.wh-calendar-menu').exists()).toBe(false)
    await calendarMenu(w,'2026-10-20','粘贴');expect(w.get('.wh-copy-row').text()).toContain('P700012-1')
    expect(api.saveWorkHours).not.toHaveBeenCalled();await button(w,'保存复制工时').trigger('click');await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledTimes(1);w.unmount()
  })

  it('剪切取消不删除，确认后通过一次移动请求完成并清空剪贴内容', async()=>{
    const data=view();vi.mocked(api.readWorkHours).mockImplementation(async()=>structuredClone(data))
    const move=vi.spyOn(api,'moveWorkHoursDay').mockImplementation(async input=>{const sheet=data.sheets.find(x=>x.month==='2026-10')!;sheet.entries=sheet.entries.map(x=>x.date===input.sourceDate?{...x,date:input.targetDate}:x);sheet.rowVersion++;return structuredClone(data)})
    const w=mountManager();await flushPromises();await calendarMenu(w,'2026-10-19','剪切')
    expect(w.get('[data-work-hours-date="2026-10-19"]').classes()).toContain('is-cut');expect(move).not.toHaveBeenCalled()
    await calendarMenu(w,'2026-10-20','粘贴');expect(w.get('[aria-label="复制记录工时"]').attributes('readonly')).toBeDefined()
    await w.get('.el-dialog__footer').findAll('button').find(x=>x.text()==='取消')!.trigger('click');await flushPromises();expect(move).not.toHaveBeenCalled()
    await calendarMenu(w,'2026-10-20','粘贴');await button(w,'确认剪切').trigger('click');await flushPromises()
    expect(move).toHaveBeenCalledWith({companyId:'c',sourceDate:'2026-10-19',targetDate:'2026-10-20',sourceRowVersion:2,targetRowVersion:2},'token')
    expect(api.saveWorkHours).not.toHaveBeenCalled();expect(w.get('[data-work-hours-date="2026-10-19"]').text()).not.toContain('8 小时');expect(w.get('[data-work-hours-date="2026-10-20"]').text()).toContain('8 小时');w.unmount()
  })

  it('跨月剪切携带源和目标各自版本，失败时保留核对和源记录', async()=>{
    const move=vi.spyOn(api,'moveWorkHoursDay').mockRejectedValue(new Error('源日期或目标日期的工时已更新，请刷新后重新剪切。'))
    const w=mountManager();await flushPromises();await calendarMenu(w,'2026-10-19','剪切')
    await w.get('[aria-label="填报日期"]').setValue('2026-09-30');await flushPromises();await calendarMenu(w,'2026-09-30','粘贴')
    await button(w,'确认剪切').trigger('click');await flushPromises()
    expect(move).toHaveBeenCalledWith(expect.objectContaining({sourceDate:'2026-10-19',targetDate:'2026-09-30',sourceRowVersion:2,targetRowVersion:0}),'token')
    expect(w.find('.wh-copy-row').exists()).toBe(true);expect(api.saveWorkHours).not.toHaveBeenCalled();w.unmount()
  })

  it('右键删除整日工时须确认且只保存该日期的空记录', async()=>{
    const confirm=vi.spyOn(ElMessageBox,'confirm').mockRejectedValueOnce('cancel').mockResolvedValueOnce('confirm' as never)
    const w=mountManager();await flushPromises();await calendarMenu(w,'2026-10-19','删除');expect(api.saveWorkHours).not.toHaveBeenCalled()
    await calendarMenu(w,'2026-10-19','删除')
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('2026-10-19'), '删除当日工时',expect.any(Object))
    expect(api.saveWorkHours).toHaveBeenCalledWith({from:'2026-10-19',to:'2026-10-19',entries:[],versions:[{month:'2026-10',rowVersion:2}]},'token');w.unmount()
  })

  it('未保存编辑阻止拖拽和右键，切换公司清空剪切及拖拽状态', async()=>{
    const w=mountManager();await flushPromises();await w.get('[aria-label="填报项目"]').setValue('child')
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('contextmenu',{clientX:300,clientY:300});await flushPromises();expect(w.find('.wh-calendar-menu').exists()).toBe(false)
    await button(w,'取消修改').trigger('click');await calendarMenu(w,'2026-10-19','剪切')
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('dragstart',{dataTransfer:transfer()});await w.setProps({companyId:'other'});await flushPromises()
    expect(w.find('.is-cut').exists()).toBe(false);expect(w.find('.is-drag-source').exists()).toBe(false)
    await w.get('[data-work-hours-date="2026-10-20"]').trigger('contextmenu',{clientX:300,clientY:300});await flushPromises()
    expect(w.get('.wh-calendar-menu').findAll('button').find(x=>x.text()==='粘贴')!.attributes('disabled')).toBeDefined();w.unmount()
  })

  it('右键菜单支持Escape和外部点击关闭', async()=>{
    const w=mountManager();await flushPromises();await w.get('[data-work-hours-date="2026-10-19"]').trigger('contextmenu',{clientX:300,clientY:300});await flushPromises()
    await w.get('.wh-calendar-menu').trigger('keydown',{key:'Escape'});expect(w.find('.wh-calendar-menu').exists()).toBe(false)
    await w.get('[data-work-hours-date="2026-10-19"]').trigger('contextmenu',{clientX:300,clientY:300});await flushPromises();document.dispatchEvent(new Event('pointerdown'));await flushPromises()
    expect(w.find('.wh-calendar-menu').exists()).toBe(false);w.unmount()
  })
  it('复制选中日期后粘贴到另一日，核对后追加并合并相同项目地点', async () => {
    const data = view(), sheet = data.sheets.find(item => item.month === '2026-10')!
    sheet.entries.push({ ...sheet.entries[0]!, id: 'target-child', date: '2026-10-20', hours: 2 },
      { ...sheet.entries[0]!, id: 'target-root', date: '2026-10-20', projectId: 'root', projectCode: 'P700012', projectName: '设备', hours: 3 })
    vi.mocked(api.readWorkHours).mockImplementation(async () => structuredClone(data))
    const w = mountManager();await flushPromises()
    await w.get('[aria-label="2026-10-19 8小时"]').trigger('click')
    await button(w, '复制当日工时').trigger('click')
    expect(button(w, '粘贴工时到此日').attributes('disabled')).toBeDefined()
    await w.get('[aria-label="2026-10-20 5小时"]').trigger('click')
    await button(w, '粘贴工时到此日').trigger('click');await flushPromises()
    expect(w.get('.wh-copy-row').text()).toContain('P700012-1')
    expect(w.text()).toContain('保存后当日合计 13 小时')
    expect(api.saveWorkHours).not.toHaveBeenCalled()
    await button(w, '保存复制工时').trigger('click');await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledWith({
      from: '2026-10-20', to: '2026-10-20', versions: [{ month: '2026-10', rowVersion: 2 }],
      entries: [{ date: '2026-10-20', projectId: 'child', location: '厂内', hours: 10 }, { date: '2026-10-20', projectId: 'root', location: '厂内', hours: 3 }],
    }, 'token')
    expect(data.sheets.find(item => item.month === '2026-10')?.entries[0]?.hours).toBe(8)
    w.unmount()
  })

  it('剪贴内容支持跨月，未来日期不能粘贴，切换公司后清空', async () => {
    const w = mountManager();await flushPromises()
    await w.get('[aria-label="2026-10-19 8小时"]').trigger('click')
    await button(w, '复制当日工时').trigger('click')
    await w.get('[aria-label="填报日期"]').setValue('2026-10-21');await flushPromises()
    expect(button(w, '粘贴工时到此日').attributes('disabled')).toBeDefined()
    await w.get('[aria-label="填报日期"]').setValue('2026-09-30');await flushPromises()
    await button(w, '粘贴工时到此日').trigger('click');await flushPromises()
    await button(w, '保存复制工时').trigger('click');await flushPromises()
    expect(api.saveWorkHours).toHaveBeenCalledWith({
      from: '2026-09-30', to: '2026-09-30', entries: [{ date: '2026-09-30', projectId: 'child', location: '厂内', hours: 8 }],
      versions: [{ month: '2026-09', rowVersion: 0 }],
    }, 'token')
    await w.setProps({ companyId: 'other' });await flushPromises()
    expect(button(w, '粘贴工时到此日').attributes('disabled')).toBeDefined()
    w.unmount()
  })

  it('追加超过24小时或复制行工时无效时不提交，并保留核对窗口', async () => {
    const data=view(),sheet=data.sheets.find(item=>item.month==='2026-10')!
    sheet.entries.push({...sheet.entries[0]!,id:'target',date:'2026-10-20',projectId:'root',hours:20})
    vi.mocked(api.readWorkHours).mockImplementation(async()=>structuredClone(data))
    const w=mountManager();await flushPromises()
    await w.get('[aria-label="2026-10-19 8小时"]').trigger('click');await button(w,'复制当日工时').trigger('click')
    await w.get('[aria-label="2026-10-20 20小时"]').trigger('click');await button(w,'粘贴工时到此日').trigger('click');await flushPromises()
    expect(w.text()).toContain('当日工时合计不能超过24小时')
    await button(w,'保存复制工时').trigger('click');await flushPromises();expect(api.saveWorkHours).not.toHaveBeenCalled()
    await w.get('[aria-label="复制记录工时"]').setValue('0');await button(w,'保存复制工时').trigger('click');await flushPromises()
    expect(api.saveWorkHours).not.toHaveBeenCalled();expect(w.find('.wh-copy-row').exists()).toBe(true);w.unmount()
  })

  it('单框选择跨月区间后清除旧汇总，查询使用完整起止日期', async () => {
    const w = mountManager(); await flushPromises(); await button(w, '汇总工时').trigger('click'); await flushPromises()
    const range = w.findComponent(ElDatePicker)
    range.vm.$emit('update:modelValue', ['2026-09-30', '2026-10-19']); await flushPromises()
    expect(w.find('.wh-report-summary').exists()).toBe(false)
    await w.get('form.wh-report-filters').trigger('submit'); await flushPromises()
    expect(api.readWorkHours).toHaveBeenLastCalledWith('2026-09-30', '2026-10-19', true, 'token')
    expect(w.get('.wh-report-summary').text()).toContain('8 小时')
    range.vm.$emit('update:modelValue', null); await flushPromises()
    vi.mocked(api.readWorkHours).mockClear()
    await w.get('form.wh-report-filters').trigger('submit'); await flushPromises()
    expect(api.readWorkHours).not.toHaveBeenCalled()
    w.unmount()
  })
  it('仅显示参与部门，清除已停用的默认及当前部门筛选，并保留历史汇总导出', async()=>{
    const data=view();data.defaultDepartmentId='t3';data.participatingDepartmentIds=['t2','child']
    data.organizationUnits=[{id:'t2',parentUnitId:null,name:'T2事业部',sortOrder:1,isActive:true},{id:'child',parentUnitId:'t2',name:'机械组',sortOrder:1,isActive:true},{id:'t3',parentUnitId:null,name:'T3事业部',sortOrder:2,isActive:true}]
    data.people[0]!.departmentId='child'
    const download=vi.spyOn(workbook,'downloadProcurementWorkbook').mockImplementation(()=>{})
    vi.mocked(api.readWorkHours).mockImplementation(async()=>structuredClone(data))
    const w=mountManager();await flushPromises();await button(w,'汇总工时').trigger('click');await flushPromises()
    const tree=w.findComponent(treeSelectStub)
    expect(tree.props('data').map((x:{id:string})=>x.id)).toEqual(['t2']);expect(tree.props('modelValue')).toBe('')
    tree.vm.$emit('update:modelValue','t2');await flushPromises()
    data.participatingDepartmentIds=[]
    await w.get('form.wh-report-filters').trigger('submit');await flushPromises()
    expect(tree.props('data')).toEqual([]);expect(tree.props('modelValue')).toBe('')
    expect(w.get('.wh-report-summary').text()).toContain('8 小时')
    await button(w,'导出Excel').trigger('click');expect(download.mock.calls[0]![2][0]).toContain(8)
    w.unmount()
  })
  it('组织树联动人员范围，切换部门清除不匹配人员并同步汇总和完整性',async()=>{
    const download=vi.spyOn(workbook,'downloadProcurementWorkbook').mockImplementation(()=>{})
    const value=view();value.defaultDepartmentId='t2-mech';value.organizationUnits=[
      {id:'t2',parentUnitId:null,name:'T2事业部',sortOrder:1,isActive:true},{id:'t2-mech',parentUnitId:'t2',name:'机械组',sortOrder:1,isActive:true},{id:'t2-elec',parentUnitId:'t2',name:'电气组',sortOrder:2,isActive:true},
      {id:'t3',parentUnitId:null,name:'T3事业部',sortOrder:2,isActive:true},{id:'t3-mech',parentUnitId:'t3',name:'机械组',sortOrder:1,isActive:true}]
    value.people=[{username:'a',displayName:'员工甲',department:'机械组',departmentId:'t2-mech',isActive:true},{username:'b',displayName:'员工乙',department:'电气组',departmentId:'t2-elec',isActive:true},{username:'c',displayName:'员工丙',department:'机械组',departmentId:'t3-mech',isActive:true}]
    const first=value.sheets.find(x=>x.month==='2026-10')!,entry=first.entries[0]!
    value.sheets=[first,{...first,username:'b',entries:[{...entry,id:'b',hours:4}]},{...first,username:'c',entries:[{...entry,id:'c',hours:6}]}]
    value.compliance=value.people.map(p=>({username:p.username,days:[{date:'2026-10-19',expected:8,actual:0,status:'未填写',muted:false,reason:''}]}))
    vi.mocked(api.readWorkHours).mockResolvedValue(value)
    const w=mountManager();await flushPromises();await button(w,'汇总工时').trigger('click');await flushPromises()
    const tree=w.findComponent(treeSelectStub)
    expect(tree.props('data')[0].children).toHaveLength(2)
    expect(w.get('[aria-label="汇总人员"]').findAll('option').map(x=>x.text())).toEqual(['全部','员工甲'])
    tree.vm.$emit('update:modelValue','t2');await flushPromises()
    expect(w.get('[aria-label="汇总人员"]').findAll('option').map(x=>x.text())).toEqual(['全部','员工甲','员工乙'])
    await w.get('[aria-label="汇总人员"]').setValue('b')
    tree.vm.$emit('update:modelValue','t3');await flushPromises()
    expect((w.get('[aria-label="汇总人员"]').element as HTMLSelectElement).value).toBe('')
    expect(w.get('[aria-label="汇总人员"]').findAll('option').map(x=>x.text())).toEqual(['全部','员工丙'])
    expect(w.get('.wh-report-summary').text()).toContain('6 小时')
    expect(w.get('.wh-report-table tbody').text()).toContain('员工丙');expect(w.get('.wh-report-table tbody').text()).not.toContain('员工甲')
    expect(w.get('.wh-compliance tbody').text()).toContain('员工丙');expect(w.get('.wh-compliance tbody').text()).not.toContain('员工乙')
    await button(w,'导出Excel').trigger('click')
    expect(download.mock.calls[0]![2]).toEqual([['员工丙','机械组',1,6,6,0,1,'2026-10-19']])
    tree.vm.$emit('update:modelValue',undefined);await flushPromises()
    expect(w.get('[aria-label="汇总人员"]').findAll('option')).toHaveLength(4)
    expect(w.get('.wh-report-summary').text()).toContain('18 小时');w.unmount()
  })
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
    expect(wrapper.find('.wh-toolbar').exists()).toBe(false)
    const projectBar = wrapper.get('.wh-project-bars button')
    expect(projectBar.attributes('title')).toContain('P700012 · 设备 厂内8小时，厂外0小时')
    await projectBar.trigger('click'); await flushPromises()
    expect(wrapper.get('.el-drawer__body').text()).toContain('上料单元')
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
