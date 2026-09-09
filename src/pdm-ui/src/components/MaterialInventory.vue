<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import type { TableColumnCtx } from 'element-plus'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { listMaterialInventory, refreshMaterialInventory } from '../api'
import type { U9InventoryRow } from '../types'
import SquareLoader from './SquareLoader.vue'

const props = withDefaults(defineProps<{
  token: string
  requestedMaterialCode?: string
  requestKey?: number
}>(), { requestedMaterialCode: '', requestKey: 0 })

const loading = ref(false)
const refreshing = ref(false)
const errorMessage = ref('')
const rows = ref<U9InventoryRow[]>([])
const warehouseOptions = ref<string[]>([])
const brandOptions = ref<string[]>([])
const projectOptions = ref<string[]>([])
const subprojectOptions = ref<Array<{ projectCode: string; subproject: string }>>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(50)
const lastSuccessfulRefreshAt = ref<string | null>(null)
const filters = reactive({
  warehouse: '',
  materialCode: '',
  itemName: '',
  brand: '',
  specification: '',
  projectCode: '',
  subproject: '',
  positiveStockOnly: true,
})
const appliedFilters = reactive({ ...filters })
const similarFilters = reactive({ specification: '', brand: '', positiveStockOnly: true })
const appliedSimilarFilters = reactive({ ...similarFilters })
const similarMode = ref(false)
let loadVersion = 0
const indexedSubprojectOptions = computed(() => {
  const projectCode = filters.projectCode.trim().toLocaleLowerCase()
  return [...new Set(subprojectOptions.value
    .filter(option => !projectCode || option.projectCode.toLocaleLowerCase() === projectCode)
    .map(option => option.subproject))]
})

async function loadInventory(resetPage = false) {
  const version = ++loadVersion
  if (resetPage) page.value = 1
  loading.value = true
  errorMessage.value = ''
  try {
    const result = await listMaterialInventory({
      ...(similarMode.value ? {
        similarSpecification: appliedSimilarFilters.specification,
        brand: appliedSimilarFilters.brand,
        positiveStockOnly: appliedSimilarFilters.positiveStockOnly,
      } : appliedFilters),
      page: page.value,
      pageSize: pageSize.value,
    }, props.token)
    if (version !== loadVersion) return
    rows.value = result.items
    warehouseOptions.value = result.warehouseNames ?? []
    brandOptions.value = result.brandNames ?? []
    projectOptions.value = result.projectCodes ?? []
    subprojectOptions.value = result.subprojectOptions ?? []
    total.value = result.total
    page.value = result.page
    pageSize.value = result.pageSize
    lastSuccessfulRefreshAt.value = result.lastSuccessfulRefreshAt ?? null
  } catch (error) {
    if (version !== loadVersion) return
    errorMessage.value = error instanceof Error ? error.message : '料品库存加载失败'
    rows.value = []
    total.value = 0
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function refreshRequestedMaterial() {
  const materialCode = props.requestedMaterialCode.trim()
  if (!materialCode || refreshing.value) return
  ++loadVersion
  loading.value = false
  similarMode.value = false
  filters.materialCode = materialCode
  filters.positiveStockOnly = false
  Object.assign(appliedFilters, filters)
  page.value = 1
  refreshing.value = true
  errorMessage.value = ''
  try {
    const result = await refreshMaterialInventory(materialCode, props.token)
    rows.value = result.items
    warehouseOptions.value = result.warehouseNames ?? []
    brandOptions.value = result.brandNames ?? []
    projectOptions.value = result.projectCodes ?? []
    subprojectOptions.value = result.subprojectOptions ?? []
    total.value = result.total
    page.value = result.page
    pageSize.value = result.pageSize
    lastSuccessfulRefreshAt.value = result.lastSuccessfulRefreshAt ?? null
    ElMessage.success(`已实时刷新料号 ${materialCode} 的U9C库存`)
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'U9C库存实时查询失败'
  } finally {
    refreshing.value = false
  }
}

function applyFilters() {
  similarMode.value = false
  Object.assign(appliedFilters, filters)
  void loadInventory(true)
}

function applySimilarFilters() {
  const specification = similarFilters.specification.trim()
  if ((specification.match(/[\p{L}\p{N}]/gu) ?? []).length < 4) {
    ElMessage.warning('相似查询请至少输入4个有效字母、数字或汉字')
    return
  }
  Object.assign(appliedSimilarFilters, similarFilters, { specification })
  similarMode.value = true
  void loadInventory(true)
}

function returnToNormal() {
  similarMode.value = false
  void loadInventory(true)
}

function handleProjectChange() {
  if (filters.subproject && !indexedSubprojectOptions.value.includes(filters.subproject)) filters.subproject = ''
}

function resetFilters() {
  similarMode.value = false
  const defaults = {
    warehouse: '',
    materialCode: '',
    itemName: '',
    brand: '',
    specification: '',
    projectCode: '',
    subproject: '',
    positiveStockOnly: true,
  }
  Object.assign(filters, defaults)
  Object.assign(appliedFilters, defaults)
  void loadInventory(true)
}

function formatQuantity(value: number) {
  return Number(value || 0).toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

function formatTime(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('zh-CN', { hour12: false })
}

function inventorySummary({ columns, data }: { columns: TableColumnCtx<U9InventoryRow>[]; data: U9InventoryRow[] }) {
  return columns.map((column, index) => {
    if (index === 0) return '本页合计'
    if (column.property === 'stockQuantity') return formatQuantity(data.reduce((sum, row) => sum + Number(row.stockQuantity || 0), 0))
    return ''
  })
}

watch(() => props.requestKey, key => {
  if (key > 0 && props.requestedMaterialCode.trim()) void refreshRequestedMaterial()
})

onMounted(() => {
  if (props.requestKey > 0 && props.requestedMaterialCode.trim()) void refreshRequestedMaterial()
  else void loadInventory()
})
</script>

<template>
  <section class="material-inventory pdm-loading-host" aria-label="料品库存">
    <SquareLoader v-if="loading || refreshing" overlay :label="refreshing ? '正在实时查询U9C库存' : '正在加载库存快照'" />
    <div class="material-inventory__layout">
      <aside class="material-inventory__search" aria-label="库存搜索">
        <h3>库存搜索</h3>
        <div class="material-inventory__filters">
          <el-select v-model="filters.warehouse" clearable filterable placeholder="存储地点名称" aria-label="存储地点名称">
            <el-option v-for="warehouse in warehouseOptions" :key="warehouse" :label="warehouse" :value="warehouse" />
          </el-select>
          <el-input v-model.trim="filters.materialCode" clearable placeholder="料号（精确）" aria-label="库存料号" />
          <el-input v-model.trim="filters.itemName" clearable placeholder="品名（包含）" aria-label="库存品名" />
          <el-select v-model="filters.brand" clearable filterable placeholder="品牌（检索）" aria-label="库存品牌">
            <el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" />
          </el-select>
          <el-input v-model.trim="filters.specification" clearable placeholder="规格（包含）" aria-label="库存规格" />
          <el-select v-model="filters.projectCode" clearable filterable placeholder="项目号（检索）" aria-label="库存项目号" @change="handleProjectChange">
            <el-option v-for="projectCode in projectOptions" :key="projectCode" :label="projectCode" :value="projectCode" />
          </el-select>
          <el-select v-model="filters.subproject" clearable filterable placeholder="子项目号（检索）" aria-label="库存子项目号">
            <el-option v-for="subproject in indexedSubprojectOptions" :key="subproject" :label="subproject" :value="subproject" />
          </el-select>
        </div>
        <div class="material-inventory__actions">
          <el-checkbox v-model="filters.positiveStockOnly">仅显示现存量大于0</el-checkbox>
          <el-button type="primary" @click="applyFilters">查询</el-button>
          <el-button @click="resetFilters">清空</el-button>
        </div>
        <section class="material-inventory__similar" aria-label="相似库存查询">
          <h3>相似库存查询</h3>
          <div class="material-inventory__filters">
            <el-input v-model.trim="similarFilters.specification" maxlength="256" clearable placeholder="规格/型号（至少4个有效字符）" aria-label="相似库存规格" @keyup.enter="applySimilarFilters" />
            <el-select v-model="similarFilters.brand" clearable filterable placeholder="品牌（可选）" aria-label="相似库存品牌">
              <el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" />
            </el-select>
          </div>
          <div class="material-inventory__actions">
            <el-checkbox v-model="similarFilters.positiveStockOnly">仅显示现存量大于0</el-checkbox>
            <el-button type="primary" :disabled="loading || refreshing" @click="applySimilarFilters">查询相似</el-button>
            <el-button :disabled="!similarMode || loading || refreshing" @click="returnToNormal">普通查询</el-button>
          </div>
          <p>显示相似度≥75%的型号。仅供查找，替代前需人工核对。</p>
        </section>
      </aside>
      <div class="material-inventory__results">
        <div class="material-inventory__meta">
          <span>库存来自U9C定时全量快照</span>
          <span>最近全量刷新：{{ formatTime(lastSuccessfulRefreshAt) }}</span>
        </div>
        <el-alert v-if="errorMessage" :title="errorMessage" type="error" :closable="false" show-icon />
        <el-alert v-if="similarMode" :title="`相似查询中：${appliedSimilarFilters.specification}；品牌：${appliedSimilarFilters.brand || '不限'}；相似度≥75%，由高到低排序。仅供查找，替代前需人工核对。`" type="info" :closable="false" show-icon />
        <div class="material-inventory__table">
          <el-table :data="rows" height="100%" border stripe show-summary :summary-method="inventorySummary" empty-text="尚无符合条件的库存记录">
            <el-table-column align="center" prop="warehouseName" label="存储地点名称" min-width="150" show-overflow-tooltip />
            <el-table-column align="center" prop="materialCode" label="料号" min-width="130" show-overflow-tooltip />
            <el-table-column align="center" prop="itemName" label="品名" min-width="150" show-overflow-tooltip />
            <el-table-column align="center" label="品牌" min-width="110" show-overflow-tooltip><template #default="{ row }">{{ row.brand || '—' }}</template></el-table-column>
            <el-table-column align="center" label="规格" min-width="190" show-overflow-tooltip><template #default="{ row }">{{ row.specification || '—' }}</template></el-table-column>
            <el-table-column v-if="similarMode" align="center" label="相似度" min-width="90"><template #default="{ row }">{{ row.similarityPercent == null ? '—' : `${Number(row.similarityPercent).toFixed(1)}%` }}</template></el-table-column>
            <el-table-column align="center" label="项目号" min-width="120" show-overflow-tooltip><template #default="{ row }">{{ row.projectCode || '—' }}</template></el-table-column>
            <el-table-column align="center" label="项目名称" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ row.projectName || '—' }}</template></el-table-column>
            <el-table-column align="center" label="子项目" min-width="110" show-overflow-tooltip><template #default="{ row }">{{ row.subproject || '—' }}</template></el-table-column>
            <el-table-column align="center" prop="stockQuantity" label="现存量（库存单位）" min-width="145"><template #default="{ row }">{{ formatQuantity(row.stockQuantity) }}</template></el-table-column>
            <el-table-column align="center" prop="refreshedAt" label="最近刷新时间" min-width="165"><template #default="{ row }">{{ formatTime(row.refreshedAt) }}</template></el-table-column>
          </el-table>
        </div>
        <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :page-sizes="[50, 100, 200]" :total="total" layout="total, sizes, prev, pager, next" @current-change="loadInventory()" @size-change="loadInventory(true)" />
      </div>
    </div>
  </section>
</template>

<style scoped>
.material-inventory{height:100%;min-height:0;padding:4px 0}.material-inventory__layout{height:100%;min-height:0;display:grid;grid-template-columns:200px minmax(0,1fr);gap:12px}.material-inventory__search{box-sizing:border-box;width:200px;min-height:0;padding:16px;border:1px solid #dbe3ec;border-radius:8px;background:#f8fafc}.material-inventory__search h3{margin:0 0 14px;font-size:15px;color:#1e293b}.material-inventory__filters{display:grid;grid-template-columns:1fr;gap:10px}.material-inventory__actions{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin-top:14px}.material-inventory__actions :deep(.el-checkbox){grid-column:1/-1}.material-inventory__actions :deep(.el-button){width:100%;margin-left:0}.material-inventory__results{min-width:0;min-height:0;display:flex;flex-direction:column;gap:12px}.material-inventory__meta{display:flex;justify-content:space-between;gap:16px;color:#64748b;font-size:12px}.material-inventory__table{min-height:0;flex:1 1 auto}@media(max-width:900px){.material-inventory{overflow:auto}.material-inventory__layout{height:auto;grid-template-columns:minmax(0,1fr)}.material-inventory__search{width:auto}.material-inventory__meta{flex-direction:column;gap:4px}.material-inventory__table{height:560px}}
</style>

<style scoped>
.material-inventory__search { overflow-y: auto; }
.material-inventory__similar { margin-top: 22px; padding-top: 18px; border-top: 1px solid #dbe3ec; }
.material-inventory__similar p { margin: 12px 0 0; color: #64748b; font-size: 12px; line-height: 1.6; }
</style>
