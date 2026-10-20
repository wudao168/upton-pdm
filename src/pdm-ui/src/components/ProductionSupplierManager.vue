<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readProductionSuppliers, saveProductionSupplier, saveProductionSupplierCategory } from '../api'
import { productionRequestId } from '../productionTracking'
import type { ProductionSupplier, ProductionSupplierCatalog, ProductionSupplierCategory, SaveProductionSupplier } from '../productionSuppliers'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ token: string }>()
const catalog = ref<ProductionSupplierCatalog>(), pending = ref(false), query = ref(''), categoryFilter = ref(''), includeInactive = ref(false)
const categoryForm = ref({ id: '', name: '', isActive: true, expectedRowVersion: 0 })
const categoryManagerOpen = ref(false), categoryFormOpen = ref(false)
const form = ref<(SaveProductionSupplier & { id: string }) | null>(null)
const rows = computed(() => catalog.value?.suppliers.filter(item => (includeInactive.value || item.isActive)
  && (!categoryFilter.value || item.categoryIds.includes(categoryFilter.value))
  && `${item.code} ${item.name} ${item.fullName} ${item.contact}`.toLowerCase().includes(query.value.trim().toLowerCase()))
  .sort((a, b) => a.code.localeCompare(b.code, 'zh-CN')) ?? [])
const categoryNames = (supplier: ProductionSupplier) => supplier.categoryIds.map(id => catalog.value?.categories.find(item => item.id === id)?.name || '').filter(Boolean).join('、')
let generation = 0
onBeforeUnmount(() => { generation++ })
function failure(cause: unknown) { ElMessage.error(cause instanceof Error ? cause.message : '供应商台账操作失败，请重试。') }
async function load() {
  const current = ++generation
  pending.value = true
  try { const value = await readProductionSuppliers(props.token); if (current === generation) catalog.value = value }
  catch (cause) { if (current === generation) failure(cause) }
  finally { if (current === generation) pending.value = false }
}
watch(() => props.token, () => { if (props.token) void load() }, { immediate: true })
function openCategoryManager() {
  categoryFormOpen.value = false
  categoryManagerOpen.value = true
}
function editCategory(category?: ProductionSupplierCategory) {
  if (pending.value || !catalog.value) return
  categoryForm.value = { id: category?.id ?? '', name: category?.name ?? '', isActive: category?.isActive ?? true, expectedRowVersion: catalog.value?.rowVersion ?? 0 }
  categoryFormOpen.value = true
}
async function saveCategory() {
  if (pending.value || !catalog.value) return
  if (!categoryForm.value.name.trim()) { ElMessage.warning('请填写供应商分类名称。'); return }
  if (!categoryForm.value.id) { categoryForm.value.id = productionRequestId(); categoryForm.value.expectedRowVersion = catalog.value.rowVersion }
  pending.value = true
  try {
    catalog.value = await saveProductionSupplierCategory(categoryForm.value.id, categoryForm.value, props.token)
    categoryFormOpen.value = false; ElMessage.success('供应商分类已保存')
  } catch (cause) { failure(cause) }
  finally { pending.value = false }
}
function edit(supplier?: ProductionSupplier) {
  if (!catalog.value) return
  form.value = supplier ? { ...supplier, categoryIds: [...supplier.categoryIds], expectedRowVersion: catalog.value.rowVersion }
    : { id: productionRequestId(), code: '', name: '', fullName: '', address: '', phone: '', contact: '', contactPhone: '', remarks: '', categoryIds: [], isActive: true, expectedRowVersion: catalog.value.rowVersion }
}
async function save() {
  if (!form.value || pending.value) return
  if (!form.value.code.trim() || !form.value.name.trim() || !form.value.fullName.trim() || !form.value.categoryIds.length) {
    ElMessage.warning('请填写供应商编码、简称、全称，并选择至少一个分类。'); return
  }
  pending.value = true
  try {
    catalog.value = await saveProductionSupplier(form.value.id, form.value, props.token)
    form.value = null; ElMessage.success('供应商已保存')
  } catch (cause) { failure(cause) }
  finally { pending.value = false }
}
async function toggleSupplier(supplier: ProductionSupplier) {
  if (!catalog.value || pending.value) return
  pending.value = true
  try {
    catalog.value = await saveProductionSupplier(supplier.id, { ...supplier, isActive: !supplier.isActive, expectedRowVersion: catalog.value.rowVersion }, props.token)
    ElMessage.success(supplier.isActive ? '供应商已停用，历史批次记录保留' : '供应商已启用')
  } catch (cause) { failure(cause) }
  finally { pending.value = false }
}
function close() { if (!pending.value) form.value = null }
</script>

<template>
  <section class="production-suppliers" aria-label="供应商库">
    <header><h2>供应商库</h2><span>按分类维护供应商台账，外协分配从启用供应商中选择。</span><button type="button" class="pdm-secondary-action" :disabled="pending || !catalog" @click="edit()">新建供应商</button><button type="button" class="pdm-secondary-action" :disabled="pending" @click="load">刷新</button></header>
    <div class="production-suppliers__layout">
      <aside class="pdm-panel production-suppliers__categories">
        <h3>供应商分类</h3>
        <button type="button" class="production-suppliers__category-filter" :class="{ 'is-active': !categoryFilter }" @click="categoryFilter = ''">全部供应商</button>
        <button v-for="category in catalog?.categories ?? []" :key="category.id" type="button" class="production-suppliers__category-filter" :class="{ 'is-active': categoryFilter === category.id }" @click="categoryFilter = category.id">{{ category.name }}<small v-if="!category.isActive">（停用）</small></button>
        <div class="production-suppliers__category-actions"><el-button type="primary" size="small" :disabled="pending || !catalog" @click="openCategoryManager">分类维护</el-button></div>
        <p v-if="catalog && !catalog.categories.length">请先维护分类，再登记供应商。</p>
      </aside>
      <main class="pdm-panel production-suppliers__main">
        <div class="production-suppliers__filters"><input v-model="query" type="search" aria-label="搜索供应商" placeholder="搜索编码、简称、全称或联系人"><select v-model="categoryFilter" aria-label="按供应商分类筛选"><option value="">全部分类</option><option v-for="category in catalog?.categories ?? []" :key="category.id" :value="category.id">{{ category.name }}</option></select><label class="production-suppliers__check"><input v-model="includeInactive" type="checkbox">包含停用</label><span>共 {{ rows.length }} 家</span></div>
        <div class="production-suppliers__table"><table class="pdm-fit-columns"><thead><tr><th>编码</th><th>简称</th><th>全称</th><th>分类</th><th>联系人</th><th>联系电话</th><th>状态</th><th>操作</th></tr></thead><tbody><tr v-for="supplier in rows" :key="supplier.id"><td>{{ supplier.code }}</td><td :title="supplier.name">{{ supplier.name }}</td><td :title="supplier.fullName">{{ supplier.fullName }}</td><td :title="categoryNames(supplier)">{{ categoryNames(supplier) }}</td><td>{{ supplier.contact || '—' }}</td><td>{{ supplier.contactPhone || supplier.phone || '—' }}</td><td>{{ supplier.isActive ? '启用' : '停用' }}</td><td><button type="button" :disabled="pending" @click="edit(supplier)">编辑</button><button type="button" :disabled="pending" @click="toggleSupplier(supplier)">{{ supplier.isActive ? '停用' : '启用' }}</button></td></tr></tbody></table><p v-if="!pending && !rows.length">暂无符合条件的供应商。</p></div>
      </main>
    </div>
    <el-dialog v-model="categoryManagerOpen" title="供应商分类维护" width="min(860px, calc(100vw - 32px))" :close-on-click-modal="!pending" :close-on-press-escape="!pending" :show-close="!pending">
      <div class="supplier-category-manager">
        <section class="supplier-category-manager__list">
          <header class="supplier-category-manager__header"><strong>供应商分类</strong><el-button type="primary" size="small" :disabled="pending" @click="editCategory()">新增分类</el-button></header>
          <div class="supplier-category-manager__rows">
            <div v-if="!catalog?.categories.length" class="supplier-category-manager__empty">暂无供应商分类</div>
            <div v-for="category in catalog?.categories ?? []" :key="category.id" class="production-suppliers__category"><div class="supplier-category-manager__name"><strong>{{ category.name }}</strong><el-tag size="small" :type="category.isActive ? 'success' : 'info'">{{ category.isActive ? '启用' : '停用' }}</el-tag></div><el-button link :disabled="pending" @click="editCategory(category)">编辑</el-button></div>
          </div>
        </section>
        <section class="supplier-category-manager__editor">
          <template v-if="categoryFormOpen">
            <h3>{{ categoryForm.id ? '编辑分类' : '新增分类' }}</h3>
            <el-form label-position="top" @submit.prevent="saveCategory">
              <el-form-item label="名称" required><el-input v-model="categoryForm.name" aria-label="分类名称" maxlength="80" :disabled="pending" /></el-form-item>
              <el-form-item><el-switch v-model="categoryForm.isActive" aria-label="启用分类" active-text="启用" inactive-text="停用" :disabled="pending" /></el-form-item>
              <div class="supplier-category-manager__editor-actions"><el-button :disabled="pending" @click="categoryFormOpen = false">取消</el-button><el-button type="primary" native-type="submit" :loading="pending">保存</el-button></div>
            </el-form>
          </template>
          <div v-else class="supplier-category-manager__placeholder"><strong>分类编辑</strong><p>在左侧新增分类或选择“编辑”。一个供应商可同时选择多个分类。</p></div>
        </section>
      </div>
      <template #footer><el-button :disabled="pending" @click="categoryManagerOpen = false">关闭</el-button></template>
    </el-dialog>
    <div v-if="form" class="production-suppliers__backdrop" @click.self="close"><section class="pdm-panel production-suppliers__editor" role="dialog" aria-modal="true" aria-label="维护供应商"><header><h2>维护供应商</h2><button type="button" aria-label="关闭供应商表单" :disabled="pending" @click="close">×</button></header><form novalidate @submit.prevent="save"><fieldset :disabled="pending">
      <label>供应商编码<input v-model="form.code" maxlength="50"></label><label>公司简称<input v-model="form.name" maxlength="100"></label><label>公司全称<input v-model="form.fullName" maxlength="200"></label><label>公司地址<input v-model="form.address" maxlength="400"></label><label>公司电话<input v-model="form.phone" maxlength="60"></label><label>联系人<input v-model="form.contact" maxlength="100"></label><label>联系人电话<input v-model="form.contactPhone" maxlength="60"></label><label>备注<input v-model="form.remarks" maxlength="1000"></label>
      <div class="production-suppliers__classification"><span>分类（可多选）</span><label v-for="category in catalog?.categories ?? []" :key="category.id" class="production-suppliers__check"><input v-model="form.categoryIds" type="checkbox" :value="category.id" :disabled="!category.isActive && !form.categoryIds.includes(category.id)">{{ category.name }}{{ category.isActive ? '' : '（停用）' }}</label><p v-if="!catalog?.categories.length">请关闭表单，先维护供应商分类。</p></div><label class="production-suppliers__check"><input v-model="form.isActive" type="checkbox">启用供应商</label>
    </fieldset><footer><button type="button" class="pdm-secondary-action" :disabled="pending" @click="close">取消</button><button type="submit" class="pdm-primary-action" :disabled="pending">保存供应商</button></footer></form></section></div>
  </section>
</template>

<style scoped>
.production-suppliers{display:flex;flex-direction:column;flex:1;min-height:0;gap:10px}.production-suppliers header{display:flex;align-items:center;flex-wrap:wrap;gap:10px}.production-suppliers h2{margin:0;font-size:16px}.production-suppliers header>span{color:var(--pdm-muted);font-size:12px;flex:1}.production-suppliers h3{margin:0 0 10px;font-size:14px}.production-suppliers__layout{display:grid;grid-template-columns:230px minmax(0,1fr);gap:10px;flex:1;min-height:0}.production-suppliers__categories{padding:12px;overflow:auto;min-height:0}.production-suppliers label{display:grid;gap:5px;font-size:12px}.production-suppliers input:not([type=checkbox]),.production-suppliers select{height:30px;box-sizing:border-box;min-width:0;max-width:100%;padding:0 8px;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:inherit;font:inherit}.production-suppliers__check{display:flex!important;align-items:center;gap:5px}.production-suppliers__check input{margin:0}.production-suppliers__category{display:flex;align-items:center;justify-content:space-between;gap:5px;padding:8px 4px;border-bottom:1px solid var(--pdm-border)}.production-suppliers td button{border:0;background:none;color:var(--pdm-blue);cursor:pointer}.production-suppliers p{font-size:12px;color:var(--pdm-muted)}
.production-suppliers__category-filter{display:block;width:100%;padding:8px;border:0;border-radius:5px;background:transparent;color:inherit;font:inherit;text-align:left;overflow-wrap:anywhere;cursor:pointer}.production-suppliers__category-filter:hover,.production-suppliers__category-filter.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.production-suppliers__category-filter small{color:var(--pdm-muted)}.production-suppliers__category-actions{margin-top:10px}
.supplier-category-manager{display:grid;grid-template-columns:minmax(0,1.45fr) minmax(270px,.85fr);gap:14px;font-size:12px}.supplier-category-manager :deep(.el-button),.supplier-category-manager :deep(.el-tag),.supplier-category-manager :deep(.el-form-item__label),.supplier-category-manager :deep(.el-input__inner),.supplier-category-manager :deep(.el-switch__label){font-size:12px}.supplier-category-manager__list,.supplier-category-manager__editor{min-width:0;padding:12px;border:1px solid var(--pdm-border-soft);border-radius:8px}.supplier-category-manager__header{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:10px}.supplier-category-manager__rows{max-height:500px;overflow:auto;border-top:1px solid var(--pdm-border)}.supplier-category-manager__name{display:flex;min-width:0;align-items:center;gap:6px}.supplier-category-manager__name strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.supplier-category-manager__empty{padding:40px 12px;color:var(--pdm-muted);text-align:center}.supplier-category-manager__editor{background:var(--pdm-table-header)}.supplier-category-manager__editor h3{margin:0 0 14px;font-size:14px}.supplier-category-manager__editor-actions{display:flex;justify-content:flex-end;gap:8px}.supplier-category-manager__placeholder{padding:34px 10px;text-align:center}.supplier-category-manager__placeholder p{color:var(--pdm-muted);line-height:1.4}
.production-suppliers__main{display:flex;flex-direction:column;min-width:0;min-height:0;padding:12px}.production-suppliers__filters{display:flex;align-items:center;flex-wrap:wrap;gap:10px;margin-bottom:10px}.production-suppliers__filters>input{width:260px}.production-suppliers__filters>span{margin-left:auto;font-size:12px;color:var(--pdm-muted)}.production-suppliers__table{overflow:auto;min-height:0;flex:1}.production-suppliers table{width:100%;border-collapse:collapse;font-size:12px}.production-suppliers th{position:sticky;top:0;background:var(--pdm-table-header)}.production-suppliers th,.production-suppliers td{padding:8px 6px;border-bottom:1px solid var(--pdm-border);text-align:center;white-space:nowrap}
.production-suppliers__backdrop{position:fixed;inset:0;z-index:2600;display:grid;place-items:center;background:var(--pdm-overlay);padding:16px}.production-suppliers__editor{width:min(760px,100%);max-height:calc(100vh - 32px);overflow:auto;padding:18px;box-sizing:border-box}.production-suppliers__editor header{justify-content:space-between;margin-bottom:12px}.production-suppliers__editor header button{border:0;background:none;color:inherit;font-size:24px;cursor:pointer}.production-suppliers__editor fieldset{border:0;margin:0;padding:0;display:grid;grid-template-columns:1fr 1fr;gap:12px}.production-suppliers__classification{grid-column:1/-1;display:flex;flex-wrap:wrap;align-items:center;gap:12px;font-size:12px}.production-suppliers__editor footer{display:flex;justify-content:flex-end;gap:10px;margin-top:18px}
@media(max-width:760px){.production-suppliers__layout{grid-template-columns:1fr;grid-template-rows:auto minmax(0,1fr)}.production-suppliers__categories{max-height:230px}.production-suppliers__editor fieldset,.supplier-category-manager{grid-template-columns:1fr}}

/* 制造中心容器间距统一为 5px。 */
.production-suppliers,
.production-suppliers__layout { gap: var(--pdm-container-gap); }
.production-suppliers__categories,
.production-suppliers__main { padding: var(--pdm-container-gap); }
.production-suppliers__filters { gap: var(--pdm-container-gap); margin-bottom: var(--pdm-container-gap); }
</style>
