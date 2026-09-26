import { strToU8, zipSync } from 'three/examples/jsm/libs/fflate.module.js'
import type { ProjectPlan, ProjectPlanTask } from './types'

export type ProjectPlanExportMetadata = {
  companyName?: string
  customerName?: string
  projectType?: string
  deviceModel?: string
  serialNumbers?: string[]
  executionUnitName?: string
  projectManager?: string
  designLead?: string
  engineers?: string[]
}
export type ProjectPlanExportItem = { projectCode: string; projectName: string; plan: ProjectPlan; inherited?: boolean; assigneeDisplayNames?: Record<string, string>; metadata?: ProjectPlanExportMetadata }
type GanttRow = { kind: 'project' | 'stage' | 'task'; label: string; assignee: string; progress: number; start: string; finish: string; milestone?: boolean }
type GanttModel = { start: string; finish: string; days: number; rows: GanttRow[]; shippingDate: string; today: string }

const detailHeaders = ['项目编码', '项目名称', '计划来源', '计划模板', '审批状态', '阶段', '任务', '责任人', '进度', '计划开始', '计划完成', '工期（天）', '实际开始', '实际完成', '基线开始', '基线完成']
const approvalLabels: Record<string, string> = { Draft: '草稿', Pending: '待审批', Rejected: '已退回', Approved: '已生效' }
const spreadsheetNamespace = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'

function isoDate(date: Date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}` }
function dateValue(value: string) { const [year, month, day] = value.split('-').map(Number); return new Date(year!, month! - 1, day) }
function dayDiff(start: string, finish: string) { return Math.round((dateValue(finish).getTime() - dateValue(start).getTime()) / 86_400_000) }
function addDays(value: string, days: number) { const date = dateValue(value); date.setDate(date.getDate() + days); return isoDate(date) }
function deliveryFinish(plan: ProjectPlan) {
  const deliveryStages = new Set((plan.stages ?? []).filter(stage => stage.participatesInDelivery !== false).map(stage => stage.code))
  return plan.tasks.filter(task => !plan.stages?.length || deliveryStages.has(task.stage)).map(task => task.plannedFinish).filter(Boolean).sort().at(-1) ?? plan.plannedFinish
}
function assigneeName(item: ProjectPlanExportItem, username?: string) { return username ? item.assigneeDisplayNames?.[username] ?? username : '—' }
function taskRow(item: ProjectPlanExportItem, task: ProjectPlanTask) {
  return [item.projectCode, item.projectName, item.inherited ? '跟随主项目计划' : '独立计划', item.plan.templateName,
    approvalLabels[item.plan.approvalStatus ?? 'Draft'] ?? item.plan.approvalStatus ?? '草稿', item.plan.stages?.find(stage => stage.code === task.stage)?.name ?? task.stage,
    task.name, assigneeName(item, task.assignee), `${task.completionPercent}%`, task.plannedStart, task.plannedFinish, task.durationDays,
    task.actualStart ?? '—', task.actualFinish ?? '—', task.baselineStart ?? '—', task.baselineFinish ?? '—']
}
export function projectPlanExportTable(items: ProjectPlanExportItem[]) {
  return { headers: detailHeaders, rows: items.flatMap(item => [...item.plan.tasks].sort((left, right) => left.sortOrder - right.sortOrder).map(task => taskRow(item, task))) }
}

export function projectPlanGanttModel(items: ProjectPlanExportItem[]): GanttModel {
  const values = items.flatMap(item => item.plan.tasks.flatMap(task => [task.plannedStart, task.plannedFinish]))
  const fallback = isoDate(new Date())
  const first = values.filter(Boolean).sort()[0] ?? fallback
  const last = values.filter(Boolean).sort().at(-1) ?? first
  const start = addDays(first, -2)
  const finish = addDays(last, 2)
  const rows: GanttRow[] = []
  for (const item of items) {
    const sortedTasks = [...item.plan.tasks].sort((left, right) => left.sortOrder - right.sortOrder)
    rows.push({ kind: 'project', label: `${item.projectCode} · ${item.projectName}`, assignee: '', progress: sortedTasks.length ? Math.round(sortedTasks.reduce((sum, task) => sum + task.completionPercent, 0) / sortedTasks.length) : 0, start: sortedTasks.map(task => task.plannedStart).sort()[0] ?? item.plan.plannedStart, finish: sortedTasks.map(task => task.plannedFinish).sort().at(-1) ?? item.plan.plannedFinish })
    const stageCodes = item.plan.stages?.map(stage => stage.code) ?? [...new Set(sortedTasks.map(task => task.stage))]
    for (const stageCode of stageCodes) {
      const tasks = sortedTasks.filter(task => task.stage === stageCode)
      if (!tasks.length) continue
      rows.push({ kind: 'stage', label: item.plan.stages?.find(stage => stage.code === stageCode)?.name ?? stageCode, assignee: '', progress: Math.round(tasks.reduce((sum, task) => sum + task.completionPercent, 0) / tasks.length), start: tasks.map(task => task.plannedStart).sort()[0]!, finish: tasks.map(task => task.plannedFinish).sort().at(-1)! })
      rows.push(...tasks.map(task => ({ kind: 'task' as const, label: task.name, assignee: assigneeName(item, task.assignee), progress: task.completionPercent, start: task.plannedStart, finish: task.plannedFinish, milestone: task.isMilestone })))
    }
  }
  return { start, finish, days: dayDiff(start, finish) + 1, rows, shippingDate: items.map(item => deliveryFinish(item.plan)).filter(Boolean).sort().at(-1) ?? '', today: isoDate(new Date()) }
}

function safeName(value: string) { return value.replace(/[\\/:*?"<>|]/g, '_') }
function stamp() { const now = new Date(); return `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}_${String(now.getHours()).padStart(2, '0')}${String(now.getMinutes()).padStart(2, '0')}` }
function xml(value: unknown) { return String(value).replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;') }
function columnName(index: number) { let value = index + 1; let result = ''; while (value > 0) { const remainder = (value - 1) % 26; result = String.fromCharCode(65 + remainder) + result; value = Math.floor((value - 1) / 26) } return result }
function excelCell(row: number, column: number, value: unknown, style: number) {
  const reference = `${columnName(column)}${row}`
  return typeof value === 'number' && Number.isFinite(value) ? `<c r="${reference}" s="${style}"><v>${value}</v></c>` : `<c r="${reference}" s="${style}" t="inlineStr"><is><t xml:space="preserve">${xml(value)}</t></is></c>`
}
type CalendarBand = { label: string; start: number; end: number }
function calendarBands(model: GanttModel, part: 'year' | 'month') {
  const bands: Array<CalendarBand & { key: string }> = []
  for (let day = 0; day < model.days; day += 1) {
    const date = dateValue(addDays(model.start, day))
    const key = part === 'year' ? String(date.getFullYear()) : `${date.getFullYear()}-${date.getMonth()}`
    const label = part === 'year' ? `${date.getFullYear()}年` : `${date.getMonth() + 1}月`
    const previous = bands.at(-1)
    if (previous?.key === key) previous.end = day
    else bands.push({ label, start: day, end: day, key })
  }
  return bands.map(({ label, start, end }) => ({ label, start, end }))
}
function itemMetadataText(item: ProjectPlanExportItem) {
  const meta = item.metadata
  return [`项目：${item.projectCode} · ${item.projectName}`, meta?.customerName ? `客户：${meta.customerName}` : '', meta?.projectType ? `类型：${meta.projectType}` : '', meta?.deviceModel ? `型号：${meta.deviceModel}` : '', meta?.serialNumbers?.length ? `序列号：${meta.serialNumbers.join('、')}` : '', meta?.executionUnitName ? `事业部：${meta.executionUnitName}` : '', meta?.projectManager ? `项目经理：${meta.projectManager}` : '', meta?.designLead ? `主设：${meta.designLead}` : '', meta?.engineers?.length ? `工程师：${meta.engineers.join('、')}` : ''].filter(Boolean).join('　')
}
function companyName(items: ProjectPlanExportItem[]) { return items.find(item => item.metadata?.companyName)?.metadata?.companyName ?? '项目计划' }

const baseStyles = [
  { name: 'body', font: 0, fill: 0, align: 'left' }, { name: 'header', font: 1, fill: 2, align: 'center' },
  { name: 'title', font: 2, fill: 0, align: 'left' }, { name: 'project', font: 3, fill: 6, align: 'left' },
  { name: 'stage', font: 3, fill: 7, align: 'left' }, { name: 'task', font: 0, fill: 0, align: 'left' },
  { name: 'remaining', font: 0, fill: 3, align: 'center' }, { name: 'progress', font: 0, fill: 4, align: 'center' },
  { name: 'milestone', font: 0, fill: 5, align: 'center' },
] as const
const styleIndex = new Map<string, number>()
for (const marker of ['none', 'today', 'shipping']) for (const base of baseStyles) styleIndex.set(`${base.name}:${marker}`, styleIndex.size)
const style = (base: typeof baseStyles[number]['name'], marker: 'none' | 'today' | 'shipping' = 'none') => styleIndex.get(`${base}:${marker}`)!

function workbookStyles() {
  const xfs = [...styleIndex.keys()].map(key => { const [name, marker] = key.split(':'); const base = baseStyles.find(item => item.name === name)!; const borderId = marker === 'today' ? 1 : marker === 'shipping' ? 2 : 0; return `<xf numFmtId="0" fontId="${base.font}" fillId="${base.fill}" borderId="${borderId}" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="${base.align}" vertical="center"/></xf>` }).join('')
  return `<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="${spreadsheetNamespace}"><fonts count="4"><font><sz val="10"/><name val="Arial"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="10"/><name val="Arial"/></font><font><b/><sz val="14"/><name val="Arial"/></font><font><b/><sz val="10"/><name val="Arial"/></font></fonts><fills count="8"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDBEAFE"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FF2563EB"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFF59E0B"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFE2E8F0"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEFF6FF"/></patternFill></fill></fills><borders count="3"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style="medium"><color rgb="FFDC2626"/></left><right/><top/><bottom/><diagonal/></border><border><left style="medium"><color rgb="FF15803D"/></left><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="${styleIndex.size}">${xfs}</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`
}
function detailSheet(items: ProjectPlanExportItem[]) {
  const table = projectPlanExportTable(items)
  const rows = [table.headers, ...table.rows].map((values, rowIndex) => `<row r="${rowIndex + 1}" ht="20" customHeight="1">${values.map((value, column) => excelCell(rowIndex + 1, column, value, rowIndex === 0 ? style('header') : style('body'))).join('')}</row>`).join('')
  return `<worksheet xmlns="${spreadsheetNamespace}"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews><cols>${detailHeaders.map((_, index) => `<col min="${index + 1}" max="${index + 1}" width="${index === 6 ? 28 : 16}" customWidth="1"/>`).join('')}</cols><sheetData>${rows}</sheetData><autoFilter ref="A1:P${table.rows.length + 1}"/></worksheet>`
}
function ganttSheet(items: ProjectPlanExportItem[]) {
  const model = projectPlanGanttModel(items); const infoHeaders = ['项目 / 阶段 / 任务', '类型', '责任人', '进度', '计划开始', '计划完成']; const rowXml: string[] = []
  const years = calendarBands(model, 'year'); const months = calendarBands(model, 'month')
  const lastColumn = columnName(model.days + 5)
  rowXml.push(`<row r="1" ht="24" customHeight="1">${excelCell(1, 0, companyName(items), style('title'))}</row>`)
  rowXml.push(`<row r="2" ht="24" customHeight="1">${excelCell(2, 0, `${items.map(item => item.projectCode).join('、')} 项目计划甘特图`, style('title'))}</row>`)
  rowXml.push(`<row r="3" ht="22" customHeight="1">${excelCell(3, 0, items.map(itemMetadataText).join('；'), style('body'))}</row>`)
  rowXml.push(`<row r="4" ht="20" customHeight="1">${excelCell(4, 0, `时间范围：${model.start} 至 ${model.finish}　计划模板：${items.map(item => item.plan.templateName).filter(Boolean).join('、') || '—'}　红线：今天　绿线：项目发货日期 ${model.shippingDate || '未排程'}`, style('body'))}</row>`)
  rowXml.push(`<row r="5" ht="18" customHeight="1">${infoHeaders.map((value, column) => excelCell(5, column, value, style('header'))).join('')}${years.map(band => excelCell(5, band.start + 6, band.label, style('header'))).join('')}</row>`)
  rowXml.push(`<row r="6" ht="18" customHeight="1">${months.map(band => excelCell(6, band.start + 6, band.label, style('header'))).join('')}</row>`)
  rowXml.push(`<row r="7" ht="18" customHeight="1">${Array.from({ length: model.days }, (_, day) => { const date = addDays(model.start, day); return excelCell(7, day + 6, date.slice(8), style('header', date === model.shippingDate ? 'shipping' : date === model.today ? 'today' : 'none')) }).join('')}</row>`)
  model.rows.forEach((item, index) => {
    const row = index + 8; const base = item.kind === 'project' ? 'project' : item.kind === 'stage' ? 'stage' : 'task'
    const values = [item.label, item.kind === 'project' ? '项目' : item.kind === 'stage' ? '阶段' : item.milestone ? '里程碑' : '任务', item.assignee, `${item.progress}%`, item.start, item.finish]
    const startOffset = dayDiff(model.start, item.start); const finishOffset = dayDiff(model.start, item.finish); const duration = Math.max(1, finishOffset - startOffset + 1); const progressFinish = startOffset + Math.ceil(duration * item.progress / 100) - 1
    const timeline = Array.from({ length: model.days }, (_, day) => { const date = addDays(model.start, day); const marker = date === model.shippingDate ? 'shipping' : date === model.today ? 'today' : 'none'; const barStyle = item.milestone && day === startOffset ? 'milestone' : day >= startOffset && day <= finishOffset ? day <= progressFinish ? 'progress' : 'remaining' : base; return excelCell(row, day + 6, '', style(barStyle, marker)) }).join('')
    rowXml.push(`<row r="${row}" ht="20" customHeight="1">${values.map((value, column) => excelCell(row, column, value, style(base))).join('')}${timeline}</row>`)
  })
  const bandMerge = (band: CalendarBand, row: number) => band.end > band.start ? `${columnName(band.start + 6)}${row}:${columnName(band.end + 6)}${row}` : ''
  const merges = [`A1:${lastColumn}1`, `A2:${lastColumn}2`, `A3:${lastColumn}3`, `A4:${lastColumn}4`, ...infoHeaders.map((_, column) => `${columnName(column)}5:${columnName(column)}7`), ...years.map(band => bandMerge(band, 5)), ...months.map(band => bandMerge(band, 6))].filter(Boolean)
  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="${spreadsheetNamespace}"><sheetViews><sheetView workbookViewId="0"><pane xSplit="6" ySplit="7" topLeftCell="G8" activePane="bottomRight" state="frozen"/></sheetView></sheetViews><cols><col min="1" max="1" width="26" customWidth="1"/><col min="2" max="2" width="9" customWidth="1"/><col min="3" max="3" width="12" customWidth="1"/><col min="4" max="4" width="8" customWidth="1"/><col min="5" max="6" width="12" customWidth="1"/><col min="7" max="${model.days + 6}" width="2.8" customWidth="1"/></cols><sheetData>${rowXml.join('')}</sheetData><mergeCells count="${merges.length}">${merges.map(reference => `<mergeCell ref="${reference}"/>`).join('')}</mergeCells></worksheet>`
}

export function createProjectPlansWorkbook(items: ProjectPlanExportItem[]) {
  const files: Record<string, string> = {
    '[Content_Types].xml': '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
    '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/workbook.xml': `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="${spreadsheetNamespace}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><bookViews><workbookView xWindow="0" yWindow="0" windowWidth="24000" windowHeight="12000"/></bookViews><sheets><sheet name="甘特图" sheetId="1" r:id="rId1"/><sheet name="项目计划" sheetId="2" r:id="rId2"/></sheets></workbook>`,
    'xl/_rels/workbook.xml.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>',
    'xl/styles.xml': workbookStyles(), 'xl/worksheets/sheet1.xml': ganttSheet(items), 'xl/worksheets/sheet2.xml': detailSheet(items),
  }
  return zipSync(Object.fromEntries(Object.entries(files).map(([name, content]) => [name, new Uint8Array(strToU8(content))])))
}
function downloadWorkbook(filename: string, bytes: Uint8Array) { const url = URL.createObjectURL(new Blob([new Uint8Array(bytes)], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })); const link = document.createElement('a'); link.href = url; link.download = filename; document.body.append(link); link.click(); link.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000) }
export function exportProjectPlansExcel(rootProjectCode: string, items: ProjectPlanExportItem[]) { downloadWorkbook(`${safeName(rootProjectCode)}_项目计划_${stamp()}.xlsx`, createProjectPlansWorkbook(items)) }

function percent(model: GanttModel, value: string) { return Math.max(0, Math.min(100, (dayDiff(model.start, value) / Math.max(1, model.days - 1)) * 100)) }
export function projectPlanPdfHtml(rootProjectCode: string, items: ProjectPlanExportItem[]) {
  const model = projectPlanGanttModel(items)
  const years = calendarBands(model, 'year')
  const months = calendarBands(model, 'month')
  const bandHtml = (bands: CalendarBand[], className: string) => bands.map(band => `<span class="calendar-band ${className}" style="left:${band.start / model.days * 100}%;width:${(band.end - band.start + 1) / model.days * 100}%">${band.label}</span>`).join('')
  const tickCount = Math.min(13, model.days)
  const ticks = Array.from({ length: tickCount }, (_, index) => { const day = Math.round(index * (model.days - 1) / Math.max(1, tickCount - 1)); return { label: addDays(model.start, day).slice(5), left: model.days === 1 ? 0 : day / (model.days - 1) * 100 } })
  const shippingOffset = model.shippingDate ? dayDiff(model.start, model.shippingDate) : -1
  const todayOffset = dayDiff(model.start, model.today)
  const shippingLeft = shippingOffset >= 0 && shippingOffset < model.days ? percent(model, model.shippingDate) : -1
  const todayLeft = todayOffset >= 0 && todayOffset < model.days ? percent(model, model.today) : -1
  const projectMeta = items.map(item => `<div class="project-meta"><strong>${xml(item.projectCode)} · ${xml(item.projectName)}</strong><span>客户：${xml(item.metadata?.customerName || '—')}</span><span>项目类型：${xml(item.metadata?.projectType || '—')}</span><span>型号：${xml(item.metadata?.deviceModel || '—')}</span><span>序列号：${xml(item.metadata?.serialNumbers?.join('、') || '—')}</span><span>事业部：${xml(item.metadata?.executionUnitName || '—')}</span><span>项目经理：${xml(item.metadata?.projectManager || '—')}</span><span>主设：${xml(item.metadata?.designLead || '—')}</span><span>工程师：${xml(item.metadata?.engineers?.join('、') || '—')}</span></div>`).join('')
  const rows = model.rows.map(row => { const left = percent(model, row.start); const finish = percent(model, row.finish); const width = Math.max(row.milestone ? 0 : .5, finish - left); const type = row.kind === 'project' ? '项目' : row.kind === 'stage' ? '阶段' : row.milestone ? '里程碑' : '任务'; const bar = row.milestone ? `<i class="milestone" style="left:${left}%" title="${xml(row.label)}"></i>` : `<i class="bar ${row.kind}" style="left:${left}%;width:${width}%"><b style="width:${row.progress}%"></b><em>${row.progress}%</em></i>`; return `<div class="gantt-row ${row.kind}"><div class="info"><strong>${xml(row.label)}</strong><span>${type}</span><span>${xml(row.assignee || '—')}</span><span>${row.progress}%</span><span>${row.start}</span><span>${row.finish}</span></div><div class="timeline">${bar}</div></div>` }).join('')
  return `<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>${xml(safeName(rootProjectCode))} 项目计划甘特图</title><style>@page{size:A4 landscape;margin:0}*{box-sizing:border-box}body{margin:0;padding:8mm;font:11px/1.35 Arial,"Microsoft YaHei",sans-serif;color:#1e293b;-webkit-print-color-adjust:exact;print-color-adjust:exact}.company{margin-bottom:1px;color:#475569;font-size:11px;font-weight:700}h1{margin:0 0 4px;font-size:16px}.project-meta{display:grid;grid-template-columns:1.25fr .9fr .7fr 1fr 1fr .9fr .9fr .8fr 1fr;margin-bottom:5px;padding:4px 6px;border:1px solid #cbd5e1;background:#f8fafc}.project-meta>*{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.project-meta span{padding-left:6px;border-left:1px solid #e2e8f0;color:#475569}p{margin:0 0 6px;color:#64748b}.legend{display:flex;gap:14px;margin:0 0 7px}.legend span{display:flex;align-items:center;gap:4px}.legend i{width:18px;height:6px;background:#dbeafe;border:1px solid #2563eb}.legend .done{background:#2563eb}.legend .mile{width:8px;height:8px;background:#f59e0b;transform:rotate(45deg)}.legend .today,.legend .shipping{width:2px;height:12px;border:0;background:#dc2626}.legend .shipping{background:#15803d}.gantt{position:relative;border:1px solid #cbd5e1}.gantt-head,.gantt-row{display:grid;grid-template-columns:430px minmax(0,1fr)}.gantt-head{height:50px;background:#eff6ff;border-bottom:1px solid #94a3b8;font-weight:700}.ticks,.timeline{position:relative;overflow:hidden;background-image:repeating-linear-gradient(90deg,transparent 0,transparent calc(7.6923% - 1px),#e2e8f0 calc(7.6923% - 1px),#e2e8f0 7.6923%)}.calendar-band{position:absolute;height:16px;display:grid;place-items:center;border-right:1px solid #cbd5e1;color:#334155;font-size:11px}.calendar-band.year{top:0;font-weight:700}.calendar-band.month{top:16px}.tick{position:absolute;bottom:5px;transform:translateX(-50%);font-size:11px;color:#475569}.tick:first-child{transform:none}.gantt-row{min-height:23px;border-bottom:1px solid #e2e8f0;break-inside:avoid}.gantt-row:last-child{border-bottom:0}.gantt-row.project{background:#e2e8f0}.gantt-row.stage{background:#eff6ff}.info{display:grid;grid-template-columns:minmax(120px,1fr) 42px 58px 42px 70px 70px;align-items:center;border-right:1px solid #cbd5e1}.info>*{min-width:0;padding:3px 4px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.info strong{text-align:left}.task .info strong{padding-left:16px;font-weight:400}.info span{color:#475569;text-align:center}.info-head{height:100%;color:#1e293b}.info-head span{color:#1e293b}.bar{position:absolute;top:5px;height:13px;min-width:2px;overflow:hidden;border:1px solid #2563eb;border-radius:3px;background:#dbeafe}.bar.project{border-radius:8px}.bar b{display:block;height:100%;background:#2563eb}.bar em{position:absolute;inset:0;display:grid;place-items:center;font-size:11px;font-style:normal}.milestone{position:absolute;top:7px;width:9px;height:9px;background:#f59e0b;transform:translateX(-50%) rotate(45deg)}.marker{position:absolute;z-index:5;top:0;bottom:0;width:0;border-left:1px solid #dc2626;pointer-events:none}.marker.shipping{border-left:2px solid #15803d}.marker label{position:absolute;top:33px;transform:translateX(-50%);padding:0 3px;background:#fff;color:#dc2626;font-size:11px;white-space:nowrap}.marker.shipping label{color:#15803d}.chart{position:relative}.marker-layer{position:absolute;z-index:4;top:0;right:0;bottom:0;left:430px;pointer-events:none}</style></head><body><div class="company">${xml(companyName(items))}</div><h1>${xml(safeName(rootProjectCode))} 项目计划甘特图</h1>${projectMeta}<p>时间范围：${model.start} 至 ${model.finish}　计划模板：${xml(items.map(item => item.plan.templateName).filter(Boolean).join('、') || '—')}　发货日期：${xml(model.shippingDate || '未排程')}</p><div class="legend"><span><i></i>计划</span><span><i class="done"></i>已完成比例</span><span><i class="mile"></i>里程碑</span><span><i class="today"></i>今天</span><span><i class="shipping"></i>发货日 ${xml(model.shippingDate || '未排程')}</span></div><div class="chart"><div class="gantt"><div class="gantt-head"><div class="info info-head"><strong>项目 / 阶段 / 任务</strong><span>类型</span><span>负责人</span><span>进度</span><span>计划开始</span><span>计划完成</span></div><div class="ticks"><div>${bandHtml(years, 'year')}</div><div>${bandHtml(months, 'month')}</div>${ticks.map(tick => `<span class="tick" style="left:${tick.left}%">${tick.label.slice(3)}</span>`).join('')}</div></div>${rows}</div><div class="marker-layer">${todayLeft >= 0 && todayLeft <= 100 ? `<i class="marker today" style="left:${todayLeft}%"><label>今天</label></i>` : ''}${shippingLeft >= 0 && shippingLeft <= 100 ? `<i class="marker shipping" style="left:${shippingLeft}%"><label>发货</label></i>` : ''}</div></div><script>window.addEventListener('load',()=>{window.print()})<\/script></body></html>`
}
export function exportProjectPlansPdf(rootProjectCode: string, items: ProjectPlanExportItem[]) { const popup = window.open('', '_blank'); if (!popup) throw new Error('浏览器已拦截 PDF 打印窗口，请允许弹出窗口后重试'); popup.opener = null; popup.document.write(projectPlanPdfHtml(rootProjectCode, items)); popup.document.close() }
