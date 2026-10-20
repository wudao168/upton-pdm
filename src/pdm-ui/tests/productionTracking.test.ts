import { afterEach, describe, expect, it, vi } from 'vitest'
import { lineState, lineTotal, productionRequestId, unassignedQuantity, type ProductionReleaseState } from '../src/productionTracking'

const release = (): ProductionReleaseState => ({
  releasePackageId: 'release-1', projectId: 'project-1', companyId: 'company-1',
  projectCode: 'P26018', projectName: '包装设备', packageNumber: 'FB-018-004',
  scope: 'NonStandardWithDrawing', publishedAt: '2026-10-18T08:00:00Z',
  lines: [{ id: 'line-1', materialKey: 'NS-01|001', drawingNumber: 'NS-01', name: '夹持座',
    revision: 'R02', unit: '件', requiredQuantity: 10, priority: 'Urgent', requiredOn: '2026-10-23' }],
  lots: [{ id: 'lot-1', lineId: 'line-1', route: 'External', quantity: 10,
    paused: false, frozen: false, schedule: [] }], movements: [],
})

afterEach(() => vi.unstubAllGlobals())

describe('生产跟踪明细', () => {
  it('剩余可分配数量计入报废补产并扣除取消和转版数量', () => {
    const state = release(), line = state.lines[0]
    state.lots[0].quantity = 6
    expect(unassignedQuantity(state, line)).toBe(4)
    for (const [kind, quantity] of [['Scrap', 2], ['Cancelled', 1], ['Transferred', 2]] as const)
      state.movements.push({ id: kind, requestId: kind, lotId: 'lot-1', kind, quantity,
        actor: '供应链', occurredAt: '2026-10-19T00:00:00Z' })
    expect(unassignedQuantity(state, line)).toBe(3)
    state.lots[0].quantity = 20
    expect(unassignedQuantity(state, line)).toBe(0)
  })

  it('区分外协交货、质检合格和项目领用', () => {
    const state = release(), line = state.lines[0]
    expect(lineState(state, line)).toBe('外协待交货')
    state.movements.push({ id: 'event-1', requestId: 'request-1', lotId: 'lot-1',
      kind: 'ExternalDelivered', quantity: 10, actor: '供应链', occurredAt: '2026-10-18T08:00:00Z' })
    expect(lineState(state, line)).toBe('待质检收料')
    state.movements.push({ id: 'event-2', requestId: 'request-2', lotId: 'lot-1',
      kind: 'QualityReceived', quantity: 10, actor: '质量', occurredAt: '2026-10-18T09:00:00Z' })
    expect(lineState(state, line)).toBe('质检中')
    state.movements.push({ id: 'event-3', requestId: 'request-3', lotId: 'lot-1',
      kind: 'Pass', quantity: 10, actor: '质量', occurredAt: '2026-10-18T10:00:00Z' })
    expect(lineState(state, line)).toBe('质检完成／合格待领')
    state.movements.push({ id: 'event-4', requestId: 'request-4', lotId: 'lot-1',
      kind: 'Issue', quantity: 10, actor: '质量', recipient: '专职领料员', occurredAt: '2026-10-18T11:00:00Z' })
    expect(lineTotal(state, line.id, 'Issue')).toBe(10)
    expect(lineState(state, line)).toBe('已领用')
  })

  it('在没有 randomUUID 的内网 HTTP 页面仍生成有效的请求标识', () => {
    vi.stubGlobal('crypto', { getRandomValues: (bytes: Uint8Array) => bytes.fill(0x42) })
    expect(productionRequestId()).toMatch(/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/)
  })
})
