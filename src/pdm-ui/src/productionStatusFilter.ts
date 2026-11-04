export const productionStatusFilters = ['待接收', '待分配', '待排产', '进行中', '待质检', '待领用', '变更待处理', '已暂停'] as const

const completed = new Set(['已完结', '变更已结案', '领料完成', '已领用', '已领用／含报废', '已取消', '已取消／转版', '已取消剩余流程', '已报废', '已转版'])

export function productionStatusGroup(status: string) {
  if (completed.has(status)) return '已完结'
  if (['待分配', '部分分配', '数量不足，待补齐'].includes(status)) return '待分配'
  if (['待排产', '待生产经理接图', '待生产签收'].includes(status)) return '待排产'
  if (['待质检收料', '质检', '质检中'].includes(status)) return '待质检'
  if (['质检完成', '质检完成／合格待领', '已齐套待领用'].includes(status)) return '待领用'
  if (['排产', '机加', '包装', '分批流转', '外协待交货', '已排产待执行', '执行中'].includes(status)) return '进行中'
  return status
}

export function matchesProductionStatus(status: string, filter: string, includeComplete: boolean) {
  if (!includeComplete && completed.has(status)) return false
  return !filter || productionStatusGroup(status) === filter
}
