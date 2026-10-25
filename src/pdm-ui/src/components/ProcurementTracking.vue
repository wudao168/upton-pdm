<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { getProjectExportMetadata, getProjectProcurementTracking, listBom, listMaterialInventory, refreshProjectProcurementTracking } from '../api'
import type { BomItem, ProjectProcurementTrackingItem, ProjectProcurementTrackingResult, U9InventoryRow } from '../types'
import { inventoryScopes, isDefaultInventoryWarehouse, sumProcurementInventory } from '../procurementInventory'
import SquareLoader from './SquareLoader.vue'
import { downloadProcurementWorkbook } from '../procurementWorkbook'
import { isManufacturing, manufacturingValue, manufacturingStats } from '../procurementManufacturing'
import { productionMovementLabels } from '../productionTracking'
import { readTablePreference, tablePreferenceVersion, writeTablePreference } from '../tablePreferences'
import { materialStockPlan } from '../materialStockPlans'

const props = defineProps<{ projectId: string; token: string; username: string }>()

type ColumnKey = keyof Pick<ProjectProcurementTrackingItem,
  'sequence' | 'projectCode' | 'subprojectCode' | 'materialCode' | 'materialName' | 'impactStage' | 'specification' | 'remark' | 'brand' |
  'quantity' | 'purchaseRequisitionNumbers' | 'purchaseRequisitionStatus' | 'purchaseRequisitionCreatedAt' | 'purchaseRequisitionDeliveryDate' |
  'purchaseOrderNumbers' | 'purchaseOrderStatus' | 'buyerName' | 'purchaseQuantity' | 'arrivedQuantity' | 'purchaseRemark' |
  'purchaseDeliveryDate' | 'latestDeliveryDate' | 'bomKind' | 'releasePackageNumber' | 'requestedQuantity' | 'approvedQuantity' |
  'assemblyStartDate' | 'commissioningStartDate'> | 'inventoryQuantity' | MovementColumn | 'dataSource' | 'revision' | 'supplier' | 'productionReceivedAt'

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
  { key: 'impactStage', label: '关键', width: 90, fixedWidth: 60, defaultVisible: true, align: 'center' },
  { key: 'specification', label: '型号', width: 260, defaultVisible: true },
  { key: 'remark', label: '备注', width: 150, defaultVisible: true },
  { key: 'brand', label: '品牌', width: 110, defaultVisible: true },
  { key: 'quantity', label: '数量', width: 88, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'inventoryQuantity', label: '库存', width: 100, fixedWidth: 40, defaultVisible: true, align: 'right' },
  { key: 'assemblyStartDate', label: '装配日期', width: 132, fixedWidth: 70, defaultVisible: true },
  { key: 'commissioningStartDate', label: '调试日期', width: 132, fixedWidth: 70, defaultVisible: true },
  { key: 'purchaseRequisitionNumbers', label: 'PR', width: 170, defaultVisible: false },
  { key: 'purchaseRequisitionStatus', label: '请购状态', width: 132, fixedWidth: 60, defaultVisible: true },
  { key: 'purchaseRequisitionCreatedAt', label: '请购日期', width: 132, fixedWidth: 70, defaultVisible: true },
  { key: 'purchaseRequisitionDeliveryDate', label: '需求日期', width: 100, fixedWidth: 70, defaultVisible: true },
  { key: 'purchaseOrderNumbers', label: 'PO', width: 170, defaultVisible: true },
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
  { key: 'dataSource', label: '数据来源', width: 110, defaultVisible: true },
  { key: 'revision', label: '版次', width: 80, defaultVisible: true },
  { key: 'supplier', label: '外协供应商', width: 150, defaultVisible: true },
  { key: 'productionReceivedAt', label: '生产接收日期', width: 132, defaultVisible: true },
]

const manufacturingColumnHints: Partial<Record<ColumnKey, string>> = {
  inventoryQuantity: '非标件显示检验合格且尚未领用的数量。',
  purchaseRequisitionNumbers: '非标件显示发布包号。',
  purchaseRequisitionStatus: '非标件显示生产接收状态。',
  purchaseRequisitionCreatedAt: '非标件显示发布日期。',
  purchaseOrderNumbers: '非标件显示制造批次。',
  purchaseOrderStatus: '非标件显示制造进度。',
  buyerName: '非标件显示制造负责人。',
  purchaseQuantity: '非标件显示已分配数量。',
  arrivedQuantity: '非标件显示已交检数量，尚不代表检验合格。',
  purchaseRemark: '非标件显示制造备注。',
  purchaseDeliveryDate: '非标件显示预计完成或交货日期。',
  receiptDate: '非标件显示最近一次检验合格日期。',
  receiptQuantity: '非标件显示累计检验合格数量。',
  issueDate: '非标件显示最近一次实际领用日期。',
  issueQuantity: '非标件显示累计实际领用数量。',
  requestedQuantity: '非标件显示需求数量。',
  approvedQuantity: '非标件显示已发布数量。',
}

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
  if (isManufacturing(row)) return '制造中心合格数量减去已领用数量；这是当前批次合格待领用量，不是U9C仓库库存。'
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
    if (isManufacturing(row)) continue
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
  return Promise.all(items.filter(row => !isManufacturing(row)).map(row => inventoryRequests.get(row.materialCode.trim())))
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
const filters = reactive({ keyword: '', brand: '', bomKind: '', purchaseRequisitionStatus: [] as string[], purchaseOrderStatus: [] as string[], buyerName: [] as string[], delayedOnly: false, unreceivedOnly: false, unissuedOnly: false })
function resetFilters() {
  Object.assign(filters, { keyword: '', brand: '', bomKind: '', purchaseRequisitionStatus: [], purchaseOrderStatus: [], buyerName: [], delayedOnly: false, unreceivedOnly: false, unissuedOnly: false })
}
function filterValues(row: ProjectProcurementTrackingItem, key: FilterKey) {
  if (['purchaseRequisitionStatus', 'purchaseOrderStatus'].includes(key) && materialStockPlan(row.materialCode)) return ['常备件']
  const value = String(manufacturingValue(row, key) ?? '').trim()
  return key === 'buyerName' ? value.split('；').map(name => name.trim()).filter(Boolean) : value ? [value] : []
}
function filterOptions(key: FilterKey) {
  return [...new Set((result.value?.items ?? []).filter(matchesBomKind).flatMap(row => filterValues(row, key)))].sort((a, b) => a.localeCompare(b, 'zh-CN'))
}
const brandOptions = computed(() => filterOptions('brand'))
const multiFilterOptions = computed(() => ({
  purchaseRequisitionStatus: filterOptions('purchaseRequisitionStatus'),
  purchaseOrderStatus: filterOptions('purchaseOrderStatus'),
  buyerName: filterOptions('buyerName'),
}))
const bomKindLabels: Record<string, string> = { Standard: '标准件', NonStandard: '非标件', Electrical: '电气件' }
function matchesBomKind(row: ProjectProcurementTrackingItem) {
  if (!filters.bomKind) return true
  return (row.bomKind ?? '').split(/[、，,;；]/).some(kind => (bomKindLabels[kind.trim()] ?? kind.trim()) === filters.bomKind)
}
function multiFilterSummary(field: typeof multiFilterFields[number]) {
  const selected = filters[field.key]
  return !selected.length ? `全部${field.label}` : selected.length === 1 ? selected[0] : `${selected[0]} +${selected.length - 1}`
}
const filteredItems = computed(() => {
  const keyword = filters.keyword.trim().toLocaleLowerCase()
  return (result.value?.items ?? []).filter(row =>
    (!keyword || [row.materialCode, row.materialName, row.specification].some(value => value?.toLocaleLowerCase().includes(keyword)))
    && (!filters.brand || filterValues(row, 'brand').some(value => value.toLocaleLowerCase().includes(filters.brand.trim().toLocaleLowerCase())))
    && matchesBomKind(row)
    && multiFilterFields.every(({ key }) => !filters[key].length || filterValues(row, key).some(value => filters[key].includes(value)))
    && (!filters.delayedOnly || hasDeliveryDelay(row))
    && (!filters.unreceivedOnly || (isManufacturing(row) ? !isFullyReceived(row) : !movementFor(row, 'receiptQuantity')))
    && (!filters.unissuedOnly || (isManufacturing(row) ? (row.manufacturing?.issuedQuantity ?? 0) < (row.quantity ?? 0) : !movementFor(row, 'issueQuantity'))))
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
  if (!sort) return [...items]
  return [...items].sort((a, b) => {
    const left = deliveryDay(manufacturingValue(a,sort.prop) as string | null)
    const right = deliveryDay(manufacturingValue(b,sort.prop) as string | null)
    if (Number.isNaN(left)) return Number.isNaN(right) ? 0 : 1
    if (Number.isNaN(right)) return -1
    return (left - right) * (sort.order === 'ascending' ? 1 : -1)
  })
})

function changeDeliverySort({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }) {
  deliverySort.value = order && deliveryColumns.includes(prop as DeliveryColumn) ? { prop: prop as DeliveryColumn, order } : null
}
const visibleKeys = ref<ColumnKey[]>(columnDefinitions.filter(column => column.defaultVisible).map(column => column.key))
let refreshTimer: number | undefined
let refreshCooldownTimer: number | undefined

const storageKey = computed(() => `upton-pdm:procurement-columns:${props.username || 'anonymous'}`)
const hasManufacturing = computed(() => !!result.value?.items.some(isManufacturing))
const manufacturingOnly = computed(() => filters.bomKind === '非标件' || !!result.value?.items.length && result.value.items.every(isManufacturing))
const mixedSources = computed(() => hasManufacturing.value && !manufacturingOnly.value && !filters.bomKind)
const manufacturingColumns = ['dataSource','revision','supplier','productionReceivedAt']
const columns = computed(() => columnDefinitions.filter(column => visibleKeys.value.includes(column.key)
  && (manufacturingOnly.value || mixedSources.value || !manufacturingColumns.includes(column.key))
  && (!manufacturingOnly.value || column.key !== 'latestDeliveryDate')))
const manufacturingDetail = ref<ProjectProcurementTrackingItem | null>(null), manufacturingDetailOpen = ref(false)
function showManufacturing(row: ProjectProcurementTrackingItem) { manufacturingDetail.value=row; manufacturingDetailOpen.value=true }

function restoreColumns() {
  visibleKeys.value = columnDefinitions.filter(column => column.defaultVisible).map(column => column.key)
  try {
    const saved = JSON.parse(readTablePreference(storageKey.value) ?? 'null') as { version?: number; order?: ColumnKey[]; visible?: ColumnKey[] } | null
    const allowed = new Set(columnDefinitions.map(column => column.key))
    const savedOrder = (saved?.order ?? []).filter((key, index, array) => allowed.has(key) && array.indexOf(key) === index)
    const savedVisible = (saved?.visible ?? []).filter(key => allowed.has(key))
    if (savedVisible.length) visibleKeys.value = savedVisible
    const savedVersion = saved?.version ?? 0
    for (const key of manufacturingColumns as ColumnKey[]) if (saved && savedVersion < 4 && !visibleKeys.value.includes(key)) visibleKeys.value.push(key)
    if (saved && savedVersion < 2 && !savedOrder.includes('inventoryQuantity')) {
      if (!visibleKeys.value.includes('inventoryQuantity')) visibleKeys.value.push('inventoryQuantity')
    }
    for (const key of [
      'impactStage',
      'purchaseRequisitionCreatedAt',
      'buyerName',
      'receiptDate',
      'receiptQuantity',
      'issueDate',
      'issueQuantity',
    ] as const) {
      if (saved && savedVersion < 2 && !savedOrder.includes(key)) {
        if (!visibleKeys.value.includes(key)) visibleKeys.value.push(key)
      }
    }
    for (const key of ['assemblyStartDate', 'commissioningStartDate'] as const) {
      if (saved && savedVersion < 3 && !visibleKeys.value.includes(key)) visibleKeys.value.push(key)
    }
  } catch {
    // 无法读取个人浏览器设置时使用系统默认列。
  }
}

async function saveColumns() {
  if (!visibleKeys.value.length) {
    ElMessage.warning('请至少保留一列')
    return
  }
  let synced: boolean
  try { synced = await writeTablePreference(storageKey.value, JSON.stringify({ version: 4, visible: visibleKeys.value })) }
  catch { ElMessage.error('列设置同步失败，当前选择已保留在本机，请重试保存。'); return }
  settingsVisible.value = false
  if (synced) ElMessage.success('采购跟踪列设置已保存到当前账号')
  else ElMessage.warning('列设置已保存在本机；服务器尚不支持账号同步，请升级后重新保存。')
}

function resetColumns() {
  visibleKeys.value = columnDefinitions.filter(column => column.defaultVisible).map(column => column.key)
}

async function load(showError = true) {
  const version = ++loadVersion
  inventoryRequests = new Map()
  inventory.value = {}
  inventorySettingsVisible.value = false
  result.value = null
  loading.value = true
  try {
    const [response, currentBom] = await Promise.all([
      getProjectProcurementTracking(props.projectId, props.token),
      Promise.allSettled([
        listBom(props.projectId, 'Standard', props.token),
        listBom(props.projectId, 'NonStandard', props.token),
        listBom(props.projectId, 'Electrical', props.token),
      ]),
    ])
    const currentImpactByMaterialCode = new Map<string, BomItem['impactStage']>()
    for (const [index,request] of currentBom.entries()) {
      if (request.status !== 'fulfilled') continue
      for (const item of request.value) {
        const materialCode = item.drawingNumber.trim().toLocaleLowerCase()
        if (materialCode) currentImpactByMaterialCode.set(`${['标准件','非标件','电气件'][index]}|${materialCode}`, item.impactStage)
      }
    }
    if (version === loadVersion) {
      result.value = {
        ...response,
        items: response.items.map(item => {
          const materialCode = `${item.bomKind}|${item.materialCode.trim().toLocaleLowerCase()}`
          return currentImpactByMaterialCode.has(materialCode)
            ? { ...item, impactStage: currentImpactByMaterialCode.get(materialCode) }
            : item
        }),
      }
    }
  } catch (error) {
    if (version === loadVersion && showError) ElMessage.error(error instanceof Error ? error.message : '采购跟踪加载失败')
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function refreshFromU9() {
  if (manufacturingOnly.value) { await load(); if(result.value) ElMessage.success('制造进度已刷新'); return }
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
  if (isManufacturing(row)) return isFullyReceived(row) ? '' : !row.manufacturing?.receivedAt ? 'is-procurement-missing'
    : !row.manufacturing.assignedQuantity ? 'is-procurement-warning' : deliveryDay(row.manufacturing.forecastOn) < deliveryDay(new Date().toISOString()) ? 'is-procurement-late' : ''
  if (row.isWarehouseMovementRow || isFullyReceived(row)) return ''
  if (!materialStockPlan(row.materialCode) && !row.purchaseRequisitionNumbers.length) return 'is-procurement-missing'
  if (!materialStockPlan(row.materialCode) && !row.purchaseOrderNumbers.length) return 'is-procurement-warning'
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
  const required = deliveryDay(manufacturingValue(row,'purchaseRequisitionDeliveryDate') as string | null)
  const expected = deliveryDay(manufacturingValue(row,'purchaseDeliveryDate') as string | null)
  const latest = deliveryDay(manufacturingValue(row,'latestDeliveryDate') as string | null)
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
  if (isManufacturing(row)) return (row.quantity ?? 0) > 0 && (row.manufacturing?.qualifiedQuantity ?? 0) >= (row.quantity ?? 0)
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
  if (isManufacturing(row)) return manufacturingStats([row],new Date().toISOString().slice(0,10)).mismatch > 0
  return row.hasDeliveryDelay ?? (deliveryDay(row.purchaseDeliveryDate) > deliveryDay(row.purchaseRequisitionDeliveryDate))
}

/** 统计覆盖当前项目所选BOM类别的有效需求行，不受其他列表筛选影响。 */
const procurementStats = computed(() => {
  const items = (result.value?.items ?? []).filter((row): row is ProjectProcurementTrackingItem & { quantity: number } => !row.isWarehouseMovementRow && matchesBomKind(row) && row.quantity !== null && row.quantity > 0)
  const today = deliveryDay(new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }))
  let complete = 0, overdue = 0, mismatch = 0, shortage = 0, critical = 0, criticalComplete = 0, pending = 0
  for (const row of items) {
    const manufacturing = isManufacturing(row)
    const available = manufacturing ? row.manufacturing?.qualifiedQuantity ?? 0 : row.arrivedQuantity
    const ready = available >= row.quantity
    const dates = manufacturing
      ? [row.manufacturing?.forecastOn, ...(row.manufacturing?.batches ?? []).filter(batch => batch.qualifiedQuantity < batch.quantity).map(batch => batch.promisedOn)]
      : [row.latestDeliveryDate || row.purchaseDeliveryDate]
    const dueDays = dates.map(deliveryDay).filter(Number.isFinite)
    const required = deliveryDay(manufacturing ? row.manufacturing?.requiredOn : row.purchaseRequisitionDeliveryDate)
    const isCritical = row.impactStage === 'Assembly' || row.impactStage === 'Commissioning'
    const stage = deliveryDay(row.impactStage === 'Assembly' ? row.assemblyStartDate : row.impactStage === 'Commissioning' ? row.commissioningStartDate : null)
    if (ready) complete++
    if (!ready && dueDays.some(day => day < today)) overdue++
    if (dueDays.some(day => day > required)) mismatch++
    if (!ready && isCritical && dueDays.some(day => day > stage)) shortage++
    if (isCritical) { critical++; if (ready) criticalComplete++ }
    const unarranged = manufacturing
      ? !row.manufacturing?.receivedAt || (row.manufacturing?.assignedQuantity ?? 0) < row.quantity
      : !row.purchaseOrderNumbers.length || row.purchaseQuantity < row.quantity
    if (unarranged) pending++
  }
  return { total: items.length, complete, overdue, mismatch, shortage, critical, criticalComplete, pending }
})

function percentText(rate: number | null) {
  return rate === null ? '—' : `${Math.round(rate * 1000) / 10}%`
}

const statsCards = computed(() => {
  const stats = procurementStats.value
  const tone = (value: number) => value > 0 ? 'is-danger' : 'is-success'
  return [
    {
      key: 'ready', label: '齐套率', value: percentText(stats.total ? stats.complete / stats.total : null),
      tone: stats.total && stats.complete === stats.total ? 'is-success' : 'is-warning',
      sub: `${stats.complete}/${stats.total} 项已齐套`,
      title: '已齐套物料行数÷有效需求物料行数。采购件按有效到货数、制造件按检验合格数满足需求判断；交检、返工、报废不计为合格。仅统计当前项目所选BOM类别，排除出入库明细及无有效需求的行。',
    },
    {
      key: 'overdue', label: '超期物料', value: String(stats.overdue), tone: tone(stats.overdue),
      sub: '交期已过，仍未齐套',
      title: '最新交期（无则取预计交期）或未完成任务的承诺交期已过今天、仍未齐套的物料行数。同一行只计一次；未维护交期的不计入。',
    },
    {
      key: 'mismatch', label: '交期不符', value: String(stats.mismatch), tone: tone(stats.mismatch),
      sub: '晚于需求日期',
      title: '预计交期晚于需求日期的物料行数。采用最新交期（无则取预计交期）及未完成任务的承诺交期；缺少日期的不计入。',
    },
    {
      key: 'shortage', label: '缺料风险', value: String(stats.shortage), tone: tone(stats.shortage),
      sub: '影响装配或调试',
      title: '尚未齐套且预计交期晚于装配或调试计划开始日期的物料行数；未维护相关日期的不计入。',
    },
    {
      key: 'critical', label: '关键物料', value: String(stats.critical), tone: stats.critical && stats.criticalComplete < stats.critical ? 'is-warning' : 'is-success',
      sub: `齐套率 ${percentText(stats.critical ? stats.criticalComplete / stats.critical : null)}`,
      title: '影响装配或调试的有效需求物料行数，及其中已齐套物料行数占比。',
    },
    {
      key: 'pending', label: '待落实物料', value: String(stats.pending), tone: tone(stats.pending),
      sub: '需求尚未完成安排',
      title: '尚无采购订单或购买数未覆盖需求，以及尚未生产接收或任务分配未覆盖需求的物料行数。同一物料行只计一次，不将不同待办状态相加。',
    },
  ]
})

function cellText(row: ProjectProcurementTrackingItem, key: ColumnKey) {
  if (['purchaseRequisitionStatus', 'purchaseOrderStatus'].includes(key) && materialStockPlan(row.materialCode)) return '常备件'
  if (isMovementColumn(key)) return movementText(row, key)
  if (key === 'impactStage') return row.impactStage === 'Assembly' ? '装配' : row.impactStage === 'Commissioning' ? '调试' : '—'
  if (row.isWarehouseMovementRow && ['purchaseQuantity', 'arrivedQuantity', 'inventoryQuantity'].includes(key)) return '—'
  if (key === 'inventoryQuantity') {
    if (isManufacturing(row)) return formatNumber(manufacturingValue(row,key) as number)
    const rows = inventory.value[row.materialCode.trim()]?.rows
    return rows ? formatNumber(sumProcurementInventory(rows, row.projectCode, selectedInventoryWarehouses.value)) : '—'
  }
  const value = manufacturingValue(row,key)
  if (Array.isArray(value)) return value.length ? value.join('、') : '—'
  if (key === 'purchaseRequisitionCreatedAt' || key === 'purchaseRequisitionDeliveryDate' || key === 'purchaseDeliveryDate' || key === 'latestDeliveryDate'
    || key === 'assemblyStartDate' || key === 'commissioningStartDate' || key === 'productionReceivedAt') return isManufacturing(row) && (key === 'purchaseDeliveryDate' || key === 'latestDeliveryDate') && !value ? '待确认' : formatDate(value as string | null)
  if (key === 'quantity' || key === 'purchaseQuantity' || key === 'arrivedQuantity' || key === 'requestedQuantity' || key === 'approvedQuantity') return formatNumber(value as number)
  return value === null || value === undefined || value === '' ? '—' : String(value)
}

function movementFor(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  return row.warehouseMovements?.find(movement => key.startsWith('receipt') ? movement.kind === 'RCV' || movement.kind === 'TRANSFER' || movement.kind === 'STOCKIN' : movement.kind === 'ISSUE' || movement.kind === 'MISC')
}

function movementText(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  if (isManufacturing(row)) return key.endsWith('Quantity') ? formatNumber(manufacturingValue(row,key) as number) : formatDate(manufacturingValue(row,key) as string | null)
  const movement = movementFor(row, key)
  if (!movement) return '—'
  if (key.endsWith('Quantity')) return formatNumber(movement.quantity)
  const date = new Date(movement.date)
  if (Number.isNaN(date.getTime())) return '—'
  return date.toLocaleDateString('zh-CN', { timeZone: 'Asia/Shanghai' })
}

function movementHint(row: ProjectProcurementTrackingItem, key: MovementColumn) {
  if (isManufacturing(row)) return `${key.startsWith('receipt')?'制造中心检验合格':'制造中心实际领用'}记录；数量为有效批次累计，日期为最近一次记录，可查看制造明细核对分批数量与日期。`
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
    const metadata = await getProjectExportMetadata(props.projectId, props.token)
    if (version !== loadVersion) return
    const rows = items.map(row => exportColumns.map(({ key }) => {
      if (isMovementColumn(key)) {
        if (isManufacturing(row)) return key.endsWith('Quantity') ? manufacturingValue(row,key) as number : movementText(row,key)
        if (key.endsWith('Quantity')) return movementFor(row, key)?.quantity ?? '—'
        return movementText(row, key)
      }
      if (row.isWarehouseMovementRow && ['purchaseQuantity', 'arrivedQuantity', 'inventoryQuantity'].includes(key)) return '—'
      if (key === 'inventoryQuantity') {
        if (isManufacturing(row)) return manufacturingValue(row,key) as number
        const stock = inventory.value[row.materialCode.trim()]
        if (!stock?.rows) throw new Error(`${row.materialCode}：${stock?.hint ?? '库存未加载'}，请刷新后重试`)
        return sumProcurementInventory(stock.rows, row.projectCode, warehouses)
      }
      const value=manufacturingValue(row,key)
      return typeof value === 'number' ? value : cellText(row, key)
    }))
    const date = new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' }).replaceAll('-', '')
    const sequenceKey = `upton-pdm:material-status-export:${props.username}:${props.projectId}:${date}`
    const previousSequence = Number(window.localStorage.getItem(sequenceKey) ?? '0')
    if (!Number.isInteger(previousSequence) || previousSequence < 0 || previousSequence >= 99) throw new Error('当日导出流水号不可用或已达99次，请次日再导出')
    const sequence = previousSequence + 1
    window.localStorage.setItem(sequenceKey, String(sequence))
    downloadProcurementWorkbook(`${projectCode}_物料状态清单_${date}_${String(sequence).padStart(2, '0')}.xlsx`, exportColumns.map(column => column.label), rows, undefined, { ...metadata, title: manufacturingOnly.value ? '非标件制造跟踪清单' : '物料采购与制造跟踪清单', scope: `当前筛选结果 · ${items.length}条`, versionLabel: '数据时间', version: manufacturingOnly.value ? result.value?.manufacturingReadAt || '未读取' : result.value?.lastSuccessfulRefreshAt || '未同步' })
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
watch(tablePreferenceVersion, () => { if (!settingsVisible.value) restoreColumns() })
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
      <label class="procurement-tracking__kind-filter">
        <select v-model="filters.bomKind" aria-label="筛选物料分类">
          <option value="">全部分类</option>
          <option value="标准件">标准件</option>
          <option value="非标件">非标件</option>
          <option value="电气件">电气件</option>
        </select>
      </label>
      <el-popover v-for="field in multiFilterFields" :key="field.key" placement="bottom-start" trigger="click" :width="190">
        <template #reference>
          <button type="button" class="procurement-tracking__multi-filter" :class="{ 'has-value': filters[field.key].length }" :aria-label="`筛选${field.label}`" :title="multiFilterSummary(field)">
            <span>{{ multiFilterSummary(field) }}</span>
          </button>
        </template>
        <div class="procurement-tracking__multi-options" :aria-label="`${field.label}多选项`">
          <label v-for="value in multiFilterOptions[field.key]" :key="value"><input v-model="filters[field.key]" type="checkbox" :value="value">{{ value }}</label>
          <span v-if="!multiFilterOptions[field.key].length">暂无选项</span>
          <button v-if="filters[field.key].length" type="button" @click="filters[field.key] = []">清空</button>
        </div>
      </el-popover>
      <el-popover placement="bottom-start" trigger="click" :width="190">
        <template #reference>
          <button type="button" class="procurement-tracking__multi-filter" :class="{ 'has-value': filters.unreceivedOnly || filters.unissuedOnly }" aria-label="筛选出入库状态" :title="manufacturingOnly ? '非标件按检验合格与实际领用记录筛选' : hasManufacturing ? '采购件按出入库记录筛选；非标件按检验合格与实际领用记录筛选' : '按U9C出入库记录筛选'">
            <span>{{ filters.unreceivedOnly && filters.unissuedOnly ? '未入库 +1' : filters.unreceivedOnly ? '未入库' : filters.unissuedOnly ? '未出库' : '全部出入库' }}</span>
          </button>
        </template>
        <div class="procurement-tracking__multi-options" aria-label="出入库状态多选项">
          <label :title="hasManufacturing ? '非标件表示尚未检验合格齐套。' : undefined"><input v-model="filters.unreceivedOnly" type="checkbox" aria-label="未入库">未入库</label>
          <label :title="hasManufacturing ? '非标件表示尚未领用完成。' : undefined"><input v-model="filters.unissuedOnly" type="checkbox" aria-label="未出库">未出库</label>
          <button v-if="filters.unreceivedOnly || filters.unissuedOnly" type="button" @click="filters.unreceivedOnly = false; filters.unissuedOnly = false">清空</button>
        </div>
      </el-popover>
      <label class="procurement-tracking__delay-filter"><input v-model="filters.delayedOnly" type="checkbox" aria-label="延期">延期</label>
      <el-button @click="resetFilters">重置筛选</el-button>
      </div>
      <div class="procurement-tracking__actions">
        <el-button @click="settingsVisible = true">列设置</el-button>
        <el-button type="primary" :loading="refreshStarting" :disabled="refreshCoolingDown" @click="refreshFromU9">立即刷新</el-button>
        <el-button v-if="!manufacturingOnly" @click="openInventorySettings">库存设置</el-button>
        <el-button :loading="exporting" :disabled="loading || !filteredItems.length || !columns.length" @click="exportExcel">导出 Excel</el-button>
      </div>
      <span class="procurement-tracking__updated" :title="hasManufacturing ? `制造数据读取：${formatDate(result?.manufacturingReadAt,true)}` : ''">{{ manufacturingOnly ? '制造读取' : '刷新' }}：{{ formatDate(manufacturingOnly ? result?.manufacturingReadAt : result?.lastSuccessfulRefreshAt, true) }}</span>
    </header>

    <el-alert v-if="result?.lastRefreshError && !manufacturingOnly" :title="`U9C最近一次刷新失败，采购数据继续显示上次完整快照：${result.lastRefreshError}`" type="warning" :closable="false" show-icon />

    <div class="procurement-tracking__stats" role="group" aria-label="采购跟踪统计">
      <article v-for="card in statsCards" :key="card.key" class="procurement-stats-card" :class="card.tone" :title="card.title" :aria-label="`${card.label}：${card.value}，${card.sub}`">
        <span class="procurement-stats-card__text">
          <span class="procurement-stats-card__label">{{ card.label }}</span>
          <span class="procurement-stats-card__sub">{{ card.sub }}</span>
        </span>
        <strong class="procurement-stats-card__value" :class="{ 'is-compact': 'compactValue' in card && card.compactValue }">{{ card.value }}</strong>
      </article>
    </div>

    <el-table
      :data="pagedItems"
      @sort-change="changeDeliverySort"
      :row-class-name="rowClassName"
      :cell-class-name="cellClassName"
      height="100%"
      stripe
      class="procurement-tracking__table"
    >
      <template #empty>
        <div class="procurement-tracking__empty-tip">
          {{ result && !result.hasPublishedBom
            ? '当前项目尚无已发布的标准件、非标件或电气件BOM，发布后将自动进入采购跟踪。'
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
        :resizable="true"
        :align="column.align ?? 'left'"
        show-overflow-tooltip
      >
        <template #header><span :title="hasManufacturing ? manufacturingColumnHints[column.key] : undefined">{{ column.label }}</span></template>
        <template #default="{ row }">
          <span v-if="isMovementColumn(column.key)" class="procurement-tracking__movement" :class="movementTone(row, column.key)" :title="movementHint(row, column.key)">{{ movementText(row, column.key) }}</span>
          <span v-else-if="(column.key === 'purchaseRequisitionStatus' || column.key === 'purchaseOrderStatus') && materialStockPlan(row.materialCode)" v-stocked-material="row.materialCode">常备件</span>
          <el-tag v-else-if="(column.key === 'purchaseRequisitionStatus' || column.key === 'purchaseOrderStatus') && !row.isWarehouseMovementRow" :type="statusType(String(manufacturingValue(row,column.key)))" size="small">{{ cellText(row,column.key) }}</el-tag>
          <el-button v-else-if="isManufacturing(row) && column.key === 'purchaseOrderNumbers'" link :aria-label="`查看${row.materialName}制造明细`" @click="showManufacturing(row)">{{ row.manufacturing?.batches.length ? cellText(row,column.key) : '查看制造明细' }}</el-button>
          <el-tag v-else-if="column.key === 'impactStage' && row.impactStage" :type="row.impactStage === 'Assembly' ? 'warning' : 'info'" size="small">{{ cellText(row, column.key) }}</el-tag>
          <span v-else-if="column.key === 'inventoryQuantity'" :title="inventoryHint(row)">{{ cellText(row, column.key) }}</span>
          <span v-else v-stocked-material="['materialCode', 'materialName'].includes(column.key) ? row.materialCode : undefined" :title="cellText(row, column.key)">{{ cellText(row, column.key) }}</span>
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

    <el-dialog class="procurement-tracking-dialog" v-model="inventorySettingsVisible" title="库存汇总设置" width="520px" append-to-body>
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

    <el-dialog class="procurement-tracking-dialog" v-model="settingsVisible" title="采购跟踪列设置" width="520px" append-to-body>
      <div class="procurement-tracking__settings-body">
      <p class="procurement-tracking__settings-note">可按当前账号选择显示列，保存后在不同客户端自动恢复；价格、税额、币种及其他财务信息不提供。</p>
      <el-checkbox-group v-model="visibleKeys" class="procurement-tracking__column-list" aria-label="采购跟踪显示列">
        <el-checkbox v-for="column in columnDefinitions" :key="column.key" :value="column.key">{{ column.label }}</el-checkbox>
      </el-checkbox-group>
      </div>
      <template #footer>
        <el-button @click="resetColumns">恢复默认</el-button>
        <el-button @click="settingsVisible = false">取消</el-button>
        <el-button type="primary" @click="saveColumns">保存</el-button>
      </template>
    </el-dialog>
    <el-drawer v-model="manufacturingDetailOpen" title="非标件制造明细" size="min(1100px, 95vw)" append-to-body>
      <template v-if="manufacturingDetail">
        <p>{{ manufacturingDetail.materialCode }} · {{ manufacturingDetail.materialName }} · {{ manufacturingDetail.specification }} · {{ manufacturingDetail.manufacturing?.revision || '无版次' }}</p>
        <p>发布包：{{ manufacturingDetail.releasePackageNumber }}　需求：{{ formatNumber(manufacturingDetail.quantity) }}　合格：{{ formatNumber(manufacturingDetail.manufacturing?.qualifiedQuantity ?? 0) }}　已领用：{{ formatNumber(manufacturingDetail.manufacturing?.issuedQuantity ?? 0) }}</p>
        <p>制造状态：{{ manufacturingDetail.manufacturing?.status || '待生产接收' }}　生产接收：{{ formatDate(manufacturingDetail.manufacturing?.receivedAt,true) }}</p>
        <p>交货、交检不代表检验合格；合格待领用来自制造记录，不是U9C仓库库存。</p>
        <p v-if="!manufacturingDetail.manufacturing?.batches.length">尚未分配制造批次。</p>
        <section v-for="batch in manufacturingDetail.manufacturing?.batches" :key="batch.id" class="procurement-manufacturing-batch">
          <h3>{{ batch.route === 'Self' ? '自制' : '外协' }} · {{ batch.id.slice(0,8) }}　{{ batch.status }}</h3>
          <p>负责人：{{ batch.assignee || '待分配' }}　供应商：{{ batch.supplier || '—' }}　承诺日期：{{ batch.promisedOn || '待确认' }}</p>
          <p>有效批次数量：{{ formatNumber(batch.quantity) }}　外协交货：{{ formatNumber(batch.deliveredQuantity) }}　已交检：{{ formatNumber(batch.submittedQuantity) }}　合格：{{ formatNumber(batch.qualifiedQuantity) }}　已领用：{{ formatNumber(batch.issuedQuantity) }}</p>
          <table class="procurement-manufacturing-history" aria-label="制造批次流转记录"><thead><tr><th>时间</th><th>操作</th><th>数量</th><th>操作人</th><th>备注</th></tr></thead><tbody><tr v-for="event in batch.history" :key="event.id"><td>{{ formatDate(event.occurredAt,true) }}</td><td>{{ event.operationName || productionMovementLabels[event.kind] || '流程记录' }}</td><td>{{ formatNumber(event.quantity) }}</td><td>{{ event.actorName || event.actor }}</td><td>{{ event.note || '—' }}</td></tr></tbody></table>
        </section>
      </template>
    </el-drawer>
  </section>
</template>

<style scoped>
.procurement-manufacturing-batch{margin-top:14px;padding:12px;border:1px solid var(--pdm-border);border-radius:6px}.procurement-manufacturing-history{width:100%;border-collapse:collapse;font-size:12px}.procurement-manufacturing-history th,.procurement-manufacturing-history td{padding:7px;border-bottom:1px solid var(--pdm-border);text-align:left}
.procurement-tracking__heading{font-size:11px;--el-font-size-base:11px}
.procurement-tracking__heading :deep(.el-button){font-size:11px}
.procurement-tracking__filters{display:flex;flex-wrap:nowrap;align-items:center;gap:4px;flex:0 0 auto;margin:0;white-space:nowrap}
.procurement-tracking__brand-filter{width:80px;flex:0 0 80px}
.procurement-tracking__brand-filter{height:30px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,var(--pdm-surface));color:var(--pdm-text);font:inherit;font-size:11px;padding:0 8px;min-width:0}
.procurement-tracking__brand-filter::-webkit-calendar-picker-indicator{display:none}
.procurement-tracking__kind-filter{display:flex;width:80px;max-width:100%;height:30px;box-sizing:border-box;flex:0 0 80px;align-items:center;color:var(--pdm-text);font:inherit;font-size:11px;min-width:0;cursor:pointer}
.procurement-tracking__kind-filter select{box-sizing:border-box;min-width:0;width:100%;height:100%;padding:0 5px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,var(--pdm-surface));color:inherit;font:inherit;font-size:11px;appearance:none;-webkit-appearance:none;cursor:pointer}
.procurement-tracking__kind-filter select:focus{outline:0}
.procurement-tracking__movement{white-space:pre-line;line-height:18px;display:block}
.procurement-tracking__movement.is-transfer{color:var(--pdm-orange)}
.procurement-tracking__movement.is-stockin{color:var(--pdm-blue)}
.procurement-tracking__search{height:30px;box-sizing:border-box;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,var(--pdm-surface));color:var(--pdm-text);font:inherit;font-size:11px;padding:0 8px;min-width:0}
.procurement-tracking__multi-filter{display:flex;width:80px;max-width:100%;height:30px;box-sizing:border-box;flex:0 0 80px;align-items:center;padding:0 5px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel,var(--pdm-surface));color:var(--pdm-text);font:inherit;font-size:11px;cursor:pointer}.procurement-tracking__multi-filter>span:first-child{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.procurement-tracking__multi-filter.has-value{border-color:var(--pdm-blue);color:var(--pdm-blue)}.procurement-tracking__search{width:168px;max-width:100%;flex:0 0 168px}
.procurement-tracking__multi-options{display:grid;gap:7px;max-height:260px;overflow:auto}.procurement-tracking__multi-options label{display:flex;align-items:center;gap:7px;color:var(--pdm-text);font-size:11px}.procurement-tracking__multi-options>span{color:var(--pdm-muted);font-size:11px}.procurement-tracking__multi-options>button{justify-self:end;border:0;background:transparent;color:var(--pdm-blue);font-size:11px;cursor:pointer}
.procurement-tracking__delay-filter{display:flex;align-items:center;gap:4px;font-size:11px;color:var(--pdm-text);white-space:nowrap}
.procurement-tracking__filters .el-button{height:30px;margin:0;white-space:nowrap}
.procurement-tracking__warehouse-list{display:flex;flex-direction:column;max-height:60vh;overflow:auto}
.procurement-tracking__pagination{display:flex;flex:0 0 auto;align-items:center;justify-content:flex-end;gap:8px;padding:8px 10px;color:var(--pdm-muted);font-size:11px}
.procurement-tracking__pagination select{height:28px;padding:0 24px 0 8px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}
.procurement-tracking__pagination .pdm-secondary-action{width:60px;min-width:60px;height:var(--pdm-symbol-compact);min-height:var(--pdm-symbol-compact);padding:0}
.procurement-tracking{display:flex;flex-direction:column;min-width:0;min-height:0;height:100%;padding:8px 18px 18px}.procurement-tracking__heading{display:flex;overflow-x:auto;overflow-y:hidden;align-items:center;justify-content:flex-start;gap:5px;min-height:30px;flex-shrink:0;margin-bottom:6px;white-space:nowrap}.procurement-tracking__actions{display:flex;align-items:center;gap:5px;flex-shrink:0;white-space:nowrap}.procurement-tracking__actions :deep(.el-button){box-sizing:border-box;width:auto;min-width:60px;height:var(--pdm-button-default);min-height:var(--pdm-button-default);flex:0 0 60px;margin:0;padding:0 5px;white-space:nowrap}.procurement-tracking__updated{margin-left:auto;flex-shrink:0;white-space:nowrap;color:var(--pdm-muted);font-size:11px}.procurement-tracking__table{min-height:0;flex:1 1 auto;margin-top:0}.procurement-tracking__settings-note{margin:0 0 12px;color:var(--pdm-muted);line-height:1.4}.procurement-tracking__column-list{display:grid;max-height:calc(100vh - 190px);grid-template-columns:repeat(2,minmax(0,1fr));gap:0;overflow:auto;border:1px solid var(--pdm-border-soft);border-radius:8px}.procurement-tracking__column-list :deep(.el-checkbox){box-sizing:border-box;width:100%;min-height:36px;margin:0;padding:7px 12px;border-bottom:1px solid var(--pdm-surface-muted)}.procurement-tracking__column-list :deep(.el-checkbox:nth-child(odd)){border-right:1px solid var(--pdm-surface-muted)}.procurement-tracking :deep(.is-procurement-late td.el-table__cell){background:var(--pdm-orange-soft)!important}@media(max-width:1000px){.procurement-tracking__heading{flex-wrap:nowrap}}

.procurement-tracking :deep(.el-button:not(.is-link):not(.is-circle):not(.el-button--text):not([aria-label])),:global(.procurement-tracking-dialog .el-button:not(.is-link):not(.is-circle):not(.el-button--text):not([aria-label])){box-sizing:border-box;width:60px;min-width:60px!important;flex:0 0 60px;padding-inline:3px!important;font-size:11px}
.procurement-tracking__multi-options>button{width:60px;min-width:60px}
</style>

<style scoped>
/* Fit the panel with single-line cells; overflow tooltips retain access to full text. */
.procurement-tracking__table { width: 100%; max-width: 100%; }
.procurement-tracking__table :deep(.cell) { min-width: 0; padding-right: 6px; padding-left: 6px; text-align: center; font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.procurement-tracking__table :deep(.cell.el-tooltip) { min-width: 0; max-width: 100%; }
.procurement-tracking__table :deep(.el-tag) { max-width: 100%; padding-right: 3px; padding-left: 3px; border: 0; background: transparent; font-size: 11px; }
.procurement-tracking__table :deep(.el-tag__content) { overflow: hidden; text-overflow: ellipsis; }
.procurement-tracking__empty-tip { position: static; transform: none; padding: 16px; line-height: 1.4; overflow-wrap: anywhere; }
.procurement-tracking :deep(td.el-table__cell.is-delivery-delay) { background: var(--pdm-danger-soft) !important; color: var(--pdm-danger); animation: procurement-delivery-delay 2s ease-in-out infinite; }
@keyframes procurement-delivery-delay {
  0%, 100% { box-shadow: inset 0 0 0 100vmax transparent; }
  50% { box-shadow: inset 0 0 0 100vmax var(--pdm-danger-border); }
}
@media (prefers-reduced-motion: reduce) {
  .procurement-tracking :deep(td.el-table__cell.is-delivery-delay) { animation: none; }
}
/* 统计卡：单行自适应铺满，高度控制在约 52px（≈1.7 行明细）。 */
.procurement-tracking__stats { display: flex; flex-shrink: 0; align-items: stretch; gap: 6px; margin: 0 0 6px; overflow-x: auto; overflow-y: hidden; }
.procurement-stats-card { display: flex; min-width: 96px; flex: 1 1 0; align-items: center; justify-content: space-between; gap: 8px; height: 54px; padding: 4px 8px; border: 1px solid var(--pdm-border-soft); border-left: 3px solid var(--pdm-muted); border-radius: 6px; background: var(--pdm-surface-muted); line-height: 1.25; }
.procurement-stats-card__text { display: flex; min-width: 0; flex-direction: column; gap: 1px; }
.procurement-stats-card__label { overflow: hidden; color: var(--pdm-muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.procurement-stats-card__sub { overflow: hidden; color: var(--pdm-muted); font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.procurement-stats-card__value { flex: 0 0 auto; color: var(--pdm-text); font-size: 24px; line-height: 1.1; white-space: nowrap; }
.procurement-stats-card__value.is-compact { font-size: 18px; }
.procurement-stats-card.is-success { border-left-color: var(--pdm-green); }
.procurement-stats-card.is-success .procurement-stats-card__value { color: var(--pdm-green); }
.procurement-stats-card.is-warning { border-left-color: var(--pdm-orange); }
.procurement-stats-card.is-warning .procurement-stats-card__value { color: var(--pdm-orange); }
.procurement-stats-card.is-danger { border-left-color: var(--pdm-danger); }
.procurement-stats-card.is-danger .procurement-stats-card__value { color: var(--pdm-danger); }
</style>
