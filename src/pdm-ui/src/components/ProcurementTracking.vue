<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { getProjectProcurementTracking, listMaterialInventory, refreshProjectProcurementTracking } from '../api'
import type { ProjectProcurementTrackingItem, ProjectProcurementTrackingResult, U9InventoryRow } from '../types'
import { inventoryScopes, isDefaultInventoryWarehouse, sumProcurementInventory } from '../procurementInventory'
import SquareLoader from './SquareLoader.vue'
import { downloadProcurementWorkbook } from '../procurementWorkbook'

const props = defineProps<{ projectId: string; token: string; username: string }>()

type ColumnKey = keyof Pick<ProjectProcurementTrackingItem,
  'sequence' | 'projectCode' | 'subprojectCode' | 'materialCode' | 'materialName' | 'specification' | 'remark' | 'brand' |
  'quantity' | 'purchaseRequisitionNumbers' | 'purchaseRequisitionStatus' | 'purchaseRequisitionCreatedAt' | 'purchaseRequisitionDeliveryDate' |
  'purchaseOrderNumbers' | 'purchaseOrderStatus' | 'buyerName' | 'purchaseQuantity' | 'arrivedQuantity' | 'purchaseRemark' |
  'purchaseDeliveryDate' | 'latestDeliveryDate' | 'bomKind' | 'releasePackageNumber' | 'requestedQuantity' | 'approvedQuantity'> | 'inventoryQuantity' | MovementColumn

type MovementColumn = 'receiptDate' | 'receiptQuantity' | 'issueDate' | 'issueQuantity'
const movementColumns: MovementColumn[] = ['receiptDate', 'receiptQuantity', 'issueDate', 'issueQuantity']
const isMovementColumn = (key: ColumnKey): key is MovementColumn => movementColumns.includes(key as MovementColumn)

interface ColumnDefinition { key: ColumnKey; label: string; width: number; fixedWidth?: number; defaultVisible: boolean; align?: 'left' | 'center' | 'right' }

const columnDefinitions: ColumnDefinition[] = [
  { key: 'sequence', label: '序号', width: 64, fixedWidth: 40, defaultVisible: true, align: 'center' },
  { key: 'projectCode', label: '项目号', width: 118, fixedWidth: 70, defaultVisible: true },
  { key: 'subprojectCode', label: '子项目号', width: 126, fixedWidth: 70, defaultVisible: true },
  { key: 'materialCode', label: '物料编码', width: 142, fixedWidth: 85, defaultVisible: true },
  { key: 'materialName', label: '物料名称', width: 240, fixedWidth: 120, defaultVisible: true },
  { key: 'specification', label: '型号', width: 260, defaultVisible: true },
  { key: 'remark', label: '备注', width: 150, defaultVisible: true },
  { key: 'brand', label: '品牌', width: 110, defaultVisible: true },
  { key: 'quantity', label: '数量', width: 88, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'inventoryQuantity', label: '库存', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'purchaseRequisitionNumbers', label: 'PR编号', width: 170, defaultVisible: false },
  { key: 'purchaseRequisitionStatus', label: '请购状态', width: 132, fixedWidth: 60, defaultVisible: true },
  { key: 'purchaseRequisitionCreatedAt', label: '请购日期', width: 132, fixedWidth: 70, defaultVisible: true },
  { key: 'purchaseRequisitionDeliveryDate', label: '需求日期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'purchaseOrderNumbers', label: 'PO编号', width: 170, defaultVisible: true },
  { key: 'purchaseOrderStatus', label: '采购状态', width: 124, fixedWidth: 60, defaultVisible: true },
  { key: 'buyerName', label: '采购员', width: 124, fixedWidth: 50, defaultVisible: true },
  { key: 'purchaseQuantity', label: '购买数', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'arrivedQuantity', label: '到货数', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'purchaseRemark', label: '采购备注', width: 180, defaultVisible: true },
  { key: 'purchaseDeliveryDate', label: '预计交期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'latestDeliveryDate', label: '最新交期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'receiptDate', label: '入库日期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'receiptQuantity', label: '入库数', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'issueDate', label: '出库日期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'issueQuantity', label: '出库数', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'bomKind', label: 'BOM类别', width: 96, defaultVisible: false },
  { key: 'releasePackageNumber', label: '发布包号', width: 170, defaultVisible: false },
  { key: 'requestedQuantity', label: '请购数量', width: 100, fixedWidth: 40, defaultVisible: false, align: 'right' },
  { key: 'approvedQuantity', label: '已批准数量', width: 112, fixedWidth: 40, defaultVisible: false, align: 'right' },
]

const loading = ref(false)
const exporting = ref(false)
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
let inventoryRequests = new Map<string, Promise<void>>()

function drainInventoryQueue() {
  while (inventoryWorkers < 4 && inventoryQueue.length) {
    const task = inventoryQueue.shift()!
    inventoryWorkers++
    void task().finally(() => { inventoryWorkers--; drainInventoryQueue() })
  }
}

function loadVisibleInventory(items = pagedItems.value) {
  const version = loadVersion
  const token = props.token
  for (const row of items) {
    const code = row.materialCode.trim()
    if (!code || inventory.value[code]) continue
    inventory.value[code] = { hint: '正在查询库存快照' }
    let completed!: () => void
    inventoryRequests.set(code, new Promise<void>(resolve => { completed = resolve }))
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
      } finally {
        completed()
      }
    })
  }
  drainInventoryQueue()
  return Promise.all(items.map(row => inventoryRequests.get(row.materialCode.trim())))
}
const page = ref(1)
const pageSize = ref(50)
const filterFields = [
  { key: 'brand', label: '品牌' },
  { key: 'purchaseRequisitionStatus', label: '请购状态' },
  { key: 'purchaseOrderStatus', label: '采购状态' },
  { key: 'buyerName', label: '采购员' },
] as const
type FilterKey = typeof filterFields[number]['key']
const multiFilterFields = filterFields.filter((field): field is Exclude<typeof filterFields[number], { key: 'brand' }> => field.key !== 'brand')
const filters = reactive({ keyword: '', brand: '', purchaseRequisitionStatus: [] as string[], purchaseOrderStatus: [] as string[], buyerName: [] as string[], delayedOnly: false, unreceivedOnly: false, unissuedOnly: false })
function resetFilters() {
  Object.assign(filters, { keyword: '', brand: '', purchaseRequisitionStatus: [], purchaseOrderStatus: [], buyerName: [], delayedOnly: false, unreceivedOnly: false, unissuedOnly: false })
}
function filterValues(row: ProjectProcurementTrackingItem, key: FilterKey) {
  const value = row[key]?.trim() ?? ''
  return key === 'buyerName' ? value.split('；').map(name => name.trim()).filter(Boolean) : value ? [value] : []
}
function filterOptions(key: FilterKey) {
  return [...new Set((result.value?.items ?? []).flatMap(row => filterValues(row, key)))].sort((a, b) => a.localeCompare(b, 'zh-CN'))
}
const brandOptions = computed(() => filterOptions('brand'))
const multiFilterOptions = computed(() => ({
  purchaseRequisitionStatus: filterOptions('purchaseRequisitionStatus'),
  purchaseOrderStatus: filterOptions('purchaseOrderStatus'),
  buyerName: filterOptions('buyerName'),
}))
function multiFilterSummary(field: typeof multiFilterFields[number]) {
  const selected = filters[field.key]
  return !selected.length ? `全部${field.label}` : selected.length === 1 ? selected[0] : `${selected[0]} +${selected.length - 1}`
}
const filteredItems = computed(() => {
  const keyword = filters.keyword.trim().toLocaleLowerCase()
  return (result.value?.items ?? []).filter(row =>
    (!keyword || [row.materialCode, row.materialName, row.specification].some(value => value?.toLocaleLowerCase().includes(keyword)))
    && (!filters.brand || filterValues(row, 'brand').some(value => value.toLocaleLowerCase().includes(filters.brand.trim().toLocaleLowerCase())))
    && multiFilterFields.every(({ key }) => !filters[key].length || filterValues(row, key).some(value => filters[key].includes(value)))
    && (!filters.delayedOnly || hasDeliveryDelay(row))
    && (!filters.unreceivedOnly || !movementFor(row, 'receiptQuantity'))
    && (!filters.unissuedOnly || !movementFor(row, 'issueQuantity')))
})
const pageCount = computed(() => Math.max(1, Math.ceil(filteredItems.value.length / pageSize.value)))
const pagedItems = computed(() => sortedItems.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
const settingsVisible = ref(false)
const deliveryColumns = ['purchaseRequisitionDeliveryDate', 'purchaseDeliveryDate', 'latestDeliveryDate'] as const
type DeliveryColumn = typeof deliveryColumns[number]
const deliverySort = ref<{ prop: DeliveryColumn; order: 'ascending' | 'descending' } | null>(null)
const sortedItems = computed(() => {
  const items = filteredItems.value
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
    for (const [key, after] of [
      ['purchaseRequisitionCreatedAt', 'purchaseRequisitionStatus'],
      ['buyerName', 'purchaseOrderStatus'],
      ['receiptDate', 'latestDeliveryDate'],
      ['receiptQuantity', 'receiptDate'],
      ['issueDate', 'receiptQuantity'],
      ['issueQuantity', 'issueDate'],
    ] as const) {
      if (saved && !savedOrder.includes(key)) {
        orderedKeys.value = orderedKeys.value.filter(column => column !== key)
        orderedKeys.value.splice(orderedKeys.value.indexOf(after) + 1, 0, key)
        if (!visibleKeys.value.includes(key)) visibleKeys.value.push(key)
      }
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
  inventoryRequests = new Map()
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
  if (row.isWarehouseMovementRow || isFullyReceived(row)) return ''
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
  if (row.isWarehouseMovementRow || isFullyReceived(row)) return ''
  const required = deliveryDay(row.purchaseRequisitionDeliveryDate)
  const expected = deliveryDay(row.purchaseDeliveryDate)
  const latest = deliveryDay(row.latestDeliveryDate)
  const delayed = column.property === 'purchaseDeliveryDate' ? expected > required
    : column.property === 'latestDeliveryDate' && latest > required && latest > expected
  return delayed ? 'is-delivery-delay' : ''
}

function purchaseBatchKey(row: ProjectProcurementTrackingItem) {
  const lines = row.details.filter(detail => detail.kind === '采购' && !detail.isCanceled)
    .map(detail => [detail.documentNumber, detail.lineNumber]).sort()
  return lines.length ? JSON.stringify([row.projectCode, row.subprojectCode, row.materialCode, lines]) : null
}

const receivedByBatch = computed(() => {
  const batches = new Map<string, Map<string, number>>()
  for (const row of result.value?.items ?? []) {
    const key = purchaseBatchKey(row)
    if (!key) continue
    const receipts = batches.get(key) ?? new Map<string, number>()
    for (const movement of row.warehouseMovements ?? []) {
      if (movement.kind === 'RCV' && Number.isFinite(movement.quantity))
        receipts.set(JSON.stringify([movement.documentNumber, movement.lineNumber]), movement.quantity)
    }
    batches.set(key, receipts)
  }
  return new Map([...batches].map(([key, receipts]) => [key, [...receipts.values()].reduce((sum, quantity) => sum + quantity, 0)]))
})

function isFullyReceived(row: ProjectProcurementTrackingItem) {
  // The server includes project transfers once across all outstanding PO batches.
  if (typeof row.isFullyReceived === 'boolean') return row.isFullyReceived
  if (!(row.purchaseQuantity > 0)) return false
  const key = purchaseBatchKey(row)
  const received = key ? receivedByBatch.value.get(key) ?? 0
    : (row.warehouseMovements ?? []).filter(movement => movement.kind === 'RCV' && Number.isFinite(movement.quantity))
      .reduce((sum, movement) => sum + movement.quantity, 0)
  return received >= row.purchaseQuantity
}

function hasDeliveryDelay(row: ProjectProcurementTrackingItem) {
  return row.hasDeliveryDelay ?? (deliveryDay(row.purchaseDeliveryDate) > deliveryDay(row.purchaseRequisitionDeliveryDate))
}

function cellText(row: ProjectProcurementTrackingItem, key: ColumnKey) {
  if (isMovementColumn(key)) return movementText(row, key)
  if (row.isWarehouseMovementRow && ['purchaseQuantity', 'arrivedQuantity', 'inventoryQuantity'].includes(key)) return '—'
  if (key === 'inventoryQuantity') {
    const rows = inventory.value[row.materialCode.trim()]?.rows
    return rows ? formatNumber(sumProcurementInventory(rows, row.projectCode, selectedInventoryWarehouses.value)) : '—'
  }
  const value = row[key]
  if (Array.isArray(value)) return value.length ? value.join('、') : '—'
  if (key === 'purchaseRequisitionCreatedAt' || key === 'purchaseRequisitionDeliveryDate' || key === 'purchaseDeliveryDate' || key === 'latestDeliveryDate') return formatDate(value as string | null)
  if (key === 'quantity' || key === 'purchaseQuantity' || key === 'arrivedQuantity' || key === 'requestedQuantity' || key === 'approvedQuantity') return formatNumber(value as number)
  return value === null || value === undefined || value === '' ? '—' : String(value)
}

function movementFor(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  return row.warehouseMovements?.find(movement => key.startsWith('receipt') ? movement.kind === 'RCV' || movement.kind === 'TRANSFER' || movement.kind === 'STOCKIN' : movement.kind === 'ISSUE' || movement.kind === 'MISC')
}

function movementText(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  const movement = movementFor(row, key)
  if (!movement) return '—'
  if (key.endsWith('Quantity')) return formatNumber(movement.quantity)
  const date = new Date(movement.date)
  if (Number.isNaN(date.getTime())) return '—'
  return date.toLocaleDateString('zh-CN', { timeZone: 'Asia/Shanghai' })
}

function movementHint(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  const movement = movementFor(row, key)
  return movement ? `${movement.kind === 'RCV' ? '标准收货·入库确认日期' : movement.kind === 'TRANSFER' ? '形态转换·转换后·日期' : movement.kind === 'STOCKIN' ? '库存领用·视同入库·确认日期' : movement.kind === 'ISSUE' ? '材料出库单·确认日期' : '杂发单·日期'}：${movement.documentNumber}，${movement.kind === 'TRANSFER' ? '子行' : '行'} ${movement.lineNumber}；${movementText(row, key)}` : ''
}

function movementTone(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  if (!key.startsWith('receipt')) return ''
  const kind = movementFor(row, key)?.kind
  return kind === 'TRANSFER' ? 'is-transfer' : kind === 'STOCKIN' ? 'is-stockin' : ''
}

async function exportExcel() {
  if (exporting.value || loading.value || !sortedItems.value.length || !columns.value.length) return
  const version = loadVersion
  const exportColumns = [...columns.value]
  const items = [...sortedItems.value]
  const warehouses = selectedInventoryWarehouses.value === null ? null : [...selectedInventoryWarehouses.value]
  const projectCode = result.value?.subprojectCode || result.value?.projectCode || props.projectId
  exporting.value = true
  try {
    if (exportColumns.some(column => column.key === 'inventoryQuantity')) await loadVisibleInventory(items)
    if (version !== loadVersion) return
    const rows = items.map(row => exportColumns.map(({ key }) => {
      if (isMovementColumn(key)) {
        if (key.endsWith('Quantity')) return movementFor(row, key)?.quantity ?? '—'
        return movementText(row, key)
      }
      if (row.isWarehouseMovementRow && ['purchaseQuantity', 'arrivedQuantity', 'inventoryQuantity'].includes(key)) return '—'
      if (key === 'inventoryQuantity') {
        const stock = inventory.value[row.materialCode.trim()]
        if (!stock?.rows) throw new Error(`${row.materialCode}：${stock?.hint ?? '库存未加载'}，请刷新后重试`)
        return sumProcurementInventory(stock.rows, row.projectCode, warehouses)
      }
      return typeof row[key] === 'number' ? row[key] as number : cellText(row, key)
    }))
    const date = new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }).replaceAll('-', '')
    const sequenceKey = `upton-pdm:material-status-export:${props.username}:${props.projectId}:${date}`
    const previousSequence = Number(window.localStorage.getItem(sequenceKey) ?? '0')
    if (!Number.isInteger(previousSequence) || previousSequence < 0 || previousSequence >= 99) throw new Error('当日导出流水号不可用或已达99次，请次日再导出')
    const sequence = previousSequence + 1
    window.localStorage.setItem(sequenceKey, String(sequence))
    downloadProcurementWorkbook(`${projectCode}_物料状态清单_${date}_${String(sequence).padStart(2, '0')}.xlsx`, exportColumns.map(column => column.label), rows)
    ElMessage.success(`已导出 ${items.length} 条采购跟踪记录`)
  } catch (error) {
    if (version === loadVersion) ElMessage.error(error instanceof Error ? error.message : '采购跟踪导出失败')
  } finally {
    exporting.value = false
  }
}

watch(() => [props.projectId, props.token], () => { resetFilters(); page.value = 1; void load() })
watch(filters, () => { page.value = 1 })
watch([pagedItems, visibleKeys], () => { if (visibleKeys.value.includes('inventoryQuantity')) void loadVisibleInventory() }, { deep: true })
watch([pageSize, deliverySort], () => { page.value = 1 })
watch(pageCount, () => { page.value = Math.min(page.value, pageCount.value) })
watch(storageKey, restoreColumns)
watch(inventoryStorageKey, restoreInventoryScopes)
onMounted(() => { restoreColumns(); restoreInventoryScopes(); void load() })
onBeforeUnmount(() => {
  loadVersion++
  window.clearTimeout(refreshTimer)
  window.clearTimeout(refreshCooldownTimer)
})
</script>

<template>
  <section class="procurement-tracking pdm-panel pdm-loading-host" aria-label="采购跟踪">
    <SquareLoader v-if="loading" overlay label="正在加载采购跟踪" />
    <header class="procurement-tracking__heading">
      <div class="procurement-tracking__filters" role="group" aria-label="采购跟踪筛选">
      <input v-model="filters.keyword" type="search" aria-label="搜索料号、名称、型号" placeholder="搜索料号、名称、型号" class="procurement-tracking__search">
      <input v-model="filters.brand" type="search" :list="`procurement-brands-${projectId}`" placeholder="全部品牌" aria-label="筛选品牌" class="procurement-tracking__brand-filter">
      <datalist :id="`procurement-brands-${projectId}`"><option v-for="brand in brandOptions" :key="brand" :value="brand" /></datalist>
      <el-popover v-for="field in multiFilterFields" :key="field.key" placement="bottom-start" trigger="click" :width="190">
        <template #reference>
          <button type="button" class="procurement-tracking__multi-filter" :class="{ 'has-value': filters[field.key].length }" :aria-label="`筛选${field.label}`">
            <span>{{ multiFilterSummary(field) }}</span><span aria-hidden="true">⌄</span>
          </button>
        </template>
        <div class="procurement-tracking__multi-options" :aria-label="`${field.label}多选项`">
          <label v-for="value in multiFilterOptions[field.key]" :key="value"><input v-model="filters[field.key]" type="checkbox" :value="value">{{ value }}</label>
          <span v-if="!multiFilterOptions[field.key].length">暂无选项</span>
          <button v-if="filters[field.key].length" type="button" @click="filters[field.key] = []">清空</button>
        </div>
      </el-popover>
      <label class="procurement-tracking__delay-filter"><input v-model="filters.delayedOnly" type="checkbox" aria-label="交期不符">交期不符</label>
      <label class="procurement-tracking__delay-filter"><input v-model="filters.unreceivedOnly" type="checkbox" aria-label="未入库">未入库</label>
      <label class="procurement-tracking__delay-filter"><input v-model="filters.unissuedOnly" type="checkbox" aria-label="未出库">未出库</label>
      <el-button @click="resetFilters">重置筛选</el-button>
      </div>
      <div class="procurement-tracking__actions">
        <el-button @click="settingsVisible = true">列设置</el-button>
        <el-button type="primary" :loading="refreshStarting" :disabled="refreshCoolingDown" @click="refreshFromU9">立即刷新</el-button>
        <el-button @click="openInventorySettings">库存设置</el-button>
        <el-button :loading="exporting" :disabled="loading || !filteredItems.length || !columns.length" @click="exportExcel">导出 Excel</el-button>
      </div>
      <span class="procurement-tracking__updated">刷新：{{ formatDate(result?.lastSuccessfulRefreshAt, true) }}</span>
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
            : result?.items.length ? '没有符合筛选条件的记录' : '暂无采购跟踪数据' }}
        </div>
      </template>
      <el-table-column
        v-for="column in columns"
        :key="column.key"
        :prop="column.key"
        :label="column.label"
        :sortable="deliveryColumns.includes(column.key as DeliveryColumn) ? 'custom' : false"
        :width="column.fixedWidth"
        :min-width="column.fixedWidth ? undefined : column.width / 2.5"
        :resizable="false"
        :align="column.align ?? 'left'"
        show-overflow-tooltip
      >
        <template #default="{ row }">
          <span v-if="isMovementColumn(column.key)" class="procurement-tracking__movement" :class="movementTone(row, column.key)" :title="movementHint(row, column.key)">{{ movementText(row, column.key) }}</span>
          <el-tag v-else-if="(column.key === 'purchaseRequisitionStatus' || column.key === 'purchaseOrderStatus') && !row.isWarehouseMovementRow" :type="statusType(row[column.key])" size="small">{{ row[column.key] }}</el-tag>
          <span v-else-if="column.key === 'inventoryQuantity'" :title="inventoryHint(row)">{{ cellText(row, column.key) }}</span>
          <span v-else :title="cellText(row, column.key)">{{ cellText(row, column.key) }}</span>
        </template>
      </el-table-column>
    </el-table>

    <div v-if="result?.items.length" class="procurement-tracking__pagination" aria-label="备料明细分页">
      <span>共 {{ filteredItems.length }} 条<span v-if="filteredItems.length !== result.items.length">（全部 {{ result.items.length }} 条）</span></span>
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
.procurement-tracking__filters{display:flex;flex-wrap:nowrap;align-items:center;gap:8px;flex:0 0 auto;margin:0}
.procurement-tracking__brand-filter{width:118px;flex:0 0 118px}
.procurement-tracking__brand-filter{height:30px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff);color:var(--pdm-text);font:inherit;font-size:12px;padding:0 8px;min-width:0}
.procurement-tracking__movement{white-space:pre-line;line-height:18px;display:block}
.procurement-tracking__movement.is-transfer{color:var(--pdm-orange)}
.procurement-tracking__movement.is-stockin{color:var(--pdm-blue)}
.procurement-tracking__search{height:30px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff);color:var(--pdm-text);font:inherit;font-size:12px;padding:0 8px;min-width:0}
.procurement-tracking__multi-filter{display:flex;width:118px;max-width:100%;height:30px;box-sizing:border-box;flex:0 0 118px;align-items:center;justify-content:space-between;gap:5px;padding:0 8px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,#fff);color:var(--pdm-text);font:inherit;font-size:12px;cursor:pointer}.procurement-tracking__multi-filter>span:first-child{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.procurement-tracking__multi-filter.has-value{border-color:var(--pdm-blue);color:var(--pdm-blue)}.procurement-tracking__search{width:220px;max-width:100%}
.procurement-tracking__multi-options{display:grid;gap:7px;max-height:260px;overflow:auto}.procurement-tracking__multi-options label{display:flex;align-items:center;gap:7px;color:var(--pdm-text);font-size:12px}.procurement-tracking__multi-options>span{color:var(--pdm-muted);font-size:12px}.procurement-tracking__multi-options>button{justify-self:end;border:0;background:transparent;color:var(--pdm-blue);font-size:12px;cursor:pointer}
.procurement-tracking__delay-filter{display:flex;align-items:center;gap:4px;font-size:12px;color:var(--pdm-text);white-space:nowrap}
.procurement-tracking__filters .el-button{height:30px;margin:0}
.procurement-tracking__warehouse-list{display:flex;flex-direction:column;max-height:60vh;overflow:auto}
.procurement-tracking__pagination{display:flex;flex:0 0 auto;align-items:center;justify-content:flex-end;gap:8px;padding:8px 10px;color:var(--pdm-muted);font-size:11px}
.procurement-tracking__pagination select{height:28px;padding:0 24px 0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;color:var(--pdm-text)}
.procurement-tracking__pagination .pdm-secondary-action{width:28px;min-width:28px;height:28px;min-height:28px;padding:0}
.procurement-tracking{display:flex;flex-direction:column;min-width:0;min-height:0;height:100%;padding:8px 18px 18px}.procurement-tracking__heading{display:flex;overflow-x:auto;align-items:center;justify-content:space-between;gap:8px;min-height:30px;flex-shrink:0;margin-bottom:6px}.procurement-tracking__actions{display:flex;align-items:center;gap:8px;flex-shrink:0}.procurement-tracking__actions :deep(.el-button){box-sizing:border-box;width:80px;min-width:80px;height:30px;min-height:30px;flex:0 0 80px;margin:0;padding:0 8px}.procurement-tracking__updated{margin-left:auto;flex-shrink:0;white-space:nowrap;color:#64748b;font-size:12px}.procurement-tracking__table{min-height:0;flex:1 1 auto;margin-top:0}.procurement-tracking__settings-note{margin:0 0 12px;color:#64748b;line-height:1.6}.procurement-tracking__column-list{max-height:460px;overflow:auto;border:1px solid #e2e8f0;border-radius:8px}.procurement-tracking__column-item{display:flex;align-items:center;justify-content:space-between;padding:7px 12px;border-bottom:1px solid #eef2f7}.procurement-tracking__column-item:last-child{border-bottom:0}.procurement-tracking :deep(.is-procurement-late td.el-table__cell){background:#fff7ed!important}@media(max-width:1000px){.procurement-tracking__heading{flex-wrap:nowrap}}
</style>

<style scoped>
/* Fit the panel with single-line cells; overflow tooltips retain access to full text. */
.procurement-tracking__table { width: 100%; max-width: 100%; }
.procurement-tracking__table :deep(.cell) { min-width: 0; padding-right: 6px; padding-left: 6px; text-align: center; font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.procurement-tracking__table :deep(.cell.el-tooltip) { min-width: 0; max-width: 100%; }
.procurement-tracking__table :deep(.el-tag) { max-width: 100%; padding-right: 3px; padding-left: 3px; border: 0; background: transparent; font-size: 11px; }
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
