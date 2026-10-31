<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { computed, ref, watch } from 'vue'
import { listMaterials, listMaterialRelationCategories } from '../api'
import type { MaterialRelationTemplate, PdmMaterial, StandardLibraryCategory } from '../types'
import MaterialRelationEditor from './MaterialRelationEditor.vue'
import MaterialRelationDetails from './MaterialRelationDetails.vue'
import StandardCategoryFilter from './StandardCategoryFilter.vue'
import { matchesStandardCategory } from '../standardLibraryCategories'

const props = defineProps<{
  token: string
  relations: MaterialRelationTemplate[]
  canManage: boolean
  canPublish: boolean
}>()
const emit = defineEmits<{ refresh: [] }>()

const query = ref('')
const categories = ref<StandardLibraryCategory[]>([])
const categoryFilter = ref('')
const expandedIds = ref<string[]>([])
const brandFilter = ref('')
const editorOpen = ref(false)
const selectedRelation = ref<MaterialRelationTemplate | null>(null)
const selectedMainMaterial = ref<PdmMaterial | null>(null)
const openingMaterialId = ref('')
const directoryMaterials = ref<PdmMaterial[]>([])
const directoryLoading = ref(false)
const createDialogOpen = ref(false)
const createMaterialId = ref('')
const createCandidates = ref<PdmMaterial[]>([])
const createLoading = ref(false)

const materialById = computed(() => new Map(directoryMaterials.value.map(item => [item.id, item])))
const brandOptions = computed(() => [...new Set(directoryMaterials.value.map(item => item.brand?.trim()).filter((value): value is string => Boolean(value)))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
const configuredMaterialIds = computed(() => new Set(props.relations.map(item => item.mainMaterialId)))

const filteredRelations = computed(() => {
  const value = query.value.trim().toLocaleLowerCase()
  return props.relations.filter(item => {
    if (!matchesStandardCategory(categories.value, categoryFilter.value, (item.standardLibraryCategories ?? []).map(category => category.id))) return false
    const material = materialById.value.get(item.mainMaterialId)
    if (brandFilter.value && material?.brand !== brandFilter.value) return false
    if (!value) return true
    return [
      item.mainMaterialCode,
      item.mainMaterialName,
      item.name,
      ...(item.standardLibraryCategories ?? []).map(category => category.name),
      material?.specification,
      material?.brand,
      material?.categoryCode,
      material?.u9CategoryCode,
    ].some(field => field?.toLocaleLowerCase().includes(value))
  })
})

watch(() => props.relations.map(item => `${item.id}:${item.updatedAt}`).join('|'), () => { void loadDirectoryMaterials() }, { immediate: true })

async function loadDirectoryMaterials() {
  const relations = [...props.relations]
  directoryLoading.value = true
  try {
    const [catalog, batches] = await Promise.all([listMaterialRelationCategories(props.token), Promise.all(relations.map(item => listMaterials(props.token, item.mainMaterialCode, true, 20)))])
    categories.value = catalog
    directoryMaterials.value = [...new Map(batches.flat().filter(item => relations.some(relation => relation.mainMaterialId === item.id)).map(item => [item.id, item])).values()]
    if (brandFilter.value && !brandOptions.value.includes(brandFilter.value)) brandFilter.value = ''
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '关联配置料品信息加载失败')
  } finally {
    directoryLoading.value = false
  }
}

function directoryMaterial(relation: MaterialRelationTemplate) {
  return materialById.value.get(relation.mainMaterialId)
}

function visibleRevision(relation: MaterialRelationTemplate) {
  return ((props.canManage || props.canPublish) ? relation.draftRevision : null) ?? relation.publishedRevision
}

function toggleExpansion(relation: MaterialRelationTemplate) {
  expandedIds.value = expandedIds.value.includes(relation.id) ? expandedIds.value.filter(id => id !== relation.id) : [...expandedIds.value, relation.id]
}

function relationStatus(relation: MaterialRelationTemplate) {
  if (relation.draftRevision && relation.publishedRevision) return { label: '待生效修改', type: 'warning' as const, version: relation.draftRevision.version }
  if (relation.draftRevision) return { label: '待发布生效', type: 'warning' as const, version: relation.draftRevision.version }
  return { label: '已生效', type: 'success' as const, version: relation.publishedRevision?.version }
}

function optionCount(relation: MaterialRelationTemplate) {
  return visibleRevision(relation)?.groups.reduce((total, group) => total + group.options.length, 0) ?? 0
}

function formatDate(value: string) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

async function openEditor(relation: MaterialRelationTemplate) {
  openingMaterialId.value = relation.mainMaterialId
  try {
    const cached = directoryMaterial(relation)
    const material = cached ?? (await listMaterials(props.token, relation.mainMaterialCode, true, 20)).find(item => item.id === relation.mainMaterialId)
    if (!material) throw new Error(`未找到料品 ${relation.mainMaterialCode}，请刷新后重试`)
    selectedRelation.value = relation
    selectedMainMaterial.value = material
    editorOpen.value = true
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品明细加载失败')
  } finally {
    openingMaterialId.value = ''
  }
}

async function searchCreateCandidates(query = '') {
  createLoading.value = true
  try {
    const rows = await listMaterials(props.token, query, false, 100)
    createCandidates.value = rows.filter(item => item.approvalStatus === 'Approved' && !item.isArchived && !configuredMaterialIds.value.has(item.id))
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '可配置料品加载失败')
  } finally {
    createLoading.value = false
  }
}

function openCreate() {
  createMaterialId.value = ''
  createDialogOpen.value = true
  void searchCreateCandidates()
}

function startNewConfiguration() {
  const material = createCandidates.value.find(item => item.id === createMaterialId.value)
  if (!material) return ElMessage.warning('请选择已批准且尚未配置的料品')
  selectedRelation.value = null
  selectedMainMaterial.value = material
  createDialogOpen.value = false
  editorOpen.value = true
}

function handleChanged() {
  emit('refresh')
}

function refresh() {
  emit('refresh')
  void loadDirectoryMaterials()
}
</script>

<template>
  <section class="material-relation-directory pdm-project-list-layout" aria-label="关联配置汇总">
    <StandardCategoryFilter v-model="categoryFilter" :categories="categories" />
    <div class="material-relation-directory__main pdm-panel pdm-project-table-panel">
    <div class="material-relation-directory__toolbar">
      <el-button :loading="directoryLoading" @click="refresh">刷新</el-button>
      <el-select v-model="brandFilter" class="material-relation-directory__brand" clearable filterable placeholder="筛选品牌">
        <el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" />
      </el-select>
      <el-input v-model="query" clearable placeholder="搜索编码、名称、规格、品牌、分类或配置名称" />
      <el-button v-if="canManage" type="primary" @click="openCreate">新增配置</el-button>
      <span>共 {{ filteredRelations.length }} 个已配置物料</span>
    </div>
    <el-table v-loading="directoryLoading" :data="filteredRelations" row-key="id" :expand-row-keys="expandedIds" @expand-change="(_row: MaterialRelationTemplate, expanded: MaterialRelationTemplate[]) => expandedIds = expanded.map(item => item.id)" height="100%" stripe empty-text="暂无已配置关联物料">
      <el-table-column type="expand" width="32"><template #default="{ row }"><div :id="`relation-contents-${row.id}`"><MaterialRelationDetails :relation="row" :show-draft="canManage || canPublish" /></div></template></el-table-column>
      <el-table-column prop="mainMaterialCode" label="物料编码" min-width="150" show-overflow-tooltip><template #default="{ row }"><button type="button" class="pdm-text-action" v-stocked-material="row.mainMaterialCode" :aria-expanded="expandedIds.includes(row.id)" :aria-controls="`relation-contents-${row.id}`" :aria-label="`${expandedIds.includes(row.id) ? '收起' : '展开'}关联配置 ${row.mainMaterialCode} 的配件明细`" @click.stop="toggleExpansion(row)">{{ row.mainMaterialCode }}</button></template></el-table-column>
      <el-table-column prop="mainMaterialName" label="物料名称" min-width="180" show-overflow-tooltip><template #default="{ row }"><span v-stocked-material="row.mainMaterialCode">{{ row.mainMaterialName }}</span></template></el-table-column>
      <el-table-column label="规格" min-width="190" show-overflow-tooltip><template #default="{ row }">{{ directoryMaterial(row)?.specification || '—' }}</template></el-table-column>
      <el-table-column label="品牌" min-width="100" show-overflow-tooltip><template #default="{ row }">{{ directoryMaterial(row)?.brand || '—' }}</template></el-table-column>
      <el-table-column label="标准物料分类" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ row.standardLibraryCategories?.map((category: StandardLibraryCategory) => category.name).join('、') || '未分类（待补选）' }}</template></el-table-column>
      <el-table-column prop="name" label="配置名称" min-width="200" show-overflow-tooltip />
      <el-table-column label="状态" width="130">
        <template #default="{ row }"><el-tag :type="relationStatus(row).type">{{ relationStatus(row).label }}</el-tag></template>
      </el-table-column>
      <el-table-column label="版本" width="80"><template #default="{ row }">{{ relationStatus(row).version ? `V${relationStatus(row).version}` : '—' }}</template></el-table-column>
      <el-table-column label="配件组" width="80"><template #default="{ row }">{{ visibleRevision(row)?.groups.length ?? 0 }}</template></el-table-column>
      <el-table-column label="候选物料" width="90"><template #default="{ row }">{{ optionCount(row) }}</template></el-table-column>
      <el-table-column label="更新时间" width="170"><template #default="{ row }">{{ formatDate(row.updatedAt) }}</template></el-table-column>
      <el-table-column label="操作" width="90" fixed="right"><template #default="{ row }"><el-button link type="primary" :loading="openingMaterialId === row.mainMaterialId" :aria-label="`维护 ${row.mainMaterialCode} 的关联配置`" @click="openEditor(row)">维护</el-button></template></el-table-column>
    </el-table>
    </div>

    <el-dialog v-model="editorOpen" :title="`${selectedRelation?.mainMaterialCode ?? ''} 关联配置`" width="min(1180px, calc(100vw - 32px))" destroy-on-close>
      <MaterialRelationEditor
        v-if="selectedMainMaterial"
        :token="token"
        :main-material="selectedMainMaterial"
        :can-manage="canManage"
        :can-publish="canPublish"
        @changed="handleChanged"
      />
    </el-dialog>
    <el-dialog v-model="createDialogOpen" title="新增关联配置" width="480px" destroy-on-close>
      <el-form label-position="top"><el-form-item label="主料品"><el-select v-model="createMaterialId" clearable filterable remote reserve-keyword :remote-method="searchCreateCandidates" :loading="createLoading" placeholder="搜索已批准料品"><el-option v-for="material in createCandidates" :key="material.id" :label="`${material.materialCode} · ${material.name}${material.specification ? ` · ${material.specification}` : ''}`" :value="material.id"><span v-stocked-material="material.materialCode">{{ material.materialCode }} · {{ material.name }}</span></el-option></el-select></el-form-item></el-form>
      <template #footer><el-button @click="createDialogOpen = false">取消</el-button><el-button type="primary" :disabled="!createMaterialId" @click="startNewConfiguration">开始配置</el-button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.material-relation-directory{display:grid;height:100%;min-height:0}.material-relation-directory__main{display:flex;min-width:0;min-height:0;flex-direction:column}.material-relation-directory__main>:deep(.el-table){flex:1;min-height:0}.material-relation-directory :deep(.el-table__expanded-cell){padding:0!important}
.material-relation-directory__toolbar{display:flex;flex:0 0 auto;align-items:center;gap:10px;margin-bottom:10px}.material-relation-directory__brand{width:150px}.material-relation-directory__toolbar .el-input{width:min(430px,42vw)}.material-relation-directory__toolbar span{color:var(--pdm-muted);font-size:12px}
</style>
