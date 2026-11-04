<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, reactive, ref, watch, onBeforeUnmount } from 'vue'
import { listMaterialRelationTemplates, listMaterials, listMaterialRelationMainOptions, listMaterialRelationCategories, publishMaterialRelationTemplate, saveMaterialRelationTemplate } from '../api'
import type { MaterialRelationMainMaterial, MaterialRelationQuantityMode, MaterialRelationSelectionMode, MaterialRelationTemplate, PdmMaterial, SaveMaterialRelationTemplateInput, StandardLibraryCategory } from '../types'
import MaterialRelationCategories from './MaterialRelationCategories.vue'
import { relationMains, relationHasMain } from '../materialRelationMains'

type DraftOption = { materialId: string; quantityMode: MaterialRelationQuantityMode; quantityPerSet: number; isDefault: boolean; sortOrder: number; selectionAdvice: string }
type DraftGroup = { name: string; isRequired: boolean; selectionMode: MaterialRelationSelectionMode; minSelection: number; maxSelection: number | null; autoSelectUnique: boolean; sortOrder: number; options: DraftOption[] }

const props = defineProps<{ token: string; mainMaterial?: PdmMaterial | null; templateId?: string; canManage: boolean; canPublish: boolean }>()
const emit = defineEmits<{ changed: [] }>()
const loading = ref(false)
const categoryStatus = ref<Record<string, boolean>>({})
const categoryCatalog = ref<StandardLibraryCategory[]>([])
const mainIds = ref<string[]>([])
const mainCache = ref<MaterialRelationMainMaterial[]>([])
const mainSearchRows = ref<MaterialRelationMainMaterial[]>([])
const mainSearchLoading = ref(false)
const mainSelection = ref('')
const activeTemplateId = ref(props.templateId || '')
const savedSignature = ref('')
let mainSearchRequest = 0, loadRequest = 0
const saving = ref(false)
const configuring = ref(false)
const materialSearchLoading = ref(false)
const relations = ref<MaterialRelationTemplate[]>([])
const materials = ref<PdmMaterial[]>([])
const materialSearchResults = ref<PdmMaterial[]>([])
const materialSearchQuery = ref('')
const draft = reactive<{ name: string; changeNote: string; expectedRevisionRowVersion: number | null; groups: DraftGroup[] }>({ name: '', changeNote: '', expectedRevisionRowVersion: null, groups: [] })
let materialSearchRequest = 0
const quickOptions = reactive(new WeakMap<DraftGroup, DraftOption>())

const relation = computed(() => relations.value.find(item => activeTemplateId.value ? item.id === activeTemplateId.value : props.mainMaterial && relationHasMain(item, props.mainMaterial.id)))
const mainRows = computed(() => mainIds.value.map(id => mainCache.value.find(item => item.materialId === id)).filter((item): item is MaterialRelationMainMaterial => !!item))
const hasCategory = computed(() => mainIds.value.length > 0 && mainIds.value.every(id => categoryStatus.value[id] === true))
const mainOptions = computed(() => mainSearchRows.value.filter(item => !mainIds.value.includes(item.materialId)))
const signature = () => JSON.stringify([mainIds.value.slice().sort(), draft.name, draft.changeNote, draft.groups])
const dirty = computed(() => savedSignature.value !== signature())
const publishedMains = computed(() => relation.value ? relationMains(relation.value, false) : [])
const removedMains = computed(() => publishedMains.value.filter(main => !mainIds.value.includes(main.materialId)))
const addedMainCount = computed(() => mainIds.value.filter(id => !publishedMains.value.some(main => main.materialId === id)).length)
function asMain(material: PdmMaterial): MaterialRelationMainMaterial { return { materialId: material.id, materialCode: material.materialCode, materialName: material.name, specification: material.specification, brand: material.brand, remark: material.remark } }
function categoryLoaded(id: string, ready: boolean) { categoryStatus.value = { ...categoryStatus.value, [id]: ready } }
async function searchMainOptions(query = '') {
  const run = ++mainSearchRequest
  mainSearchLoading.value = true
  try {
    const values = await listMaterialRelationMainOptions(props.token, query)
    if (run !== mainSearchRequest) return
    mainSearchRows.value = values
    mainCache.value = [...new Map([...mainCache.value, ...values].map(item => [item.materialId, item])).values()]
  } catch (cause) { if (run === mainSearchRequest) ElMessage.error(cause instanceof Error ? cause.message : '主物料搜索失败') }
  finally { if (run === mainSearchRequest) mainSearchLoading.value = false }
}
function mainOwner(id: string) { return relations.value.find(item => item.id !== relation.value?.id && relationHasMain(item, id)) }
function addMain(id: string) {
  mainSelection.value = ''
  if (!props.canManage || saving.value || !id || mainIds.value.includes(id) || mainOwner(id)) return
  mainIds.value.push(id)
}
const selectedMaterialIds = computed(() => new Set(draft.groups.flatMap(group => group.options.map(option => option.materialId).filter(Boolean))))
const approvedMaterials = computed(() => {
  const query = materialSearchQuery.value.trim().toLocaleLowerCase()
  const visibleIds = new Set([...materialSearchResults.value.map(item => item.id), ...(query ? [] : selectedMaterialIds.value)])
  const candidates = materials.value.filter(item => visibleIds.has(item.id) && item.approvalStatus === 'Approved' && !item.isArchived && !mainIds.value.includes(item.id))
  const exact = query ? candidates.filter(item => item.materialCode.trim().toLocaleLowerCase() === query) : []
  return exact.length ? exact : candidates
})
const materialById = computed(() => new Map(materials.value.map(item => [item.id, item])))
const canConfigure = computed(() => props.canManage && (!props.mainMaterial || props.mainMaterial.approvalStatus === 'Approved' && !props.mainMaterial.isArchived))
const relationStatus = computed(() => {
  if (dirty.value && !loading.value) return relation.value?.publishedRevision ? `未保存修改 · 当前生效V${relation.value.publishedRevision.version}` : '未保存修改'
  if (relation.value?.draftRevision && relation.value.publishedRevision) return `有待生效修改 · V${relation.value.draftRevision.version}`
  if (relation.value?.draftRevision) return `待发布生效 · V${relation.value.draftRevision.version}`
  if (relation.value?.publishedRevision) return `已生效 · V${relation.value.publishedRevision.version}`
  return !props.mainMaterial || props.mainMaterial.approvalStatus === 'Approved' ? '未配置' : '料品批准后可配置'
})
const statusType = computed(() => dirty.value && !loading.value || relation.value?.draftRevision ? 'warning' : relation.value?.publishedRevision ? 'success' : 'info')

watch(() => [props.mainMaterial?.id, props.templateId, props.token], () => { activeTemplateId.value = props.templateId || ''; void load() }, { immediate: true })
onBeforeUnmount(() => { loadRequest++; mainSearchRequest++; materialSearchRequest++ })

function normalizeSelectionMode(value: unknown): MaterialRelationSelectionMode {
  return value === 'Single' || value === 0 ? 'Single' : 'Multiple'
}

function normalizeQuantityMode(value: unknown): MaterialRelationQuantityMode {
  return value === 'PerMainQuantity' || value === 0 ? 'PerMainQuantity' : 'Fixed'
}

function loadDraft(item?: MaterialRelationTemplate) {
  const revision = item?.draftRevision ?? item?.publishedRevision
  Object.assign(draft, {
    name: item?.name ?? '',
    changeNote: item?.draftRevision?.changeNote ?? '',
    expectedRevisionRowVersion: item?.draftRevision?.rowVersion ?? null,
    groups: (revision?.groups ?? []).map(group => ({
      name: group.name,
      isRequired: group.isRequired,
      selectionMode: normalizeSelectionMode(group.selectionMode),
      minSelection: group.minSelection,
      maxSelection: group.maxSelection ?? null,
      autoSelectUnique: false,
      sortOrder: group.sortOrder,
      options: group.options.map(option => ({
        materialId: option.materialId,
        quantityMode: normalizeQuantityMode(option.quantityMode),
        quantityPerSet: option.quantityPerSet,
        isDefault: option.isDefault,
        sortOrder: option.sortOrder,
        selectionAdvice: option.selectionAdvice ?? '',
      })),
    })),
  })
  configuring.value = Boolean(revision) || !props.mainMaterial
}

async function load() {
  const run = ++loadRequest
  materialSearchRequest++
  materialSearchQuery.value = ''
  materialSearchLoading.value = false
  loading.value = true
  try {
    const [relationRows, categories] = await Promise.all([listMaterialRelationTemplates(props.token, props.canManage || props.canPublish), listMaterialRelationCategories(props.token)])
    const currentRelation = relationRows.find(item => activeTemplateId.value ? item.id === activeTemplateId.value : props.mainMaterial && relationHasMain(item, props.mainMaterial.id))
    const revision = currentRelation?.draftRevision ?? currentRelation?.publishedRevision
    const selectedCodes = [...new Set((revision?.groups ?? []).flatMap(group => group.options.map(option => option.materialCode)).filter(Boolean))]
    const materialBatches = await Promise.all([
      listMaterials(props.token, '', false, 100),
      ...selectedCodes.map(code => listMaterials(props.token, code, false, 20)),
    ])
    if (run !== loadRequest) return
    const materialRows = [...new Map(materialBatches.flat().map(item => [item.id, item])).values()]
    relations.value = relationRows
    categoryCatalog.value = categories
    activeTemplateId.value = currentRelation?.id || ''
    mainCache.value = currentRelation ? relationMains(currentRelation) : props.mainMaterial ? [asMain(props.mainMaterial)] : []
    mainIds.value = mainCache.value.map(item => item.materialId)
    mainSelection.value = ''
    materials.value = materialRows
    materialSearchResults.value = materialBatches[0]
    loadDraft(currentRelation)
    savedSignature.value = signature()
    if (props.canManage) void searchMainOptions()
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '关联物料加载失败')
  } finally {
    if (run === loadRequest) loading.value = false
  }
}

async function searchMaterialOptions(query: string) {
  const request = ++materialSearchRequest
  materialSearchQuery.value = query.trim()
  materialSearchResults.value = []
  materialSearchLoading.value = true
  try {
    const rows = await listMaterials(props.token, materialSearchQuery.value, false, 100)
    if (request !== materialSearchRequest) return
    materials.value = [...new Map([...materials.value, ...rows].map(item => [item.id, item])).values()]
    materialSearchResults.value = rows
  } catch (error) {
    if (request === materialSearchRequest) ElMessage.error(error instanceof Error ? error.message : '候选物料搜索失败')
  } finally {
    if (request === materialSearchRequest) materialSearchLoading.value = false
  }
}

function startConfiguration() {
  if (!canConfigure.value) return
  Object.assign(draft, { changeNote: '', expectedRevisionRowVersion: null, groups: [] })
  configuring.value = true
}

function relationMaterial(materialId: string) {
  return materialById.value.get(materialId)
}

function addGroup() {
  draft.groups.push({ name: '', isRequired: true, selectionMode: 'Single', minSelection: 1, maxSelection: 1, autoSelectUnique: false, sortOrder: draft.groups.length + 1, options: [] })
}

function optionRows(group: DraftGroup) {
  if (!props.canManage) return group.options
  let quick = quickOptions.get(group)
  if (!quick) {
    quick = { materialId: '', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: false, sortOrder: group.options.length + 1, selectionAdvice: '' }
    quickOptions.set(group, quick)
  }
  return [...group.options, quick]
}

function selectOption(group: DraftGroup, option: DraftOption) {
  if (!props.canManage || saving.value || !option.materialId || group.options.includes(option)) return
  group.options.push(option)
  quickOptions.delete(group)
}

function normalizeGroup(group: DraftGroup) {
  group.minSelection = group.isRequired ? Math.max(1, group.minSelection || 1) : 0
  if (group.selectionMode === 'Single') group.maxSelection = 1
}

async function save() {
  if (!mainIds.value.length) return ElMessage.warning('请选择已批准物料或已发布套件作为主物料')
  if (!draft.groups.length || draft.groups.some(group => !group.name.trim() || !group.options.length || group.options.some(option => !option.materialId))) {
    ElMessage.warning('每个配件组都需要名称和至少一个物料选项')
    return
  }
  saving.value = true
  try {
    const input: SaveMaterialRelationTemplateInput = {
      mainMaterialId: mainIds.value[0]!,
      mainMaterialIds: [...mainIds.value],
      name: draft.name.trim() || `${mainRows.value[0]?.materialCode}关联物料`,
      changeNote: draft.changeNote.trim(),
      expectedRevisionRowVersion: draft.expectedRevisionRowVersion,
      groups: draft.groups.map((group, groupIndex) => ({
        ...group,
        autoSelectUnique: false,
        sortOrder: groupIndex + 1,
        options: group.options.map((option, optionIndex) => ({ ...option, sortOrder: optionIndex + 1 })),
      })),
    }
    const saved = await saveMaterialRelationTemplate(input, props.token, relation.value?.id)
    activeTemplateId.value = saved.id
    await load()
    emit('changed')
    ElMessage.success('关联物料修改已保存，发布后对工程师生效')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '关联物料保存失败')
  } finally {
    saving.value = false
  }
}

async function publish() {
  const current = relation.value
  if (!current?.draftRevision) return
  try {
    if (dirty.value) return ElMessage.warning('请先保存主料范围和配件规则的修改，再发布生效')
    await ElMessageBox.confirm(`发布共享配置 V${current.draftRevision.version}？发布后对本组 ${mainIds.value.length} 个主料统一生效，工程师将按此版本选配。`, '发布关联配置', { type: 'warning', confirmButtonText: '确认发布' })
    await publishMaterialRelationTemplate(current.id, current.draftRevision.id, current.draftRevision.rowVersion, props.token)
    await load()
    emit('changed')
    ElMessage.success('关联配置已发布生效')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '发布失败')
  }
}
</script>

<template>
  <section v-loading="loading" class="material-relation-editor" aria-label="关联物料">
    <div class="material-relation-summary">
      <label><span>配置名称</span><el-input v-model="draft.name" :maxlength="160" :disabled="!canManage || saving" placeholder="如：M6螺钉配套配置" aria-label="关联配置名称" /></label>
      <div class="material-relation-summary__status"><span>关联配置</span><el-tag :type="statusType">{{ relationStatus }}</el-tag></div>
    </div>
    <div class="material-relation-main-toolbar"><strong>主物料 {{ mainIds.length }} 个</strong></div>
    <div class="material-relation-main-scroll">
      <table class="material-relation-main-table pdm-fit-columns" aria-label="共享配置主物料">
        <colgroup><col><col><col><col><col><col data-pdm-fixed-width="400" style="width:400px"><col v-if="canManage" data-pdm-fixed-width="80" style="width:80px"></colgroup>
        <thead><tr><th>物料料号</th><th>名称</th><th>型号</th><th>品牌</th><th>备注</th><th>标准物料分类</th><th v-if="canManage">操作</th></tr></thead>
        <tbody>
          <tr v-for="main in mainRows" :key="main.materialId"><td v-stocked-material="main.materialCode">{{ main.materialCode }}</td><td>{{ main.materialName }}</td><td>{{ main.specification || '—' }}</td><td>{{ main.brand || '—' }}</td><td>{{ main.remark || '—' }}</td><td><MaterialRelationCategories :token="token" :material-id="main.materialId" :can-manage="canManage" :catalog="categoryCatalog" compact @loaded="categoryLoaded(main.materialId, $event)" @changed="emit('changed')" /></td><td v-if="canManage"><el-button link type="danger" :disabled="saving" :aria-label="`移除主料 ${main.materialCode}`" @click="mainIds = mainIds.filter(id => id !== main.materialId)">移除</el-button></td></tr>
          <tr v-if="canManage" class="material-relation-main-add-row is-quick-entry">
            <td colspan="3"><el-select v-model="mainSelection" filterable remote :remote-method="searchMainOptions" :loading="mainSearchLoading" :disabled="saving" popper-class="material-relation-main-popper" placeholder="输入料号、名称或型号添加主物料" aria-label="新增主物料" @change="addMain">
              <el-option v-for="material in mainOptions" :key="material.materialId" :value="material.materialId" :disabled="!!mainOwner(material.materialId)" :label="`${material.isEngineeringKit ? '【料品套件】' : ''}${material.materialCode} · ${material.materialName} · ${material.specification || '—'}`"><span v-stocked-material="material.materialCode">{{ material.isEngineeringKit ? '【料品套件】' : '' }}{{ material.materialCode }} · {{ material.materialName }} · {{ material.specification || '—' }}<small v-if="mainOwner(material.materialId)">（已归属：{{ mainOwner(material.materialId)?.name }}）</small></span></el-option>
            </el-select></td><td colspan="4" class="material-relation-add-hint">选择后自动增加一行</td>
          </tr>
          <tr v-else-if="!mainRows.length"><td colspan="6">暂无主物料。</td></tr>
        </tbody>
      </table>
    </div>
    <p class="material-relation-main-hint">同组主料共享配件规则和版本；主料范围与规则保存草稿后统一发布生效。分类由各主料继承，缺分类请由标准化人员补选。</p>
    <p v-if="publishedMains.length && (addedMainCount || removedMains.length)" class="material-relation-main-hint">当前生效 {{ publishedMains.length }} 个主料；本次拟新增 {{ addedMainCount }} 个<span v-if="removedMains.length">，移除 {{ removedMains.map(main => main.materialCode).join('、') }}</span>，统一发布后生效。</p>
    <el-empty v-if="!configuring" :description="!mainMaterial || mainMaterial.approvalStatus === 'Approved' ? '该物料尚未配置关联物料' : '料品批准后方可配置关联物料'">
      <el-button v-if="canConfigure" type="primary" @click="startConfiguration">开始配置关联物料</el-button>
    </el-empty>

    <template v-else>
      <div class="material-relation-change-note">
        <label>修改说明</label>
        <el-input v-model="draft.changeNote" :disabled="!canManage" placeholder="说明本次关联物料变化" />
      </div>
      <section v-for="(group, groupIndex) in draft.groups" :key="groupIndex" class="material-relation-group">
        <header>
          <label class="material-relation-group-name"><span>分类名称 <em>*</em></span><el-input v-model="group.name" :disabled="!canManage" aria-label="分类名称（必填）" placeholder="如伺服控制器" /></label>
          <el-checkbox v-model="group.isRequired" :disabled="!canManage" @change="normalizeGroup(group)">需要工程师核对</el-checkbox>
          <el-select v-model="group.selectionMode" :disabled="!canManage" aria-label="工程师选择方式" @change="normalizeGroup(group)"><el-option value="Single" label="只能选 1 项" /><el-option value="Multiple" label="可同时选多项" /></el-select>
          <el-button v-if="canManage" link type="danger" @click="draft.groups.splice(groupIndex, 1)">删除组</el-button>
        </header>
        <div class="material-relation-option-table">
          <div class="material-relation-option-head"><span>料号</span><span>名称</span><span>型号</span><span>品牌</span><span>备注</span><span>选型建议</span><span>数量计算</span><span>每套数量</span><span>优先推荐</span><span>操作</span></div>
          <div v-for="(option, optionIndex) in optionRows(group)" :key="option.materialId ? `option:${optionIndex}:${option.materialId}` : 'new-option'" class="material-relation-option-row" :class="{ 'is-quick-entry': !option.materialId }">
            <el-select v-stocked-material="relationMaterial(option.materialId)?.materialCode" v-model="option.materialId" filterable remote :remote-method="searchMaterialOptions" :loading="materialSearchLoading" popper-class="material-relation-material-popper" :disabled="!canManage || saving" :aria-label="option.materialId ? `候选配件 ${relationMaterial(option.materialId)?.materialCode || optionIndex + 1}` : '新增候选配件'" placeholder="输入料号或型号搜索" @change="selectOption(group, option)">
              <el-option v-for="item in approvedMaterials" :key="item.id" :value="item.id" :label="item.materialCode">
                <span class="material-relation-select-option"><b v-stocked-material="item.materialCode">{{ item.materialCode }}</b><span v-stocked-material="item.materialCode">{{ item.name || '—' }}</span><span>{{ item.specification || '—' }}</span><span>{{ item.remark || '—' }}</span></span>
              </el-option>
            </el-select>
            <span v-stocked-material="relationMaterial(option.materialId)?.materialCode" :title="relationMaterial(option.materialId)?.name || ''">{{ relationMaterial(option.materialId)?.name || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.specification || ''">{{ relationMaterial(option.materialId)?.specification || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.brand || ''">{{ relationMaterial(option.materialId)?.brand || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.remark || ''">{{ relationMaterial(option.materialId)?.remark || '—' }}</span>
            <el-input v-model="option.selectionAdvice" :disabled="!canManage || !option.materialId" maxlength="500" placeholder="输入建议" :aria-label="option.materialId ? '选型建议' : '新增候选选型建议'" />
            <el-select v-model="option.quantityMode" :disabled="!canManage || !option.materialId"><el-option value="PerMainQuantity" label="按主物料数量" /><el-option value="Fixed" label="固定数量" /></el-select>
            <el-input-number v-model="option.quantityPerSet" :disabled="!canManage || !option.materialId" :min="1" :step="1" :precision="0" controls-position="right" />
            <el-checkbox v-model="option.isDefault" :disabled="!canManage || !option.materialId" aria-label="默认候选" />
            <el-button v-if="canManage && option.materialId" link type="danger" @click="group.options.splice(optionIndex, 1)">移除</el-button>
          </div>
          <div v-if="!canManage && !group.options.length" class="material-relation-option-empty">暂无候选配件</div>
        </div>
      </section>
      <div class="material-relation-actions">
        <el-button v-if="canManage" @click="addGroup">添加配件组</el-button>
        <span />
        <el-button v-if="canManage" type="primary" :loading="saving" @click="save">保存修改</el-button>
        <el-button v-if="canPublish && relation?.draftRevision" type="success" :disabled="!hasCategory || dirty || saving" :title="hasCategory ? '' : '请先由标准化人员补选主料的标准物料分类'" @click="publish">发布生效</el-button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.material-relation-main-add-row :deep(.el-select){width:100%}.material-relation-add-hint{color:var(--pdm-muted);white-space:nowrap}.is-quick-entry{background:var(--pdm-surface-muted)}
.material-relation-summary{grid-template-columns:minmax(0,1fr) 200px!important}.material-relation-summary>label{display:grid;grid-template-columns:80px minmax(0,1fr);align-items:center;gap:8px;padding:8px}.material-relation-main-toolbar{display:flex;align-items:center;gap:12px;margin:12px 0;font-size:12px}.material-relation-main-scroll{max-height:280px;overflow:auto;border:1px solid var(--pdm-border)}.material-relation-main-table{width:100%;border-collapse:collapse;font-size:12px}.material-relation-main-table th,.material-relation-main-table td{padding:8px;border-bottom:1px solid var(--pdm-border);text-align:left;vertical-align:middle}.material-relation-main-table th{background:var(--pdm-surface-muted);white-space:nowrap}.material-relation-main-table :deep(.material-relation-categories){padding:0}.material-relation-main-hint{margin:8px 0;color:var(--pdm-muted);font-size:12px}
.material-relation-editor{min-height:420px;max-height:72vh;overflow:auto;padding:1px 3px 2px}.material-relation-summary{display:grid;grid-template-columns:150px minmax(120px,1fr) minmax(120px,1fr) minmax(100px,.8fr) minmax(140px,1.1fr) 150px;overflow:hidden;border:1px solid var(--pdm-border);border-radius:7px}.material-relation-summary>div{display:grid;min-width:0;border-right:1px solid var(--pdm-border)}.material-relation-summary>div:last-child{border-right:0}.material-relation-summary span{padding:6px 9px;background:var(--pdm-surface-muted);color:var(--pdm-text-soft);font-size:11px;font-weight:600}.material-relation-summary strong{min-width:0;padding:8px 9px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-summary__status .el-tag{justify-self:start;margin:6px 9px}.material-relation-change-note{display:grid;margin-top:12px;grid-template-columns:80px minmax(0,1fr);align-items:center;gap:8px;color:var(--pdm-text-soft)}.material-relation-group{margin-top:12px;padding:12px;border:1px solid var(--pdm-border);border-radius:8px;background:var(--pdm-bg)}.material-relation-group>header{display:grid;grid-template-columns:minmax(260px,1fr) auto 150px auto;align-items:center;gap:10px}.material-relation-group-name{display:grid;min-width:0;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:8px;color:var(--pdm-text-soft);font-size:11px;font-weight:600}.material-relation-group-name em{color:var(--pdm-danger);font-style:normal}.material-relation-option-table{margin:10px 0;overflow:hidden;border:1px solid var(--pdm-border);border-radius:6px}.material-relation-option-head,.material-relation-option-row{display:grid;width:100%;grid-template-columns:minmax(105px,1.1fr) minmax(0,.9fr) minmax(0,.9fr) minmax(0,.75fr) minmax(0,.85fr) minmax(105px,1.2fr) minmax(112px,1fr) 88px 64px 64px;align-items:center}.material-relation-option-head{background:var(--pdm-surface-muted);color:var(--pdm-text-soft);font-size:11px;font-weight:600}.material-relation-option-head span{min-width:0;padding:8px 6px;overflow:hidden;border-right:1px solid var(--pdm-border);text-overflow:ellipsis;white-space:nowrap}.material-relation-option-row{min-height:43px;border-top:1px solid var(--pdm-border-soft)}.material-relation-option-row>span{min-width:0;padding:7px 6px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-option-row>.el-select,.material-relation-option-row>.el-input,.material-relation-option-row>.el-input-number{min-width:0;margin:5px 4px;width:calc(100% - 8px)}.material-relation-option-row>.el-checkbox,.material-relation-option-row>.el-button{justify-self:center}.material-relation-option-empty{padding:20px;color:var(--pdm-muted);text-align:center}.material-relation-actions{display:flex;align-items:center;gap:8px;margin-top:12px}.material-relation-actions>span{flex:1}@media(max-width:1000px){.material-relation-summary{grid-template-columns:1fr 1fr}.material-relation-summary>div{border-bottom:1px solid var(--pdm-border)}.material-relation-group>header{grid-template-columns:1fr}.material-relation-group-name{grid-template-columns:80px minmax(0,1fr)}}
</style>

<style>
.material-relation-material-popper{min-width:760px!important}.material-relation-select-option{display:grid;width:720px;grid-template-columns:155px 180px 180px 1fr;gap:12px}.material-relation-select-option>b,.material-relation-select-option>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-select-option>b{font-weight:600}
</style>
