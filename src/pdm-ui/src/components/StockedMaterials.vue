<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { Folder } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { addStockedMaterials, listMaterialStockPlans, listMaterials, listStandardLibraryCategories, listStockedMaterials,
  maintainStockedMaterial, materialAttachmentObjectUrl, readStockedMaterialPlan, refreshStockedMaterials, removeStockedMaterial } from '../api'
import { refreshMaterialStockPlans } from '../materialStockPlans'
import { ElMessage } from '../statusMessage'
import { formalStockedMaterial, stockSyncLabel, stockTime, type StockedMaterialPage, type StockedMaterialRow } from '../stockedMaterials'
import type { PdmMaterial, StandardLibraryCategory } from '../types'
import { userErrorMessage } from '../userErrorMessage'
import MaterialImagePreview from './MaterialImagePreview.vue'
import StockedMaterialCategories from './StockedMaterialCategories.vue'

const props = defineProps<{ token: string; active?: boolean; canManage: boolean }>()
type TreeNode = StandardLibraryCategory & { children: TreeNode[]; disabled: boolean }
type SelectionTable = { clearSelection: () => void; toggleRowSelection: (row: PdmMaterial, selected: boolean) => void }
const categories = ref<StandardLibraryCategory[]>([]), result = ref<StockedMaterialPage | null>(null)
const loading = ref(false), refreshing = ref(false), error = ref(''), categoryId = ref(''), query = ref(''), brand = ref('')
const page = ref(1), pageSize = ref(50), selected = ref<StockedMaterialRow[]>([]), removing = ref(false)
const table = ref<{ clearSelection: () => void } | null>(null), categoryManagerOpen = ref(false)
const coverUrls = reactive<Record<string, string>>({}), preview = ref<{ materialId: string; initialId: string } | null>(null)
let loadVersion = 0
const rows = computed(() => result.value?.items ?? [])
const brands = computed(() => [...new Set(rows.value.map(item => item.material.brand).filter((value): value is string => !!value))].sort())
const tree = computed(() => {
  const nodes = new Map(categories.value.map(item => [item.id, { ...item, children: [], disabled: !item.isActive } as TreeNode]))
  const roots: TreeNode[] = []
  for (const node of nodes.values()) { const parent = node.parentId ? nodes.get(node.parentId) : null; if (parent) parent.children.push(node); else roots.push(node) }
  return roots
})
function message(reason: unknown, fallback: string) { return userErrorMessage(reason instanceof Error ? reason.message : fallback) }
function releaseCovers() { for (const url of Object.values(coverUrls)) if (url.startsWith('blob:')) URL.revokeObjectURL(url); for (const key of Object.keys(coverUrls)) delete coverUrls[key] }
async function load() {
  const version = ++loadVersion; loading.value = true; error.value = ''
  try {
    const [categoryRows, data] = await Promise.all([listStandardLibraryCategories(props.token),
      listStockedMaterials({ categoryId: categoryId.value, query: query.value, brand: brand.value, page: page.value, pageSize: pageSize.value }, props.token)])
    if (version !== loadVersion) return
    categories.value = categoryRows; result.value = data; selected.value = []; table.value?.clearSelection(); releaseCovers()
    await Promise.all(data.items.filter(row => row.coverImage).map(async row => {
      try { const url = await materialAttachmentObjectUrl(row.material.id, row.coverImage!.id, props.token, 120)
        if (version === loadVersion) coverUrls[row.material.id] = url; else if (url.startsWith('blob:')) URL.revokeObjectURL(url)
      } catch { /* 封面失败不阻塞库存数据。 */ }
    }))
  } catch (reason) { if (version === loadVersion) error.value = message(reason, '常备件列表加载失败，请重试。') }
  finally { if (version === loadVersion) loading.value = false }
}
function search() { page.value = 1; void load() }
function selectCategory(node?: StandardLibraryCategory) { categoryId.value = node?.id ?? ''; search() }
async function refresh(manual = false) {
  if (refreshing.value || manual && !result.value?.canSynchronize) return
  refreshing.value = true; error.value = ''
  try { await refreshStockedMaterials(props.token); await load(); if (result.value?.lastError) ElMessage.warning(result.value.lastError); else ElMessage.success('U9C常备件已同步，库存规划及现存量已回读') }
  catch (reason) { await load(); error.value = message(reason, '回读失败，已保留上次成功数据。') }
  finally { await refreshMaterialStockPlans(() => listMaterialStockPlans(props.token)); refreshing.value = false }
}
async function remove() {
  const selection = [...selected.value]
  try { await ElMessageBox.confirm(`将所选${selection.length}项从${categoryId.value ? '当前分类' : '全部常备件分类'}移除？U9C料品及库存规划参数保留；若安全库存量仍大于0，下次同步将再次收录。`, '移除常备件', { type: 'warning' }) } catch { return }
  removing.value = true; error.value = ''
  try {
    for (const row of selection) {
      // 父分类展示包含子分类，移除当前显示范围内的收录关系。
      const ids = new Set([categoryId.value]); for (let before = -1; before !== ids.size;) { before = ids.size; for (const node of categories.value) if (node.parentId && ids.has(node.parentId)) ids.add(node.id) }
      if (categoryId.value) for (const category of row.categories.filter(item => ids.has(item.id))) await removeStockedMaterial(row.material.id, category.id, props.token)
      else await removeStockedMaterial(row.material.id, undefined, props.token)
    }
    await load(); if (!rows.value.length && page.value > 1) { page.value = Math.max(1, Math.ceil((result.value?.total ?? 0) / pageSize.value)); await load() }
    ElMessage.success('已移除常备件收录关系')
  } catch (reason) { await load(); error.value = message(reason, '移除未完成，请刷新核对。') }
  finally { removing.value = false }
}

const addOpen = ref(false), adding = ref(false), eligibleQuery = ref(''), eligibleBrand = ref(''), eligible = ref<PdmMaterial[]>([])
const picked = ref<PdmMaterial[]>([]), targetIds = ref<string[]>([]), eligibleLoading = ref(false), addError = ref('')
const selectionTable = ref<SelectionTable | null>(null), addTree = ref<{ setCheckedKeys: (keys: string[]) => void } | null>(null)
let searchTimer: ReturnType<typeof setTimeout> | undefined, searchController: AbortController | undefined
let restoringSelection = false
const eligibleBrands = computed(() => [...new Set(eligible.value.map(item => item.brand).filter((value): value is string => !!value))].sort())
const eligibleRows = computed(() => eligible.value.filter(item => !eligibleBrand.value || item.brand === eligibleBrand.value))
async function openAdd() {
  eligibleQuery.value = ''; eligibleBrand.value = ''; eligible.value = []; picked.value = []; targetIds.value = categoryId.value ? [categoryId.value] : []; addError.value = ''; addOpen.value = true
  await nextTick(); selectionTable.value?.clearSelection(); addTree.value?.setCheckedKeys(targetIds.value)
}
function cancelSearch() { clearTimeout(searchTimer); searchController?.abort(); eligibleLoading.value = false }
function queueSearch() {
  cancelSearch(); eligible.value = []; eligibleBrand.value = ''; addError.value = ''
  const keyword = eligibleQuery.value.trim(); if (!addOpen.value || keyword.length < 4) return
  const controller = new AbortController(); searchController = controller; eligibleLoading.value = true
  searchTimer = setTimeout(async () => {
    try { const data = await listMaterials(props.token, keyword, false, 100, '', controller.signal)
      if (!controller.signal.aborted) { restoringSelection = true; eligible.value = data.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.u9SyncConfirmed)
        await nextTick(); for (const item of eligible.value) if (picked.value.some(row => row.id === item.id)) selectionTable.value?.toggleRowSelection(item, true)
        restoringSelection = false }
    } catch (reason) { if (!controller.signal.aborted) addError.value = message(reason, '料品搜索失败，请重试。') }
    finally { if (!controller.signal.aborted) eligibleLoading.value = false }
  }, 300)
}
function updatePicked(selection: PdmMaterial[]) {
  if (restoringSelection) return
  const visible = new Set(eligibleRows.value.map(row => row.id))
  picked.value = [...picked.value.filter(item => !visible.has(item.id)), ...selection].filter((item, index, all) => all.findIndex(row => row.id === item.id) === index)
}
function unpick(row: PdmMaterial) { picked.value = picked.value.filter(item => item.id !== row.id); const visible = eligible.value.find(item => item.id === row.id); if (visible) selectionTable.value?.toggleRowSelection(visible, false) }
async function add() {
  adding.value = true; addError.value = ''
  try { await addStockedMaterials(targetIds.value, picked.value.map(item => item.id), props.token); addOpen.value = false; page.value = 1; await load(); ElMessage.success('已加入常备草稿，可维护库存参数后保存生效') }
  catch (reason) { addError.value = message(reason, '加入常备件失败，请刷新核对。') }
  finally { adding.value = false }
}
watch(eligibleQuery, queueSearch)
watch(addOpen, value => { if (!value) cancelSearch() })

const editing = ref<StockedMaterialRow | null>(null), editorOpen = ref(false), saving = ref(false), editorError = ref('')
const maintaining = ref(false)
const form = reactive({ safetyStockQuantity: 0, reorderQuantity: 0 })
async function maintain() {
  const row = selected.value[0]; if (!row || maintaining.value || !props.canManage) return
  maintaining.value = true; error.value = ''
  try {
    const snapshot = await readStockedMaterialPlan(row.material.id, props.token)
    editing.value = { ...row, snapshot }; form.safetyStockQuantity = snapshot.plan?.safetyStockQuantity ?? 0; form.reorderQuantity = snapshot.plan?.reorderQuantity ?? 0
    await load(); await refreshMaterialStockPlans(() => listMaterialStockPlans(props.token))
    editorError.value = ''; editorOpen.value = true
  } catch (reason) { error.value = message(reason, '库存规划读取失败，请检查U9C连接后重新维护。') }
  finally { maintaining.value = false }
}
async function savePlan() {
  if (!editing.value || saving.value) return
  if (!Number.isFinite(form.safetyStockQuantity) || !Number.isFinite(form.reorderQuantity) || form.safetyStockQuantity < 0 || form.reorderQuantity < 0) {
    editorError.value = '请填写有效的安全库存量和再订货量，均须为非负数。'; return
  }
  saving.value = true; editorError.value = ''
  try {
    await maintainStockedMaterial(editing.value.material.id, { ...form, expectedSyncedAt: editing.value.snapshot.plan?.syncedAt ?? null }, props.token)
    editorOpen.value = false; ElMessage.success('库存参数已同步U9C并回查，安全库存量为0时保留草稿'); await load(); await refreshMaterialStockPlans(() => listMaterialStockPlans(props.token))
  } catch (reason) { editorError.value = message(reason, '保存未完成，请刷新回查U9C结果后重试。') }
  finally { saving.value = false }
}
watch(() => [props.token, props.active], () => { if (props.active !== false) void load() }, { immediate: true })
onBeforeUnmount(() => { loadVersion++; cancelSearch(); releaseCovers() })
</script>

<template>
  <section class="stocked-materials" aria-label="常备件">
    <aside class="stocked-tree pdm-panel"><strong class="category-title">分类筛选</strong><button type="button" class="all-categories" :class="{ 'is-active': !categoryId }" @click="selectCategory()">全部分类</button>
      <el-tree :data="tree" node-key="id" :current-node-key="categoryId || undefined" :props="{ label: 'name', children: 'children' }" highlight-current @node-click="selectCategory"><template #default="{ data }"><span class="category-label"><Folder :size="15" />{{ data.name }}</span></template></el-tree>
      <div v-if="canManage" class="category-actions"><el-button type="primary" size="small" @click="categoryManagerOpen = true">分类维护</el-button></div>
    </aside>
    <main class="stocked-content pdm-panel pdm-project-table-panel">
      <div class="stocked-toolbar"><el-button type="primary" :disabled="!canManage || refreshing" @click="openAdd">新增常备件</el-button><el-button :disabled="!canManage || selected.length !== 1 || refreshing || maintaining" :loading="maintaining" @click="maintain">维护</el-button><el-button type="danger" plain :disabled="!canManage || !selected.length || refreshing" :loading="removing" @click="remove">移除</el-button>
        <div class="stocked-filters"><el-input v-model="query" clearable placeholder="名称/料号/型号/品牌" @keyup.enter="search" /><el-select v-model="brand" filterable clearable placeholder="品牌"><el-option v-for="item in brands" :key="item" :value="item" :label="item" /></el-select><el-button :loading="loading" :disabled="refreshing" @click="load">刷新</el-button><el-button type="primary" @click="search">搜索</el-button><el-button type="primary" :loading="refreshing" :disabled="!result?.canSynchronize || loading" :title="result?.canSynchronize ? '从U9C同步全部常备件及库存数据' : '仅标准化助理可立即同步'" @click="refresh(true)">{{ refreshing ? '同步中…' : '立即同步' }}</el-button></div>
      </div>
      <p v-if="error || result?.lastError" class="stocked-error" role="alert">{{ error || result?.lastError }}</p>
      <div class="stocked-summary"><span>正式 {{ result?.formalCount ?? rows.filter(formalStockedMaterial).length }} 项 · 草稿 {{ result?.draftCount ?? rows.filter(row => !formalStockedMaterial(row)).length }} 项 · 仅常备管理 {{ result?.managementOnlyCount ?? rows.filter(row => row.managementOnly).length }} 项</span><span :class="{ 'low-stock-text': result?.lowStockCount }">低于安全库存 {{ result?.lowStockCount ?? 0 }} 项，已置顶高亮</span><span>上次完整回读：{{ stockTime(result?.lastSuccessAt) }}</span></div>
      <div class="stocked-legend"><span class="low-stock-text">红色：低库存且未请购</span><span class="requested-stock-text">橙色：低库存且已请购未到货（含尚未采购）</span><span>按近3个月的请购业务日期汇总；采购、到货数量仅统计到货少于采购的请购行。</span></div>
      <!-- 列宽由共享布局按可用宽度等比适配，表格不设置最小总宽度。 -->
      <el-table ref="table" v-loading="loading || refreshing" class="stocked-table" :data="rows" row-key="material.id" stripe height="100%" :row-class-name="({ row }: { row: StockedMaterialRow }) => row.snapshot.isLowStock ? `is-low-stock${row.snapshot.hasPendingRequisition ? ' is-requested' : ''}` : ''" @selection-change="selected = $event">
        <el-table-column type="selection" width="38" align="center" />
        <el-table-column label="状态" width="60" align="center"><template #default="{ row }"><el-tag size="small" :type="formalStockedMaterial(row) ? 'success' : 'info'">{{ formalStockedMaterial(row) ? '正式' : '草稿' }}</el-tag></template></el-table-column><el-table-column label="名称" width="145" show-overflow-tooltip><template #default="{ row }"><el-tag v-if="row.managementOnly" size="small" type="info">仅常备管理</el-tag> <span>{{ row.material.name }}</span></template></el-table-column>
        <el-table-column label="图片" width="40" align="center"><template #default="{ row }"><button v-if="coverUrls[row.material.id]" class="cover-button" type="button" aria-label="预览料品图片" @click="preview = { materialId: row.material.id, initialId: row.coverImage.id }"><img :src="coverUrls[row.material.id]" alt="料品封面" /></button><span v-else>—</span></template></el-table-column>
        <el-table-column label="料号" width="100" show-overflow-tooltip><template #default="{ row }"><span>{{ row.material.materialCode }}</span></template></el-table-column>
        <el-table-column prop="material.specification" label="型号" width="110" show-overflow-tooltip /><el-table-column prop="material.brand" label="品牌" width="75" show-overflow-tooltip />
        <el-table-column label="现存量" width="60" align="right"><template #default="{ row }"><span :class="{ 'low-stock-text': row.snapshot.isLowStock }">{{ row.snapshot.stockQuantity ?? '—' }}</span></template></el-table-column>
        <el-table-column label="已请购" width="60" align="center"><template #default="{ row }"><span :title="row.snapshot.procurementSyncedAt ? `请购采购回读：${stockTime(row.snapshot.procurementSyncedAt)}` : ''">{{ !formalStockedMaterial(row) ? '—' : row.snapshot.hasPendingRequisition == null ? '待回读' : row.snapshot.hasPendingRequisition ? '是' : '否' }}</span></template></el-table-column>
        <el-table-column label="请购日期" width="85" align="center" show-overflow-tooltip><template #default="{ row }">{{ formalStockedMaterial(row) ? row.snapshot.requisitionDate || '—' : '—' }}</template></el-table-column>
        <el-table-column label="采购数量" width="65" align="center"><template #default="{ row }">{{ formalStockedMaterial(row) && (row.snapshot.purchaseQuantity ?? 0) > 0 ? row.snapshot.purchaseQuantity : '—' }}</template></el-table-column>
        <el-table-column label="到货数量" width="65" align="center"><template #default="{ row }">{{ formalStockedMaterial(row) && (row.snapshot.purchaseQuantity ?? 0) > 0 ? row.snapshot.arrivedQuantity : '—' }}</template></el-table-column>
        <el-table-column label="预计到货日期" width="90" align="center" show-overflow-tooltip><template #default="{ row }">{{ formalStockedMaterial(row) ? row.snapshot.expectedArrivalDate || '—' : '—' }}</template></el-table-column>
        <el-table-column label="安全库存量" width="75" align="right"><template #default="{ row }">{{ row.snapshot.plan?.safetyStockQuantity ?? 0 }}</template></el-table-column>
        <el-table-column label="再订货量" width="75" align="right"><template #default="{ row }">{{ row.snapshot.plan?.reorderQuantity ?? 0 }}</template></el-table-column>
        <el-table-column label="单位" width="45" align="center"><template #default="{ row }">{{ row.snapshot.plan?.unitName || row.snapshot.plan?.unitCode || row.material.unitCode }}</template></el-table-column>
        <el-table-column label="库存提醒" width="95" align="center"><template #default="{ row }"><strong v-if="row.snapshot.isLowStock" class="low-stock-text">{{ row.snapshot.hasPendingRequisition ? '低库存·已请购' : '低于安全库存' }}</strong><span v-else>{{ row.snapshot.stockQuantity === null ? '待回读' : '—' }}</span></template></el-table-column>
        <el-table-column label="U9C同步" width="70" align="center"><template #default="{ row }"><span :title="row.snapshot.lastError || ''" :class="row.snapshot.lastError ? 'stocked-error' : 'sync-success'">{{ stockSyncLabel(row) }}</span></template></el-table-column>
        <el-table-column label="上次同步" width="150" align="center" show-overflow-tooltip><template #default="{ row }">{{ stockTime(row.snapshot.inventorySyncedAt || row.snapshot.plan?.syncedAt) }}</template></el-table-column>
      </el-table>
      <footer><span>U9C组织：{{ result?.scope.organizationCode || '—' }}</span><el-pagination v-model:current-page="page" v-model:page-size="pageSize" :total="result?.total ?? 0" :page-sizes="[20, 50, 100]" layout="total, sizes, prev, pager, next" small @current-change="load" @size-change="search" /></footer>
    </main>

    <el-dialog v-model="addOpen" title="从料品加入常备件" width="min(1380px, calc(100vw - 32px))" :close-on-click-modal="!adding" :show-close="!adding">
      <p v-if="addError" role="alert" class="stocked-error">{{ addError }}</p>
      <div class="stocked-add-layout">
        <section><header><strong>可选料品</strong><p>满4个字符后搜索，最多100项；仅显示已批准且U9C已确认的正式料品。</p><div class="eligible-filters"><el-input v-model="eligibleQuery" clearable placeholder="搜索料号/名称/型号/品牌" @keyup.enter="queueSearch" /><el-select v-model="eligibleBrand" clearable filterable placeholder="品牌"><el-option v-for="item in eligibleBrands" :key="item" :label="item" :value="item" /></el-select></div></header>
          <el-table ref="selectionTable" v-loading="eligibleLoading" :data="eligibleRows" row-key="id" height="100%" :empty-text="eligibleQuery.trim().length < 4 ? '请输入至少4个字符搜索' : '未找到符合条件的正式料品'" @selection-change="updatePicked"><el-table-column type="selection" width="38" /><el-table-column prop="materialCode" label="料号" width="115" show-overflow-tooltip /><el-table-column prop="name" label="名称" min-width="115" show-overflow-tooltip /><el-table-column prop="specification" label="型号" min-width="130" show-overflow-tooltip /><el-table-column prop="brand" label="品牌" width="75" show-overflow-tooltip /></el-table>
        </section>
        <section><header><strong>已选料品（{{ picked.length }}）</strong></header><el-table :data="picked" height="100%" empty-text="请从左侧勾选料品"><el-table-column prop="materialCode" label="料号" width="115" show-overflow-tooltip /><el-table-column prop="name" label="名称" min-width="115" show-overflow-tooltip /><el-table-column prop="specification" label="型号" min-width="130" show-overflow-tooltip /><el-table-column label="操作" width="55"><template #default="{ row }"><el-button link type="danger" @click="unpick(row)">移除</el-button></template></el-table-column></el-table></section>
        <section><header><strong>目标标准分类（{{ targetIds.length }}）</strong><p>所选分类应用到全部已选料品；先加入常备草稿，正式生效后同步收录到标准库。</p></header><el-tree ref="addTree" :data="tree" node-key="id" show-checkbox check-strictly default-expand-all :props="{ label: 'name', children: 'children', disabled: 'disabled' }" @check="(_node: unknown, checked: { checkedKeys: string[] }) => targetIds = checked.checkedKeys" /></section>
      </div>
      <template #footer><el-button :disabled="adding" @click="addOpen = false">取消</el-button><el-button type="primary" :loading="adding" :disabled="!picked.length || !targetIds.length || picked.length > 100" @click="add">批量加入指定分类</el-button></template>
    </el-dialog>

    <el-dialog v-model="editorOpen" title="维护常备件库存规划" width="min(600px, calc(100vw - 32px))" :close-on-click-modal="!saving" :show-close="!saving">
      <p v-if="editorError" class="stocked-error" role="alert">{{ editorError }}</p>
      <el-form v-if="editing" label-width="100px" class="stocked-editor"><el-form-item label="料号"><div class="stocked-editor__readonly">{{ editing.material.materialCode }}</div></el-form-item><el-form-item label="名称"><div class="stocked-editor__readonly">{{ editing.material.name }}</div></el-form-item><el-form-item label="型号"><div class="stocked-editor__readonly">{{ editing.material.specification || '—' }}</div></el-form-item><el-form-item label="品牌"><div class="stocked-editor__readonly">{{ editing.material.brand || '—' }}</div></el-form-item><el-form-item label="U9C组织"><div class="stocked-editor__readonly">{{ result?.scope.organizationCode || '—' }}</div></el-form-item><el-form-item label="库存单位"><div class="stocked-editor__readonly">{{ editing.snapshot.plan?.unitName || editing.snapshot.plan?.unitCode || editing.material.unitCode }}</div></el-form-item><el-form-item label="安全库存量" required><el-input-number v-model="form.safetyStockQuantity" aria-label="安全库存量" :min="0" :max="999999999999" controls-position="right" /></el-form-item><el-form-item label="再订货量" required><el-input-number v-model="form.reorderQuantity" aria-label="再订货量" :min="0" :max="999999999999" controls-position="right" /></el-form-item></el-form>
      <p class="field-help">保存将这两项参数同步到U9C，并回查核对。安全库存量设为0后，该料品不再显示全局常备件标识。</p>
      <template #footer><el-button :disabled="saving" @click="editorOpen = false">取消</el-button><el-button type="primary" :loading="saving" @click="savePlan">保存并同步U9C</el-button></template>
    </el-dialog>
    <StockedMaterialCategories v-model="categoryManagerOpen" :token="token" @changed="load" />
    <MaterialImagePreview v-if="preview" :model-value="true" :material-id="preview.materialId" :initial-id="preview.initialId" :token="token" @update:model-value="value => { if (!value) preview = null }" />
  </section>
</template>

<style scoped>
.stocked-materials{display:grid;grid-template-columns:200px minmax(0,1fr);gap:6px;height:100%;min-height:0;font-size:var(--pdm-font-size-body)}.stocked-tree{display:flex;flex-direction:column;padding:10px 6px;overflow:auto;min-height:0}.category-title{padding:5px 10px 12px;border-bottom:1px solid var(--pdm-border-soft)}.all-categories{border:0;border-radius:5px;background:transparent;text-align:left;padding:10px;margin:6px 0;color:var(--pdm-text);font:inherit;cursor:pointer}.all-categories.is-active{color:var(--pdm-primary);background:var(--pdm-primary-soft)}.category-label{display:flex;align-items:center;gap:6px}.category-actions{margin-top:auto;padding:16px 8px 4px}.stocked-content{display:flex;min-width:0;min-height:0;flex-direction:column;padding:12px;overflow:hidden}.stocked-toolbar{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-bottom:10px}.stocked-toolbar :deep(.el-button+.el-button){margin-left:0}.stocked-filters{display:flex;gap:8px;flex:1;min-width:0;flex-wrap:wrap}.stocked-filters .el-input{flex:1;min-width:120px}.stocked-filters .el-select{width:150px;flex:0 0 150px}.stocked-summary{display:flex;justify-content:space-between;gap:12px;font-size:12px;color:var(--pdm-muted);margin:0 0 10px;flex-wrap:wrap}.stocked-table{flex:1;min-height:0;width:100%;max-width:100%;min-width:0}.stocked-table :deep(.el-table__cell .cell){padding-inline:4px;box-sizing:border-box;min-width:0!important;width:100%!important;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.stocked-table :deep(.el-table-column--selection .cell){padding-inline:0}.stocked-table :deep(.el-table__cell){font-size:11px;height:30px;padding:0;text-align:center}.stocked-table :deep(.el-table__body tr.is-low-stock){--el-table-tr-bg-color:var(--el-color-danger-light-9);--el-table-row-hover-bg-color:var(--el-color-danger-light-8)}.stocked-table :deep(tr.is-low-stock td.el-table__cell){background:var(--el-color-danger-light-9)}.stocked-legend{display:flex;flex-wrap:wrap;gap:6px 16px;margin:0 0 8px;color:var(--pdm-muted);font-size:11px;line-height:1.5}.requested-stock-text{color:var(--el-color-warning-dark-2)}.stocked-table :deep(tr.is-low-stock.is-requested td.el-table__cell){background:var(--el-color-warning-light-9)}.stocked-table :deep(tr.is-requested .low-stock-text){color:var(--el-color-warning-dark-2)}.low-stock-text{color:var(--el-color-danger);font-weight:600}.stocked-error{color:var(--el-color-danger);font-size:12px;line-height:1.6}.sync-success{color:var(--el-color-success)}.cover-button{width:26px;height:26px;border:0;padding:0;background:transparent;cursor:pointer}.cover-button img{width:26px;height:26px;object-fit:contain}.stocked-content footer{display:flex;align-items:center;justify-content:space-between;border-top:1px solid var(--pdm-border-soft);padding-top:10px;margin-top:10px;font-size:12px;color:var(--pdm-muted);gap:8px;flex-wrap:wrap}.stocked-add-layout{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 250px;gap:12px;height:min(600px,65vh)}.stocked-add-layout>section{display:flex;flex-direction:column;min-width:0;min-height:0;padding:12px;border:1px solid var(--pdm-border-soft);border-radius:6px}.stocked-add-layout header{min-height:100px}.stocked-add-layout p,.field-help{font-size:12px;line-height:1.6;color:var(--pdm-muted)}.eligible-filters{display:flex;gap:6px}.eligible-filters .el-select{flex:0 0 100px}.stocked-add-layout .el-table,.stocked-add-layout .el-tree{flex:1;min-height:0;overflow:auto}.stocked-editor .el-input-number{width:100%}.stocked-editor__readonly{box-sizing:border-box;width:100%;min-height:32px;padding:0 11px;border:1px solid var(--pdm-border-soft);border-radius:var(--el-border-radius-base);background:var(--el-fill-color-light);color:var(--pdm-muted);line-height:30px;overflow-wrap:anywhere}@media(max-width:1520px){.stocked-filters{flex-basis:100%}}@media(max-width:800px){.stocked-materials{grid-template-columns:150px minmax(0,1fr)}.stocked-filters{min-width:0;flex-wrap:wrap}.stocked-filters .el-input{flex-basis:100%}.stocked-add-layout{display:flex;flex-direction:column;overflow:auto}.stocked-add-layout>section{min-height:320px;flex-shrink:0}}@media(max-width:600px){.stocked-materials{grid-template-columns:1fr}.stocked-tree{max-height:180px}.stocked-content{min-height:500px}}
</style>
