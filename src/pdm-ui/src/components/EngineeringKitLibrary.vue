<script setup lang="ts">
import QuantityInputNumber from './QuantityInputNumber.vue'
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { listStandardLibraryCategories, listEngineeringKits, listMaterials, publishEngineeringKit, saveEngineeringKit } from '../api'
import EngineeringKitDetails from './EngineeringKitDetails.vue'
import EngineeringKitActivityButton from './EngineeringKitActivityButton.vue'
import { standardCategoryCode, matchesStandardCategory, isUncategorized } from '../standardLibraryCategories'
import StandardCategoryFilter from './StandardCategoryFilter.vue'
import { u9UnitName } from '../u9Units'
import type { EngineeringKit, StandardLibraryCategory, PdmMaterial } from '../types'

const props = defineProps<{ token: string; canManage: boolean; active?: boolean }>()
const loading = ref(false)
const saving = ref(false)
const publishing = ref(false)
const query = ref('')
const categoryFilter = ref('')
const brandFilter = ref('')
const kits = ref<EngineeringKit[]>([])
const materials = ref<PdmMaterial[]>([])
const materialSearchResults = ref<PdmMaterial[]>([])
const materialSearchLoading = ref(false)
let materialSearchRequest = 0
const editorOpen = ref(false)
const editing = ref<EngineeringKit | null>(null)
const standardCategories = ref<StandardLibraryCategory[]>([])
const detailKit = ref<EngineeringKit | null>(null)
const expandedKitIds = ref<string[]>([])
const form = ref({
  name: '', brand: '', description: '', changeNote: '',
  modelMode: 'Auto' as 'Auto' | 'Manual', model: '', standardLibraryCategoryId: '',
  components: [] as Array<{ materialId: string; quantity: number; isOptional: false }>,
})

const eligibleMaterials = computed(() => materials.value.filter(item => !item.isArchived && item.approvalStatus === 'Approved'))
const materialOptions = computed(() => {
  const ids = new Set(materialSearchResults.value.map(item => item.id))
  return eligibleMaterials.value.filter(item => ids.has(item.id))
})
const brandOptions = computed(() => [...new Set(kits.value.map(item => item.brand?.trim()).filter((value): value is string => Boolean(value)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))
const filteredKits = computed(() => {
  const keyword = query.value.trim().toLowerCase()
  return kits.value.filter(item => (!brandFilter.value || item.brand === brandFilter.value)
    && matchesStandardCategory(standardCategories.value, categoryFilter.value, kitCategories(item).map(category => category.id))
    && (!keyword || [item.code, item.model, item.name, item.brand].some(value => value?.toLowerCase().includes(keyword))))
})
const categoryTree = computed(() => {
  type Node = { id: string; label: string; parentId?: string | null; children: Node[] }
  const nodes = standardCategories.value.filter(item => !isUncategorized(item)).map(item => {
    const code = standardCategoryCode(standardCategories.value, item.id)
    return { id: item.id, label: code ? item.name + '（' + code + '）' : item.name, parentId: item.parentId, children: [] as Node[] }
  })
  const byId = new Map(nodes.map(item => [item.id, item]))
  const roots: Node[] = []
  for (const node of nodes) { const parent = node.parentId ? byId.get(node.parentId) : undefined; if (parent) parent.children.push(node); else roots.push(node) }
  return roots
})

function draftRevision(kit?: EngineeringKit | null) {
  return kit ? [...kit.revisions].filter(item => item.state === 'Draft').sort((a, b) => b.versionNumber - a.versionNumber)[0] : undefined
}
function releasedRevision(kit?: EngineeringKit | null) {
  return kit?.revisions.find(item => item.id === kit.currentReleasedRevisionId)
}
function displayRevision(kit: EngineeringKit) {
  return (props.canManage ? draftRevision(kit) : undefined) ?? releasedRevision(kit)
}
function kitCategories(kit: EngineeringKit) {
  const id = (props.canManage ? draftRevision(kit)?.standardLibraryCategoryId : null) ?? kit.standardLibraryCategoryId ?? releasedRevision(kit)?.standardLibraryCategoryId
  return standardCategories.value.filter(item => item.id === id && !isUncategorized(item))
}
function toggleKitExpansion(kit: EngineeringKit) {
  expandedKitIds.value = expandedKitIds.value.includes(kit.id)
    ? expandedKitIds.value.filter(id => id !== kit.id) : [...expandedKitIds.value, kit.id]
}
function syncExpandedKits(_kit: EngineeringKit, expanded: EngineeringKit[]) {
  expandedKitIds.value = expanded.map(kit => kit.id)
}
function selectedMaterial(materialId: string) {
  return eligibleMaterials.value.find(item => item.id === materialId)
}
function materialLabel(material: PdmMaterial) {
  return `${material.materialCode} · ${material.name}`
}

async function load() {
  loading.value = true
  try {
    const [loadedKits, loadedMaterials, loadedCategories] = await Promise.all([
      listEngineeringKits(props.token, !props.canManage),
      props.canManage ? listMaterials(props.token, '', false, 100) : Promise.resolve([]),
      listStandardLibraryCategories(props.token),
    ])
    kits.value = loadedKits
    materials.value = loadedMaterials
    materialSearchResults.value = loadedMaterials
    standardCategories.value = loadedCategories.filter(item => item.isActive)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件加载失败')
  } finally {
    loading.value = false
  }
}

async function searchMaterialOptions(query: string) {
  const request = ++materialSearchRequest
  materialSearchResults.value = []
  materialSearchLoading.value = true
  try {
    const rows = await listMaterials(props.token, query.trim(), false, 100)
    if (request !== materialSearchRequest) return
    materials.value = [...new Map([...materials.value, ...rows].map(item => [item.id, item])).values()]
    materialSearchResults.value = rows
  } catch (error) {
    if (request === materialSearchRequest) {
      console.error('套件物料搜索失败', error)
      ElMessage.error('物料搜索失败，请稍后重试')
    }
  } finally {
    if (request === materialSearchRequest) materialSearchLoading.value = false
  }
}

async function loadSelectedMaterials(components: Array<{ materialId: string; materialCode: string }>) {
  const codes = [...new Set(components.filter(item => !materials.value.some(material => material.id === item.materialId)).map(item => item.materialCode).filter(Boolean))]
  if (!codes.length) return
  try {
    const rows = (await Promise.all(codes.map(code => listMaterials(props.token, code, false, 20)))).flat()
    materials.value = [...new Map([...materials.value, ...rows].map(item => [item.id, item])).values()]
  } catch (error) {
    console.error('套件已选物料读取失败', error)
    ElMessage.error('套件明细物料读取失败，请稍后重试')
  }
}

function openEditor(kit?: EngineeringKit) {
  editing.value = kit ?? null
  const revision = kit ? displayRevision(kit) : undefined
  const categoryId = draftRevision(kit)?.standardLibraryCategoryId ?? kit?.standardLibraryCategoryId ?? ''
  form.value = {
    name: kit?.name ?? '',
    brand: kit?.brand ?? '',
    description: kit?.description ?? '',
    changeNote: draftRevision(kit)?.changeNote ?? '',
    modelMode: kit?.modelMode === 'Manual' ? 'Manual' : 'Auto',
    model: kit?.modelMode === 'Manual' ? kit?.model ?? '' : '',
    standardLibraryCategoryId: standardCategories.value.some(item => item.id === categoryId) ? categoryId : '',
    components: revision?.components.map(item => ({ materialId: item.materialId, quantity: item.quantity, isOptional: false })) ?? [],
  }
  editorOpen.value = true
  materialSearchRequest++
  materialSearchResults.value = []
  materialSearchLoading.value = false
  void loadSelectedMaterials(revision?.components ?? [])
}
function closeEditor() {
  editorOpen.value = false
  editing.value = null
}
function addComponent() {
  form.value.components.push({ materialId: '', quantity: 1, isOptional: false })
}
function removeComponent(index: number) {
  form.value.components.splice(index, 1)
}

async function save() {
  if (!form.value.name.trim()) return ElMessage.warning('请填写套件名称')
  if (!form.value.brand.trim()) return ElMessage.warning('请填写套件品牌')
  if (form.value.modelMode === 'Manual' && !form.value.model.trim()) return ElMessage.warning('请填写套件型号，或改为自动生成')
  if (!form.value.standardLibraryCategoryId) return ElMessage.warning('请选择标准物料分类')
  if (form.value.components.length === 0) return ElMessage.warning('套件至少需要一个明细物料')
  if (form.value.components.some(item => !item.materialId || item.quantity <= 0)) return ElMessage.warning('请完整填写物料和每套数量')
  if (new Set(form.value.components.map(item => item.materialId)).size !== form.value.components.length) return ElMessage.warning('同一真实物料只能出现一次')
  saving.value = true
  try {
    const saved = await saveEngineeringKit({
      name: form.value.name.trim(),
      brand: form.value.brand.trim(),
      description: form.value.description.trim(),
      changeNote: form.value.changeNote.trim(),
      modelMode: form.value.modelMode,
      model: form.value.model.trim(),
      standardLibraryCategoryId: form.value.standardLibraryCategoryId,
      components: form.value.components.map((item, index) => ({ ...item, isOptional: false, sortOrder: index + 1 })),
      expectedRowVersion: editing.value?.rowVersion,
    }, props.token, editing.value?.id)
    editing.value = saved
    kits.value = kits.value.some(item => item.id === saved.id) ? kits.value.map(item => item.id === saved.id ? saved : item) : [saved, ...kits.value]
    ElMessage.success('套件草稿已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件保存失败')
  } finally {
    saving.value = false
  }
}

async function publishCurrent() {
  const kit = editing.value
  const draft = draftRevision(kit)
  if (!kit || !draft) return
  try {
    await ElMessageBox.confirm(
      `发布后将固定为 V${String(draft.versionNumber).padStart(2, '0')}，套件将按唯一明细组展开为真实物料。是否继续？`,
      kit.code ? `发布 ${kit.code} 新版本` : '首次发布并生成套件料号和型号',
      { confirmButtonText: '确认发布', cancelButtonText: '取消', type: 'warning' },
    )
  } catch { return }
  publishing.value = true
  try {
    const saved = await publishEngineeringKit(kit.id, kit.rowVersion, props.token)
    editing.value = saved
    kits.value = kits.value.map(item => item.id === saved.id ? saved : item)
    ElMessage.success(`已发布 ${saved.code} / V${String(draft.versionNumber).padStart(2, '0')}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '套件发布失败')
  } finally {
    publishing.value = false
  }
}

function applyKitActivity(kit: EngineeringKit) {
  kits.value = kits.value.map(item => item.id === kit.id ? kit : item)
  if (editing.value?.id === kit.id) editing.value = kit
  if (detailKit.value?.id === kit.id) detailKit.value = kit
}

watch(() => props.active, active => { if (active) void load() })
onMounted(load)
</script>

<template>
  <section class="engineering-kit-library" aria-label="料品套件">
    <template v-if="!editorOpen">
      <div class="engineering-kit-library__layout pdm-project-list-layout">
      <StandardCategoryFilter v-model="categoryFilter" :categories="standardCategories" />
      <div class="engineering-kit-library__main pdm-panel pdm-project-table-panel">
      <div class="engineering-kit-library__toolbar pdm-toolbar">
        <el-button @click="load">刷新</el-button>
        <el-select v-model="brandFilter" clearable filterable placeholder="筛选品牌"><el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" /></el-select>
        <el-input v-model="query" clearable placeholder="搜索套件料号、名称、型号或品牌" />
        <div class="engineering-kit-library__actions">
          <el-button v-if="canManage" type="primary" @click="openEditor()">新建套件</el-button>
        </div>
        <span>共 {{ filteredKits.length }} 个套件</span>
      </div>
      <el-table v-loading="loading" data-pdm-column-layout="scroll" :data="filteredKits" row-key="id" :expand-row-keys="expandedKitIds" @expand-change="syncExpandedKits" height="100%" empty-text="暂无套件">
        <el-table-column type="expand" width="32" class-name="pdm-column-fixed-width">
          <template #default="{ row }">
            <section :id="`kit-contents-${row.id}`" class="engineering-kit-library__contents" :aria-label="`套件 ${row.code || row.name} 的物料明细`">
              <header><strong>套件明细</strong><span v-if="displayRevision(row)">V{{ String(displayRevision(row)!.versionNumber).padStart(2, '0') }} {{ displayRevision(row)!.state === 'Draft' ? '草稿' : '已发布' }} · 共 {{ displayRevision(row)!.components.length }} 种物料</span></header>
              <el-table data-pdm-column-layout="scroll" :data="displayRevision(row)?.components ?? []" max-height="320" empty-text="该套件暂无物料明细">
                <el-table-column label="料号" min-width="135"><template #default="{ row: component }"><span v-stocked-material="component.materialCode">{{ component.materialCode }}</span></template></el-table-column>
                <el-table-column label="名称" min-width="150"><template #default="{ row: component }"><span v-stocked-material="component.materialCode">{{ component.materialName }}</span></template></el-table-column>
                <el-table-column prop="specification" label="型号" min-width="160" />
                <el-table-column prop="brand" label="品牌" min-width="95" />
                <el-table-column label="单位" width="70"><template #default="{ row: component }">{{ u9UnitName(component.unit) }}</template></el-table-column>
                <el-table-column prop="quantity" label="每套数量" width="100" />
                <el-table-column prop="remark" label="备注" min-width="140" />
              </el-table>
            </section>
          </template>
        </el-table-column>
        <el-table-column label="套件料号" min-width="145"><template #default="{ row }"><button type="button" class="kit-code pdm-text-action" :aria-expanded="expandedKitIds.includes(row.id)" :aria-controls="`kit-contents-${row.id}`" :aria-label="`${expandedKitIds.includes(row.id) ? '收起' : '展开'}套件 ${row.code || row.name} 的物料明细`" @click.stop="toggleKitExpansion(row)">{{ row.code || '发布时生成' }}</button></template></el-table-column>
        <el-table-column prop="name" label="套件名称" min-width="180" />
        <el-table-column label="型号" min-width="145"><template #default="{ row }">{{ row.model || '发布时生成' }}</template></el-table-column>
        <el-table-column prop="brand" label="品牌" min-width="130" />
        <el-table-column label="标准物料分类" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ kitCategories(row).map(category => category.name).join('、') || '未分类（待补选）' }}</template></el-table-column>
        <el-table-column prop="description" label="备注" min-width="180" show-overflow-tooltip />
        <el-table-column label="套件内物料数量" width="150"><template #default="{ row }"><el-button link type="primary" @click="detailKit = row">{{ displayRevision(row)?.components.length ?? 0 }}</el-button></template></el-table-column>
        <el-table-column label="状态" width="145"><template #default="{ row }"><el-tag v-if="row.isActive === false" type="info" size="small">已停用</el-tag><el-tag v-else-if="canManage && draftRevision(row)" type="warning" size="small">V{{ String(draftRevision(row)?.versionNumber).padStart(2, '0') }} 草稿</el-tag><el-tag v-else-if="releasedRevision(row)" type="success" size="small">V{{ String(releasedRevision(row)?.versionNumber).padStart(2, '0') }} 已发布</el-tag></template></el-table-column>
        <el-table-column v-if="canManage" label="操作" width="160" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="openEditor(row)">维护</el-button><EngineeringKitActivityButton :kit="row" :token="token" :can-manage="canManage" link @changed="applyKitActivity" /></template></el-table-column>
      </el-table>
      </div>
      </div>
    </template>

    <template v-else>
      <header class="engineering-kit-editor__title">
        <div><el-button link type="primary" @click="closeEditor">返回套件列表</el-button><h1>{{ editing ? `${editing.code || '套件草稿'} 套件配置` : '新建套件' }}</h1></div>
        <span>套件主项为虚拟对象，发布后进入所选标准物料分类；引用时按已发布版本展开真实物料。</span>
      </header>
      <main class="engineering-kit-editor">
        <section class="engineering-kit-summary">
          <div><label>套件料号</label><strong>{{ editing?.code || '发布时生成' }}</strong></div>
          <div><label>名称</label><el-input v-model="form.name" maxlength="160" placeholder="请输入套件名称" /></div>
          <div class="is-model">
            <div class="engineering-kit-model-head">
              <label>型号</label>
              <el-radio-group v-model="form.modelMode" size="small" :disabled="!!editing?.currentReleasedRevisionId">
                <el-radio-button label="Auto">自动生成</el-radio-button>
                <el-radio-button label="Manual">手动输入</el-radio-button>
              </el-radio-group>
            </div>
            <el-input v-if="form.modelMode === 'Manual'" v-model="form.model" maxlength="160" placeholder="请输入套件型号" :disabled="!!editing?.currentReleasedRevisionId" />
            <el-input v-else-if="editing?.currentReleasedRevisionId" :model-value="editing.model" disabled />
            <el-tree-select v-model="form.standardLibraryCategoryId" :data="categoryTree" node-key="id" :props="{ label: 'label', children: 'children' }" check-strictly filterable placeholder="选择标准物料分类" />
          </div>
          <div><label>品牌</label><el-input v-model="form.brand" maxlength="160" placeholder="请输入品牌" /></div>
          <div><label>备注信息</label><el-input v-model="form.description" maxlength="500" placeholder="备注信息（可选）" /></div>
          <div><label>套件内物料数量</label><strong>{{ form.components.length }}</strong></div>
          <div><label>状态</label><strong>{{ editing?.isActive === false ? '已停用' : draftRevision(editing) ? `待发布 · V${draftRevision(editing)?.versionNumber}` : releasedRevision(editing) ? '已发布' : '新建草稿' }}</strong></div>
        </section>
        <label class="engineering-kit-change-note"><span>修改说明</span><el-input v-model="form.changeNote" maxlength="500" placeholder="说明本次套件变化" /></label>
        <section class="engineering-kit-group">
          <header class="engineering-kit-group__header">
            <label><span>明细组</span><el-input model-value="套件明细" readonly class="engineering-kit-group-name" /></label>
            <span class="engineering-kit-group__count">共 {{ form.components.length }} 项</span>
            <el-button type="primary" plain @click="addComponent">添加物料</el-button>
          </header>
          <el-table :data="form.components" empty-text="请添加至少一个已批准真实物料">
            <el-table-column label="料号" min-width="260"><template #default="{ row }"><el-select v-model="row.materialId" filterable remote :remote-method="searchMaterialOptions" :loading="materialSearchLoading" placeholder="输入料号、名称或型号搜索" no-match-text="未找到已批准的真实物料" no-data-text="未找到已批准的真实物料" style="width:100%"><template #label><span v-stocked-material="selectedMaterial(row.materialId)?.materialCode">{{ selectedMaterial(row.materialId) ? materialLabel(selectedMaterial(row.materialId)!) : '已选择物料' }}</span></template><el-option v-for="material in materialOptions" :key="material.id" :label="materialLabel(material)" :value="material.id"><span v-stocked-material="material.materialCode">{{ materialLabel(material) }}</span></el-option></el-select></template></el-table-column>
            <el-table-column label="名称" min-width="170"><template #default="{ row }"><span v-stocked-material="selectedMaterial(row.materialId)?.materialCode">{{ selectedMaterial(row.materialId)?.name || '—' }}</span></template></el-table-column>
            <el-table-column label="型号" min-width="170"><template #default="{ row }">{{ selectedMaterial(row.materialId)?.specification || '—' }}</template></el-table-column>
            <el-table-column label="品牌" min-width="120"><template #default="{ row }">{{ selectedMaterial(row.materialId)?.brand || '—' }}</template></el-table-column>
            <el-table-column label="备注" min-width="170" show-overflow-tooltip><template #default="{ row }">{{ selectedMaterial(row.materialId)?.remark || '—' }}</template></el-table-column>
            <el-table-column label="每套数量" width="150"><template #default="{ row }"><QuantityInputNumber v-model="row.quantity" :min="0.0001" :precision="4" controls-position="right" style="width:100%" /></template></el-table-column>
            <el-table-column label="操作" width="80"><template #default="{ $index }"><el-button link type="danger" @click="removeComponent($index)">移除</el-button></template></el-table-column>
          </el-table>
        </section>
        <footer class="engineering-kit-editor__footer">
          <el-button @click="closeEditor">返回</el-button>
          <EngineeringKitActivityButton v-if="editing" :kit="editing" :token="token" :can-manage="canManage" :disabled="saving || publishing" @changed="applyKitActivity" />
          <el-button type="primary" :loading="saving" @click="save">保存草稿</el-button>
          <el-button type="success" :disabled="!draftRevision(editing) || editing?.isActive === false" :loading="publishing" @click="publishCurrent">发布生效</el-button>
        </footer>
      </main>
    </template>

    <EngineeringKitDetails :kit="detailKit" :can-manage="canManage" @close="detailKit = null" />
  </section>
</template>

<style scoped>
.engineering-kit-library__layout{display:grid;flex:1;min-height:0}.engineering-kit-library__main{display:flex;min-height:0;min-width:0;flex-direction:column}.engineering-kit-library__main>:deep(.el-table){min-height:0;flex:1}
.engineering-kit-library{display:flex;height:100%;min-height:0;flex-direction:column;overflow:hidden}.engineering-kit-editor__title{display:flex;padding:10px 16px;border-bottom:1px solid var(--pdm-border);align-items:center;justify-content:space-between}.engineering-kit-editor__title h1{margin:0;font-size:18px}.engineering-kit-editor__title>span{margin:4px 0 0;color:var(--pdm-muted);font-size:12px}.engineering-kit-library__toolbar{display:flex;flex:0 0 auto;align-items:center;gap:10px;margin-bottom:10px}.engineering-kit-library__toolbar>.el-select{width:150px}.engineering-kit-library__toolbar .el-input{width:min(430px,42vw)}.engineering-kit-library__toolbar span{color:var(--pdm-muted);font-size:12px}.engineering-kit-library>:deep(.el-table){min-height:0;flex:1}.kit-code{color:var(--pdm-theme-accent)}.engineering-kit-editor__title>div{display:flex;align-items:center;gap:12px}.engineering-kit-editor{padding:12px 16px;overflow:auto}.engineering-kit-summary{display:grid;overflow:hidden;border:1px solid var(--pdm-border);border-radius:8px;grid-template-columns:1.05fr 1.25fr 1.5fr 1.1fr 1.2fr .85fr .9fr}.engineering-kit-library__actions{display:flex;gap:8px}.engineering-kit-summary .is-model{gap:6px}.engineering-kit-model-head{display:flex;align-items:center;gap:8px}.engineering-kit-model-head label{white-space:nowrap}.engineering-kit-model-head :deep(.el-radio-group){flex:0 0 auto}.engineering-kit-summary>div{display:flex;min-height:68px;padding:9px 12px;border-right:1px solid var(--pdm-border);flex-direction:column;justify-content:center;gap:8px}.engineering-kit-summary>div:last-child{border-right:0}.engineering-kit-summary label,.engineering-kit-change-note>span,.engineering-kit-group__header label>span{color:var(--pdm-text-soft);font-size:12px;font-weight:600}.engineering-kit-summary strong{display:flex;min-height:32px;align-items:center}.engineering-kit-change-note{display:grid;margin:12px 0;align-items:center;grid-template-columns:82px 1fr}.engineering-kit-group{padding:12px;border:1px solid var(--pdm-border);border-radius:8px;background:var(--pdm-bg)}.engineering-kit-group :deep(.el-table__header .cell),.engineering-kit-group :deep(.el-table__body .cell){text-align:center;justify-content:center}.engineering-kit-group__header{display:flex;margin-bottom:10px;align-items:center;gap:12px}.engineering-kit-group__header label{display:flex;align-items:center;gap:10px}.engineering-kit-group-name{width:200px}.engineering-kit-group__count{color:var(--pdm-muted);font-size:12px}.engineering-kit-group__header>.el-button{margin-left:auto}.engineering-kit-editor__footer{display:flex;margin-top:14px;justify-content:flex-end;gap:8px}@media(max-width:1000px){.engineering-kit-library__toolbar{flex-wrap:wrap}.engineering-kit-summary{grid-template-columns:repeat(2,1fr)}.engineering-kit-summary>div{border-bottom:1px solid var(--pdm-border)}.engineering-kit-editor__title{align-items:flex-start;flex-direction:column;gap:6px}}
.engineering-kit-group{--pdm-detail-row-height:40px}
.engineering-kit-group :deep(.el-table__header th.el-table__cell){height:40px;padding-block:0;box-sizing:border-box}
.engineering-kit-group :deep(.el-table__body .cell){white-space:nowrap}
.engineering-kit-library :deep(.el-table__expanded-cell) { padding: 0 !important; }
.engineering-kit-library__contents { padding: 3px; }
.engineering-kit-library__contents > header { display: flex; align-items: center; gap: 10px; padding: 5px 0; font-size: 12px; }
.engineering-kit-library__contents > header > span { color: var(--pdm-text-soft); }
.kit-code { border: 0; padding: 0; background: transparent; font: inherit; font-weight: 600; text-align: left; cursor: pointer; }
.engineering-kit-library :deep(button.el-table__expand-icon),
.engineering-kit-library :deep(button.el-table__expand-icon:is(:hover, :focus-visible)) { border-color: transparent !important; outline: none !important; box-shadow: none !important; background: transparent !important; }
</style>
