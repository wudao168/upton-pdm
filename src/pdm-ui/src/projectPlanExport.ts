import type { ProjectPlan, ProjectPlanTask } from './types'
import { downloadProcurementWorkbook } from './procurementWorkbook'

export type ProjectPlanExportItem = {
  projectCode: string
  projectName: string
  plan: ProjectPlan
  inherited?: boolean
}

const headers = ['项目编码', '项目名称', '计划来源', '计划模板', '审批状态', '阶段', '任务', '责任人', '进度', '计划开始', '计划完成', '工期（天）', '实际开始', '实际完成', '基线开始', '基线完成']
const approvalLabels: Record<string, string> = { Draft: '草稿', Pending: '待审批', Rejected: '已退回', Approved: '已生效' }

function taskRow(item: ProjectPlanExportItem, task: ProjectPlanTask) {
  return [
    item.projectCode, item.projectName, item.inherited ? '跟随主项目计划' : '独立计划', item.plan.templateName,
    approvalLabels[item.plan.approvalStatus ?? 'Draft'] ?? item.plan.approvalStatus ?? '草稿',
    item.plan.stages?.find(stage => stage.code === task.stage)?.name ?? task.stage,
    task.name, task.assignee ?? '—', `${task.completionPercent}%`, task.plannedStart, task.plannedFinish, task.durationDays,
    task.actualStart ?? '—', task.actualFinish ?? '—', task.baselineStart ?? '—', task.baselineFinish ?? '—',
  ]
}

export function projectPlanExportTable(items: ProjectPlanExportItem[]) {
  return { headers, rows: items.flatMap(item => [...item.plan.tasks].sort((left, right) => left.sortOrder - right.sortOrder).map(task => taskRow(item, task))) }
}

function safeName(value: string) {
  return value.replace(/[\\/:*?"<>|]/g, '_')
}

function stamp() {
  const now = new Date()
  return `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}_${String(now.getHours()).padStart(2, '0')}${String(now.getMinutes()).padStart(2, '0')}`
}

export function exportProjectPlansExcel(rootProjectCode: string, items: ProjectPlanExportItem[]) {
  const table = projectPlanExportTable(items)
  downloadProcurementWorkbook(`${safeName(rootProjectCode)}_项目计划_${stamp()}.xlsx`, table.headers, table.rows, '项目计划')
}

function html(value: unknown) {
  return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

export function exportProjectPlansPdf(rootProjectCode: string, items: ProjectPlanExportItem[]) {
  const table = projectPlanExportTable(items)
  const popup = window.open('', '_blank')
  if (!popup) throw new Error('浏览器已拦截 PDF 打印窗口，请允许弹出窗口后重试')
  popup.opener = null
  const title = `${safeName(rootProjectCode)} 项目计划`
  popup.document.write(`<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>${html(title)}</title><style>@page{size:A4 landscape;margin:10mm}body{font:11px/1.45 "Microsoft YaHei","SimSun",sans-serif;color:#172033}h1{font-size:18px;margin:0 0 4px}p{margin:0 0 12px;color:#64748b}table{width:100%;border-collapse:collapse;table-layout:fixed}th,td{border:1px solid #cbd5e1;padding:4px 5px;overflow-wrap:anywhere}th{background:#eff6ff}tr{break-inside:avoid}</style></head><body><h1>${html(title)}</h1><p>导出项目：${items.map(item => `${html(item.projectCode)} · ${html(item.projectName)}`).join('、')}</p><table><thead><tr>${table.headers.map(header => `<th>${html(header)}</th>`).join('')}</tr></thead><tbody>${table.rows.map(row => `<tr>${row.map(cell => `<td>${html(cell)}</td>`).join('')}</tr>`).join('')}</tbody></table><script>window.addEventListener('load',()=>{window.print()})<\/script></body></html>`)
  popup.document.close()
}
