import { describe, expect, it, vi } from 'vitest'
import { exportProjectPlansPdf, projectPlanExportTable } from '../src/projectPlanExport'
import type { ProjectPlan } from '../src/types'

const plan = {
  id: 'plan', projectId: 'child', templateId: 'template', templateName: '设备模板', currentStage: 'design',
  approvalStatus: 'Approved', stages: [{ code: 'design', name: '设计' }], baselineVersion: 1,
  plannedStart: '2026-09-10', plannedFinish: '2026-09-15', forecastFinish: '2026-09-15', rowVersion: 1,
  tasks: [{ id: 'task', name: '方案检查', stage: 'design', assignee: '工程师', plannedStart: '2026-09-10', plannedFinish: '2026-09-15', durationDays: 6, status: 'InProgress', completionPercent: 50, weight: 1, isRequired: true, isMilestone: false, predecessorTaskIds: [], sortOrder: 10 }],
  createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
} as ProjectPlan

describe('项目计划导出', () => {
  it('输出中文字段并标记跟随主计划的子项目', () => {
    const table = projectPlanExportTable([{ projectCode: 'P1-1', projectName: '设备一', plan, inherited: true }])
    expect(table.headers).toContain('计划来源')
    expect(table.rows[0]).toEqual(expect.arrayContaining(['P1-1', '设备一', '跟随主项目计划', '设计', '方案检查', '工程师', '50%']))
  })

  it('PDF 打印页包含项目和任务，并在加载后调用打印', () => {
    const write = vi.fn()
    const close = vi.fn()
    const popup = { document: { write, close }, opener: window } as unknown as Window
    vi.spyOn(window, 'open').mockReturnValue(popup)
    exportProjectPlansPdf('P1', [{ projectCode: 'P1-1', projectName: '设备一', plan }])
    expect(write).toHaveBeenCalledWith(expect.stringContaining('<title>P1 项目计划</title>'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('方案检查'))
    expect(write).toHaveBeenCalledWith(expect.stringContaining('window.print()'))
    expect(close).toHaveBeenCalled()
  })
})
