import { effectScope, shallowRef, watchEffect, type Directive } from 'vue'

export interface MaterialStockPlan {
  enterpriseCode: string
  organizationCode: string
  materialCode: string
  safetyStockQuantity: number
  reorderQuantity: number | null
  isEffective: boolean
  unitName: string | null
  syncedAt: string
}
export interface MaterialStockPlanSnapshot {
  enterpriseCode: string
  organizationCode: string
  items: MaterialStockPlan[]
}
const plans = shallowRef(new Map<string, MaterialStockPlan>())
const key = (code: unknown) => typeof code === 'string' ? code.trim().toUpperCase() : ''
let generation = 0
let contextKey = ''
let pending: Promise<void> | undefined
export function setMaterialStockContext(context: string) {
  if (contextKey === context) return
  contextKey = context
  generation++
  pending = undefined
  plans.value = new Map()
}
export async function refreshMaterialStockPlans(load: () => Promise<MaterialStockPlanSnapshot>) {
  if (pending) return pending
  const current = generation
  const request = (async () => {
    try {
      const result = await load()
      if (current !== generation) return
      plans.value = new Map(result.items.filter(item => item.isEffective && item.safetyStockQuantity > 0
        && item.enterpriseCode === result.enterpriseCode && item.organizationCode === result.organizationCode)
        .map(item => [key(item.materialCode), item]))
    } catch { /* 网络失败时保留本次组织上次成功的快照。 */ }
  })()
  pending = request
  try { await request } finally { if (pending === request) pending = undefined }
}
export const materialStockPlan = (code: unknown) => plans.value.get(key(code))
export function materialStockTooltip(plan: MaterialStockPlan) {
  return `常备件\n安全库存：${plan.safetyStockQuantity}${plan.unitName || ''}\n再订货量：${plan.reorderQuantity ?? '未提供'}${plan.unitName || ''}\n同步时间：${new Date(plan.syncedAt).toLocaleString('zh-CN', { hour12: false })}`
}
type StockElement = HTMLElement & { stockState?: { code: unknown; title: string; appliedTitle: string; stop: () => void; render: () => void } }
export const stockedMaterial: Directive<StockElement, unknown> = {
  mounted(element, binding) {
    const scope = effectScope()
    const render = () => {
      const state = element.stockState!
      const plan = materialStockPlan(state.code)
      element.classList.toggle('pdm-stocked-material', !!plan)
      if (plan) element.title = [state.title, materialStockTooltip(plan)].filter(Boolean).join('\n')
      else if (state.title) element.title = state.title
      else element.removeAttribute('title')
      state.appliedTitle = element.getAttribute('title') || ''
    }
    element.stockState = { code: binding.value, title: element.getAttribute('title') || '', appliedTitle: '', stop: () => scope.stop(), render }
    scope.run(() => watchEffect(render))
  },
  updated(element, binding) {
    if (!element.stockState) return
    const title = element.getAttribute('title') || ''
    if (title !== element.stockState.appliedTitle) element.stockState.title = title
    element.stockState.code = binding.value
    element.stockState.render()
  },
  beforeUnmount(element) { element.stockState?.stop(); delete element.stockState },
}
