import { describe, expect, it } from 'vitest'
import { projectAttentionDetails, projectAttentionReasons, projectRiskRows, releaseFailureReasons } from '../src/projectAttention'
import type { WorkbenchProject } from '../src/projectWorkbench'
import type { ProjectPlanTask, ReleasePackageSummary } from '../src/types'

const today = '2026-11-07'
const task = (id: string, patch: Partial<ProjectPlanTask> = {}): ProjectPlanTask => ({ id, name:id, stage:'Design', plannedStart:'2026-11-01', plannedFinish:'2026-11-10', status:'NotStarted', completionPercent:0, predecessorTaskIds:[], durationDays:1, weight:1, isMilestone:false, isRequired:true, sortOrder:0, ...patch })
const item = (tasks: ProjectPlanTask[] = []): WorkbenchProject => ({
  project:{ id:'p', code:'P1', name:'项目', owner:'pm', stage:'进行中', vaultName:'P1', vaultLocation:'D:/PDM/P1', releaseLocation:'D:/Release/P1', quantity:1, serialNumbers:[], responsibleUsers:[], collaborativeProjectManagers:[], designers:[], canAssignExecutionUnit:false, canManageMainStaffing:false, canAssignDesigners:false, canReadContent:true },
  plan:{ id:'plan', projectId:'p', templateId:'', templateName:'', currentStage:'Design', plannedStart:'2026-11-01', plannedFinish:'2026-11-10', forecastFinish:'2026-11-10', baselineVersion:1, approvalStatus:'Approved', tasks, createdBy:'pm', createdAt:'', updatedBy:'pm', updatedAt:'', rowVersion:1 },
  planState:'Approved', completionPercent:0, budget:null, procurement:null, quality:null, warnings:[], canRegisterRisk:false,
})
const release = (previewState: string, state = '已发布') => ({ id:'r', scope:'NonStandardWithDrawing', locksDocuments:true, state, previewState } as ReleasePackageSummary)

describe('项目橙色色带的四类异常', () => {
  it('将每类异常关联到实际处理页签，正常后同时移除原因与关联', () => {
    const value = item([task('late', { plannedFinish:'2026-11-06' })])
    value.project.deliveryDate = '2026-11-06'
    value.procurement = { published:true, pendingOrder:0, missing:0, delayed:2, refreshedAt:null, syncError:null }
    expect(projectAttentionDetails(value, today, [release('Failed')])).toEqual([
      { tab:'project-plan', reason:'生效计划任务逾期 1 项' },
      { tab:'project-plan', reason:'项目交期逾期（2026-11-06）' },
      { tab:'procurement', reason:'采购交付延期 2 项' },
      { tab:'release', reason:'发布／转图失败 1 项' },
    ])
    value.plan!.tasks[0]!.status = 'Completed'
    value.project.deliveryDate = '2026-11-10'
    value.procurement.delayed = 0
    expect(projectAttentionDetails(value, today, [release('Succeeded')])).toEqual([])
  })
  it('仅生效计划的未完成任务逾期，今日到期及已完成任务不误报', () => {
    const value=item([task('late',{plannedFinish:'2026-11-06'}),task('today',{plannedFinish:today}),task('done',{plannedFinish:'2026-11-05',status:'Completed'}),task('actual',{plannedFinish:'2026-11-05',actualFinish:'2026-11-06'})])
    expect(projectAttentionReasons(value,today)).toEqual(['生效计划任务逾期 1 项'])
    value.plan!.approvalStatus='Pending';expect(projectAttentionReasons(value,today)).toEqual([])
  })
  it('只有已经到开始日期且前置未完成的任务才算阻塞', () => {
    const value=item([task('before'),task('blocked',{predecessorTaskIds:['before']}),task('future',{plannedStart:'2026-11-08',predecessorTaskIds:['before']})])
    expect(projectAttentionReasons(value,today)).toEqual(['生效计划任务阻塞 1 项'])
    value.plan!.tasks[0]!.status='Completed';expect(projectAttentionReasons(value,today)).toEqual([])
  })
  it('项目约定交期独立判断，草稿计划的估算交期不算异常，已完成项目不报逾期', () => {
    const value=item();value.plan!.approvalStatus='Draft';value.plan!.plannedFinish='2026-11-01'
    expect(projectAttentionReasons(value,today)).toEqual([])
    value.project.deliveryDate='2026-11-06';expect(projectAttentionReasons(value,today)).toEqual(['项目交期逾期（2026-11-06）'])
    value.project.stage='已完成';expect(projectAttentionReasons(value,today)).toEqual([])
  })
  it('采购仅交付延期触发，未齐套、验收失败、预算超支与普通待办不触发', () => {
    const value=item();value.procurement={published:true,pendingOrder:3,missing:5,delayed:0,refreshedAt:null,syncError:null};value.quality={state:'Effective',total:1,pending:0,failed:1,records:0};value.budget={budget:1,actual:5,ordered:null,estimated:5,variance:4}
    expect(projectAttentionReasons(value,today)).toEqual([])
    value.procurement.delayed=2;expect(projectAttentionReasons(value,today)).toEqual(['采购交付延期 2 项'])
  })
  it('转图排队、运行和等待服务器均不报失败；实际转图或发布失败才触发', () => {
    for(const state of ['Pending','Running','WaitingForServer','Succeeded'])expect(releaseFailureReasons([release(state)])).toEqual([])
    expect(releaseFailureReasons([release('Failed')])).toEqual(['发布／转图失败 1 项'])
    expect(releaseFailureReasons([release('None','发布失败')])).toEqual(['发布／转图失败 1 项'])
    expect(releaseFailureReasons([{...release('Failed'),scope:'StandardFormal'}])).toEqual([])
    expect(releaseFailureReasons([release('Failed','已撤回')])).toEqual([])
  })
  it('按任务和发布包逐条返回明细，同一任务同时逾期和阻塞时只占一行', () => {
    const first = task('before', { name: '机械设计' })
    const second = task('after', { name: '内部评审', plannedFinish: '2026-11-05', predecessorTaskIds: ['before'] })
    const value = item([first, second])
    const rows = projectRiskRows([{ ...value, procurement: null, incomplete: false, releases: [
      { ...release('None', 'PublishFailed'), id: 'pkg-one', number: 'RP-01', createdAt: '2026-11-01T00:00:00Z' },
      { ...release('Failed', 'Published'), id: 'pkg-two', number: 'RP-02', createdAt: '2026-11-02T00:00:00Z' },
    ] }], today)
    expect(rows.map(row => [row.subject, row.reason])).toEqual([
      ['内部评审', '计划任务逾期、阻塞'], ['RP-01', '发布失败'], ['RP-02', '转图失败'],
    ])
    expect(rows[0]!.fields).toContainEqual({ label: '未完成的前置任务', value: '机械设计' })
  })
  it('无计划且无真实异常时不生成橙色提醒', () => { const value=item();value.plan=null;expect(projectAttentionReasons(value,today)).toEqual([]) })
  it('进度100%不代替正式完成状态，也不代表已经按期交付', () => {
    const value=item([task('late',{plannedFinish:'2026-11-06',completionPercent:100})]);value.completionPercent=100;value.project.deliveryDate='2026-11-06'
    expect(projectAttentionReasons(value,today)).toEqual(['生效计划任务逾期 1 项','项目交期逾期（2026-11-06）'])
  })
})
