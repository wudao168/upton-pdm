import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElDatePicker } from 'element-plus'
import { defineComponent } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../src/api'
import { complianceSummary } from '../src/workHours'
import type { WorkHoursManagement, WorkHoursView } from '../src/workHours'
import WorkHoursSettings from '../src/components/WorkHoursSettings.vue'
import WorkHoursCompliance from '../src/components/WorkHoursCompliance.vue'
import { ElMessage } from '../src/statusMessage'

vi.mock('../src/statusMessage',()=>({ElMessage:{success:vi.fn(),warning:vi.fn(),error:vi.fn(),info:vi.fn()}}))
const selectStub = defineComponent({name:'ElSelect',props:['modelValue','multiple','disabled'],emits:['update:modelValue'],template:'<select :value="modelValue" :multiple="multiple" :disabled="disabled" @change="$emit(\'update:modelValue\',$event.target.value)"><slot /></select>'})
const optionStub = defineComponent({name:'ElOption',props:['value','label'],template:'<option :value="value">{{label}}</option>'})
const global = {plugins:[ElementPlus],stubs:{teleport:true,ElSelect:selectStub,ElOption:optionStub}}
const people = [{username:'worker',displayName:'员工甲',department:'设计部',isActive:true},{username:'test',displayName:'测试账号',department:'设计部',isActive:true}]
const settings = (): WorkHoursManagement => ({today:'2026-10-21',rowVersion:3,canAuthorize:false,scope:'授权部门',people,units:[],assistants:[],requirements:[{id:'r',username:'worker',from:'2026-10-20',to:null,hours:8}],exceptions:[],grants:[],history:[],departmentGroups:[{id:'design',name:'设计部',participating:true,people:people.map(p=>({...p,required:p.username==='worker',canEdit:true}))},{id:'assembly',name:'装配部',participating:true,people:[{username:'outside',displayName:'员工乙',department:'装配部',isActive:true,required:true,canEdit:false}]}]})
const view = (): WorkHoursView => ({today:'2026-10-21',startDate:'2026-10-20',username:'assistant',displayName:'技术助理',scope:'本人及管理部门',people,projects:[],sheets:[],canManage:true,manageableUsernames:['worker'],compliance:[{username:'worker',days:[{date:'2026-10-20',expected:8,actual:4,status:'填写不完整',muted:true,reason:'出差暂停提醒'}]}]})
beforeEach(()=>{vi.spyOn(api,'readWorkHoursSettings').mockResolvedValue(settings());vi.spyOn(api,'saveWorkHoursSettings').mockResolvedValue(settings());vi.spyOn(api,'setWorkHoursException').mockResolvedValue(settings());vi.spyOn(api,'remindWorkHours').mockResolvedValue({sent:1,skipped:0})})
afterEach(()=>vi.restoreAllMocks())
describe('工时名单及完整性管理',()=>{
  it('单人和批量手动提醒可重复发送，零发送时显示具体原因而不显示成功',async()=>{
    const value=view();value.compliance![0]!.days[0]!.muted=false
    const wrapper=mount(WorkHoursCompliance,{props:{view:value,token:'t',companyId:'c',username:'',department:''},global});await flushPromises()
    const single=wrapper.findAll('button').find(b=>b.text()==='提醒')!,batch=wrapper.findAll('button').find(b=>b.text()==='提醒当前缺填人员')!
    await single.trigger('click');await flushPromises();await single.trigger('click');await flushPromises();await batch.trigger('click');await flushPromises()
    expect(api.remindWorkHours).toHaveBeenCalledTimes(3)
    expect(ElMessage.success).toHaveBeenLastCalledWith('已向1人发送站内工时提醒。')
    vi.mocked(api.remindWorkHours).mockResolvedValue({sent:0,skipped:1,results:[{username:'worker',displayName:'员工甲',sent:false,reason:'缺填日期已暂停提醒'}]})
    vi.mocked(ElMessage.success).mockClear();vi.mocked(ElMessage.info).mockClear()
    await single.trigger('click');await flushPromises()
    expect(ElMessage.success).not.toHaveBeenCalled();expect(ElMessage.info).toHaveBeenLastCalledWith('本次未发送提醒：员工甲：缺填日期已暂停提醒。')
    expect(wrapper.findAll('.wh-check-help')).toHaveLength(0);wrapper.unmount()
  })
  it('仅应填名单进入检查，暂停提醒仍显示不足和缺少小时',async()=>{
    const wrapper=mount(WorkHoursCompliance,{props:{view:view(),token:'t',companyId:'c',username:'',department:''},global});await flushPromises()
    expect(wrapper.get('tbody').text()).toContain('员工甲');expect(wrapper.get('tbody').text()).not.toContain('测试账号')
    expect(wrapper.get('tbody').text()).toContain('已暂停提醒')
    const sum=complianceSummary(view().compliance![0]!.days);expect(sum).toMatchObject({short:1,deficit:4,pending:0,muted:1,complete:0})
    wrapper.unmount()
  })
  it('切换部门展示，助理只提交授权人员变化，无需日期和原因',async()=>{
    const wrapper=mount(WorkHoursSettings,{props:{token:'t',companyId:'c'},global});await flushPromises()
    expect(wrapper.findAll('.wh-department-body').filter(x=>x.isVisible())).toHaveLength(1)
    expect(wrapper.get('[aria-label="设计部参与工时"]').attributes('disabled')).toBeDefined()
    await wrapper.findAll('.wh-department-name').find(x=>x.get('.wh-department-label').text()==='装配部')!.trigger('click')
    expect(wrapper.get('[aria-label="装配部员工乙需要填报"]').attributes('disabled')).toBeDefined()
    await wrapper.findAll('.wh-department-name').find(x=>x.get('.wh-department-label').text()==='设计部')!.trigger('click')
    expect(wrapper.find('[aria-label="名单生效日期"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="名单设置原因"]').exists()).toBe(false)
    await wrapper.get('[aria-label="设计部测试账号需要填报"]').setValue(true)
    await wrapper.findAll('button').find(x=>x.text()==='保存设置')!.trigger('click');await flushPromises()
    expect(api.saveWorkHoursSettings).toHaveBeenCalledWith({companyId:'c',rowVersion:3,departments:[],people:[{username:'test',required:true}]},'t')
    expect(wrapper.emitted('changed')).toHaveLength(1);wrapper.unmount()
  })
  it('特殊处理区分调整工时和暂停提醒，保留日期范围及原因',async()=>{
    const wrapper=mount(WorkHoursCompliance,{props:{view:view(),token:'t',companyId:'c',username:'',department:''},global});await flushPromises()
    const button=(text:string)=>wrapper.findAll('button').find(x=>x.text()===text)!
    await button('逐日检查').trigger('click');await flushPromises();await button('调整应填／暂停提醒').trigger('click');await flushPromises()
    wrapper.findComponent(ElDatePicker).vm.$emit('update:modelValue',['2026-10-19','2026-10-20']);await flushPromises()
    await wrapper.get('[aria-label="特殊处理方式"]').setValue('mute');await wrapper.get('[aria-label="特殊处理原因"]').setValue('临时暂停')
    await wrapper.get('.wh-check-form').trigger('submit');await flushPromises()
    expect(api.setWorkHoursException).toHaveBeenCalledWith({username:'worker',from:'2026-10-19',to:'2026-10-21',kind:'mute',hours:null,reason:'临时暂停',rowVersion:3},'t')
    wrapper.unmount()
  })
  it('代更正使用原月版本，未填原因不提交，填写后提交当天全部记录',async()=>{
    vi.spyOn(api,'readWorkHoursCorrection').mockResolvedValue({sheet:{companyId:'c',username:'worker',month:'2026-10',rowVersion:7,history:[],entries:[{id:'e',date:'2026-10-20',projectId:'p',projectCode:'P700001',projectName:'设备',rootProjectId:'p',rootProjectCode:'P700001',rootProjectName:'设备',location:'厂内',hours:4}]},projects:[]})
    vi.spyOn(api,'correctWorkHours').mockResolvedValue(undefined)
    const wrapper=mount(WorkHoursCompliance,{props:{view:view(),token:'t',companyId:'c',username:'',department:''},global});await flushPromises()
    const button=(text:string)=>wrapper.findAll('button').find(x=>x.text()===text)!
    await button('逐日检查').trigger('click');await flushPromises();await button('代更正实际工时').trigger('click');await flushPromises()
    await wrapper.get('[aria-label="代更正小时"]').setValue('8');await wrapper.get('.wh-check-form').trigger('submit');await flushPromises()
    expect(api.correctWorkHours).not.toHaveBeenCalled()
    await wrapper.get('[aria-label="代更正原因"]').setValue('核对更正');await wrapper.get('.wh-check-form').trigger('submit');await flushPromises()
    expect(api.correctWorkHours).toHaveBeenCalledWith({username:'worker',reason:'核对更正',sheet:{from:'2026-10-20',to:'2026-10-20',entries:[{date:'2026-10-20',projectId:'p',location:'厂内',hours:8}],versions:[{month:'2026-10',rowVersion:7}]}},'t')
    wrapper.unmount()
  })
  it('仅有汇总查看权限时不能出现代更正或批量提醒操作',async()=>{
    const wrapper=mount(WorkHoursCompliance,{props:{view:{...view(),canManage:false,manageableUsernames:[]},token:'t',companyId:'c',username:'',department:''},global});await flushPromises()
    expect(wrapper.text()).not.toContain('提醒当前缺填人员')
    await wrapper.findAll('button').find(x=>x.text()==='逐日检查')!.trigger('click');await flushPromises()
    expect(wrapper.text()).not.toContain('代更正实际工时');wrapper.unmount()
  })
})
