import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import WorkHoursSettings from '../src/components/WorkHoursSettings.vue'
import * as api from '../src/api'
import type { WorkHoursManagement } from '../src/workHours'
vi.mock('../src/statusMessage',()=>({ElMessage:{success:vi.fn(),warning:vi.fn(),error:vi.fn()}}))
const data = (admin=false):WorkHoursManagement => ({today:'2026-10-21',rowVersion:4,canAuthorize:admin,scope:'当前公司',people:[],units:[],assistants:[],requirements:[],exceptions:[],grants:[],history:[],departmentGroups:[
  {id:'t2',name:'T2事业部',participating:true,people:[{username:'a',displayName:'甲',department:'机械组',isActive:true,required:true,canEdit:!admin},{username:'b',displayName:'乙',department:'电气组',isActive:true,required:false,canEdit:!admin}]},
  {id:'t3',name:'T3事业部',participating:true,people:[{username:'c',displayName:'丙',department:'项目组',isActive:true,required:true,canEdit:!admin}]}
]})
const setup = async (value:WorkHoursManagement) => {
  vi.spyOn(api,'readWorkHoursSettings').mockResolvedValue(value)
  vi.spyOn(api,'saveWorkHoursSettings').mockImplementation(async input=>{const next=structuredClone(value);next.rowVersion++;for(const group of next.departmentGroups!){const d=input.departments.find(x=>x.unitId===group.id);if(d)group.participating=d.participating;for(const p of group.people){const change=input.people.find(x=>x.username===p.username);if(change)p.required=change.required}}return next})
  const wrapper=mount(WorkHoursSettings,{props:{token:'t',companyId:'company'},global:{plugins:[ElementPlus],stubs:{teleport:true}}});await flushPromises();return wrapper
}
afterEach(()=>vi.restoreAllMocks())
describe('按部门分组的统一设置',()=>{
  it('部门列表保留服务端组织顺序，不因名称或参与状态重新排列',async()=>{
    const value=data(true)
    value.departmentGroups=[
      {id:'t1',name:'T1事业部',participating:false,people:[]},
      {id:'t2',name:'T2事业部',participating:true,people:[]},
      {id:'t3',name:'T3事业部',participating:false,people:[]}
    ]
    const w=await setup(value)
    expect(w.findAll('.wh-department-label').map(x=>x.text())).toEqual(['T1事业部','T2事业部','T3事业部'])
    w.unmount()
  })
  it('管理员只改参与部门，人员只读，统一保存不包含人员字段修改',async()=>{
    const w=await setup(data(true));expect(w.findAll('button').filter(x=>x.text()==='保存设置')).toHaveLength(1)
    expect(w.get('.wh-department-name.active .wh-department-participating').text()).toBe('参与填报')
    expect(w.get('[aria-label="T2事业部甲需要填报"]').attributes('disabled')).toBeDefined()
    await w.get('[aria-label="T2事业部参与工时"]').setValue(false)
    expect(w.find('.wh-department-name.active .wh-department-participating').exists()).toBe(false)
    await w.findAll('button').find(x=>x.text()==='保存设置')!.trigger('click');await flushPromises()
    expect(w.find('.wh-department-name.active .wh-department-participating').exists()).toBe(false)
    expect(api.saveWorkHoursSettings).toHaveBeenCalledWith({companyId:'company',rowVersion:4,departments:[{unitId:'t2',participating:false}],people:[]},'t')
    w.unmount()
  })
  it('多个授权部门一次保存，切换部门保留待保存的勾选及当前选择',async()=>{
    const w=await setup(data());await w.get('[aria-label="T2事业部乙需要填报"]').setValue(true)
    await w.findAll('.wh-department-name').find(x=>x.get('.wh-department-label').text()==='T3事业部')!.trigger('click')
    expect(w.find('[aria-label="T2事业部乙需要填报"]').exists()).toBe(false)
    await w.get('[aria-label="T3事业部丙需要填报"]').setValue(false)
    await w.findAll('.wh-department-name').find(x=>x.get('.wh-department-label').text()==='T2事业部')!.trigger('click');await flushPromises()
    expect((w.get('[aria-label="T2事业部乙需要填报"]').element as HTMLInputElement).checked).toBe(true)
    await w.findAll('.wh-department-name').find(x=>x.get('.wh-department-label').text()==='T3事业部')!.trigger('click')
    expect(w.findAll('.wh-department-body').filter(x=>x.isVisible())).toHaveLength(1)
    await w.findAll('button').find(x=>x.text()==='保存设置')!.trigger('click');await flushPromises()
    expect(api.saveWorkHoursSettings).toHaveBeenCalledWith({companyId:'company',rowVersion:4,departments:[],people:[{username:'b',required:true},{username:'c',required:false}]},'t')
    expect(w.emitted('dirtyChange')?.at(-1)).toEqual([false])
    expect(w.get('.wh-department-name.active .wh-department-label').text()).toBe('T3事业部');w.unmount()
  })
  it('失败保留各部门勾选，不能误报保存成功',async()=>{
    const w=await setup(data());vi.mocked(api.saveWorkHoursSettings).mockRejectedValue(new Error('工时设置已被其他人更新，请刷新后重新勾选。'))
    await w.get('[aria-label="T2事业部乙需要填报"]').setValue(true);await w.findAll('button').find(x=>x.text()==='保存设置')!.trigger('click');await flushPromises()
    expect((w.get('[aria-label="T2事业部乙需要填报"]').element as HTMLInputElement).checked).toBe(true)
    expect(w.get('[role="alert"]').text()).toContain('其他人更新');expect(w.emitted('changed')).toBeUndefined();w.unmount()
  })
  it('未参与部门人员只读，启用后恢复授权勾选',async()=>{
    const value=data(true);value.departmentGroups![0]!.participating=false;value.departmentGroups![0]!.people[1]!.canEdit=true
    const w=await setup(value)
    expect(w.get('[aria-label="T2事业部乙需要填报"]').attributes('disabled')).toBeDefined()
    await w.get('[aria-label="T2事业部参与工时"]').setValue(true)
    expect(w.get('[aria-label="T2事业部乙需要填报"]').attributes('disabled')).toBeUndefined()
    await w.get('[aria-label="T2事业部乙需要填报"]').setValue(true)
    await w.get('[aria-label="T2事业部参与工时"]').setValue(false)
    expect((w.get('[aria-label="T2事业部乙需要填报"]').element as HTMLInputElement).checked).toBe(false);w.unmount()
  })
  it('全选只修改有权限人员，不动只读人员',async()=>{
    const value=data();value.departmentGroups![0]!.people[0]!.canEdit=false
    const w=await setup(value);await w.get('[aria-label="全选T2事业部人员"]').setValue(true);await w.findAll('button').find(x=>x.text()==='保存设置')!.trigger('click');await flushPromises()
    expect(api.saveWorkHoursSettings).toHaveBeenCalledWith(expect.objectContaining({people:[{username:'b',required:true}]}),'t');w.unmount()
  })
})
