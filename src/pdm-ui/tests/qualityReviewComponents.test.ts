import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import QualityIssuePanel from '../src/components/QualityIssuePanel.vue'
import ProjectQualityIssues from '../src/components/ProjectQualityIssues.vue'
import QualityReviewWorkbench from '../src/components/QualityReviewWorkbench.vue'
import ProjectRetrospective from '../src/components/ProjectRetrospective.vue'
import type { QualityWorkspace, QualityIssueWorkspace, QualityIssueProfile, RetrospectiveWorkspace } from '../src/qualityReview'

const api=vi.hoisted(()=>({requestJson:vi.fn()}))
vi.mock('../src/api',()=>({...api,PdmApiError:class extends Error{constructor(message:string,public status:number){super(message)}}}))
const mounted:Array<ReturnType<typeof mount>>=[]
const issue={id:'issue-1',projectId:'project-1',number:1,description:'传感器误触发',priority:'高',status:'进行中',owner:'account-a',dueDate:null,solution:'检查安装位置',createdBy:'account-b',createdAt:'2026-11-01',completedAt:null,updatedBy:'account-a',updatedAt:'2026-11-01',rowVersion:2,attachments:[]}
const row={issue,profile:null,rootProjectId:'project-1',projectCode:'P001',projectName:'装配设备',ownerName:'张工',createdByName:'李工',canOpenProject:true}
function company():QualityWorkspace{return {catalog:{companyId:'c1',rowVersion:7,profiles:[],themes:[{id:'theme-1',name:'传感器选型',description:'复杂工况',isActive:true}],summaries:[],improvements:[],experiences:[],adoptions:[],settings:{windowDays:90,eventThreshold:3,projectThreshold:2}},issues:[row],metrics:[],projects:[{id:'project-1',code:'P001',name:'装配设备',rootProjectId:'project-1'}],people:[{username:'account-a',displayName:'张工'}],capabilities:{canViewDetails:true},windowFrom:'2026-08-14',windowTo:'2026-11-11'}}
function issueView():QualityIssueWorkspace{return {rowVersion:7,row,themes:[],people:[],relatedIssues:[],canManageIssues:false,canVerifyIssues:false,canCloseIssues:false,canReopenIssues:false,canVoidIssues:false}}
function profile(overrides:Partial<QualityIssueProfile>={}):QualityIssueProfile{return {issueId:'issue-1',domain:'质量',category:'历史检验分类',severity:'普通',occurredOn:'2026-11-01',themeId:null,duplicateOfIssueId:null,recurrenceOfIssueId:null,immediateAction:'隔离',rootCause:'选型错误',correctiveAction:'更换',verificationResult:'未验证',evidenceNote:'',evidenceAttachmentIds:[],updatedBy:'account-a',updatedAt:'2026-11-01',...overrides}}
function project():RetrospectiveWorkspace{return {projectId:'project-1',rowVersion:7,summaries:[],issues:[row],experiences:[],adoptions:[],canEdit:false,canReview:false,canAdopt:false,canPublish:false,themes:[],people:[]}}
async function click(label:string,root:ParentNode=document.body){const button=[...root.querySelectorAll('button')].find(item=>item.textContent?.trim()===label);expect(button,`button ${label}`).toBeTruthy();button!.click();await flushPromises()}
async function create(component:Parameters<typeof mount>[0],props:Record<string,unknown>){const wrapper=mount(component,{props,attachTo:document.body,global:{plugins:[ElementPlus]}});mounted.push(wrapper);await flushPromises();return wrapper}
async function createIssue(props:InstanceType<typeof QualityIssuePanel>['$props']){const wrapper=mount(QualityIssuePanel,{props,attachTo:document.body,global:{plugins:[ElementPlus]}});mounted.push(wrapper);await flushPromises();return wrapper}
beforeEach(()=>{vi.clearAllMocks();api.requestJson.mockResolvedValue(company())})
afterEach(()=>{mounted.splice(0).forEach(wrapper=>wrapper.unmount());document.body.innerHTML=''})

describe('质量与复盘界面权限和流程',()=>{
  it('没有管理能力时只读，人员展示姓名且不显示账号',async()=>{
    const wrapper=await create(QualityReviewWorkbench,{token:'token'})
    expect(wrapper.text()).toContain('张工');expect(wrapper.text()).not.toContain('account-a')
    await click('共性问题');expect(wrapper.text()).not.toContain('新增主题')
    await click('改善事项');expect(wrapper.text()).not.toContain('新增改善')
    expect(wrapper.text()).not.toContain('统计设置')
  })
  it('只有汇总权限时不给出空明细等于全公司无问题的误导',async()=>{
    const result=company();result.capabilities={};result.issues=[];result.metrics=[{themeId:'theme-1',name:'传感器选型',eventCount:4,projectCount:3,majorCount:1,isCandidate:true,activeImprovementId:null,unknownDateCount:0}];api.requestJson.mockResolvedValue(result)
    const wrapper=await create(QualityReviewWorkbench,{token:'token'})
    expect(wrapper.text()).toContain('传感器选型');expect(wrapper.text()).not.toContain('问题总表');expect(wrapper.text()).not.toContain('没有符合条件的问题')
  })
  it('验证人员只能编辑验证字段，保存使用真实项目及版本',async()=>{
    const view=issueView();view.canVerifyIssues=true;view.row={...row,profile:{issueId:'issue-1',domain:'质量',category:'检验',severity:'普通',occurredOn:'2026-11-01',themeId:null,duplicateOfIssueId:null,recurrenceOfIssueId:null,immediateAction:'隔离',rootCause:'选型错误',correctiveAction:'更换',verificationResult:'未验证',evidenceNote:'',evidenceAttachmentIds:[],updatedBy:'account-a',updatedAt:'2026-11-01'}};api.requestJson.mockResolvedValue(view)
    const wrapper=await create(QualityIssuePanel,{token:'token',projectId:'project-1',issueId:'issue-1'})
    expect(wrapper.get('fieldset[aria-label="日常填报"]').attributes()).toHaveProperty('disabled');expect(wrapper.get('fieldset[aria-label="效果验证资料"]').attributes()).not.toHaveProperty('disabled');expect(wrapper.get('fieldset[aria-label="公司复盘归类"]').attributes()).toHaveProperty('disabled')
    await wrapper.get('[aria-label="效果验证"]').setValue('通过');await wrapper.findAll('textarea').at(-1)!.setValue('已连续运行8小时，无误触发')
    await wrapper.get('form').trigger('submit');await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(call[0]).toBe('/api/projects/project-1/quality-review/issues/issue-1');expect(JSON.parse(call[1].body)).toMatchObject({expectedRowVersion:7,domain:'质量',verificationResult:'通过',evidenceNote:'已连续运行8小时，无误触发'})
  })
  it.each([{duplicateOfIssueId:'historical-duplicate',recurrenceOfIssueId:null},{duplicateOfIssueId:null,recurrenceOfIssueId:'historical-recurrence'}])('隐藏历史分类和关系操作，日常保存原样保留历史字段 %j',async relations=>{
    const view=issueView();view.canManageIssues=true;view.row={...row,profile:profile(relations)};api.requestJson.mockResolvedValue(view)
    const wrapper=await create(QualityIssuePanel,{token:'token',projectId:'project-1',issueId:'issue-1'})
    expect(wrapper.find('[aria-label="问题类型"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('问题领域');expect(wrapper.find('input[maxlength="100"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('重复登记');expect(wrapper.text()).not.toContain('独立复发');expect(wrapper.text()).not.toContain('历史检验分类')
    expect(wrapper.get('details').attributes('open')).toBeUndefined();expect(wrapper.get('summary').text()).toContain('未归类');expect(wrapper.text()).not.toContain('维护共性问题')
    await wrapper.get('textarea').setValue('已隔离，等待处理');await wrapper.get('form').trigger('submit');await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(JSON.parse(call[1].body)).toMatchObject({category:'历史检验分类',...relations,immediateAction:'已隔离，等待处理'})
  })
  it('维护共性问题返回只更新选项和并发版本，保留未保存草稿',async()=>{
    const view=issueView();view.canManageIssues=true;view.row={...row,profile:profile()};api.requestJson.mockResolvedValue(view)
    const wrapper=await createIssue({token:'token',projectId:'project-1',issueId:'issue-1',showCompanyReviewEntry:true})
    await wrapper.get('textarea').setValue('尚未保存的临时处置')
    await click('维护共性问题');expect(wrapper.emitted('openCompanyReview')).toEqual([[]]);expect(api.requestJson.mock.calls.some(([,init])=>init?.method==='PUT')).toBe(false)
    api.requestJson.mockResolvedValue({...view,rowVersion:8,themes:[{id:'new-theme',name:'新增共性问题',description:'',isActive:true}]})
    await wrapper.setProps({companyReviewRevision:1});await flushPromises()
    expect(wrapper.get('textarea').element.value).toBe('尚未保存的临时处置')
    await wrapper.get('[aria-label="归入公司共性问题"]').setValue('new-theme');await wrapper.get('form').trigger('submit');await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(JSON.parse(call[1].body)).toMatchObject({expectedRowVersion:8,themeId:'new-theme',immediateAction:'尚未保存的临时处置'})
  })
  it('维护返回发现问题资料已被修改时保留旧版本和草稿，不越过并发冲突',async()=>{
    const view=issueView();view.canManageIssues=true;view.row={...row,profile:profile()};api.requestJson.mockResolvedValue(view)
    const wrapper=await createIssue({token:'token',projectId:'project-1',issueId:'issue-1'})
    await wrapper.get('textarea').setValue('当前人员的草稿')
    api.requestJson.mockResolvedValue({...view,rowVersion:9,row:{...row,profile:profile({immediateAction:'其他人员已保存的处置'})}})
    await wrapper.setProps({companyReviewRevision:1});await flushPromises()
    expect(wrapper.get('textarea').element.value).toBe('当前人员的草稿');expect(wrapper.get('[role="alert"]').text()).toContain('已被其他人员更新')
    await wrapper.get('form').trigger('submit');await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(JSON.parse(call[1].body)).toMatchObject({expectedRowVersion:7,immediateAction:'当前人员的草稿'})
  })
  it('项目问题显示问题类型，待分类筛选包含空类型并透传维护入口',async()=>{
    const result=project();result.canViewIssues=true;result.issues=[{...row,profile:profile({domain:''})}]
    api.requestJson.mockImplementation(url=>String(url).endsWith('/issues/issue-1')?Promise.resolve({...issueView(),row:result.issues[0]}):Promise.resolve(result))
    const wrapper=await create(ProjectQualityIssues,{token:'token',projectId:'project-1',showCompanyReviewEntry:true,companyReviewRevision:2})
    await wrapper.get('select').setValue('unclassified');expect(wrapper.get('table').text()).toContain('传感器误触发');expect(wrapper.get('table').text()).toContain('问题类型');expect(wrapper.text()).not.toContain('历史检验分类')
    await click('闭环资料');const panel=wrapper.getComponent(QualityIssuePanel);expect(panel.props('companyReviewRevision')).toBe(2)
    await click('维护共性问题');expect(wrapper.emitted('openCompanyReview')).toEqual([[]])
  })
  it('切换项目后迟到响应不会显示旧项目资料',async()=>{
    let resolveOld!:(value:QualityIssueWorkspace)=>void
    api.requestJson.mockImplementationOnce(()=>new Promise(resolve=>{resolveOld=resolve})).mockResolvedValue({...issueView(),row:{...row,issue:{...issue,id:'issue-2',description:'第二项目'},projectCode:'P002'}})
    const wrapper=mount(QualityIssuePanel,{props:{token:'token',projectId:'project-1',issueId:'issue-1'},attachTo:document.body,global:{plugins:[ElementPlus]}});mounted.push(wrapper)
    await wrapper.setProps({projectId:'project-2',issueId:'issue-2'});await flushPromises();resolveOld({...issueView(),row:{...row,profile:{issueId:'issue-1',domain:'质量',category:'旧项目机密分类',severity:'普通',occurredOn:null,themeId:null,duplicateOfIssueId:null,recurrenceOfIssueId:null,immediateAction:'',rootCause:'',correctiveAction:'',verificationResult:'未验证',evidenceNote:'',evidenceAttachmentIds:[],updatedBy:'',updatedAt:''}}});await flushPromises()
    expect(wrapper.html()).not.toContain('旧项目机密分类')
  })
  it('读取失败展示中文，不暴露网络英文异常',async()=>{
    api.requestJson.mockRejectedValue(new Error('ECONNRESET stack internal'))
    const wrapper=await create(QualityIssuePanel,{token:'token',projectId:'project-1',issueId:'issue-1'})
    expect(wrapper.get('[role="alert"]').text()).toContain('读取失败');expect(wrapper.text()).not.toContain('ECONNRESET')
  })
  it('总结草稿提交保存包含期间和目标结果，已发布快照与当前状态分别显示',async()=>{
    const result=project();result.canEdit=true;result.summaries=[{id:'summary-1',projectId:'project-1',revision:1,kind:'阶段总结',state:'草稿',title:'设计阶段总结',achievements:'完成设计',lessons:'选型检查不足',remainingPlan:'落实新检查表',coverageFrom:'2026-11-01',coverageTo:'2026-11-10',targetResults:'计划完成，预算未超支',createdBy:'account-a',createdAt:'2026-11-11',reviewedBy:null,publishedAt:null,issues:[{issueId:issue.id,number:1,projectId:'project-1',projectCode:'P001',description:issue.description,status:'待处理',ownerName:'张工',solution:'',profile:null,sourceRowVersion:1}]}];api.requestJson.mockResolvedValue(result)
    const wrapper=await create(ProjectRetrospective,{token:'token',projectId:'project-1',projectCode:'P001'})
    expect(wrapper.text()).toContain('待处理');expect(wrapper.text()).toContain('进行中')
    await click('编辑');await wrapper.get('form').trigger('submit');await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(call[0]).toBe('/api/projects/project-1/retrospectives/summary-1');expect(JSON.parse(call[1].body)).toMatchObject({coverageFrom:'2026-11-01',coverageTo:'2026-11-10',targetResults:'计划完成，预算未超支',expectedRowVersion:7})
  })
  it('经验采用只能确认待检查记录，使用后端已采用状态',async()=>{
    const result=project();result.canAdopt=true;result.adoptions=[{id:'adopt-1',experienceId:'exp-1',projectId:'project-1',owner:'account-a',dueOn:'2026-11-20',state:'待检查',evidence:''}];result.experiences=[{id:'exp-1',improvementId:'improve-1',title:'选型检查',content:'检查实际负载',applicability:'装配设备',publishedBy:'account-a',publishedAt:'2026-11-10',revision:1}];api.requestJson.mockResolvedValue(result)
    const wrapper=await create(ProjectRetrospective,{token:'token',projectId:'project-1'});await click('经验采用');await wrapper.get('textarea').setValue('已更新项目选型检查表');await click('确认落实')
    const call=api.requestJson.mock.calls.find(([url,init])=>String(url).endsWith('/confirm')&&init?.method==='POST')!
    expect(JSON.parse(call[1].body)).toMatchObject({state:'已采用',evidence:'已更新项目选型检查表',expectedRowVersion:7})
  })
  it('执行人员可以登记措施证据，已批准的措施定义和验证标准保持锁定',async()=>{
    const result=company();result.capabilities={canExecuteImprovements:true,canViewDetails:true};result.catalog.improvements=[{id:'improve-1',themeId:'theme-1',title:'增加负载检查',state:'实施中',owner:'account-a',rootCause:'忽略实际负载',goal:'减少误触发',dueOn:'2026-12-01',minimumSamples:3,observationDays:30,maximumFailures:0,actions:[{id:'action-1',title:'修订选型表',owner:'account-a',dueOn:'2026-11-20',completed:false,evidence:''}],observations:[],observationStartedAt:null,createdBy:'account-b',createdAt:'2026-11-11',history:[]}];api.requestJson.mockResolvedValue(result)
    await create(QualityReviewWorkbench,{token:'token'});await click('改善事项');await click('更新措施')
    const dialog=document.querySelector('.el-dialog')!
    const title=[...dialog.querySelectorAll('input')].find(input=>input.value==='修订选型表')!
    expect(title.disabled).toBe(true)
    const add=[...dialog.querySelectorAll('button')].find(button=>button.textContent?.trim()==='新增措施')!
    expect(add.disabled).toBe(true)
    const evidence=[...dialog.querySelectorAll('textarea')].at(-1)!
    evidence.value='检查表已发布R2';evidence.dispatchEvent(new Event('input',{bubbles:true}));const checkbox=dialog.querySelector<HTMLInputElement>('input[type=checkbox]')!;checkbox.checked=true;checkbox.dispatchEvent(new Event('change',{bubbles:true}));await flushPromises()
    dialog.querySelector('form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await flushPromises()
    const call=api.requestJson.mock.calls.find(([,init])=>init?.method==='PUT')!
    expect(JSON.parse(call[1].body)).toMatchObject({minimumSamples:3,observationDays:30,actions:[{title:'修订选型表',completed:true,evidence:'检查表已发布R2'}]})
  })
  it('经验修订使用新记录标识，保留适用标签并且不覆盖历史版本',async()=>{
    const result=company();result.capabilities={canPublishExperience:true};result.catalog.experiences=[{id:'old-exp',improvementId:'improve-1',title:'选型检查',content:'检查实际负载',applicability:'装配设备',publishedBy:'account-a',publishedAt:'2026-11-10',revision:1,projectTags:['装配']}];api.requestJson.mockResolvedValue(result)
    await create(QualityReviewWorkbench,{token:'token'});await click('经验复用');await click('修订发布');document.querySelector('.el-dialog form')!.dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await flushPromises()
    const call=api.requestJson.mock.calls.find(([url,init])=>String(url).includes('/experiences/')&&init?.method==='PUT')!
    expect(call[0]).not.toBe('/api/quality-review/experiences/old-exp');expect(JSON.parse(call[1].body)).toMatchObject({projectTags:['装配'],improvementId:'improve-1'})
  })
  it('子项目筛选按问题实际关联子项目匹配',async()=>{
    const result=company();result.projects.push({id:'child-1',code:'P001-1',name:'子设备',rootProjectId:'project-1'});result.issues=[{...row,issue:{...issue,relatedProjectId:'child-1'}}];api.requestJson.mockResolvedValue(result)
    const wrapper=await create(QualityReviewWorkbench,{token:'token'});await wrapper.get('select').setValue('child-1')
    expect(wrapper.text()).toContain('传感器误触发');expect(wrapper.text()).not.toContain('没有符合条件的问题')
    await click('原问题');expect(wrapper.emitted('openIssue')?.at(-1)).toEqual(['child-1','issue-1'])
  })
  it('仅有总结查看权限时不把隐藏的遗留问题报告为没有问题',async()=>{
    const result=project();result.issues=[];result.canViewIssues=false;api.requestJson.mockResolvedValue(result)
    const wrapper=await create(ProjectRetrospective,{token:'token',projectId:'project-1'});await click('遗留跟踪')
    expect(wrapper.text()).toContain('无法判断遗留问题数量');expect(wrapper.text()).not.toContain('当前没有未关闭')
  })
})
