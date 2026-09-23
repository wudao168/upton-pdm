import { describe, expect, it, vi } from 'vitest'
import { strFromU8, unzipSync } from 'three/examples/jsm/libs/fflate.module.js'
import { createProjectPlansWorkbook, exportProjectPlansPdf, projectPlanExportTable, projectPlanGanttModel, projectPlanPdfHtml } from '../src/projectPlanExport'
import type { ProjectPlan } from '../src/types'

const plan = {
  id: 'plan', projectId: 'child', templateId: 'template', templateName: '设备模板', currentStage: 'design',
  approvalStatus: 'Approved', stages: [{ code: 'design', name: '设计' }], baselineVersion: 1,
  plannedStart: '2026-09-10', plannedFinish: '2026-09-15', forecastFinish: '2026-09-15', rowVersion: 1,
  tasks: [{ id: 'task', name: '方案检查', stage: 'design', assignee: '工程师', plannedStart: '2026-09-10', plannedFinish: '2026-09-15', durationDays: 6, status: 'InProgress', completionPercent: 50, weight: 1, isRequired: true, isMilestone: false, predecessorTaskIds: [], sortOrder: 10 }],
  createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
} as ProjectPlan
const exportItem = {
  projectCode: 'P1-1', projectName: '设备一', plan,
  assigneeDisplayNames: { 工程师: '马文豪' },
  metadata: { companyName: '昆山阿普顿自动化系统有限公司', customerName: '测试客户', projectType: '自动化设备', deviceModel: 'AK-1', serialNumbers: ['70000032'], executionUnitName: 'T3事业部', projectManager: '刘鹏搏', designLead: '马文豪', engineers: ['马文豪'] },
}

describe('项目计划导出', () => {
  it('输出中文字段并标记跟随主计划的子项目', () => {
    const table = projectPlanExportTable([{ ...exportItem, inherited: true }])
    expect(table.headers).toContain('计划来源')
    expect(table.rows[0]).toEqual(expect.arrayContaining(['P1-1', '设备一', '跟随主项目计划', '设计', '方案检查', '马文豪', '50%']))
  })

  it('PDF 打印页包含项目和任务，并在加载后调用打印', () => {
    const write = vi.fn()
    const close = vi.fn()
    const popup = { document: { write, close }, opener: window } as unknown as Window
    vi.spyOn(window, 'open').mockReturnValue(popup)
    exportProjectPlansPdf('P1', [exportItem])
    expect(write).toHaveBeenCalledWith(expect.stringContaining('<title>P1 项目计划甘特图</title>'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('@page{size:A4 landscape;margin:0}'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('body{margin:0;padding:8mm'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('方案检查'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('项目计划甘特图'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('<span>类型</span><span>负责人</span><span>进度</span><span>计划开始</span><span>计划完成</span>'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('<span>任务</span><span>马文豪</span><span>50%</span><span>2026-09-10</span><span>2026-09-15</span>'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('昆山阿普顿自动化系统有限公司'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('型号：AK-1'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('calendar-band year'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('2026年'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('9月'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('class="bar task"'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('发货日'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('window.print()'))
    expect(close).toHaveBeenCalled()
  })

  it('Excel 同时生成甘特图和项目计划工作表', () => {
    const files = unzipSync(createProjectPlansWorkbook([exportItem]))
    const workbook = strFromU8(files['xl/workbook.xml']!)
    const gantt = strFromU8(files['xl/worksheets/sheet1.xml']!)
    const detail = strFromU8(files['xl/worksheets/sheet2.xml']!)
    expect(workbook).toContain('name="甘特图"')
    expect(workbook).toContain('name="项目计划"')
    expect(gantt).toContain('项目计划甘特图')
    expect(gantt).toContain('昆山阿普顿自动化系统有限公司')
    expect(gantt).toContain('2026年')
    expect(gantt).toContain('9月')
    expect(gantt).toContain('马文豪')
    expect(gantt).toContain('方案检查')
    expect(gantt).toContain('项目发货日期')
    expect(gantt).toContain('workbookViewId="0"')
    expect(gantt).not.toContain('workbookId=')
    expect(gantt).toContain('width="2.8"')
    expect(gantt).toContain('<mergeCell ref="G6:P6"/>')
    expect(new DOMParser().parseFromString(gantt, 'application/xml').querySelector('parsererror')).toBeNull()
    expect(gantt).not.toContain('<t xml:space="preserve">09-10</t>')
    expect(gantt).toContain('<t xml:space="preserve">10</t>')
    expect(detail).toContain('计划来源')
  })

  it('Excel 月份表头完整覆盖跨月日期列', () => {
    const crossMonthPlan = { ...plan, tasks: [{ ...plan.tasks[0]!, plannedStart: '2026-09-29', plannedFinish: '2026-10-02' }] }
    const files = unzipSync(createProjectPlansWorkbook([{ ...exportItem, plan: crossMonthPlan }]))
    const gantt = strFromU8(files['xl/worksheets/sheet1.xml']!)
    expect(gantt).toContain('<mergeCell ref="G6:J6"/>')
    expect(gantt).toContain('<mergeCell ref="K6:N6"/>')
    expect(gantt).toContain('<c r="G6" s="1" t="inlineStr"><is><t xml:space="preserve">9月</t>')
    expect(gantt).toContain('<c r="K6" s="1" t="inlineStr"><is><t xml:space="preserve">10月</t>')
  })

  it('甘特图模型包含项目、阶段、任务和交付阶段发货日期', () => {
    const model = projectPlanGanttModel([exportItem])
    expect(model.rows.map(row => row.kind)).toEqual(['project', 'stage', 'task'])
    expect(model.start).toBe('2026-09-08')
    expect(model.finish).toBe('2026-09-17')
    expect(model.shippingDate).toBe('2026-09-15')
    expect(projectPlanPdfHtml('P1', [exportItem])).toContain('marker shipping')
  })
})
