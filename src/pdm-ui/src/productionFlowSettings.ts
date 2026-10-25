import { productionRequestId, type ProductionScanCatalog, type ProductionPostBinding, type ProductionFlowNode } from './productionTracking'

export interface ProductionBindingEditor extends ProductionPostBinding { roleCodes: string[]; roleIds: Record<string, string> }
export function editProductionBindings(bindings: ProductionPostBinding[]): ProductionBindingEditor[] {
  const rows: ProductionBindingEditor[] = [], groups = new Map<string, ProductionBindingEditor>()
  for (const binding of bindings) {
    const key = JSON.stringify([binding.post, binding.flowNodeId, [...binding.workstationIds].sort(), binding.isActive])
    const row = binding.subjectType === 'Role' ? groups.get(key) : undefined
    if (row) { row.roleCodes.push(binding.subject); row.roleIds[binding.subject] = binding.id; continue }
    const next = { ...binding, workstationIds: [...binding.workstationIds], roleCodes: binding.subjectType === 'Role' ? [binding.subject] : [], roleIds: binding.subjectType === 'Role' ? { [binding.subject]: binding.id } : {} }
    rows.push(next); if (binding.subjectType === 'Role') groups.set(key, next)
  }
  return rows
}
export function saveProductionBindings(rows: ProductionBindingEditor[]): ProductionPostBinding[] {
  return rows.flatMap(row => (row.subjectType === 'Role' ? row.roleCodes : [row.subject]).map(subject => ({
    id: row.subjectType === 'Role' ? row.roleIds[subject] ?? (row.roleIds[subject] = productionRequestId()) : row.id,
    subjectType: row.subjectType, subject, post: row.post, workstationIds: [...row.workstationIds], isActive: row.isActive,
    ...(row.flowNodeId ? { flowNodeId: row.flowNodeId } : {}),
  })))
}

export const departmentFlowSteps = [
  { stage: '排产', post: 'Reception', action: '生产经理接图扫码', required: true },
  { stage: '机加', post: 'MachiningReception', action: '机加主管接图扫码', required: true },
  { stage: '仅追溯，不推进阶段', post: 'Trace', action: '车床、铣床、CNC、线切割、激光切割等资源组扫码', required: false },
  { stage: '包装', post: 'Packing', action: '包装接收扫码，可打印二维码标签', required: true },
  { stage: '质检', post: 'QualityReceiving', action: '质量部门扫码收货', required: true },
  { stage: '质检完成', post: 'Inspection', action: '检验完成后扫码确认结果', required: true },
  { stage: '领料完成', post: 'Issue', action: '物料员扫码确认实际领料人和数量', required: true },
]
export function defaultProductionFlow(): ProductionFlowNode[] {
  const names = ['生产接图', '机加接图', '资源组追溯', '包装接收', '质量收货', '检验完成', '物料领料']
  const kinds = ['ProductionReceived', 'MachiningReceived', 'Trace', 'PackingStarted', 'QualityReceived', 'Pass', 'Issue']
  return departmentFlowSteps.map((step, index) => ({ id: `81000000-0000-0000-0000-${String(index + 1).padStart(12, '0')}`, name: names[index]!, stage: step.required ? step.stage : '保持当前阶段',
    post: step.post, kind: kinds[index]!, required: step.required, afterNodeId: index === 2 ? '81000000-0000-0000-0000-000000000002' : null }))
}

export function initialDepartmentFlow(catalog: ProductionScanCatalog, roles: Array<{ role: string; name: string }>): ProductionScanCatalog {
  const stations = ['生产接图台', '机加主管接图台', '车床', '铣床', 'CNC', '线切割', '激光切割', '包装标签工位', '质量收货台', '质量检验台', '物料领料台']
    .map((name, index) => ({ id: productionRequestId(), name, resourceGroup: index >= 2 && index <= 6 ? `${name}组` : '', isActive: true }))
  const bindings: ProductionScanCatalog['bindings'] = []
  function bind(name: string, post: string, indices: number[]) {
    const role = roles.find(role => role.name === name)
    if (role) bindings.push({ id: productionRequestId(), subjectType: 'Role', subject: role.role, post, workstationIds: indices.map(index => stations[index]!.id), isActive: true })
  }
  bind('生产经理', 'Reception', [0]); bind('机加主管', 'MachiningReception', [1]); bind('机加人员', 'Trace', [2, 3, 4, 5, 6])
  for (const name of ['质量经理', '质检员']) { bind(name, 'QualityReceiving', [8]); bind(name, 'Inspection', [9]) }
  bind('生产物料员', 'Issue', [10])
  return { ...catalog, flowMode: 'DepartmentHandover', flowNodes: defaultProductionFlow(), workstations: stations, bindings,
    controlRoleCodes: roles.filter(role => ['生产经理', '生产助理', '技术助理'].includes(role.name)).map(role => role.role), labelWidthMm: 60, labelHeightMm: 40 }
}
