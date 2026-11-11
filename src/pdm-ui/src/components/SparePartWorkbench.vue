<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, watch } from 'vue'
import { readSparePartFile, requestJson } from '../api'
import CategoryNavigation from './CategoryNavigation.vue'
import SearchField from './SearchField.vue'

const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ token: string }>()
interface Project { id: string; parentId?: string; rootId: string; code: string; name: string; deviceModel?: string; serialNumbers?: string[] }
interface Drawing { name: string; revision: string; canPreview: boolean }
interface Part {
  id: string; projectId: string; materialCode: string; name: string; specification?: string; brand?: string
  unit: string; quantity: number; category: string; machined: boolean; material?: string; surfaceTreatment?: string; note?: string
  sourceKind?: string; bomVersion?: string; configuration?: string; drawings: Drawing[]; manualPrice?: number; rowVersion: number
  quantityPerSet?: number | null
}
interface Row {
  part: Part; project: Project; root: Project; cost?: number; price?: number; priceSource: string; costStatus: string
  currency: string; purchasedAt?: string; synchronizedAt?: string; rawCost?: number; purchaseDocument?: string; categoryName: string
}
interface Category { id: string; name: string; isActive: boolean }
interface CategorySettings { rowVersion: number; categories: Category[] }
interface Page {
  items: Row[]; projects: Project[]; total: number; missingPrice: number; missingDrawing: number; page: number; pageSize: number
  canSeeCost: boolean; canMaintain: boolean; canPrice: boolean; canPreview: boolean; canDownload: boolean; canExport: boolean
  categories: Category[]; canManageCategories: boolean
}
interface Band { from: number; to: number | null; multiplier: number }
interface Pricing { rowVersion: number; costMultiplier: number; bands: Band[]; u9OrganizationCode: string; currency: string }

const pageData = ref<Page>()
const projectId = ref('')
const query = ref('')
const appliedQuery = ref('')
const category = ref('')
const selectedIds = ref<string[]>([])
const page = ref(1)
const loading = ref(false)
const busy = ref(false)
const message = ref('')
const error = ref('')
const panel = ref<'pricing' | 'categories' | 'manual' | 'detail'>()
const pricing = ref<Pricing>()
const categorySettings = ref<CategorySettings>()
const focused = ref<Row>()
const manualPrice = ref<number | string>('')
const detail = ref({ materialCode: '', category: 'Spare', quantity: 1, quantityPerSet: '' as number | string, note: '' })
const manual = ref(newManual())
const preview = ref<{ url: string; name: string; image: boolean }>()
let sequence = 0
let previewSequence = 0

function newManual(defaultCategory = 'Spare') {
  return { projectId: projectId.value, materialCode: '', name: '', specification: '', brand: '', unit: '件',
    quantity: 1, quantityPerSet: '' as number | string, category: defaultCategory, machined: false, material: '', surfaceTreatment: '', note: '' }
}

const projects = computed(() => pageData.value?.projects ?? [])
const projectCategories = computed(() => projects.value
  .filter(project => project.id === project.rootId)
  .map(project => ({ id: project.id, label: project.code, children: [] })))
const pageCount = computed(() => Math.max(1, Math.ceil((pageData.value?.total ?? 0) / (pageData.value?.pageSize ?? 50))))
const selectedProject = computed(() => projects.value.find(project => project.id === projectId.value))
const categories = computed(() => pageData.value?.categories ?? [])
const activeCategories = computed(() => categories.value.filter(item => item.isActive))
const pageRowIds = computed(() => (pageData.value?.items ?? []).map(row => row.part.id))
const allSelected = computed(() => pageRowIds.value.length > 0 && selectedIds.value.length === pageRowIds.value.length)
const exportDisabled = computed(() => busy.value || loading.value || selectedIds.value.length === 0)
const columnCount = computed(() => 10 + Number(!!pageData.value?.canSeeCost) + Number(!!pageData.value?.canExport))
const panelTitle = computed(() => panel.value === 'pricing' ? '定价设置' : panel.value === 'categories' ? '分类设置'
  : panel.value === 'manual' ? '新增备件' : '备件详情')
const sourceLabels: Record<string, string> = { Standard: '标准件 BOM', NonStandard: '非标件 BOM', Electrical: '电气件 BOM' }
const formatAmount = (value?: number | null) => value == null ? '—' : value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const date = (value?: string) => value ? new Date(value).toLocaleDateString('zh-CN', { timeZone: 'Asia/Shanghai' }) : '—'
const categoryLabel = (item: Category) => item.isActive ? item.name : item.name + '（已停用）'
const serialText = (project: Project) => project.serialNumbers?.join('、') || '—'
const bandLower = (index: number) => index === 0 ? 0 : pricing.value?.bands[index - 1]?.to ?? 0

function filters() {
  const params = new URLSearchParams({ page: String(page.value), pageSize: '50' })
  if (projectId.value) params.set('projectId', projectId.value)
  if (appliedQuery.value) params.set('query', appliedQuery.value)
  if (category.value) params.set('category', category.value)
  return params
}

async function load() {
  const request = ++sequence
  loading.value = true
  error.value = ''
  try {
    const result = await requestJson<Page>('/api/spare-parts?' + filters().toString(), {}, props.token)
    if (request === sequence) {
      pageData.value = result
      selectedIds.value = selectedIds.value.filter(id => pageRowIds.value.includes(id))
      page.value = result.page
      if (focused.value) {
        const updated = result.items.find(row => row.part.id === focused.value?.part.id)
        if (updated) setDetail(updated)
        else if (panel.value === 'detail') closePanel()
      }
    }
  } catch (ex) {
    if (request === sequence) {
      pageData.value = undefined
      selectedIds.value = []
      error.value = failure(ex)
    }
  } finally {
    if (request === sequence) loading.value = false
  }
}

function failure(ex: unknown) {
  const text = ex instanceof Error ? ex.message : ''
  return /[\u4e00-\u9fff]/.test(text) ? text : '操作未完成，请检查连接并重试。'
}
function selectProject(id: string) {
  projectId.value = id
  selectedIds.value = []
  page.value = 1
  void load()
}
function search() {
  appliedQuery.value = query.value.trim()
  selectedIds.value = []
  page.value = 1
  void load()
}
function changePage(direction: number) {
  page.value += direction
  selectedIds.value = []
  void load()
}
function selectPage(event: Event) {
  selectedIds.value = (event.target as HTMLInputElement).checked ? [...pageRowIds.value] : []
}
function clearPreview() {
  previewSequence++
  if (preview.value) URL.revokeObjectURL(preview.value.url)
  preview.value = undefined
}
function closePanel() {
  clearPreview()
  panel.value = undefined
  focused.value = undefined
}
function setDetail(row: Row) {
  focused.value = row
  manualPrice.value = row.part.manualPrice ?? ''
  detail.value = { materialCode: row.part.materialCode, category: row.part.category, quantity: row.part.quantity,
    quantityPerSet: row.part.quantityPerSet ?? '', note: row.part.note ?? '' }
}
function openDetail(row: Row) {
  clearPreview()
  error.value = ''
  setDetail(row)
  panel.value = 'detail'
}
function openManual() {
  error.value = ''
  manual.value = newManual(activeCategories.value.find(item => item.id === 'Spare')?.id ?? activeCategories.value[0]?.id ?? '')
  panel.value = 'manual'
}
async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  message.value = ''
  try { await action() } catch (ex) { error.value = failure(ex) }
  finally { busy.value = false }
}
function write<T>(path: string, method: 'POST' | 'PUT', value?: object) {
  return requestJson<T>('/api/spare-parts/' + path, { method, body: value === undefined ? undefined : JSON.stringify(value) }, props.token)
}
function openPricing() {
  void run(async () => {
    const result = await requestJson<Pricing>('/api/spare-parts/pricing', {}, props.token)
    pricing.value = { ...result, bands: result.bands.length ? result.bands : [{ from: 0, to: null, multiplier: 1 }] }
    panel.value = 'pricing'
  })
}
function openCategories() {
  void run(async () => {
    categorySettings.value = await requestJson<CategorySettings>('/api/spare-parts/categories', {}, props.token)
    panel.value = 'categories'
  })
}
function addCategory() {
  categorySettings.value?.categories.push({ id: '', name: '', isActive: true })
}
function saveCategories() {
  if (!categorySettings.value) return
  void run(async () => {
    await write('categories', 'PUT', categorySettings.value)
    closePanel()
    message.value = '备件分类已保存。'
    await load()
  })
}
function addBand() {
  const bands = pricing.value?.bands
  if (!bands || bands.length >= 100) return
  const last = bands[bands.length - 1]!
  last.to = bandLower(bands.length - 1) + 100
  bands.push({ from: last.to, to: null, multiplier: last.multiplier })
}
function removeBand(index: number) {
  const bands = pricing.value?.bands
  if (!bands || bands.length < 2) return
  bands.splice(index, 1)
  bands[bands.length - 1]!.to = null
}
function savePricing() {
  const draft = pricing.value
  if (!draft) return
  void run(async () => {
    if (!Number.isFinite(draft.costMultiplier) || draft.costMultiplier <= 0 ||
        draft.bands.some((band, index) => !Number.isFinite(band.multiplier) || band.multiplier <= 0 ||
          (index < draft.bands.length - 1 && (!Number.isFinite(band.to) || Number(band.to) <= bandLower(index))))) {
      throw new Error('请填写大于0的倍率，并检查各档上限大于下限。')
    }
    await write('pricing', 'PUT', { ...draft, bands: draft.bands.map((band, index) => ({ ...band, from: bandLower(index) })) })
    closePanel()
    message.value = '公司定价设置已保存。'
    await load()
  })
}
function addManual() {
  void run(async () => {
    const result = await write<{ added: number }>('manual', 'POST',
      { ...manual.value, quantityPerSet: manual.value.quantityPerSet === '' ? null : manual.value.quantityPerSet })
    closePanel()
    message.value = result.added ? '备件已加入项目。' : '该项目已有相同的备件。'
    await load()
  })
}
function saveDetails() {
  const row = focused.value
  if (!row) return
  void run(async () => {
    const clearQuantityPerSet = detail.value.quantityPerSet === ''
    await write(row.part.id + '/details', 'PUT', { ...detail.value, rowVersion: row.part.rowVersion,
      clearQuantityPerSet, quantityPerSet: clearQuantityPerSet ? null : detail.value.quantityPerSet })
    message.value = '备件资料已保存。'
    await load()
  })
}
function saveManualPrice(reset = false) {
  const row = focused.value
  if (!row) return
  void run(async () => {
    const price = reset || manualPrice.value === '' ? null : Number(manualPrice.value)
    await write(row.part.id + '/price', 'PUT', { rowVersion: row.part.rowVersion, price })
    message.value = price === null ? '已恢复倍率定价。' : '手动售价已保存。'
    await load()
  })
}
function synchronize() {
  void run(async () => {
    const result = await write<{ matched: number }>('synchronize', 'POST')
    message.value = '采购价同步完成，取得 ' + result.matched + ' 个料号的采购单价。'
    await load()
  })
}
function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 1500)
}
function exportList(quotation: boolean) {
  if (exportDisabled.value) return
  void run(async () => {
    const params = filters()
    params.set('quotation', String(quotation))
    for (const id of selectedIds.value) params.append('ids', id)
    saveBlob(await readSparePartFile('export?' + params.toString(), props.token),
      quotation ? '备品备件报价清单.xlsx' : '备品备件内部清单.xlsx')
  })
}
function openDrawing(row: Row, drawing: Drawing, index: number, download = false) {
  if (busy.value) return
  if (!download) openDetail(row)
  const request = ++previewSequence
  void run(async () => {
    const blob = await readSparePartFile(row.part.id + '/drawings/' + index + (download ? '/download' : '/preview'), props.token)
    if (download) saveBlob(blob, drawing.name)
    else if (request === previewSequence && panel.value === 'detail') {
      clearPreview()
      preview.value = { url: URL.createObjectURL(blob), name: drawing.name, image: blob.type.startsWith('image/') }
    }
  })
}

watch(() => props.token, () => { closePanel(); selectedIds.value = []; if (props.token) void load() }, { immediate: true })
onBeforeUnmount(() => { sequence++; clearPreview() })
</script>

<template>
  <section class="spare-parts" aria-label="备品备件">
    <CategoryNavigation class="spare-parts__projects" :nodes="projectCategories" :model-value="projectId"
      aria-label="项目目录" all-label="全部项目" @update:model-value="selectProject" />
    <div class="spare-parts__workspace">
      <div v-if="pageData" class="spare-parts__metrics">
        <article><span>可查项目</span><strong>{{ projects.length }}</strong></article>
        <article><span>备件记录</span><strong>{{ pageData.total }}</strong></article>
        <article><span>待定价</span><strong>{{ pageData.missingPrice }}</strong></article>
        <article v-if="pageData.canPreview"><span>机加件待补图纸</span><strong>{{ pageData.missingDrawing }}</strong></article>
      </div>
      <section class="pdm-panel spare-parts__list" aria-label="备件清单">
        <div class="pdm-toolbar spare-parts__toolbar">
          <div class="pdm-toolbar__group spare-parts__filters">
            <select v-model="category" aria-label="备件分类" @change="search">
              <option value="">全部分类</option>
              <option v-for="item in categories" :key="item.id" :value="item.id">{{ categoryLabel(item) }}</option>
              <optgroup label="属性"><option value="Machined">机加件</option></optgroup>
            </select>
            <SearchField><input v-model="query" type="search" aria-label="料号、名称或项目" placeholder="料号 / 名称 / 项目" @keydown.enter.prevent="search">
              <template #action><button type="button" class="pdm-primary-action" @click="search">搜索</button></template>
            </SearchField>
            <button v-if="pageData?.canExport && pageData.canSeeCost" type="button" class="pdm-secondary-action" :disabled="exportDisabled" @click="exportList(false)">内部清单</button>
            <button v-if="pageData?.canExport" type="button" class="pdm-secondary-action" :disabled="exportDisabled" @click="exportList(true)">报价清单</button>
            <span v-if="selectedIds.length" role="status">已选 {{ selectedIds.length }} 项</span>
          </div>
          <div class="pdm-toolbar__group spare-parts__actions spare-parts__list-actions">
            <button type="button" class="pdm-secondary-action" :disabled="loading" @click="load">刷新</button>
            <button v-if="pageData?.canMaintain" type="button" class="pdm-primary-action" @click="openManual">新增</button>
            <button v-if="pageData?.canPrice" type="button" class="pdm-secondary-action" :disabled="busy" @click="synchronize">同步采购价</button>
            <button v-if="pageData?.canPrice" type="button" class="pdm-secondary-action" :disabled="busy" @click="openPricing">定价设置</button>
            <button v-if="pageData?.canManageCategories" type="button" class="pdm-secondary-action" :disabled="busy" @click="openCategories">分类设置</button>
          </div>
        </div>
        <div v-if="error && !panel" class="spare-parts__error spare-parts__list-notice" role="alert">{{ error }}</div>
        <div v-if="message && !panel" class="spare-parts__message spare-parts__list-notice" role="status">{{ message }}</div>
        <div class="spare-parts__table-wrap" :aria-busy="loading">
          <table class="spare-parts__table">
            <thead><tr>
              <th v-if="pageData?.canExport" class="spare-parts__selection">
                <input type="checkbox" aria-label="勾选当前页全部备件" :checked="allSelected"
                  :indeterminate.prop="selectedIds.length > 0 && !allSelected" :disabled="loading || !pageRowIds.length" @change="selectPage">
              </th>
              <th>项目 / 子项目</th><th>设备型号</th><th>设备序列号</th><th>物料 / 规格</th><th>分类</th><th>图纸</th>
              <th>单套数量</th><th>最小起订量</th><th v-if="pageData?.canSeeCost">未税成本</th><th>未税售价</th><th>操作</th>
            </tr></thead>
            <tbody>
              <tr v-for="row in pageData?.items ?? []" :key="row.part.id">
                <td v-if="pageData?.canExport" class="spare-parts__selection"><input v-model="selectedIds" type="checkbox" :value="row.part.id"
                  :aria-label="'勾选备件 ' + row.project.code + ' ' + row.part.name" :disabled="loading"></td>
                <td><strong>{{ row.root.code }}</strong><small>{{ row.root.id === row.project.id ? row.project.name : row.project.code + ' · ' + row.project.name }}</small></td>
                <td><span class="spare-parts__equipment" :title="row.project.deviceModel">{{ row.project.deviceModel || '—' }}</span></td>
                <td><span class="spare-parts__equipment" :title="serialText(row.project)">{{ serialText(row.project) }}</span></td>
                <td><strong>{{ row.part.name }}</strong><small>{{ row.part.materialCode || '待补料号' }}</small><small>{{ [row.part.specification, row.part.brand].filter(Boolean).join(' · ') }}</small></td>
                <td>{{ row.categoryName }}<small v-if="row.part.machined">机加件</small></td>
                <td>
                  <template v-if="!pageData?.canPreview">—</template>
                  <button v-else-if="row.part.drawings[0]" type="button" class="spare-parts__link" @click="row.part.drawings[0].canPreview ? openDrawing(row, row.part.drawings[0], 0) : openDetail(row)">
                    {{ row.part.drawings[0].revision }}{{ row.part.drawings.length > 1 ? ' +' + (row.part.drawings.length - 1) : '' }}
                  </button>
                  <span v-else :class="{ 'spare-parts__warning': row.part.machined }">{{ row.part.machined ? '待补图纸' : '—' }}</span>
                </td>
                <td>{{ row.part.quantityPerSet == null ? '—' : row.part.quantityPerSet + ' ' + row.part.unit }}</td>
                <td>{{ row.part.quantity }} {{ row.part.unit }}</td>
                <td v-if="pageData?.canSeeCost"><strong>{{ formatAmount(row.cost) }}</strong><small>{{ row.cost != null ? date(row.purchasedAt) : row.costStatus }}</small></td>
                <td><strong>{{ formatAmount(row.price) }}<small v-if="row.price != null"> {{ row.currency }}</small></strong><small>{{ row.priceSource }}</small></td>
                <td><button type="button" class="spare-parts__link" @click="openDetail(row)">详情</button></td>
              </tr>
              <tr v-if="!pageData?.items.length"><td :colspan="columnCount" class="spare-parts__empty">{{ loading ? '正在读取备件…' : '暂无符合条件的备件' }}</td></tr>
            </tbody>
          </table>
        </div>
        <footer class="spare-parts__footer">
          <span>{{ selectedProject?.code || '全部授权项目' }} · 共 {{ pageData?.total ?? 0 }} 条</span>
          <div><button type="button" class="pdm-secondary-action" :disabled="page <= 1 || loading" @click="changePage(-1)">上一页</button>
            <span>{{ page }} / {{ pageCount }}</span><button type="button" class="pdm-secondary-action" :disabled="page >= pageCount || loading" @click="changePage(1)">下一页</button></div>
        </footer>
      </section>
    </div>

    <el-drawer :model-value="!!panel" :title="panelTitle"
      size="min(720px, 96vw)" :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy" @close="closePanel">
      <p v-if="error" class="spare-parts__error" role="alert">{{ error }}</p>
      <p v-if="message" class="spare-parts__message" role="status">{{ message }}</p>
      <form v-if="panel === 'pricing' && pricing" class="spare-parts__form" @submit.prevent="savePricing">
        <p>按显示成本匹配区间，自动售价为显示成本乘以该档报价倍率。每档含下限、不含上限。</p>
        <div class="spare-parts__fields">
          <label>成本倍率<input v-model.number="pricing.costMultiplier" type="number" min="0.0001" max="10000" step="any" required></label>
          <label>U9C 组织编码<input v-model.trim="pricing.u9OrganizationCode" maxlength="50" placeholder="填写当前公司对应的组织"></label>
          <label>计价本位币<input v-model.trim="pricing.currency" :readonly="pricing.rowVersion > 0" maxlength="3" required></label>
        </div>
        <table class="spare-parts__table spare-parts__bands">
          <thead><tr><th>成本下限</th><th>成本上限</th><th>报价倍率</th><th>操作</th></tr></thead>
          <tbody><tr v-for="(band, index) in pricing.bands" :key="index">
            <td>{{ formatAmount(bandLower(index)) }}</td>
            <td><input v-if="index < pricing.bands.length - 1" v-model.number="band.to" type="number" step="0.01" min="0.01" required :aria-label="'第' + (index + 1) + '档成本上限'"><span v-else>不限</span></td>
            <td><input v-model.number="band.multiplier" type="number" step="any" min="0.0001" max="10000" required :aria-label="'第' + (index + 1) + '档报价倍率'"></td>
            <td><button type="button" class="spare-parts__link" :disabled="pricing.bands.length < 2" @click="removeBand(index)">删除</button></td>
          </tr></tbody>
        </table>
        <div class="spare-parts__actions"><button type="button" class="pdm-secondary-action" :disabled="pricing.bands.length >= 100" @click="addBand">新增区间</button>
          <button type="submit" class="pdm-primary-action" :disabled="busy">保存</button></div>
      </form>
      <form v-else-if="panel === 'categories' && categorySettings" class="spare-parts__form" @submit.prevent="saveCategories">
        <p>分类适用于当前公司。停用后仍可查看已归类的备件。</p>
        <table class="spare-parts__table spare-parts__category-table">
          <thead><tr><th>分类名称</th><th>启用</th><th>操作</th></tr></thead>
          <tbody><tr v-for="(item, index) in categorySettings.categories" :key="item.id || index">
            <td><input v-model.trim="item.name" maxlength="40" required :aria-label="'第' + (index + 1) + '项分类名称'"></td>
            <td><input v-model="item.isActive" type="checkbox" :aria-label="'启用第' + (index + 1) + '项分类'"></td>
            <td><button v-if="!item.id" type="button" class="spare-parts__link" @click="categorySettings.categories.splice(index, 1)">移除</button>
              <span v-else>—</span></td>
          </tr></tbody>
        </table>
        <div class="spare-parts__actions"><button type="button" class="pdm-secondary-action" :disabled="busy || categorySettings.categories.length >= 100" @click="addCategory">新增分类</button>
          <button type="submit" class="pdm-primary-action" :disabled="busy">保存</button></div>
      </form>
      <form v-else-if="panel === 'manual'" class="spare-parts__form" @submit.prevent="addManual">
        <div class="spare-parts__fields">
          <label>关联项目<select v-model="manual.projectId" required><option value="">请选择项目</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label>
          <label>物料名称<input v-model.trim="manual.name" maxlength="200" required></label>
          <label>物料号<input v-model.trim="manual.materialCode" maxlength="160"></label>
          <label>规格<input v-model.trim="manual.specification" maxlength="200"></label>
          <label>品牌<input v-model.trim="manual.brand" maxlength="100"></label>
          <label>分类<select v-model="manual.category" required><option v-for="item in activeCategories" :key="item.id" :value="item.id">{{ item.name }}</option></select></label>
          <label>单位<input v-model.trim="manual.unit" maxlength="32" required></label>
          <label>单套数量<input v-model.number="manual.quantityPerSet" v-quantity type="number" min="0" step="any"></label>
          <label>最小起订量<input v-model.number="manual.quantity" v-quantity type="number" min="0" step="any" required></label>
          <label>材质<input v-model.trim="manual.material" maxlength="100"></label>
          <label>表面处理<input v-model.trim="manual.surfaceTreatment" maxlength="100"></label>
        </div>
        <label>备注<textarea v-model="manual.note" maxlength="500" rows="2"></textarea></label>
        <button type="submit" class="pdm-primary-action" :disabled="busy">保存</button>
      </form>
      <div v-else-if="panel === 'detail' && focused" class="spare-parts__form">
        <div><h3>{{ focused.part.name }}</h3><p>{{ focused.part.materialCode || '待补料号' }} · {{ focused.project.code }} · {{ focused.project.name }}</p></div>
        <dl class="spare-parts__info">
          <dt>来源</dt><dd>{{ [sourceLabels[focused.part.sourceKind || ''] || '人工新增', focused.part.bomVersion, focused.part.configuration].filter(Boolean).join(' · ') }}</dd>
          <dt>分类</dt><dd>{{ focused.categoryName }}</dd>
          <dt>单套数量</dt><dd>{{ focused.part.quantityPerSet == null ? '—' : focused.part.quantityPerSet + ' ' + focused.part.unit }}</dd>
          <dt>最小起订量</dt><dd>{{ focused.part.quantity }} {{ focused.part.unit }}</dd>
          <dt>规格 / 品牌</dt><dd>{{ [focused.part.specification, focused.part.brand].filter(Boolean).join(' · ') || '—' }}</dd>
          <dt>材质 / 表面处理</dt><dd>{{ [focused.part.material, focused.part.surfaceTreatment].filter(Boolean).join(' · ') || '—' }}</dd>
          <dt v-if="pageData?.canSeeCost">未税成本</dt><dd v-if="pageData?.canSeeCost">{{ formatAmount(focused.cost) }} {{ focused.currency }} · {{ focused.costStatus }}</dd>
          <dt>未税售价</dt><dd>{{ formatAmount(focused.price) }} {{ focused.currency }} · {{ focused.priceSource }}</dd>
          <dt v-if="pageData?.canSeeCost">采购 / 同步日期</dt><dd v-if="pageData?.canSeeCost">{{ date(focused.purchasedAt) }} / {{ date(focused.synchronizedAt) }}</dd>
          <dt v-if="pageData?.canPrice">原始采购价 / 单号</dt><dd v-if="pageData?.canPrice">{{ formatAmount(focused.rawCost) }} / {{ focused.purchaseDocument || '—' }}</dd>
          <dt>备注</dt><dd>{{ focused.part.note || '—' }}</dd>
        </dl>
        <template v-if="pageData?.canPreview">
          <h3>图纸</h3>
          <p v-if="!focused.part.drawings.length">待补图纸</p>
          <ul v-else class="spare-parts__drawings">
            <li v-for="(drawing, index) in focused.part.drawings" :key="index"><span>{{ drawing.name }} · {{ drawing.revision }}</span>
              <button v-if="drawing.canPreview" type="button" class="pdm-secondary-action" :disabled="busy" @click="openDrawing(focused, drawing, index)">预览</button>
              <button v-if="pageData.canDownload" type="button" class="pdm-secondary-action" :disabled="busy" @click="openDrawing(focused, drawing, index, true)">下载</button>
            </li>
          </ul>
          <img v-if="preview?.image" class="spare-parts__image" :src="preview.url" :alt="preview.name">
          <PdfDrawingViewer v-else-if="preview" :url="preview.url" class="spare-parts__pdf" />
        </template>
        <form v-if="pageData?.canMaintain" class="spare-parts__form spare-parts__section" @submit.prevent="saveDetails">
          <h3>维护备件</h3>
          <div class="spare-parts__fields">
            <label v-if="!focused.part.materialCode && !focused.part.sourceKind">补充料号<input v-model.trim="detail.materialCode" maxlength="160"></label>
            <label>分类<select v-model="detail.category" required>
              <option v-for="item in categories.filter(item => item.isActive || item.id === focused?.part.category)" :key="item.id" :value="item.id">{{ categoryLabel(item) }}</option>
            </select></label>
            <label>单套数量<input v-model.number="detail.quantityPerSet" v-quantity type="number" min="0" step="any"></label>
            <label>最小起订量<input v-model.number="detail.quantity" v-quantity type="number" min="0" step="any" required></label>
          </div>
          <label>备注<textarea v-model="detail.note" maxlength="500" rows="2"></textarea></label>
          <button type="submit" class="pdm-primary-action" :disabled="busy">保存资料</button>
        </form>
        <form v-if="pageData?.canPrice" class="spare-parts__form spare-parts__section" @submit.prevent="saveManualPrice()">
          <label>手动未税售价<input v-model.number="manualPrice" type="number" min="0" step="0.01" placeholder="留空时按倍率计价"></label>
          <div class="spare-parts__actions"><button type="submit" class="pdm-primary-action" :disabled="busy">保存售价</button>
            <button type="button" class="pdm-secondary-action" :disabled="busy || focused.part.manualPrice == null" @click="saveManualPrice(true)">恢复倍率</button></div>
        </form>
      </div>
    </el-drawer>
  </section>
</template>

<style scoped>
.spare-parts { display: grid; grid-template-columns: 190px minmax(0, 1fr); gap: var(--pdm-container-gap, 12px); min-width: 0; min-height: 0; flex: 1; }
.spare-parts__workspace { display: flex; flex-direction: column; min-width: 0; min-height: 0; gap: 12px; }
.spare-parts__toolbar, .spare-parts__footer { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; padding: 8px 10px; }
.spare-parts h3, .spare-parts p { margin: 0; }
.spare-parts__table small { display: block; color: var(--el-text-color-secondary); }
.spare-parts__actions, .spare-parts__footer > div, .spare-parts__filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.spare-parts__filters { min-width: 0; }
.spare-parts__list-actions { margin-inline-start: auto; justify-content: flex-end; }
.spare-parts__list-notice { padding-inline: 10px; }
.spare-parts__metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(135px, 1fr)); gap: 10px; }
.spare-parts__metrics article { display: grid; padding: 14px; gap: 4px; border-radius: 16px; background: var(--el-bg-color); box-shadow: 0 4px 14px #00000014; transition: transform .18s ease; }
.spare-parts__metrics article:hover { transform: translateY(-3px); }
.spare-parts__metrics strong { font-size: 24px; font-variant-numeric: tabular-nums; }
.spare-parts__list { display: flex; flex-direction: column; flex: 1; min-width: 0; min-height: 0; }
.spare-parts__filters :deep(.pdm-search-field) { width: min(300px, 100%); }
.spare-parts__table-wrap { min-height: 0; overflow: auto; }
.spare-parts__table { width: 100%; border-collapse: collapse; }
.spare-parts__table-wrap .spare-parts__table { min-width: 930px; }
.spare-parts__selection { width: 36px; }
.spare-parts__equipment { display: inline-block; max-width: 190px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle; }
.spare-parts__table :is(th, td) { text-align: center; vertical-align: middle; padding: 8px 6px; border-bottom: 1px solid var(--el-border-color-lighter); }
.spare-parts__table th { background: var(--el-fill-color-light); }
.spare-parts__table td strong { font-variant-numeric: tabular-nums; }
.spare-parts__table td strong small { display: inline; }
.spare-parts__table .spare-parts__empty { padding: 34px 8px; }
.spare-parts__link { border: 0; background: transparent; height: 24px; padding-inline: 4px; color: var(--el-color-primary); cursor: pointer; }
.spare-parts__link:disabled { opacity: .5; cursor: default; }
.spare-parts__warning { color: var(--el-color-warning); }
.spare-parts__error { color: var(--el-color-danger); }
.spare-parts__message { color: var(--el-color-success); }
.spare-parts__form { display: grid; gap: 12px; }
.spare-parts__fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 185px), 1fr)); gap: 10px; }
.spare-parts__form label { display: grid; gap: 5px; }
.spare-parts__form :is(input:not([type=checkbox]), select, textarea), .spare-parts__filters select { min-width: 0; width: 100%; padding: 3px 10px; border: 1px solid var(--el-border-color); background: var(--el-bg-color); color: inherit; border-radius: 999px; height: 28px; }
.spare-parts__filters select { width: 112px; }
.spare-parts__form textarea { border-radius: 16px; height: auto; }
.spare-parts__bands input, .spare-parts__category-table input:not([type=checkbox]) { max-width: 180px; text-align: center; height: 24px !important; }
.spare-parts__info { display: grid; grid-template-columns: auto 1fr; gap: 6px 14px; margin: 0; }
.spare-parts__info dd { margin: 0; overflow-wrap: anywhere; }
.spare-parts__info dt { color: var(--el-text-color-secondary); }
.spare-parts__section { padding-top: 12px; border-top: 1px solid var(--el-border-color-lighter); }
.spare-parts__drawings { list-style: none; margin: 0; padding: 0; }
.spare-parts__drawings li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-bottom: 5px; }
.spare-parts__drawings span { flex: 1; overflow-wrap: anywhere; }
.spare-parts__image { max-width: 100%; max-height: 60vh; object-fit: contain; }
.spare-parts__pdf { height: 55vh; }
@media (max-width: 750px) { .spare-parts { grid-template-columns: minmax(0, 1fr); grid-template-rows: auto minmax(0, 1fr); } .spare-parts__projects { max-height: 160px; } }
@media (prefers-reduced-motion: reduce) { .spare-parts__metrics article { transition: none; } .spare-parts__metrics article:hover { transform: none; } }
</style>
