<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { getProjectProcurementTracking, listMaterialInventory, refreshProjectProcurementTracking } from '../api'
import type { ProjectProcurementTrackingItem, ProjectProcurementTrackingResult, U9InventoryRow } from '../types'
import { inventoryScopes, isDefaultInventoryWarehouse, sumProcurementInventory } from '../procurementInventory'
import SquareLoader from './SquareLoader.vue'

const props = defineProps<{ projectId: string; token: string; username: string }>()

type ColumnKey = keyof Pick<ProjectProcurementTrackingItem,
  'sequence' | 'projectCode' | 'subprojectCode' | 'materialCode' | 'materialName' | 'specification' | 'remark' | 'brand' |
  'quantity' | 'purchaseRequisitionNumbers' | 'purchaseRequisitionStatus' | 'purchaseRequisitionDeliveryDate' |
  'purchaseOrderNumbers' | 'purchaseOrderStatus' | 'purchaseQuantity' | 'arrivedQuantity' | 'purchaseRemark' |
  'purchaseDeliveryDate' | 'latestDeliveryDate' | 'bomKind' | 'releasePackageNumber' | 'requestedQuantity' | 'approvedQuantity'> | 'inventoryQuantity'

interface ColumnDefinition { key: ColumnKey; label: string; width: number; fixedWidth?: number; defaultVisible: boolean; align?: 'left' | 'center' | 'right' }

const columnDefinitions: ColumnDefinition[] = [
  { key: 'sequence', label: '序号', width: 64, defaultVisible: true, align: 'center' },
  { key: 'projectCode', label: '项目号', width: 118, fixedWidth: 85, defaultVisible: true },
  { key: 'subprojectCode', label: '子项目号', width: 126, fixedWidth: 85, defaultVisible: true },
  { key: 'materialCode', label: '物料编码', width: 142, fixedWidth: 100, defaultVisible: true },
  { key: 'materialName', label: '物料名称', width: 240, defaultVisible: true },
  { key: 'specification', label: '型号', width: 260, defaultVisible: true },
  { key: 'remark', label: '备注', width: 150, defaultVisible: true },
  { key: 'brand', label: '品牌', width: 110, defaultVisible: true },
  { key: 'quantity', label: '数量', width: 88, defaultVisible: true, align: 'right' },
  { key: 'inventoryQuantity', label: '库存', width: 100, defaultVisible: true, align: 'right' },
  { key: 'purchaseRequisitionNumbers', label: 'PR编号', width: 170, defaultVisible: false },
  { key: 'purchaseRequisitionStatus', label: '请购状态', width: 132, defaultVisible: true },
  { key: 'purchaseRequisitionDeliveryDate', label: '请购交期', width: 116, defaultVisible: true },
  { key: 'purchaseOrderNumbers', label: 'PO编号', width: 170, defaultVisible: true },
  { key: 'purchaseOrderStatus', label: '采购状态', width: 124, defaultVisible: true },
  { key: 'purchaseQuantity', label: '购买数', width: 100, defaultVisible: true, align: 'right' },
  { key: 'arrivedQuantity', label: '到货数', width: 100, defaultVisible: true, align: 'right' },
  { key: 'purchaseRemark', label: '采购备注', width: 180, defaultVisible: true },
  { key: 'purchaseDeliveryDate', label: '预计交期', width: 116, defaultVisible: true },
  { key: 'latestDeliveryDate', label: '最新交期', width: 116, defaultVisible: true },
  { key: 'bomKind', label: 'BOM类别', width: 96, defaultVisible: false },
  { key: 'releasePackageNumber', label: '发布包号', width: 170, defaultVisible: false },
  { key: 'requestedQuantity', label: '请购数量', width: 100, defaultVisible: false, align: 'right' },
  { key: 'approvedQuantity', label: '已批准数量', width: 112, defaultVisible: false, align: 'right' },
]

const loading = ref(false)
const refreshStarting = ref(false)
const refreshCoolingDown = ref(false)
const result = ref<ProjectProcurementTrackingResult | null>(null)
const inventory = ref<Record<string, { rows?: U9InventoryRow[]; hint: string }>>({})
const selectedInventoryWarehouses = ref<string[] | null>(null)
const draftInventoryWarehouses = ref<string[]>([])
const inventoryWarehouseNames = ref<string[]>([])
const inventoryWarehousesLoading = ref(false)
const inventoryWarehousesError = ref('')
const inventorySettingsVisible = ref(false)
const inventoryStorageKey = computed(() => `upton-pdm:procurement-inventory:${props.username || 'anonymous'}`)

function restoreInventoryScopes() {
  selectedInventoryWarehouses.value = null
  try {
    const saved = JSON.parse(window.localStorage.getItem(inventoryStorageKey.value) ?? 'null') as { warehouses?: unknown } | null
    if (Array.isArray(saved?.warehouses)) selectedInventoryWarehouses.value = saved.warehouses.filter((name): name is string => typeof name === 'string')
  } catch { /* 无法读取个人设置时采用默认五类仓库。 */ }
}

async function openInventorySettings() {
  const version = loadVersion
  inventoryWarehousesLoading.value = true
  inventoryWarehousesError.value = ''
  inventorySettingsVisible.value = true
  try {
    const response = await listMaterialInventory({ positiveStockOnly: false, page: 1, pageSize: 1 }, props.token)
    if (version !== loadVersion) return
    inventoryWarehouseNames.value = [...new Set(response.warehouseNames)].sort((a, b) => a.localeCompare(b, 'zh-CN'))
    draftInventoryWarehouses.value = selectedInventoryWarehouses.value === null
      ? inventoryWarehouseNames.value.filter(isDefaultInventoryWarehouse)
      : inventoryWarehouseNames.value.filter(name => selectedInventoryWarehouses.value!.includes(name))
  } catch (error) {
    if (version === loadVersion) inventoryWarehousesError.value = error instanceof Error ? error.message : '仓库清单加载失败，请重新打开'
  } finally { inventoryWarehousesLoading.value = false }
}

function saveInventoryScopes() {
  try {
    window.localStorage.setItem(inventoryStorageKey.value, JSON.stringify({ warehouses: draftInventoryWarehouses.value }))
    selectedInventoryWarehouses.value = [...draftInventoryWarehouses.value]
    inventorySettingsVisible.value = false
    ElMessage.success('库存汇总范围已保存到当前账号')
  } catch { ElMessage.error('库存汇总范围保存失败') }
}

function inventoryHint(row: ProjectProcurementTrackingItem) {
  return `汇总范围：${(selectedInventoryWarehouses.value ?? inventoryScopes).join('、') || '未选择仓库'}；项目仓仅计入项目 ${row.projectCode}（含子项目）。${inventory.value[row.materialCode.trim()]?.hint ?? '暂无库存信息'}`
}
let loadVersion = 0
let inventoryWorkers = 0
let inventoryQueue: Array<() => Promise<void>> = []

function drainInventoryQueue() {
  while (inventoryWorkers < 4 && inventoryQueue.length) {
    const task = inventoryQueue.shift()!
    inventoryWorkers++
    void task().finally(() => { inventoryWorkers--; drainInventoryQueue() })
  }
}

function loadVisibleInventory() {
  if (!visibleKeys.value.includes('inventoryQuantity')) return
  const version = loadVersion
  const token = props.token
  for (const row of pagedItems.value) {
    const code = row.materialCode.trim()
    if (!code || inventory.value[code]) continue
    inventory.value[code] = { hint: '正在查询库存快照' }
    inventoryQueue.push(async () => {
      try {
        const rows: U9InventoryRow[] = []
        let count = 0
        let total: number | undefined
        let refreshedAt: string | null | undefined
        for (let page = 1; ; page++) {
          if (version !== loadVersion) return
          const response = await listMaterialInventory({ materialCode: code, positiveStockOnly: false, page, pageSize: 200 }, token)
          if (version !== loadVersion) return
          if (total !== undefined && (response.total !== total || response.lastSuccessfulRefreshAt !== refreshedAt)) throw new Error('库存快照已变化，请重新刷新')
          total = response.total
          refreshedAt = response.lastSuccessfulRefreshAt
          for (const item of response.items) {
            if (item.materialCode !== code || !Number.isFinite(item.stockQuantity)) throw new Error('库存返回数据不完整')
            rows.push(item)
          }
          count += response.items.length
          if (count === total) break
          if (!response.items.length || count > total) throw new Error('库存明细不完整')
        }
        inventory.value[code] = !refreshedAt && !total
          ? { hint: '暂无成功同步的库存快照' }
          : { rows, hint: `U9C库存快照：按所选范围汇总现存量（库存单位，非可用量）；同料号各批次共用此库存，不可重复累加。最近全量刷新：${formatDate(refreshedAt, true)}` }
      } catch (error) {
        if (version === loadVersion) inventory.value[code] = { hint: error instanceof Error ? `库存查询失败：${error.message}` : '库存查询失败' }
      }
    })
  }
  drainInventoryQueue()
}
const page = ref(1)
const pageSize = ref(50)
const pageCount = computed(() => Math.max(1, Math.ceil((result.value?.items.length ?? 0) / pageSize.value)))
const pagedItems = computed(() => sortedItems.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
const settingsVisible = ref(false)
const deliveryColumns = ['purchaseRequisitionDeliveryDate', 'purchaseDeliveryDate', 'latestDeliveryDate'] as const
type DeliveryColumn = typeof deliveryColumns[number]
const deliverySort = ref<{ prop: DeliveryColumn; order: 'ascending' | 'descending' } | null>(null)
const sortedItems = computed(() => {
  const items = result.value?.items ?? []
  const sort = deliverySort.value
  if (!sort) return items
  return [...items].sort((a, b) => {
    const left = deliveryDay(a[sort.prop])
    const right = deliveryDay(b[sort.prop])
    if (Number.isNaN(left)) return Number.isNaN(right) ? 0 : 1
    if (Number.isNaN(right)) return -1
    return (left - right) * (sort.order === 'ascending' ? 1 : -1)
  })
})

function changeDeliverySort({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }) {
  deliverySort.value = order && deliveryColumns.includes(prop as DeliveryColumn) ? { prop: prop as DeliveryColumn, order } : null
}
const orderedKeys = ref<ColumnKey[]>(columnDefinitions.map(column => column.key))
const visibleKeys = ref<ColumnKey[]>(columnDefinitions.filter(column => column.defaultVisible).map(column => column.key))
let refreshTimer: number | undefined
let refreshCooldownTimer: number | undefined

const storageKey = computed(() => `upton-pdm:procurement-columns:${props.username || 'anonymous'}`)
const columns = computed(() => orderedKeys.value
  .map(key => columnDefinitions.find(column => column.key === key))
  .filter((column): column is ColumnDefinition => Boolean(column) && visibleKeys.value.includes(column!.key)))

function restoreColumns() {
  orderedKeys.value = columnDefinitions.map(column => column.key)
  visibleKeys.value = columnDefinitions.filter(column => column.defaultVisible).map(column => column.key)
  try {
    const saved = JSON.parse(window.localStorage.getItem(storageKey.value) ?? 'null') as { order?: ColumnKey[]; visible?: ColumnKey[] } | null
    const allowed = new Set(columnDefinitions.map(column => column.key))
    const savedOrder = (saved?.order ?? []).filter((key, index, array) => allowed.has(key) && array.indexOf(key) === index)
    orderedKeys.value = [...savedOrder, ...columnDefinitions.map(column => column.key).filter(key => !savedOrder.includes(key))]
    const savedVisible = (saved?.visible ?? []).filter(key => allowed.has(key))
    if (savedVisible.length) visibleKeys.value = savedVisible
    if (saved && !savedOrder.includes('inventoryQuantity')) {
      orderedKeys.value = orderedKeys.value.filter(key => key !== 'inventoryQuantity')
      orderedKeys.value.splice(orderedKeys.value.indexOf('quantity') + 1, 0, 'inventoryQuantity')
      if (!visibleKeys.value.includes('inventoryQuantity')) visibleKeys.value.push('inventoryQuantity')
    }
  } catch {
    // 无法读取个人浏览器设置时使用系统默认列。
  }
}

function saveColumns() {
  if (!visibleKeys.value.length) {
    ElMessage.warning('请至少保留一列')
    return
  }
  window.localStorage.setItem(storageKey.value, JSON.stringify({ order: orderedKeys.value, visible: visibleKeys.value }))
  settingsVisible.value = false
  ElMessage.success('采购跟踪列设置已保存到当前账号')
}

function resetColumns() {
  orderedKeys.value = columnDefinitions.map(column => column.key)
  visibleKeys.value = columnDefinitions.filter(column => column.defaultVisible).map(column => column.key)
}

function moveColumn(key: ColumnKey, direction: -1 | 1) {
  const index = orderedKeys.value.indexOf(key)
  const target = index + direction
  if (index < 0 || target < 0 || target >= orderedKeys.value.length) return
  const next = [...orderedKeys.value]
  ;[next[index], next[target]] = [next[target], next[index]]
  orderedKeys.value = next
}

async function load(showError = true) {
  const version = ++loadVersion
  inventoryQueue = []
  inventory.value = {}
  inventorySettingsVisible.value = false
  result.value = null
  loading.value = true
  try {
    const response = await getProjectProcurementTracking(props.projectId, props.token)
    if (version === loadVersion) result.value = response
  } catch (error) {
    if (version === loadVersion && showError) ElMessage.error(error instanceof Error ? error.message : '采购跟踪加载失败')
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function refreshFromU9() {
  if (refreshStarting.value || refreshCoolingDown.value) return
  refreshStarting.value = true
  refreshCoolingDown.value = true
  refreshCooldownTimer = window.setTimeout(() => { refreshCoolingDown.value = false }, 2000)
  try {
    const response = await refreshProjectProcurementTracking(props.projectId, props.token)
    ElMessage.success(response.message)
    window.clearTimeout(refreshTimer)
    refreshTimer = window.setTimeout(() => void load(false), 2500)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '采购跟踪刷新启动失败')
  } finally {
    refreshStarting.value = false
  }
}

function formatDate(value?: string | null, withTime = false) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return withTime
    ? date.toLocaleString('zh-CN', { hour12: false })
    : date.toLocaleDateString('zh-CN')
}

function formatNumber(value?: number | null) {
  if (value === null || value === undefined) return '—'
  return Number(value).toLocaleString('zh-CN', { maximumFractionDigits: 6 })
}

function statusType(status: string) {
  if (status.includes('取消') || status.includes('未请购')) return 'danger'
  if (status.includes('未采购') || status.includes('审核') || status.includes('开立')) return 'warning'
  if (status.includes('关闭') || status.includes('核准') || status.includes('执行')) return 'success'
  return 'info'
}

function rowClassName({ row }: { row: ProjectProcurementTrackingItem }) {
  if (!row.purchaseRequisitionNumbers.length) return 'is-procurement-missing'
  if (!row.purchaseOrderNumbers.length) return 'is-procurement-warning'
  if (row.latestDeliveryDate && row.arrivedQuantity < row.purchaseQuantity && new Date(row.latestDeliveryDate).getTime() < Date.now()) return 'is-procurement-late'
  return ''
}

function deliveryDay(value?: string | null) {
  if (!value) return Number.NaN
  const date = new Date(value)
  return date.setHours(0, 0, 0, 0)
}

function cellClassName({ row, column }: { row: ProjectProcurementTrackingItem; column: { property: string } }) {
  return column.property === 'purchaseDeliveryDate'
    && (row.hasDeliveryDelay ?? (deliveryDay(row.purchaseDeliveryDate) > deliveryDay(row.purchaseRequisitionDeliveryDate)))
    ? 'is-delivery-delay' : ''
}

function cellText(row: ProjectProcurementTrackingItem, key: ColumnKey) {
  if (key === 'inventoryQuantity') {
    const rows = inventory.value[row.materialCode.trim()]?.rows
    return rows ? formatNumber(sumProcurementInventory(rows, row.projectCode, selectedInventoryWarehouses.value)) : '—'
  }
  const value = row[key]
  if (Array.isArray(value)) return value.length ? value.join('、') : '—'
  if (key === 'purchaseRequisitionDeliveryDate' || key === 'purchaseDeliveryDate' || key === 'latestDeliveryDate') return formatDate(value as string | null)
  if (key === 'quantity' || key === 'purchaseQuantity' || key === 'arrivedQuantity' || key === 'requestedQuantity' || key === 'approvedQuantity') return formatNumber(value as number)
  return value === null || value === undefined || value === '' ? '—' : String(value)
}

watch(() => [props.projectId, props.token], () => { page.value = 1; void load() })
watch([pagedItems, visibleKeys], loadVisibleInventory, { deep: true })
watch([pageSize, deliverySort], () => { page.value = 1 })
watch(pageCount, () => { page.value = Math.min(page.value, pageCount.value) })
watch(storageKey, restoreColumns)
watch(inventoryStorageKey, restoreInventoryScopes)
onMounted(() => { restoreColumns(); restoreInventoryScopes(); void load() })
onBeforeUnmount(() => {
  loadVersion++
  inventoryQueue = []
  window.clearTimeout(refreshTimer)
  window.clearTimeout(refreshCooldownTimer)
})
</script>

<template>
  <section class="procurement-tracking pdm-panel pdm-loading-host" aria-label="采购跟踪">
    <SquareLoader v-if="loading" overlay label="正在加载采购跟踪" />
    <header class="procurement-tracking__heading">
      <div class="procurement-tracking__actions">
        <el-button @click="settingsVisible = true">列设置</el-button>
        <el-button type="primary" :loading="refreshStarting" :disabled="refreshCoolingDown" @click="refreshFromU9">立即刷新</el-button>
        <el-button @click="openInventorySettings">库存设置</el-button>
      </div>
      <span class="procurement-tracking__updated">最近成功刷新：{{ formatDate(result?.lastSuccessfulRefreshAt, true) }}</span>
    </header>

    <el-alert v-if="result?.lastRefreshError" :title="`最近一次刷新失败，当前继续显示上次完整快照：${result.lastRefreshError}`" type="warning" :closable="false" show-icon />

    <el-table
      :data="pagedItems"
      @sort-change="changeDeliverySort"
      :row-class-name="rowClassName"
      :cell-class-name="cellClassName"
      height="100%"
      border
      stripe
      class="procurement-tracking__table"
    >
      <template #empty>
        <div class="procurement-tracking__empty-tip">
          {{ result && !result.hasPublishedBom
            ? '当前项目尚无已发布的标准件或电气件BOM，发布后将自动进入采购跟踪。'
            : '暂无采购跟踪数据' }}
        </div>
      </template>
      <el-table-column
        v-for="column in columns"
        :key="column.key"
        :prop="column.key"
        :label="column.label"
        :sortable="deliveryColumns.includes(column.key as DeliveryColumn) ? 'custom' : false"
        :width="column.fixedWidth"
        :min-width="column.fixedWidth ? undefined : column.width / 10"
        :resizable="false"
        :align="column.align ?? 'left'"
        show-overflow-tooltip
      >
        <template #default="{ row }">
          <el-tag v-if="column.key === 'purchaseRequisitionStatus' || column.key === 'purchaseOrderStatus'" :type="statusType(row[column.key])" size="small">{{ row[column.key] }}</el-tag>
          <span v-else-if="column.key === 'inventoryQuantity'" :title="inventoryHint(row)">{{ cellText(row, column.key) }}</span>
          <span v-else :title="cellText(row, column.key)">{{ cellText(row, column.key) }}</span>
        </template>
      </el-table-column>
    </el-table>

    <div v-if="result?.items.length" class="procurement-tracking__pagination" aria-label="备料明细分页">
      <span>共 {{ result.items.length }} 条</span>
      <select v-model.number="pageSize" aria-label="备料明细每页条数"><option :value="30">30条/页</option><option :value="50">50条/页</option><option :value="100">100条/页</option><option :value="200">200条/页</option></select>
      <button type="button" class="pdm-secondary-action" aria-label="备料明细上一页" :disabled="page <= 1" @click="page -= 1">‹</button>
      <span>{{ page }} / {{ pageCount }}</span>
      <button type="button" class="pdm-secondary-action" aria-label="备料明细下一页" :disabled="page >= pageCount" @click="page += 1">›</button>
    </div>

    <el-dialog v-model="inventorySettingsVisible" title="库存汇总设置" width="520px" append-to-body>
      <p class="procurement-tracking__settings-note">打开时从最新库存快照更新全部仓库清单，可逐仓勾选。默认选择项目仓、呆滞仓、常备仓、退料仓、应急仓。项目仓仅统计当前项目号（含子项目），其他仓库汇总该仓现存量；库存快照不表示实时可用量。</p>
      <p v-if="inventoryWarehousesLoading">正在更新仓库清单…</p>
      <el-alert v-else-if="inventoryWarehousesError" :title="inventoryWarehousesError" type="error" :closable="false" />
      <p v-else-if="!inventoryWarehouseNames.length">当前库存快照暂无仓库。</p>
      <el-checkbox-group v-else v-model="draftInventoryWarehouses" aria-label="库存汇总仓库" class="procurement-tracking__warehouse-list">
        <el-checkbox v-for="name in inventoryWarehouseNames" :key="name" :value="name">{{ name }}</el-checkbox>
      </el-checkbox-group>
      <template #footer>
        <el-button :disabled="inventoryWarehousesLoading || !!inventoryWarehousesError" @click="draftInventoryWarehouses = inventoryWarehouseNames.filter(isDefaultInventoryWarehouse)">恢复默认</el-button>
        <el-button @click="inventorySettingsVisible = false">取消</el-button>
        <el-button type="primary" :disabled="inventoryWarehousesLoading || !!inventoryWarehousesError" @click="saveInventoryScopes">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="settingsVisible" title="采购跟踪列设置" width="520px" append-to-body>
      <div class="procurement-tracking__settings-body">
      <p class="procurement-tracking__settings-note">可按当前账号隐藏、显示和调整列顺序；价格、税额、币种及其他财务信息不提供。</p>
      <el-checkbox-group v-model="visibleKeys" class="procurement-tracking__column-list">
        <div v-for="(key, index) in orderedKeys" :key="key" class="procurement-tracking__column-item">
          <el-checkbox :value="key">{{ columnDefinitions.find(column => column.key === key)?.label }}</el-checkbox>
          <span>
            <el-button text :disabled="index === 0" @click="moveColumn(key, -1)">上移</el-button>
            <el-button text :disabled="index === orderedKeys.length - 1" @click="moveColumn(key, 1)">下移</el-button>
          </span>
        </div>
      </el-checkbox-group>
      </div>
      <template #footer>
        <el-button @click="resetColumns">恢复默认</el-button>
        <el-button @click="settingsVisible = false">取消</el-button>
        <el-button type="primary" @click="saveColumns">保存</el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.procurement-tracking__warehouse-list{display:flex;flex-direction:column;max-height:60vh;overflow:auto}
.procurement-tracking__pagination{display:flex;flex:0 0 auto;align-items:center;justify-content:flex-end;gap:8px;padding:8px 10px;color:var(--pdm-muted);font-size:11px}
.procurement-tracking__pagination select{height:28px;padding:0 24px 0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text)}
.procurement-tracking__pagination .pdm-secondary-action{width:28px;min-width:28px;height:28px;min-height:28px;padding:0}
.procurement-tracking{display:flex;flex-direction:column;min-width:0;min-height:0;height:100%;padding:8px 18px 18px}.procurement-tracking__heading{display:flex;align-items:center;justify-content:space-between;gap:8px;min-height:30px;flex-shrink:0;margin-bottom:6px}.procurement-tracking__actions{display:flex;align-items:center;gap:8px;flex-shrink:0}.procurement-tracking__actions :deep(.el-button){box-sizing:border-box;width:80px;min-width:80px;height:30px;min-height:30px;flex:0 0 80px;margin:0;padding:0 8px}.procurement-tracking__updated{margin-left:auto;color:#64748b;font-size:12px}.procurement-tracking__table{min-height:0;flex:1 1 auto;margin-top:0}.procurement-tracking__settings-note{margin:0 0 12px;color:#64748b;line-height:1.6}.procurement-tracking__column-list{max-height:460px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px}.procurement-tracking__column-item{display:flex;align-items:center;justify-content:space-between;padding:7px 12px;border-bottom:1px solid #eef2f7}.procurement-tracking__column-item:last-child{border-bottom:0}.procurement-tracking :deep(.is-procurement-late td.el-table__cell){background:#fff7ed!important}@media(max-width:1000px){.procurement-tracking__heading{flex-wrap:wrap}}
</style>

<style scoped>
/* Fit the panel with single-line cells; overflow tooltips retain access to full text. */
.procurement-tracking__table { width: 100%; max-width: 100%; }
.procurement-tracking__table :deep(.cell) { min-width: 0; padding-right: 6px; padding-left: 6px; text-align: center; font-size: 13px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.procurement-tracking__table :deep(.cell.el-tooltip) { min-width: 0; max-width: 100%; }
.procurement-tracking__table :deep(.el-tag) { max-width: 100%; padding-right: 3px; padding-left: 3px; border: 0; background: transparent; font-size: 13px; }
.procurement-tracking__table :deep(.el-tag__content) { overflow: hidden; text-overflow: ellipsis; }
.procurement-tracking__settings-body { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.procurement-tracking__settings-note { flex: 0 0 auto; }
.procurement-tracking__column-list { flex: 1 1 auto; min-height: 0; max-height: none; }
.procurement-tracking__empty-tip { position: static; transform: none; padding: 16px; line-height: 1.6; overflow-wrap: anywhere; }
.procurement-tracking :deep(td.el-table__cell.is-delivery-delay) { background: #fee2e2 !important; color: #991b1b; animation: procurement-delivery-delay 2s ease-in-out infinite; }
@keyframes procurement-delivery-delay {
  0%, 100% { box-shadow: inset 0 0 0 100vmax transparent; }
  50% { box-shadow: inset 0 0 0 100vmax #fca5a5; }
}
@media (prefers-reduced-motion: reduce) {
  .procurement-tracking :deep(td.el-table__cell.is-delivery-delay) { animation: none; }
}
</style>
