import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import WorkflowNotificationSettings from '../src/components/WorkflowNotificationSettings.vue'
import ApprovalWorkflowSettings from '../src/components/ApprovalWorkflowSettings.vue'
import type { OrganizationDirectory, PdmSystemSettings, RolePermissionSettings } from '../src/types'
import type { WorkflowNotificationCatalog } from '../src/workflowNotifications'
const api = vi.hoisted(() => ({ readWorkflowNotifications: vi.fn(), saveWorkflowNotification: vi.fn(), readWorkflowNotificationRecords: vi.fn() }))
vi.mock('../src/api', () => api)
const directory = { organizations: [{ id:'company',name:'当前公司',isActive:true }],units:[
 {id:'t1',name:'T1事业部',organizationId:'company',isActive:true},{id:'supply',name:'供应链',organizationId:'company',isActive:true},
 {id:'foreign',name:'其他公司部门',organizationId:'other',isActive:true},{id:'inactive',name:'停用部门',organizationId:'company',isActive:false},
 {id:'hidden-child',name:'停用部门下级',organizationId:'company',parentUnitId:'inactive',isActive:true}],memberships:[],managers:[],
 users:[{username:'assistant',displayName:'技术助理甲',companyId:'company',isActive:true},{username:'foreign-user',displayName:'外公司人员',companyId:'other',isActive:true}]
} as unknown as OrganizationDirectory
function data():WorkflowNotificationCatalog {return{companyId:'company',rowVersion:3,positions:[],assignments:[],flows:[{flowCode:'standard-release',enabled:true,recipients:[{departmentSource:'SubmitterDepartment',departmentId:null,roleCode:'TechnicalAssistant'}]}]}}
const roles=[{role:'TechnicalAssistant',name:'技术助理'},{role:'SupplyChain',name:'供应链'},{role:'custom-manager',name:'供应链主管'}] as RolePermissionSettings[]
const props={roles,token:'token',companyId:'company',flowCode:'standard-release',directory,active:true}
const save=(w:ReturnType<typeof mount>)=>w.findAll('button').find(button=>button.text()==='保存完成抄送')!.trigger('click')
describe('完成抄送',()=>{
 beforeEach(()=>{vi.clearAllMocks();api.readWorkflowNotifications.mockResolvedValue(data());api.saveWorkflowNotification.mockImplementation(async(_code,value)=>({...data(),rowVersion:4,flows:[{flowCode:'standard-release',...value}]}));api.readWorkflowNotificationRecords.mockResolvedValue([])})
 it('retains old role scopes and permits selecting a receiving department and its role',async()=>{
  const w=mount(WorkflowNotificationSettings,{props});await flushPromises();
  expect(w.get('[aria-label="抄送对象1范围"]').element).toHaveProperty('value','SubmitterDepartment');
  expect(w.get('[aria-label="抄送对象1角色"]').element).toHaveProperty('value','TechnicalAssistant');
  await w.get('[aria-label="抄送对象1范围"]').setValue('Department');
  const department=w.get('[aria-label="抄送对象1部门"]');expect(department.text()).not.toContain('其他公司部门');expect(department.text()).not.toContain('停用部门下级');
  await department.setValue('supply');await w.get('[aria-label="抄送对象1角色"]').setValue('custom-manager');await save(w);await flushPromises();
  expect(api.saveWorkflowNotification).toHaveBeenCalledWith('standard-release',expect.objectContaining({recipients:[expect.objectContaining({departmentSource:'Fixed',departmentId:'supply',roleCode:'custom-manager'})]}),'token');w.unmount()
 })
 it('combines nested department roles, project roles and the submitter and reloads saved settings',async()=>{
  const w=mount(WorkflowNotificationSettings,{props});await flushPromises();
  await w.get('[aria-label="抄送对象1包含下级部门"]').setValue(true);
  const add=()=>w.findAll('button').find(button=>button.text()==='添加抄送对象')!.trigger('click');
  await add();await w.get('[aria-label="抄送对象2范围"]').setValue('ProjectRole');await w.get('[aria-label="抄送对象2项目角色"]').setValue('StandardProcurement');
  await add();await w.get('[aria-label="抄送对象3范围"]').setValue('Submitter');await save(w);await flushPromises();
  const value=api.saveWorkflowNotification.mock.calls[0]![1];expect(value.recipients).toEqual([
   expect.objectContaining({departmentSource:'SubmitterDepartment',roleCode:'TechnicalAssistant',includeSubdepartments:true}),
   expect.objectContaining({departmentSource:'ProjectRole',target:'StandardProcurement'}),expect.objectContaining({departmentSource:'Submitter'})]);
  w.unmount();api.readWorkflowNotifications.mockResolvedValue({...data(),flows:[{flowCode:'standard-release',...value}]});
  const reloaded=mount(WorkflowNotificationSettings,{props});await flushPromises();expect(reloaded.get('[aria-label="抄送对象2项目角色"]').element).toHaveProperty('value','StandardProcurement');reloaded.unmount()
 })
 it('validates incomplete selections and keeps edits after a version conflict',async()=>{
  const w=mount(WorkflowNotificationSettings,{props});await flushPromises();
  await w.findAll('button').find(button=>button.text()==='添加抄送对象')!.trigger('click');await save(w);expect(api.saveWorkflowNotification).not.toHaveBeenCalled();
  await w.get('[aria-label="抄送对象2范围"]').setValue('Role');await w.get('[aria-label="抄送对象2角色"]').setValue('SupplyChain');
  api.saveWorkflowNotification.mockRejectedValue(new Error('抄送配置已被其他人修改，请刷新后重新保存。'));await save(w);await flushPromises();
  expect(w.get('[role="alert"]').text()).toContain('已被其他人修改');expect(w.get('[aria-label="抄送对象2角色"]').element).toHaveProperty('value','SupplyChain');w.unmount()
 })
 it('does not broaden old position assignments or silently accept deleted roles',async()=>{
  const legacy=data();legacy.flows[0]!.recipients=[{departmentSource:'Fixed',departmentId:'t1',positionId:'legacy-post'}];api.readWorkflowNotifications.mockResolvedValue(legacy);
  const w=mount(WorkflowNotificationSettings,{props});await flushPromises();expect(w.get('[role="alert"]').text()).toContain('重新选择角色');await save(w);expect(api.saveWorkflowNotification).not.toHaveBeenCalled();
  await w.get('[aria-label="抄送对象1角色"]').setValue('TechnicalAssistant');await save(w);expect(api.saveWorkflowNotification).toHaveBeenCalled();w.unmount()
 })
 it('opens completion copies outside the flow canvas without changing approval nodes',async()=>{
  const settings={} as PdmSystemSettings,onSave=vi.fn();const w=mount(ApprovalWorkflowSettings,{props:{settings,pending:false,onSave,roles,token:'token',companyId:'company',directory},global:{plugins:[ElementPlus]}});await flushPromises();
  expect(w.findAll('.workflow-layout')).toHaveLength(6);expect(w.findAll('.flow-canvas').every(canvas=>!canvas.text().includes('业务完成通知'))).toBe(true);
  const card=w.findAll('.workflow-card')[0]!;await card.get('.completion-copy-action').trigger('click');expect(card.get('.workflow-notifications').isVisible()).toBe(true);
  await card.get('.workflow-notifications').findAll('button').find(button=>button.text()==='保存完成抄送')!.trigger('click');await flushPromises();expect(api.saveWorkflowNotification).toHaveBeenCalled();expect(onSave).not.toHaveBeenCalled();expect(settings).toEqual({});w.unmount()
 })
})
