import { describe, expect, it } from 'vitest'
import { departmentFlowSteps, editProductionBindings, initialDepartmentFlow, saveProductionBindings } from '../src/productionFlowSettings'
import { lotStage, type ProductionReleaseState } from '../src/productionTracking'

describe('部门交接初版流程', () => {
  it('只合并同岗位、同工位和同启用状态的角色，保存保持每个角色原编号', () => {
    const items = ['a', 'b', 'c'].map((subject, index) => ({ id: 'id-' + subject, subjectType: 'Role' as const, subject, post: 'Inspection', workstationIds: [index === 2 ? 'other' : 'station'], isActive: true }))
    const rows = editProductionBindings(items)
    expect(rows).toHaveLength(2); expect(rows[0]!.roleCodes).toEqual(['a', 'b'])
    expect(saveProductionBindings(rows)).toEqual(items)
  })
  it('使用现有角色编号生成可编辑绑定，包装PDA待指定，资源组仅追溯', () => {
    const roles = ['生产经理', '生产助理', '技术助理', '机加主管', '机加人员', '质量经理', '质检员', '生产物料员'].map((name, index) => ({ role: `custom-${index}`, name }))
    const catalog = initialDepartmentFlow({ companyId: 'company', rowVersion: 0, workstations: [], bindings: [] }, roles)
    expect(catalog.flowMode).toBe('DepartmentHandover')
    expect(catalog.controlRoleCodes).toEqual(['custom-0', 'custom-1', 'custom-2'])
    expect(catalog.bindings.find(binding => binding.post === 'Trace')?.workstationIds).toHaveLength(5)
    expect(catalog.bindings.some(binding => binding.post === 'Packing')).toBe(false)
    expect(catalog.bindings.find(binding => binding.post === 'MachiningReception')?.subject).toBe('custom-3')
    expect(departmentFlowSteps).toHaveLength(7); expect(departmentFlowSteps.filter(step => !step.required).map(step => step.post)).toEqual(['Trace'])
  })
  it('主管接图、包装接收和检验结果决定阶段，资源组追溯不推进', () => {
    const state: ProductionReleaseState = { flowMode: 'DepartmentHandover', releasePackageId: 'release', projectId: 'project', companyId: 'company', projectCode: 'P', projectName: '项目', packageNumber: 'RP', scope: 'NonStandardWithDrawing', publishedAt: '', lines: [], lots: [{ id: 'lot', lineId: 'line', route: 'Self', quantity: 2, schedule: [], frozen: false, paused: false }], movements: [] }
    const stages = ['排产', '机加', '机加', '包装', '质检', '质检完成', '领料完成']
    for (const [index, kind] of ['ProductionReceived', 'MachiningReceived', 'Trace', 'PackingStarted', 'QualityReceived', 'Pass', 'Issue'].entries()) {
      state.movements.push({ id: `${index}`, requestId: `${index}`, lotId: 'lot', kind, quantity: 2, actor: 'user', occurredAt: '' })
      expect(lotStage(state, state.lots[0]!)).toBe(stages[index])
    }
  })
})
