<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { listMaterialInventory, matchApprovedMaterials, refreshMaterialInventory } from '../api'
import type { PdmMaterial, U9InventoryRow } from '../types'
import { groupInventoryCandidates, hasOriginalInventory, inventoryModelDifference, normalizeInventoryModel } from '../bomInventoryRecommendations'
import type { InventoryBomRow, InventoryCandidate } from '../bomInventoryRecommendations'
import { userErrorMessage } from '../userErrorMessage'
import { u9UnitName } from '../u9Units'
import { isDefaultInventoryWarehouse } from '../procurementInventory'
import RawDrawerPanel from './RawDrawerPanel.vue'

const props = defineProps<{ token: string; kind: 'Standard' | 'Electrical'; items: InventoryBomRow[]; initialKey?: string; username?: string }>()
const emit = defineEmits<{
  close: []
  replace: [item: InventoryBomRow, material: PdmMaterial]
  scanned: [counts: Array<{ key: string; signature: string; count: number }>]
}>()
type ScanResult = { candidates: InventoryCandidate[]; error?: string }
const results = ref<Record<string, ScanResult>>({})
const warehouseNames = ref<string[]>([])
const selectedWarehouses = ref<string[] | null>(null)
const draftWarehouses = ref<string[]>([])
const settingsOpen = ref(false)
const warehouseStorageKey = `upton-pdm:bom-inventory-warehouses:${props.username || 'anonymous'}`
const effectiveWarehouses = computed(() => selectedWarehouses.value ?? warehouseNames.value.filter(isDefaultInventoryWarehouse))
function inSelectedWarehouse(row: U9InventoryRow) { return effectiveWarehouses.value.includes(row.warehouseName.trim()) }
function restoreWarehouses() {
  try {
    const saved = JSON.parse(window.localStorage.getItem(warehouseStorageKey) ?? 'null') as { warehouses?: unknown } | null
    if (Array.isArray(saved?.warehouses)) selectedWarehouses.value = saved.warehouses.filter((value): value is string => typeof value === 'string')
  } catch { /* Personal warehouse settings fall back to the existing five default categories. */ }
}
function openSettings() {
  draftWarehouses.value = [...effectiveWarehouses.value]
  settingsOpen.value = true
}
async function saveSettings() {
  try { window.localStorage.setItem(warehouseStorageKey, JSON.stringify({ warehouses: draftWarehouses.value })) }
  catch { error.value = '库存设置保存失败，请检查浏览器存储权限后重试。'; return }
  selectedWarehouses.value = [...draftWarehouses.value]
  settingsOpen.value = false
  await scan()
}
const decisions = ref<Record<string, string>>({})
const selectedKey = ref(props.initialKey || '')
let selectionTouched = false
const scanning = ref(false)
const completed = ref(0)
const error = ref('')
const candidate = ref<InventoryCandidate>()
const material = ref<PdmMaterial>()
const candidateError = ref('')
const candidateLoading = ref(false)
const refreshing = ref(false)
const confirmed = ref(false)
let generation = 0
let selectionGeneration = 0
let controller: AbortController | undefined
const recommendedItems = computed(() => props.items.filter(item => results.value[item.key]?.candidates.length))
const selected = computed(() => recommendedItems.value.find(item => item.key === selectedKey.value))
const currentResult = computed(() => results.value[selectedKey.value])
const matchCount = computed(() => Object.values(results.value).filter(result => result.candidates.length).length)
const canAdopt = computed(() => !!material.value && !!candidate.value && candidate.value.stockQuantity > 0 && confirmed.value && !candidateLoading.value
  && !refreshing.value && !candidateError.value && !decisions.value[selectedKey.value]
  && candidate.value.materialCode.toUpperCase() !== selected.value?.drawingNumber.trim().toUpperCase())
function requestKey(item: InventoryBomRow) { return JSON.stringify([item.specification?.trim().toUpperCase(), item.brand?.trim().toUpperCase()]) }
function canScan(item: InventoryBomRow) {
  const model = normalizeInventoryModel(item.specification)
  return !item.disabledReason && model.length >= 4 && model.length <= 256
}
function rowStatus(item: InventoryBomRow) {
  if (decisions.value[item.key]) return decisions.value[item.key]
  if (item.disabledReason) return item.disabledReason
  if (!canScan(item)) return normalizeInventoryModel(item.specification).length > 256 ? '型号过长，请先核对' : '型号不足4个有效字符'
  const result = results.value[item.key]
  return result?.error ? '查询失败，可重新扫描' : result ? `候选 ${result.candidates.length}` : '等待扫描'
}
function selectRow(item: InventoryBomRow | undefined, manual = true) {
  if (manual) selectionTouched = true
  selectedKey.value = item?.key ?? ''
  selectionGeneration++
  candidate.value = undefined
  material.value = undefined
  candidateLoading.value = false
  candidateError.value = ''
  confirmed.value = false
}
function synchronizeSelection(preferFirst = false) {
  if ((!preferFirst || selectionTouched) && selected.value) return
  const next = recommendedItems.value.find(item => item.key === props.initialKey) ?? recommendedItems.value[0]
  if (next?.key !== selectedKey.value) selectRow(next, false)
}
watch(recommendedItems, () => synchronizeSelection())
function publishCounts() {
  emit('scanned', props.items.map(item => ({ key: item.key, signature: item.signature,
    count: results.value[item.key]?.candidates.length ?? 0 })))
}
async function scan() {
  const run = ++generation
  ++selectionGeneration
  controller?.abort()
  controller = new AbortController()
  const signal = controller.signal
  selectionTouched = false
  results.value = {}
  candidate.value = undefined
  material.value = undefined
  candidateError.value = ''
  candidateLoading.value = false
  confirmed.value = false
  completed.value = 0
  error.value = ''
  scanning.value = true
  let originalStock: U9InventoryRow[] = []
  try {
    let page = 1, total = 0
    const snapshot: U9InventoryRow[] = []
    do {
      const response = await listMaterialInventory({ positiveStockOnly: true, page, pageSize: 200 }, props.token, signal)
      if (run !== generation) return
      warehouseNames.value = [...new Set([...(response.warehouseNames ?? []), ...warehouseNames.value, ...response.items.map(row => row.warehouseName)].map(name => name.trim()))].sort((a, b) => a.localeCompare(b, 'zh-CN'))
      snapshot.push(...response.items)
      total = response.total
      if (!response.items.length) break
      page++
    } while (snapshot.length < total)
    originalStock = snapshot.filter(inSelectedWarehouse)
  } catch (cause) {
    if (run !== generation) return
    error.value = userErrorMessage(cause instanceof Error ? cause.message : '库存范围加载失败，请重新扫描。')
    scanning.value = false
    publishCounts()
    return
  }
  if (!effectiveWarehouses.value.length) {
    scanning.value = false
    completed.value = props.items.length
    publishCounts()
    return
  }
  const groups = new Map<string, InventoryBomRow[]>()
  for (const item of props.items) {
    if (!canScan(item) || decisions.value[item.key] || hasOriginalInventory(item, originalStock)) { completed.value++; continue }
    const key = requestKey(item)
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  const queue = [...groups.values()]
  async function worker() {
    while (queue.length && run === generation) {
      const items = queue.shift()!
      const first = items[0]!
      try {
        const stock: U9InventoryRow[] = []
        let page = 1, total = 0
        do {
          const response = await listMaterialInventory({ similarSpecification: first.specification, brand: first.brand,
            positiveStockOnly: true, page, pageSize: 200 }, props.token, signal)
          if (run !== generation) return
          stock.push(...response.items)
          total = response.total
          if (!response.items.length) break
          page++
        } while (stock.length < total)
        const scopedStock = stock.filter(inSelectedWarehouse)
        const candidates = groupInventoryCandidates(scopedStock)
        for (const item of items) results.value[item.key] = { candidates: hasOriginalInventory(item, scopedStock) ? [] : candidates }
      } catch (cause) {
        if (run !== generation) return
        const message = userErrorMessage(cause instanceof Error ? cause.message : '库存查询失败，请重新扫描。')
        for (const item of items) results.value[item.key] = { candidates: [], error: message }
        error.value = `部分型号查询失败：${message} 请重新扫描。`
      }
      completed.value += items.length
    }
  }
  await Promise.all(Array.from({ length: Math.min(3, queue.length) }, worker))
  if (run !== generation) return
  scanning.value = false
  synchronizeSelection(true)
  publishCounts()
}
async function inspect(stock: InventoryCandidate) {
  selectionTouched = true
  const request = ++selectionGeneration
  const item = selected.value
  if (!item) return
  candidate.value = stock
  material.value = undefined
  confirmed.value = false
  candidateError.value = ''
  candidateLoading.value = true
  try {
    const matches = await matchApprovedMaterials(props.token, 'drawingNumber', stock.materialCode)
    if (request !== selectionGeneration) return
    const found = matches.find(value => value.materialCode.trim().toUpperCase() === stock.materialCode.trim().toUpperCase())
    if (!found || found.isArchived || found.approvalStatus !== 'Approved') {
      candidateError.value = '该料品未批准或已停用，请先在料品管理中核对。'
    } else if (found.kind !== props.kind) {
      candidateError.value = '候选料品分类与当前BOM不一致，请选择同分类物料。'
    } else if (u9UnitName(found.unitCode) !== u9UnitName(item.unit)) {
      candidateError.value = '候选料品单位与原BOM不一致，请先核对数量和单位，不能直接替换。'
    } else if (normalizeInventoryModel(found.specification ?? '') !== normalizeInventoryModel(stock.specification)) {
      candidateError.value = '库存型号与料品主档不一致，请刷新库存并核对料品资料。'
    } else material.value = found
  } catch (cause) {
    if (request === selectionGeneration) candidateError.value = userErrorMessage(cause instanceof Error ? cause.message : '料品查询失败，请重试。')
  } finally { if (request === selectionGeneration) candidateLoading.value = false }
}
async function refreshCandidate() {
  const current = candidate.value
  const request = selectionGeneration
  if (!current || refreshing.value) return
  refreshing.value = true
  try {
    const response = await refreshMaterialInventory(current.materialCode, props.token)
    if (request !== selectionGeneration) return
    const stock = [...response.items]
    for (let page = 2; stock.length < response.total; page++) {
      const more = await listMaterialInventory({ materialCode: current.materialCode, positiveStockOnly: true, page, pageSize: 200 }, props.token, controller?.signal)
      if (request !== selectionGeneration) return
      if (!more.items.length) break
      stock.push(...more.items)
    }
    // A refresh is an exact-code lookup, so restore the model match score for grouping.
    const fresh = groupInventoryCandidates(stock.filter(inSelectedWarehouse).map(row => ({ ...row, similarityPercent: current.similarity, similarityReason: current.reason })))[0]
    if (!fresh) {
      for (const result of Object.values(results.value)) result.candidates = result.candidates.filter(value => value.materialCode !== current.materialCode)
      candidate.value = { ...current, stockQuantity: 0, locations: [] }
      publishCounts()
      error.value = '该料品在所选仓库中已无正库存，已从推荐列表移除。'
      candidateError.value = error.value
      material.value = undefined
      confirmed.value = false
      return
    }
    if (normalizeInventoryModel(fresh.specification) !== normalizeInventoryModel(current.specification)) {
      candidateError.value = '刷新后库存型号已变化，请重新扫描并核对候选。'
      material.value = undefined
      confirmed.value = false
      return
    }
    for (const result of Object.values(results.value)) {
      result.candidates = result.candidates.map(item => item.materialCode === fresh.materialCode ? fresh : item)
    }
    publishCounts()
    // Recheck the latest specification against the approved material before allowing adoption.
    await inspect(fresh)
  } catch (cause) {
    if (request === selectionGeneration) candidateError.value = userErrorMessage(cause instanceof Error ? cause.message : '库存刷新失败，请重试。')
  } finally { refreshing.value = false }
}
function adopt() {
  if (!canAdopt.value || !selected.value || !material.value) return
  emit('replace', selected.value, material.value)
  decisions.value[selectedKey.value] = `已暂存替换 → ${material.value.materialCode}`
  confirmed.value = false
}
function retain() {
  decisions.value[selectedKey.value] = '保留原物料'
  confirmed.value = false
}
function quantity(value: number) { return new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 4 }).format(value) }
function time(value: string) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
onMounted(() => { restoreWarehouses(); void scan() })
onBeforeUnmount(() => { ++generation; ++selectionGeneration; controller?.abort() })
</script>

<template>
  <Teleport to="body">
    <div class="pdm-dialog-backdrop" @click.self="emit('close')" @keydown.esc="emit('close')">
      <RawDrawerPanel class="pdm-bom-batch-dialog bom-inventory" role="dialog" aria-modal="true" aria-labelledby="bom-inventory-title">
        <template #header>
          <header><div><h3 id="bom-inventory-title">库存相似型号推荐 · {{ kind === 'Standard' ? '标准件BOM' : '电气件BOM' }}</h3>
            <p>当前项目完整BOM，共 {{ items.length }} 行；型号匹配度≥80%，同品牌优先。型号相似，待工程师核对。</p></div>
            <button type="button" class="pdm-icon-button" aria-label="关闭库存推荐" @click="emit('close')">×</button>
          </header>
        </template>
        <div class="bom-inventory__toolbar"><span role="status">{{ scanning ? `扫描中 ${completed} / ${items.length}` : `扫描完成 ${completed} / ${items.length}，${matchCount} 行有库存候选` }}</span>
          <div class="bom-inventory__toolbar-actions"><button type="button" class="pdm-secondary-action" :disabled="scanning || refreshing" @click="openSettings">库存设置</button><button type="button" class="pdm-secondary-action" :disabled="scanning || refreshing" @click="scan">重新扫描</button></div></div>
        <div class="bom-inventory__scope" :title="effectiveWarehouses.join('、')">对比仓库：{{ effectiveWarehouses.join('、') || '未选择仓库，请打开库存设置' }}；原物料已有正库存的行不推荐。</div>
        <section v-if="settingsOpen" class="bom-inventory__settings" aria-label="库存设置">
          <p>仅对比勾选仓库内的库存。默认选择项目仓、呆滞仓、常备仓、退料仓和应急仓。设置在当前账号下通用。</p>
          <div class="bom-inventory__warehouses" role="group" aria-label="推荐库存仓库"><label v-for="name in warehouseNames" :key="name"><input v-model="draftWarehouses" type="checkbox" :value="name">{{ name }}</label><span v-if="!warehouseNames.length">当前库存快照暂无仓库。</span></div>
          <div class="bom-inventory__settings-actions"><button type="button" class="pdm-secondary-action" @click="draftWarehouses = warehouseNames.filter(isDefaultInventoryWarehouse)">恢复默认</button><button type="button" class="pdm-secondary-action" @click="settingsOpen = false">取消</button><button type="button" class="pdm-primary-action" @click="saveSettings">保存并扫描</button></div>
        </section>
        <p v-if="error" class="bom-inventory__error" role="alert">{{ error }}</p>
        <div class="bom-inventory__body">
          <section class="bom-inventory__pane bom-inventory__existing" aria-label="现有BOM物料">
            <header><strong>现有BOM物料</strong><span>有推荐 {{ recommendedItems.length }} 项</span></header>
            <div class="bom-inventory__rows">
              <table class="bom-inventory__existing-table" aria-label="有库存推荐的BOM物料">
                <thead><tr><th>物料编码</th><th>物料名称</th><th>型号</th><th>需求数量</th><th>状态</th></tr></thead>
                <tbody><tr v-for="item in recommendedItems" :key="item.key" :class="{ 'is-selected': selectedKey === item.key }"
                  :aria-selected="selectedKey === item.key" :aria-label="`选择现有物料 ${item.drawingNumber || item.name}`" tabindex="0"
                  @click="selectRow(item)" @keydown.enter="selectRow(item)" @keydown.space.prevent="selectRow(item)">
                  <td v-stocked-material="item.drawingNumber" :title="item.drawingNumber"><div class="bom-inventory__cell">{{ item.drawingNumber || '—' }}</div></td><td :title="`${item.name} · ${item.brand || '—'}`"><div class="bom-inventory__cell"><span v-stocked-material="item.drawingNumber">{{ item.name }}</span><small>{{ item.brand || '—' }}</small></div></td>
                  <td :title="item.specification"><div class="bom-inventory__cell bom-inventory__model-cell"><template v-for="(part, index) in inventoryModelDifference(selectedKey === item.key && candidate ? candidate.specification : item.specification || '', item.specification || '')" :key="index"><mark v-if="part.changed">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></div></td>
                  <td><div class="bom-inventory__cell">{{ quantity(item.quantity) }} {{ u9UnitName(item.unit) }}</div></td><td :title="rowStatus(item)"><div class="bom-inventory__cell">{{ rowStatus(item) }}</div></td>
                </tr></tbody>
              </table>
              <div v-if="!recommendedItems.length" class="bom-inventory__empty" role="status"><strong>{{ scanning ? '正在查找库存推荐…' : '暂无有库存推荐的物料' }}</strong><span>{{ scanning ? '找到匹配物料后在此显示。' : '所选仓库暂无替代推荐，或原物料已有正库存。' }}</span></div>
            </div>
          </section>
          <section class="bom-inventory__pane bom-inventory__stock" aria-label="库存物料推荐">
            <header><strong>库存物料推荐</strong><span v-if="selected">候选 {{ currentResult?.candidates.length ?? 0 }} 项</span></header>
            <main class="bom-inventory__detail">
              <template v-if="selected">
                <p class="bom-inventory__selection">对应左侧：{{ selected.drawingNumber || selected.name }}</p>
                <div class="bom-inventory__table-wrap"><table class="bom-inventory__candidates"><thead><tr><th>料号／名称</th><th>库存型号／品牌</th><th>匹配依据</th><th>现存量</th><th>操作</th></tr></thead>
                  <tbody><tr v-for="stock in currentResult?.candidates ?? []" :key="stock.materialCode" :class="{ 'is-selected': candidate?.materialCode === stock.materialCode }">
                    <td :title="`${stock.materialCode} · ${stock.name}`"><div class="bom-inventory__cell"><span v-stocked-material="stock.materialCode">{{ stock.materialCode }}</span><small>{{ stock.name }}</small></div></td>
                    <td :title="`${stock.specification} · ${stock.brand || '—'}`"><div class="bom-inventory__cell"><span class="bom-inventory__model"><template v-for="(part, index) in inventoryModelDifference(selected.specification || '', stock.specification)" :key="index"><mark v-if="part.changed">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></span><small>{{ stock.brand || '—' }}</small></div></td>
                    <td :title="`${quantity(stock.similarity)}% · ${stock.reason}`"><div class="bom-inventory__cell"><span>{{ quantity(stock.similarity) }}%</span><small>{{ stock.reason }}</small></div></td><td><div class="bom-inventory__cell"><span>{{ quantity(stock.stockQuantity) }}</span><small>{{ stock.locations.length }} 条库位明细</small></div></td>
                    <td><button type="button" class="pdm-secondary-action" :disabled="refreshing" @click="inspect(stock)">{{ stock.materialCode.trim().toUpperCase() === selected.drawingNumber.trim().toUpperCase() ? '原料号库存' : '查看对比' }}</button></td>
                  </tr></tbody>
                </table></div>
                <section v-if="candidate" class="bom-inventory__comparison" aria-label="替换物料对比">
                  <header><strong>库存明细与确认</strong><button type="button" class="pdm-secondary-action" :disabled="refreshing || candidateLoading" @click="refreshCandidate">{{ refreshing ? '刷新中…' : '刷新库存' }}</button></header>
                  <p v-if="candidateLoading" role="status">正在核对已批准料品…</p>
                  <p v-if="candidateError" class="bom-inventory__error" role="alert">{{ candidateError }}</p>
                  <p v-if="material" class="bom-inventory__material">已批准料品：{{ material.materialCode }} · {{ material.name }}<br>型号 {{ material.specification || '—' }} · 品牌 {{ material.brand || '—' }} · 单位 {{ u9UnitName(material.unitCode) }}</p>
                  <div class="bom-inventory__table-wrap"><table class="bom-inventory__locations"><thead><tr><th>仓库／库位</th><th>项目／子项目</th><th>现存量</th><th>更新时间</th></tr></thead><tbody><tr v-for="(location, index) in candidate.locations" :key="index"><td :title="`${location.warehouseName} · ${location.binName || location.binCode || '—'}`"><div class="bom-inventory__cell"><span>{{ location.warehouseName }}</span><small>{{ location.binName || location.binCode || '—' }}</small></div></td><td :title="`${location.projectCode || '公共库存'} · ${location.subproject || '—'}`"><div class="bom-inventory__cell"><span>{{ location.projectCode || '公共库存' }}</span><small>{{ location.subproject || '—' }}</small></div></td><td><div class="bom-inventory__cell">{{ quantity(location.stockQuantity) }}</div></td><td :title="time(location.refreshedAt)"><div class="bom-inventory__cell">{{ time(location.refreshedAt) }}</div></td></tr></tbody></table></div>
                  <p class="bom-inventory__hint">库存按快照展示，现存量不等于可领用量；其他项目库存需先协调调拨。型号相似不代表适用，图档来源物料还需核对模型及装配。</p>
                  <div class="bom-inventory__decision" v-if="!decisions[selectedKey]">
                    <label><input v-model="confirmed" type="checkbox" :disabled="!material || !!candidateError || refreshing">我已核对型号差异，确认选用此物料</label>
                    <div><button type="button" class="pdm-secondary-action" @click="retain">保留原物料</button><button type="button" class="pdm-primary-action" :disabled="!canAdopt" @click="adopt">确认替换</button></div>
                  </div>
                  <p v-else class="bom-inventory__decision-status" role="status">{{ decisions[selectedKey] }}</p>
                </section>
              </template>
              <div v-else class="bom-inventory__empty"><strong>{{ scanning ? '正在扫描库存…' : '暂无库存推荐' }}</strong><span>仅显示有库存推荐的BOM物料，选中左侧物料后查看对应库存。</span></div>
            </main>
          </section>
        </div>
        <footer><span>确认替换后为未保存修改，请关闭此窗口后点击“保存BOM”；已发布历史版本保留。</span><button type="button" class="pdm-secondary-action" @click="emit('close')">关闭</button></footer>
      </RawDrawerPanel>
    </div>
  </Teleport>
</template>

<style scoped>
.bom-inventory { width: min(1360px, 96vw); }
.bom-inventory button.pdm-secondary-action, .bom-inventory button.pdm-primary-action { flex-shrink: 0; width: 80px; min-width: 80px; height: 30px; padding: 0 4px; white-space: nowrap; }
.bom-inventory__toolbar { display: flex; justify-content: space-between; align-items: center; padding: 10px 16px; border-bottom: 1px solid var(--pdm-border); font-size: 12px; gap: 8px; }
.bom-inventory__body { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); flex: 1; min-height: 0; overflow: hidden; gap: 10px; padding: 10px; }
.bom-inventory__pane { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; border: 1px solid var(--pdm-border); border-radius: 6px; }
.bom-inventory__pane > header { display: flex; flex: 0 0 auto; align-items: center; justify-content: space-between; gap: 8px; padding: 10px 12px; border-bottom: 1px solid var(--pdm-border); background: var(--pdm-surface-muted); font-size: 12px; }
.bom-inventory__pane > header span { color: var(--pdm-muted); font-size: 11px; }
.bom-inventory__rows { min-height: 0; flex: 1; overflow: auto; }
.bom-inventory__existing-table { min-width: 500px; table-layout: fixed; }
.bom-inventory__existing-table th:nth-child(1) { width: 18%; }
.bom-inventory__existing-table th:nth-child(2) { width: 23%; }
.bom-inventory__existing-table th:nth-child(3) { width: 29%; }
.bom-inventory__existing-table th:nth-child(4) { width: 12%; }
.bom-inventory__existing-table th:nth-child(5) { width: 18%; }
.bom-inventory__existing-table thead { position: sticky; top: 0; z-index: 1; }
.bom-inventory__existing-table tbody tr { cursor: pointer; }
.bom-inventory__existing-table tbody tr.is-selected td { background: var(--pdm-theme-accent-soft); color: var(--pdm-primary); }
.bom-inventory__existing-table tbody tr:focus-visible { outline: 2px solid var(--pdm-primary); outline-offset: -2px; }
.bom-inventory small { color: var(--pdm-muted); font-size: 11px; }
.bom-inventory__detail { min-width: 0; min-height: 0; flex: 1; overflow: auto; padding: 10px; }
.bom-inventory__selection, .bom-inventory__material { margin: 0 0 10px; font-size: 12px; overflow-wrap: anywhere; line-height: 1.7; }
.bom-inventory table { border-collapse: collapse; width: 100%; font-size: 12px; }
.bom-inventory th, .bom-inventory td { padding: 8px; border: 1px solid var(--pdm-border); text-align: center; vertical-align: middle; overflow-wrap: anywhere; }
.bom-inventory th { background: var(--pdm-surface-muted); font-weight: 500; }
.bom-inventory td small { display: block; margin-top: 4px; }
.bom-inventory__candidates { min-width: 530px; }
.bom-inventory__candidates .is-selected { background: var(--pdm-theme-accent-soft); }
.bom-inventory__table-wrap { max-width: 100%; overflow: auto; }
.bom-inventory__comparison { margin-top: 18px; }
.bom-inventory__comparison > header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px; font-size: 12px; }
.bom-inventory__locations { min-width: 470px; }
.bom-inventory__hint { font-size: 11px; color: var(--pdm-muted); line-height: 1.7; }
.bom-inventory mark { background: var(--pdm-orange-soft); color: var(--pdm-orange); font-weight: 600; padding: 1px 2px; border-radius: 2px; }
.bom-inventory__error { color: var(--pdm-danger); font-size: 12px; padding: 0 12px; }
.bom-inventory__decision { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; padding: 14px 0; font-size: 12px; }
.bom-inventory__decision label { display: flex; gap: 6px; align-items: center; }
.bom-inventory__decision > div { display: flex; gap: 5px; }
.bom-inventory__decision-status { color: var(--pdm-primary); font-size: 12px; }
.bom-inventory__empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; min-height: 160px; padding: 20px; color: var(--pdm-muted); font-size: 12px; text-align: center; }
/* Keep all three material lists at a fixed row height; titles retain the full text. */
.bom-inventory .bom-inventory__existing-table tr, .bom-inventory .bom-inventory__candidates tr, .bom-inventory .bom-inventory__locations tr { height: 40px; }
.bom-inventory .bom-inventory__existing-table th, .bom-inventory .bom-inventory__existing-table td,
.bom-inventory .bom-inventory__candidates th, .bom-inventory .bom-inventory__candidates td,
.bom-inventory .bom-inventory__locations th, .bom-inventory .bom-inventory__locations td { box-sizing: border-box; height: 40px; padding: 4px; line-height: 15px; }
.bom-inventory__candidates, .bom-inventory__locations { table-layout: fixed; }
.bom-inventory__candidates th:nth-child(1) { width: 22%; }
.bom-inventory__candidates th:nth-child(2) { width: 25%; }
.bom-inventory__candidates th:nth-child(3) { width: 20%; }
.bom-inventory__candidates th:nth-child(4) { width: 16%; }
.bom-inventory__candidates th:nth-child(5) { width: 17%; }
.bom-inventory__cell { max-height: 30px; line-height: 15px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bom-inventory__cell > span, .bom-inventory__cell > small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bom-inventory td .bom-inventory__cell small { margin-top: 0; line-height: 15px; }
.bom-inventory__model-cell { white-space: normal; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
.bom-inventory__model-cell mark { display: inline; }
.bom-inventory__toolbar-actions, .bom-inventory__settings-actions { display: flex; gap: 5px; flex-shrink: 0; }
.bom-inventory__scope { padding: 6px 16px; font-size: 11px; color: var(--pdm-muted); overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.bom-inventory__settings { flex: 0 0 auto; padding: 10px 16px; border-bottom: 1px solid var(--pdm-border); font-size: 12px; }
.bom-inventory__settings p { margin: 0 0 8px; color: var(--pdm-muted); }
.bom-inventory__warehouses { display: grid; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); gap: 7px 12px; max-height: 130px; overflow: auto; }
.bom-inventory__warehouses label { display: flex; align-items: center; gap: 5px; }
.bom-inventory__settings-actions { justify-content: flex-end; margin-top: 10px; }
@media (max-width: 900px) { .bom-inventory__body { grid-template-columns: 1fr; grid-template-rows: minmax(180px, .8fr) minmax(240px, 1.2fr); } }
</style>
