import { describe, expect, it } from 'vitest'
import { matchesProductionStatus, productionStatusFilters, productionStatusGroup } from '../src/productionStatusFilter'

describe('生产状态快捷筛选', () => {
  it('保留8个业务阶段及全部状态入口所需的选项', () => {
    expect(productionStatusFilters).toEqual(['待接收', '待分配', '待排产', '进行中', '待质检', '待领用', '变更待处理', '已暂停'])
  })
  it.each([
    ['部分分配', '待分配'], ['数量不足，待补齐', '待分配'], ['待生产经理接图', '待排产'], ['待生产签收', '待排产'],
    ['排产', '进行中'], ['机加', '进行中'], ['包装', '进行中'], ['分批流转', '进行中'], ['外协待交货', '进行中'],
    ['已排产待执行', '进行中'], ['执行中', '进行中'], ['待质检收料', '待质检'], ['质检中', '待质检'], ['质检', '待质检'],
    ['质检完成', '待领用'], ['质检完成／合格待领', '待领用'], ['已齐套待领用', '待领用'],
    ['待接收', '待接收'], ['变更待处理', '变更待处理'], ['已暂停', '已暂停'],
  ])('%s可以通过%s定位，具体状态不改写', (status, group) => {
    expect(productionStatusGroup(status)).toBe(group)
    expect(matchesProductionStatus(status, group, false)).toBe(true)
    expect(matchesProductionStatus(status, '', false)).toBe(true)
  })
  it.each(['已完结', '变更已结案', '领料完成', '已领用', '已领用／含报废', '已取消', '已取消／转版', '已取消剩余流程', '已报废', '已转版'])('保留已完结勾选对%s的控制', status => {
    expect(matchesProductionStatus(status, '', false)).toBe(false)
    expect(matchesProductionStatus(status, '', true)).toBe(true)
    expect(matchesProductionStatus(status, '进行中', true)).toBe(false)
  })
  it('全部状态仍能看到新状态及无新增需求，已完结勾选不清除阶段筛选', () => {
    expect(matchesProductionStatus('未知的新状态', '', false)).toBe(true)
    expect(matchesProductionStatus('无新增生产需求', '', false)).toBe(true)
    expect(matchesProductionStatus('待质检收料', '待排产', true)).toBe(false)
  })
})
