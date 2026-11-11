import { describe, expect, it } from 'vitest'
import { buildReportingRows, drawingStats, knownSum, rate, type ReportingData } from '../src/projectReporting'
import type { WorkbenchProject } from '../src/projectWorkbench'
function fixture():ReportingData {
 const project={id:'p',code:'P001',name:'测试项目',customerCode:'C1',customerName:'客户一',executionUnitName:'事业部一',canReadContent:true,deliveryDate:'2026-11-05',primaryProjectManager:'经理',collaborativeProjectManagers:[],designers:[],responsibleUsers:[],serialNumbers:[]}
 const task={id:'t',name:'设计',stage:'Design',plannedStart:'2026-11-01',plannedFinish:'2026-11-05',actualFinish:'2026-11-04',status:'Completed',completionPercent:100,weight:1,predecessorTaskIds:[],isRequired:true,isMilestone:false,sortOrder:0}
 const entry={project,plan:{id:'plan',currentStage:'Design',approvalStatus:'Approved',tasks:[task],stages:[{code:'Design',name:'设计',participatesInDelivery:true}]},planState:'Approved',warnings:[],budget:null,quality:null,procurement:null,completionPercent:100,canRegisterRisk:true} as unknown as WorkbenchProject
 return {canViewCompany:true,scopeLabel:'公司',workbench:{views:[],view:'',updatedAt:'2026-11-08T00:00:00Z',projects:[entry],risks:[]},details:[{projectId:'p',finance:{settlement:100,budget:80,actual:90,estimated:110,expenses:[]},stageEnteredAt:null,drawings:[],warnings:[]}]}
}
const rows=(data:ReportingData)=>buildReportingRows(data,'2026-11-08','2026-11-01','2026-11-08')
describe('经营报表真实统计边界',()=>{
 it('到期未完成计入分母，未来任务不计入，缺少日期不计准时',()=>{const d=fixture();expect(rate(rows(d),'deliveryResult').value).toBe(100);d.workbench.projects[0]!.plan!.tasks[0]!.actualFinish=undefined;expect(rate(rows(d),'deliveryResult')).toMatchObject({onTime:0,total:1,value:0});d.workbench.projects[0]!.project.deliveryDate='2026-11-20';expect(rate(rows(d),'deliveryResult').value).toBeNull()})
 it('无交付阶段不得用设计或末节点冒充交付结果',()=>{const d=fixture();d.workbench.projects[0]!.plan!.stages![0]!.participatesInDelivery=false;expect(rate(rows(d),'deliveryResult').total).toBe(0);expect(rows(d)[0]!.issues).toContain('交付节点待补充')})
 it('取消项目不计准时率，草稿计划不能算有效节点',()=>{const d=fixture();d.workbench.projects[0]!.project.businessStatus='Cancelled';expect(rate(rows(d),'designResult').total).toBe(0);d.workbench.projects[0]!.project.businessStatus='';d.workbench.projects[0]!.plan!.approvalStatus='Draft';expect(rate(rows(d),'designResult').value).toBeNull()})
 it('母子项目去重，金额不重复加总，无金额不回填零',()=>{const d=fixture();const child=structuredClone(d.workbench.projects[0]!);child.project.id='c';child.project.parentProjectId='p';d.workbench.projects.push(child);expect(rows(d)).toHaveLength(1);expect(rows(d)[0]!.finance?.settlement).toBe(100);expect(knownSum([null,undefined])).toBeNull();expect(knownSum([null,0])).toBe(0)})
 it('首发不算修改，同图多次修改只计一张，期间边界正确',()=>{const d=fixture();d.details[0]!.drawings=[{id:'drawing',name:'D001',firstPublishedAt:'2026-10-01T00:00:00Z',changes:[{at:'2026-11-02T00:00:00Z',revision:'B',reasons:['客户变更']},{at:'2026-11-03T00:00:00Z',revision:'C',reasons:['设计优化']}]}];const result=drawingStats(rows(d),'2026-11-01','2026-11-08');expect(result).toMatchObject({total:1,changed:1,value:100});expect(result.changes).toHaveLength(2);expect(drawingStats(rows(d),'2026-10-01','2026-10-31').value).toBe(0)})
 it('未知客户不冒充一家客户，跨事业部筛选数据保持标识',()=>{const d=fixture();d.workbench.projects[0]!.project.customerCode=undefined;expect(rows(d)[0]!.customerKey).toBe('')})
})
