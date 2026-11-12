<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { requestJson, exportPurchaseAnalysis } from '../api'
import { defaultPurchaseFilter, type PurchaseAnalysis, type PurchaseFilter, type PurchaseMetric, type PurchaseSettings } from '../purchaseAnalysis'
import SquareLoader from './SquareLoader.vue'
import QuantityInputNumber from './QuantityInputNumber.vue'

const props = defineProps<{ token: string }>()
const filter = reactive(defaultPurchaseFilter())
const applied = ref<PurchaseFilter>(defaultPurchaseFilter())
const data = ref<PurchaseAnalysis>()
const detail = ref<PurchaseAnalysis>()
const busy = ref(false), loading = ref(false), detailLoading = ref(false), running = ref(false)
const error = ref(''), message = ref(''), drawerError = ref('')
const advanced = ref(false), settingsOpen = ref(false), detailOpen = ref(false)
const settings = ref<PurchaseSettings>(), organizationText = ref('')
const focused = ref<PurchaseMetric>(), detailPage = ref(1)
const active = ref<'frequent' | 'volume' | 'spend' | 'prices' | 'fragmented' | 'suppliers'>('frequent')
const tabs = [{ key: 'frequent', label: '高频采购' }, { key: 'volume', label: '大批量采购' }, { key: 'spend', label: '高金额采购', cost: true },
  { key: 'prices', label: '价格差异', cost: true }, { key: 'fragmented', label: '零散采购' }, { key: 'suppliers', label: '供应商集中度', cost: true }] as const
const visibleTabs = computed(() => tabs.filter(t => !('cost' in t) || data.value?.canCost))
const rows = computed(() => data.value?.[active.value] ?? [])
const scopes = [{ key: 'organizations', label: 'U9C组织' }, { key: 'categories', label: '物料分类' },
  { key: 'suppliers', label: '供应商' }, { key: 'buyers', label: '采购员' }, { key: 'projects', label: '项目／子项目' }] as const
const number = (n?: number | null, digits = 2) => n == null ? '—' : n.toLocaleString('zh-CN', { maximumFractionDigits: digits })
const time = (s?: string) => s ? new Date(s).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '尚未同步'
const amounts = computed(() => Object.entries(data.value?.amounts ?? {}))
const quantityView = computed(() => active.value !== 'suppliers' && !(active.value === 'spend' && applied.value.dimension !== 'material'))
const trendMax = computed(() => Math.max(1, ...(detail.value?.trend ?? data.value?.trend ?? []).map(t => t.orders)))
const cardIcon = (path: string) => ({ '--pdm-stat-icon': `url("data:image/svg+xml,${encodeURIComponent(`<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='white' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='${path}'/></svg>`)}")` })
let serial = 0, detailSerial = 0, disposed = false, timer: ReturnType<typeof setTimeout> | undefined
async function load(apply = true) {
  const current = ++serial
  if (apply) { applied.value = JSON.parse(JSON.stringify(filter)) as PurchaseFilter; applied.value.detailKey = ''; applied.value.page = 1 }
  loading.value = true; error.value = ''
  try {
    const response = await requestJson<{ data: PurchaseAnalysis; running: boolean }>('/api/purchase-analysis/query', { method: 'POST', body: JSON.stringify(applied.value) }, props.token)
    if (disposed || current !== serial) return
    data.value = response.data; running.value = response.running
    if (!visibleTabs.value.some(t => t.key === active.value)) active.value = 'frequent'
    clearTimeout(timer)
    if (running.value) timer = setTimeout(() => void load(false), 5000)
  } catch { if (current === serial && !disposed) { data.value = undefined; error.value = '采购分析读取失败，请检查筛选范围、登录状态及权限后重试。' } }
  finally { if (current === serial && !disposed) loading.value = false }
}
function reset() { Object.assign(filter, defaultPurchaseFilter()); void load() }
async function showSettings() {
  error.value = ''; busy.value = true
  try { settings.value = await requestJson<PurchaseSettings>('/api/purchase-analysis/settings', {}, props.token); organizationText.value = settings.value.organizations.join('、'); settingsOpen.value = true }
  catch { error.value = '采集设置读取失败，请核对配置权限后重试。' } finally { busy.value = false }
}
async function saveSettings() {
  if (!settings.value) return
  busy.value = true; drawerError.value = ''
  try {
    await requestJson('/api/purchase-analysis/settings', { method: 'PUT', body: JSON.stringify({ ...settings.value, organizations: organizationText.value.split(/[,，、\s]+/).filter(Boolean) }) }, props.token)
    settingsOpen.value = false; message.value = '采集设置已保存。'; await load(false)
  } catch { drawerError.value = '保存失败，请核对组织编码、历史月份和同步间隔，或重新打开设置后重试。' } finally { busy.value = false }
}
async function synchronize() {
  busy.value = true; error.value = ''
  try { await requestJson('/api/purchase-analysis/synchronize', { method: 'POST' }, props.token); running.value = true; message.value = '采集任务已进入后台，完成后自动刷新。'; await load(false) }
  catch { error.value = '采集启动失败，请核对采集组织、同步权限及U9C设置。' } finally { busy.value = false }
}
async function showDetail(row?: PurchaseMetric) {
  focused.value = row; detailPage.value = 1; detail.value = undefined; detailOpen.value = true; await loadDetail()
}
async function loadDetail() {
  const current = ++detailSerial; detailLoading.value = true; drawerError.value = ''
  try {
    const response = await requestJson<{ data: PurchaseAnalysis }>('/api/purchase-analysis/query', { method: 'POST', body: JSON.stringify({ ...applied.value, detailKey: focused.value?.key ?? '', page: detailPage.value }) }, props.token)
    if (current === detailSerial && !disposed) detail.value = response.data
  } catch { detail.value = undefined; drawerError.value = '采购明细读取失败，请核对当前数据权限后重新打开。' }
  finally { if (current === detailSerial) detailLoading.value = false }
}
async function download(inDrawer = false) {
  busy.value = true; error.value = ''; drawerError.value = ''
  try { await exportPurchaseAnalysis({ ...applied.value, detailKey: inDrawer ? focused.value?.key ?? '' : '' }, props.token); message.value = '采购分析明细已导出。' }
  catch { const text = '导出失败，请核对导出权限和筛选范围后重试。'; if (inDrawer) drawerError.value = text; else error.value = text }
  finally { busy.value = false }
}
onMounted(() => void load())
onBeforeUnmount(() => { disposed = true; serial++; detailSerial++; clearTimeout(timer) })
</script>

<template>
  <section class="purchase-analysis pdm-loading-host" aria-label="采购分析">
    <p v-if="error" role="alert" class="purchase-analysis__error">{{ error }}</p><p v-if="message" role="status">{{ message }}</p>
    <form class="purchase-analysis__filters" @submit.prevent="load()">
      <label>开始日期<el-date-picker v-model="filter.from" type="date" value-format="YYYY-MM-DD" :clearable="false" aria-label="开始日期" /></label>
      <label>结束日期<el-date-picker v-model="filter.through" type="date" value-format="YYYY-MM-DD" :clearable="false" aria-label="结束日期" /></label>
      <label v-for="scope in scopes" :key="scope.key">{{ scope.label }}<el-select v-model="filter[scope.key]" multiple filterable clearable collapse-tags collapse-tags-tooltip :aria-label="scope.label" placeholder="全部已授权范围">
        <el-option v-for="option in data?.options[scope.key] ?? []" :key="option.value" :value="option.value" :label="option.label" /></el-select></label>
      <label>料号／名称／型号／品牌<el-input v-model="filter.query" clearable placeholder="关键词" title="搜索料号、名称、型号、品牌或采购单号" aria-label="采购关键词" /></label>
      <div class="purchase-analysis__actions">
        <el-button type="primary" native-type="submit" :disabled="loading">查询</el-button>
        <el-button :disabled="loading" @click="reset">重置</el-button>
        <el-button :disabled="loading" @click="load(false)">刷新</el-button>
        <el-button v-if="data?.canExport" :disabled="busy || loading" @click="download()">导出</el-button>
        <el-button v-if="data?.canSync" :disabled="busy || running" @click="synchronize">{{ running ? '同步中' : '同步' }}</el-button>
        <el-button v-if="data?.canConfigure" :disabled="busy" @click="showSettings">采集设置</el-button>
        <el-button @click="advanced = !advanced">分析条件</el-button>
      </div>
      <div v-if="advanced" class="purchase-analysis__advanced">
        <label>采购次数下限<el-input-number v-model="filter.minOrders" :min="0" :step="1" :precision="0" aria-label="采购次数下限" /></label><label>采购次数上限<el-input-number v-model="filter.maxOrders" :min="0" :step="1" :precision="0" aria-label="采购次数上限" /></label>
        <label>数量下限<QuantityInputNumber v-model="filter.minQuantity" :min="0" aria-label="数量下限" /></label><label>数量上限<QuantityInputNumber v-model="filter.maxQuantity" :min="0" aria-label="数量上限" /></label>
        <template v-if="data?.canCost"><label>金额下限<el-input-number v-model="filter.minAmount" :min="0" :step="100" aria-label="金额下限" /></label><label>金额上限<el-input-number v-model="filter.maxAmount" :min="0" :step="100" aria-label="金额上限" /></label>
          <label>价格涨幅下限（%）<el-input-number v-model="filter.minPriceChange" :step="1" aria-label="价格涨幅下限" /></label><label>小额订单阈值<el-input-number v-model="filter.smallOrderAmount" :min="0" :step="100" aria-label="小额订单阈值" /></label></template>
        <label>高频采购次数<el-input-number v-model="filter.frequentOrders" :min="2" :step="1" :precision="0" aria-label="高频采购次数" /></label><label>零散采购窗口（天）<el-input-number v-model="filter.fragmentDays" :min="1" :max="365" :step="1" :precision="0" aria-label="零散采购窗口" /></label>
        <label>展示前几项<el-select v-model="filter.top" aria-label="排名数量"><el-option v-for="n in [10,20,50,100,200]" :key="n" :value="n" :label="`${n}项`" /></el-select></label>
        <label v-if="data?.canCost">金额分析维度<el-select v-model="filter.dimension" aria-label="金额分析维度"><el-option value="material" label="物料" /><el-option value="category" label="物料分类" /><el-option value="supplier" label="供应商" /></el-select></label>
      </div>
    </form>
    <div v-if="data" class="purchase-analysis__sync"><span>最后成功同步：{{ time(data.status.lastSuccess) }}</span><span>覆盖日期：{{ data.status.from || '—' }} 至 {{ data.status.through || '—' }}</span><span v-if="running">正在采集，当前展示上次完整数据</span></div>
    <el-alert v-if="data?.status.error" :title="data.status.error" type="warning" :closable="false" />
    <div v-if="data" class="purchase-analysis__cards">
      <article v-if="data.canCost" class="pdm-stat-card" :style="cardIcon('M5 4l7 7 7-7M5 11h14M5 16h14M12 11v10')"><span>采购订单未税金额</span><div v-for="[currency, amount] in amounts" :key="currency" class="purchase-analysis__amount"><strong>{{ amount == null ? '—' : number(Math.trunc(amount), 0) }}</strong><small>{{ currency || '币种待核对' }}</small></div><strong v-if="!amounts.length">—</strong><small>不同币种分列 · 非结算支出</small></article>
      <article v-for="card in [{ name: '采购订单', value: data.orderCount, unit: '单', icon: 'M6 3h12v18H6zM9 8h6M9 12h6M9 16h4' }, { name: '采购物料', value: data.materialCount, unit: '种', icon: 'M3 7l9-4 9 4v10l-9 4-9-4zM3 7l9 4 9-4M12 11v10' }, { name: '供应商', value: data.supplierCount, unit: '家', icon: 'M4 21V7l8-4 8 4v14M2 21h20M8 9v2m8-2v2M8 14v2m8-2v2M11 21v-5h2v5' }, { name: '高频采购物料组', value: data.frequentCount, unit: '组', icon: 'M4 8h16l-4-4M20 16H4l4 4M20 8v4M4 16v-4' }]" :key="card.name" class="pdm-stat-card" :style="cardIcon(card.icon)"><span>{{ card.name }}</span><div><strong>{{ number(card.value, 0) }}</strong><small>{{ card.unit }}</small></div><small>{{ data.from }} 至 {{ data.through }}</small></article>
    </div>
    <div v-if="data" class="purchase-analysis__results">
      <el-tabs v-model="active"><el-tab-pane v-for="tab in visibleTabs" :key="tab.key" :name="tab.key" :label="tab.label" /></el-tabs>
      <div class="purchase-analysis__sync"><span>高频 ≥ {{ applied.frequentOrders }} 次 · 零散窗口 {{ applied.fragmentDays }} 天 · 每组前 {{ applied.top }} 项</span><el-button @click="showDetail()">全部明细</el-button></div>
      <p v-if="active === 'volume'" class="purchase-analysis__muted">数量按交易单位分组展示；请结合物料分类或关键词缩小到可比较物料，异单位不换算。</p>
      <p v-if="active === 'suppliers'" class="purchase-analysis__muted">占比以当前筛选范围、对应币种的完整金额为分母。A/B/C按累计80%/95%划分；需同时评估单一来源风险。</p>
      <el-table data-pdm-column-layout="scroll" :data="rows" row-key="key" empty-text="当前条件下暂无分析结果" max-height="560">
        <el-table-column prop="code" label="编码" min-width="135" /><el-table-column prop="name" label="名称" min-width="180" show-overflow-tooltip />
        <el-table-column prop="orders" label="采购次数" width="90" />
        <el-table-column v-if="quantityView" label="数量／单位" min-width="130"><template #default="{ row }">{{ number(row.quantity, 4) }} {{ row.unit }}</template></el-table-column>
        <el-table-column v-if="active === 'frequent'" label="月均次数" width="95"><template #default="{ row }">{{ number(row.ordersPerMonth) }}</template></el-table-column>
        <el-table-column v-if="active === 'frequent'" label="平均间隔（天）" width="125"><template #default="{ row }">{{ number(row.averageInterval) }}</template></el-table-column>
        <el-table-column v-if="active === 'volume' || active === 'fragmented'" label="单次平均数量" width="120"><template #default="{ row }">{{ number(row.averageBatch, 4) }}</template></el-table-column>
        <el-table-column v-if="data.canCost" label="未税金额" min-width="125"><template #default="{ row }">{{ number(row.amount) }}</template></el-table-column><el-table-column v-if="data.canCost" prop="currency" label="币种" min-width="130" />
        <template v-if="active === 'prices'"><el-table-column label="加权均价" width="110"><template #default="{ row }">{{ number(row.averagePrice, 4) }}</template></el-table-column><el-table-column label="最近日均价" width="110"><template #default="{ row }">{{ number(row.lastPrice, 4) }}</template></el-table-column><el-table-column label="最低／最高价" width="155"><template #default="{ row }">{{ number(row.minPrice, 4) }} / {{ number(row.maxPrice, 4) }}</template></el-table-column><el-table-column label="首末日涨幅" width="110"><template #default="{ row }">{{ row.priceChange == null ? '—' : number(row.priceChange) + '%' }}</template></el-table-column></template>
        <template v-if="active === 'spend' || active === 'suppliers'"><el-table-column label="金额占比" width="95"><template #default="{ row }">{{ number(row.share) }}%</template></el-table-column><el-table-column label="累计占比" width="95"><template #default="{ row }">{{ number(row.cumulativeShare) }}%</template></el-table-column><el-table-column prop="abc" label="分级" width="65" /></template>
        <el-table-column v-if="active === 'fragmented'" prop="fragmentOrders" label="短期下单次数" width="120" /><el-table-column v-if="active === 'fragmented' && data.canCost" prop="smallOrders" label="物料小额批次" width="120" />
        <el-table-column prop="projects" label="项目数" width="80" /><el-table-column v-if="active !== 'suppliers'" prop="suppliers" label="供应商数" width="90" />
        <el-table-column label="操作" width="80" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="showDetail(row)">查看明细</el-button></template></el-table-column>
      </el-table>
      <details class="purchase-analysis__rules"><summary>统计口径与数据说明</summary><p v-for="notice in data.notices" :key="notice">{{ notice }}</p><p v-if="data.canCost && data.missingAmounts">{{ data.missingAmounts }}行金额缺失或无权查看，相关汇总显示“—”，不按零处理。</p></details>
    </div>
    <SquareLoader v-if="loading" overlay label="正在读取采购分析" />
    <el-drawer v-model="detailOpen" :title="focused ? focused.name + ' · 采购明细' : '采购明细'" size="92%" destroy-on-close>
      <div class="pdm-loading-host purchase-analysis__detail"><p v-if="drawerError" role="alert">{{ drawerError }}</p><p v-if="focused">{{ focused.reason }}</p>
        <el-button v-if="detail?.canExport" :disabled="busy" @click="download(true)">导出</el-button>
        <template v-if="detail?.priceComparisons.length"><h3>同料供应商价格对比</h3><el-table :data="detail.priceComparisons" size="small"><el-table-column prop="name" label="供应商" min-width="180" /><el-table-column prop="orders" label="采购次数" /><el-table-column prop="quantity" label="采购数量" /><el-table-column prop="unit" label="单位" /><el-table-column label="加权均价"><template #default="{ row }">{{ number(row.averagePrice, 4) }}</template></el-table-column><el-table-column prop="currency" label="币种" /></el-table><p class="purchase-analysis__muted">按本条目全部筛选记录计算；议价时需同时核对批量、交期和质量条件。</p></template>
        <div class="purchase-analysis__trend" aria-label="月度采购次数趋势"><div v-for="point in detail?.trend ?? []" :key="point.month"><span>{{ point.month }}</span><i :style="{ width: `${Math.max(2, point.orders / trendMax * 100)}%` }" /><strong>{{ point.orders }}次</strong><small v-if="focused && detail?.canCost">均价 {{ number(point.averagePrice, 4) }}</small></div></div>
        <el-table data-pdm-column-layout="scroll" :data="detail?.details ?? []" empty-text="暂无可查看的采购明细" max-height="540"><el-table-column prop="date" label="日期" width="110" /><el-table-column prop="orderNumber" label="采购单号" width="155" /><el-table-column prop="lineNumber" label="行号" width="60" /><el-table-column prop="typeName" label="采购类型" width="130" /><el-table-column prop="organization" label="组织" width="90" /><el-table-column prop="materialCode" label="料号" width="125" /><el-table-column prop="materialName" label="名称" min-width="150" /><el-table-column prop="specification" label="型号" min-width="155" /><el-table-column prop="brand" label="品牌" width="110" /><el-table-column prop="supplierName" label="供应商" min-width="190" /><el-table-column prop="buyerName" label="采购员" width="90" /><el-table-column prop="projectCode" label="项目" width="120" /><el-table-column prop="subprojectCode" label="子项目" width="130" /><el-table-column prop="quantity" label="数量" width="95" /><el-table-column prop="unitName" label="单位" width="70" /><el-table-column v-if="detail?.canCost" label="未税单价" width="110"><template #default="{ row }">{{ number(row.unitPrice, 4) }}</template></el-table-column><el-table-column v-if="detail?.canCost" label="未税金额" width="125"><template #default="{ row }">{{ number(row.netAmount) }}</template></el-table-column><el-table-column v-if="detail?.canCost" prop="currency" label="币种" width="140" /></el-table>
        <el-pagination v-model:current-page="detailPage" :page-size="50" :total="detail?.detailCount ?? 0" layout="total, prev, pager, next" @current-change="loadDetail" />
        <SquareLoader v-if="detailLoading" overlay label="正在读取采购明细" />
      </div>
    </el-drawer>
    <el-dialog v-model="settingsOpen" title="采购分析采集设置" width="min(560px, 94vw)">
      <el-form v-if="settings" label-position="top"><el-alert title="组织编码须对应当前公司。只读采集已核准采购订单；不同组织整批成功后更新快照。" type="info" :closable="false" />
        <el-form-item label="U9C组织编码（多个用顿号或逗号分隔）"><el-input v-model="organizationText" aria-label="采集组织编码" /></el-form-item>
        <el-form-item label="历史范围（月）"><el-input-number v-model="settings.historyMonths" :min="1" :max="120" :step="1" :precision="0" /></el-form-item>
        <el-form-item label="同步间隔（分钟）"><el-input-number v-model="settings.intervalMinutes" :min="15" :max="1440" :step="15" :precision="0" /></el-form-item>
        <el-form-item label="自动同步"><el-switch v-model="settings.autoSync" /></el-form-item><p>当前U9C接口使用按日期分批全量核对。数据量较大时可延长同步间隔。</p>
        <p v-if="drawerError" role="alert">{{ drawerError }}</p></el-form>
      <template #footer><el-button :disabled="busy" @click="settingsOpen = false">取消</el-button><el-button type="primary" :loading="busy" @click="saveSettings">保存</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.purchase-analysis { display:flex; flex-direction:column; gap:16px; min-width:0; padding-bottom:20px; }
.purchase-analysis p { margin:5px 0; }.purchase-analysis__muted { color:var(--pdm-text-soft); font-size:12px; }
.purchase-analysis__actions { grid-column:1/-1; display:flex; flex-wrap:wrap; gap:8px; align-items:center; }.purchase-analysis__actions :deep(.el-button+.el-button) { margin-left:0; }
.purchase-analysis__filters,.purchase-analysis__advanced { display:grid; grid-template-columns:repeat(auto-fit,minmax(175px,1fr)); gap:12px; align-items:end; }
.purchase-analysis__filters { background:var(--pdm-surface); padding:16px; border-radius:var(--pdm-stat-card-radius); box-shadow:var(--pdm-stat-card-shadow); }
.purchase-analysis__filters label { display:flex; flex-direction:column; gap:6px; font-size:12px; color:var(--pdm-text-soft); min-width:0; }
.purchase-analysis__filters :deep(.el-date-editor),.purchase-analysis__filters :deep(.el-input-number) { width:100%; }
.purchase-analysis__advanced { grid-column:1/-1; border-top:1px solid var(--pdm-border); padding-top:12px; }
.purchase-analysis__sync { display:flex; flex-wrap:wrap; justify-content:space-between; gap:10px; align-items:center; color:var(--pdm-text-soft); font-size:12px; }
.purchase-analysis__cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:16px; }.purchase-analysis__cards strong { font-size:28px; font-weight:650; overflow-wrap:anywhere; }
.purchase-analysis__cards small { color:var(--pdm-text-soft); font-size:12px; margin-left:4px; }.purchase-analysis__cards article>small { margin-top:auto; margin-left:0; }
.purchase-analysis__amount { display:flex; flex-wrap:wrap; align-items:baseline; gap:4px; }
.purchase-analysis__results { min-width:0; }.purchase-analysis__results>.purchase-analysis__sync { margin-bottom:12px; }
.purchase-analysis__error { color:var(--pdm-danger); }.purchase-analysis__rules { margin-top:16px; font-size:12px; color:var(--pdm-text-soft); line-height:1.7; }.purchase-analysis__rules summary { cursor:pointer; color:var(--pdm-text); }
.purchase-analysis__detail { min-height:250px; }.purchase-analysis__trend { margin:12px 0; display:grid; gap:6px; max-height:230px; overflow:auto; }.purchase-analysis__trend>div { display:grid; grid-template-columns:70px minmax(50px,1fr) 65px 130px; align-items:center; gap:10px; font-size:12px; }.purchase-analysis__trend i { display:block; height:9px; border-radius:99px; background:var(--pdm-primary,#10b981); }.purchase-analysis__trend strong { font-weight:500; }
@media(max-width:680px) { .purchase-analysis__filters { grid-template-columns:repeat(2,minmax(0,1fr)); padding:12px; }.purchase-analysis__cards { grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }.purchase-analysis__cards strong { font-size:23px; }.purchase-analysis__trend>div { grid-template-columns:65px minmax(35px,1fr) 50px; }.purchase-analysis__trend small { grid-column:2/-1; } }
</style>
