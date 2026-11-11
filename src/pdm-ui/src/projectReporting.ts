import type { WorkbenchData, WorkbenchTab } from './projectWorkbench'
import { effectiveTasks, addDays, validDate } from './projectWorkbench'
import { buildProjectReport, type ReportProject } from './projectReport'
export interface ReportFinance { settlement: number|null; budget: number|null; actual: number|null; estimated: number|null; expenses: {category:string;budget:number|null;actual:number|null;estimated:number|null}[] }
export interface ReportDrawing { id:string;name:string;firstPublishedAt:string;changes:{at:string;revision:string;reasons:string[]}[] }
export interface ReportingData { workbench:WorkbenchData;canViewCompany:boolean;scopeLabel:string;details:{projectId:string;finance:ReportFinance|null;stageEnteredAt:string|null;drawings:ReportDrawing[]|null;warnings:string[]}[] }
export interface ReportRow extends ReportProject { customerKey:string;unitId:string;finance:ReportFinance|null;deliveryResult:Result;designResult:Result;stageDays:number|null;drawings:ReportDrawing[]|null;issues:string[];riskLabels:string[] }
export interface Result { due:string|null;actual:string|null;status:'on-time'|'late'|'pending'|'unknown'; eligible:boolean }
export const businessDay=(value:string)=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(value))
export const money=(value:number|null|undefined)=>value==null?'暂无数据':new Intl.NumberFormat('zh-CN',{minimumFractionDigits:2,maximumFractionDigits:2}).format(value)
export const expenseNames:Record<string,string>={Standard:'标准件',Nonstandard:'非标件',Equipment:'外购设备',Logistics:'物流费用',Other:'其他费用',MechanicalDesign:'机械设计',ElectricalDesign:'电气设计',InternalCommissioning:'装配调试',SiteElectrical:'现场电气',SiteCommissioning:'厂外调试',Electrical:'电气',Design:'设计',Assembly:'装配',Commissioning:'调试',Travel:'差旅',RiskReserve:'风险预留'}
export const knownSum=(values:(number|null|undefined)[])=>values.some(v=>v!=null)?values.reduce<number>((s,v)=>s+(v??0),0):null
export function buildReportingRows(data:ReportingData,today:string,from:string,to:string):ReportRow[]{
 const report=buildProjectReport(data.workbench,'',today)
 return report.projects.map(row=>{
  const root=data.workbench.projects.find(p=>p.project.id===row.id)!
  const detail=data.details.find(d=>d.projectId===row.id)
  const tasks=[...new Map(data.workbench.projects.filter(p=>row.projectIds.includes(p.project.id)).flatMap(p=>effectiveTasks(p)).sort((a,b)=>Number(!!b.sourceTaskId)-Number(!!a.sourceTaskId)).map(t=>[t.sourceTaskId||t.id,t])).values()]
  const closed=['cancelled','terminated'].includes(row.state)
  const outcome=(subset:typeof tasks,override?:string|null):Result=>{
   const dated=subset.filter(t=>validDate(t.plannedFinish))
   const due=override||(dated.length===subset.length?dated.map(t=>t.plannedFinish).sort().at(-1):null)||null
   const actual=subset.length&&subset.every(t=>t.actualFinish&&validDate(t.actualFinish.slice(0,10)))?subset.map(t=>t.actualFinish!.slice(0,10)).sort().at(-1)!:null
   const eligible=!closed&&!!due&&due>=from&&due<=to&&due<=today
   const status=!due||!subset.length?'unknown':actual?(actual<=due?'on-time':'late'):due<today?'late':'pending'
   return {due,actual,status,eligible:eligible&&subset.length>0}
  }
  const deliveryStages=new Set(data.workbench.projects.filter(p=>row.projectIds.includes(p.project.id)).flatMap(p=>(p.plan?.stages||[]).filter(s=>s.participatesInDelivery===true).map(s=>s.code)))
  const deliveryResult=outcome(tasks.filter(t=>deliveryStages.has(t.stage)),row.deliveryDate)
  const designResult=outcome(tasks.filter(t=>t.stage==='Design'||['design.mechanical','drawing.review'].includes(t.workflowKey||'')))
  const stageDays=detail?.stageEnteredAt?Math.max(0,Math.floor((Date.parse(today+'T00:00:00+08:00')-Date.parse(detail.stageEnteredAt))/86400000)):null
  const finance=detail?.finance??null
  const issues=[...new Set([...row.warnings,...detail?.warnings||[],...(!root.project.customerCode?['客户编码待补充']:[]),...(!row.deliveryDate?['交期待补充']:[]),...(!root.plan||root.plan.approvalStatus!=='Approved'?['生效计划待补充']:[]),...(finance?.settlement==null?['结算金额缺失或未授权']:[]),...(finance?.budget==null?['预算缺失或未授权']:[]),...(deliveryResult.status==='unknown'?['交付节点待补充']:[]),...(designResult.status==='unknown'?['设计节点待补充']:[])])]
  const riskLabels=[...(deliveryResult.status==='late'?['交货逾期']:[]),...(designResult.status==='late'?['设计逾期']:[]),...(finance?.budget!=null&&finance.actual!=null&&finance.actual>finance.budget?['实际超预算']:[]),...(finance?.budget!=null&&finance.estimated!=null&&finance.estimated>finance.budget?['预计超预算']:[]),...(row.openRisks?['登记风险']:[])]
  return {...row,customerKey:root.project.customerCode?.trim().toLowerCase()||'',unitId:root.project.executionUnitId||'',finance,deliveryResult,designResult,stageDays,drawings:detail?.drawings??null,issues,riskLabels}
 })
}
export const rate=(rows:ReportRow[],key:'deliveryResult'|'designResult')=>{const sample=rows.filter(r=>r[key].eligible);const onTime=sample.filter(r=>r[key].status==='on-time').length;return {total:sample.length,onTime,value:sample.length?onTime/sample.length*100:null}}
export function drawingStats(rows:ReportRow[],from:string,to:string){const drawings=[...new Map(rows.flatMap(r=>r.drawings||[]).filter(d=>businessDay(d.firstPublishedAt)<=to).map(d=>[d.id,d])).values()];const changes=drawings.flatMap(d=>d.changes.filter(c=>businessDay(c.at)>=from&&businessDay(c.at)<=to).map(c=>({...c,id:d.id})));return {total:drawings.length,changed:new Set(changes.map(c=>c.id)).size,changes,value:drawings.length?new Set(changes.map(c=>c.id)).size/drawings.length*100:null}}
export function nextDue(rows:ReportRow[],today:string,days:number){return rows.filter(r=>!['completed','cancelled','terminated'].includes(r.state)&&r.deliveryDate&&r.deliveryDate>=today&&r.deliveryDate<=addDays(today,days))}
export const targetTab=(kind:string):WorkbenchTab=>kind==='预算控制'?'budget':kind==='图纸变更'?'release':'project-plan'
