<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, reactive, ref, watch } from 'vue'
import { listMaterialRelationTemplates, listMaterials, publishMaterialRelationTemplate, saveMaterialRelationTemplate } from '../api'
import type { MaterialRelationQuantityMode, MaterialRelationSelectionMode, MaterialRelationTemplate, PdmMaterial, SaveMaterialRelationTemplateInput } from '../types'

type DraftOption = { materialId: string; quantityMode: MaterialRelationQuantityMode; quantityPerSet: number; isDefault: boolean; sortOrder: number; selectionAdvice: string }
type DraftGroup = { name: string; isRequired: boolean; selectionMode: MaterialRelationSelectionMode; minSelection: number; maxSelection: number | null; autoSelectUnique: boolean; sortOrder: number; options: DraftOption[] }

const props = defineProps<{ token: string; mainMaterial: PdmMaterial; canManage: boolean; canPublish: boolean }>()
const emit = defineEmits<{ changed: [] }>()
const loading = ref(false)
const saving = ref(false)
const configuring = ref(false)
const materialSearchLoading = ref(false)
const relations = ref<MaterialRelationTemplate[]>([])
const materials = ref<PdmMaterial[]>([])
const materialSearchResults = ref<PdmMaterial[]>([])
const draft = reactive<{ changeNote: string; expectedRevisionRowVersion: number | null; groups: DraftGroup[] }>({ changeNote: '', expectedRevisionRowVersion: null, groups: [] })
let materialSearchRequest = 0

const relation = computed(() => relations.value.find(item => item.mainMaterialId === props.mainMaterial.id))
const selectedMaterialIds = computed(() => new Set(draft.groups.flatMap(group => group.options.map(option => option.materialId).filter(Boolean))))
const approvedMaterials = computed(() => {
  const visibleIds = new Set([...materialSearchResults.value.map(item => item.id), ...selectedMaterialIds.value])
  return materials.value.filter(item => visibleIds.has(item.id) && item.approvalStatus === 'Approved' && !item.isArchived && item.id !== props.mainMaterial.id)
})
const materialById = computed(() => new Map(materials.value.map(item => [item.id, item])))
const canConfigure = computed(() => props.canManage && props.mainMaterial.approvalStatus === 'Approved' && !props.mainMaterial.isArchived)
const visibleRevision = computed(() => relation.value?.draftRevision ?? relation.value?.publishedRevision)
const relationStatus = computed(() => {
  if (relation.value?.draftRevision && relation.value.publishedRevision) return `有待生效修改 · V${relation.value.draftRevision.version}`
  if (relation.value?.draftRevision) return `待发布生效 · V${relation.value.draftRevision.version}`
  if (relation.value?.publishedRevision) return `已生效 · V${relation.value.publishedRevision.version}`
  return props.mainMaterial.approvalStatus === 'Approved' ? '未配置' : '料品批准后可配置'
})
const statusType = computed(() => relation.value?.draftRevision ? 'warning' : relation.value?.publishedRevision ? 'success' : 'info')

watch(() => props.mainMaterial.id, () => { void load() }, { immediate: true })

function normalizeSelectionMode(value: unknown): MaterialRelationSelectionMode {
  return value === 'Single' || value === 0 ? 'Single' : 'Multiple'
}

function normalizeQuantityMode(value: unknown): MaterialRelationQuantityMode {
  return value === 'PerMainQuantity' || value === 0 ? 'PerMainQuantity' : 'Fixed'
}

function loadDraft(item?: MaterialRelationTemplate) {
  const revision = item?.draftRevision ?? item?.publishedRevision
  Object.assign(draft, {
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
  configuring.value = Boolean(revision)
}

async function load() {
  loading.value = true
  try {
    const relationRows = await listMaterialRelationTemplates(props.token, props.canManage || props.canPublish)
    const currentRelation = relationRows.find(item => item.mainMaterialId === props.mainMaterial.id)
    const revision = currentRelation?.draftRevision ?? currentRelation?.publishedRevision
    const selectedCodes = [...new Set((revision?.groups ?? []).flatMap(group => group.options.map(option => option.materialCode)).filter(Boolean))]
    const materialBatches = await Promise.all([
      listMaterials(props.token, '', false, 100),
      ...selectedCodes.map(code => listMaterials(props.token, code, false, 20)),
    ])
    const materialRows = [...new Map(materialBatches.flat().map(item => [item.id, item])).values()]
    relations.value = relationRows
    materials.value = materialRows
    materialSearchResults.value = materialBatches[0]
    loadDraft(currentRelation)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '关联物料加载失败')
  } finally {
    loading.value = false
  }
}

async function searchMaterialOptions(query: string) {
  const request = ++materialSearchRequest
  materialSearchLoading.value = true
  try {
    const rows = await listMaterials(props.token, query, false, 100)
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

function addOption(group: DraftGroup) {
  group.options.push({ materialId: '', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: false, sortOrder: group.options.length + 1, selectionAdvice: '' })
}

function normalizeGroup(group: DraftGroup) {
  group.minSelection = group.isRequired ? Math.max(1, group.minSelection || 1) : 0
  if (group.selectionMode === 'Single') group.maxSelection = 1
}

async function save() {
  if (!draft.groups.length || draft.groups.some(group => !group.name.trim() || !group.options.length || group.options.some(option => !option.materialId))) {
    ElMessage.warning('每个配件组都需要名称和至少一个物料选项')
    return
  }
  saving.value = true
  try {
    const input: SaveMaterialRelationTemplateInput = {
      mainMaterialId: props.mainMaterial.id,
      name: `${props.mainMaterial.materialCode}关联物料`,
      changeNote: draft.changeNote.trim(),
      expectedRevisionRowVersion: draft.expectedRevisionRowVersion,
      groups: draft.groups.map((group, groupIndex) => ({
        ...group,
        autoSelectUnique: false,
        sortOrder: groupIndex + 1,
        options: group.options.map((option, optionIndex) => ({ ...option, sortOrder: optionIndex + 1 })),
      })),
    }
    await saveMaterialRelationTemplate(input, props.token, relation.value?.id)
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
    await ElMessageBox.confirm(`发布 ${props.mainMaterial.materialCode} 的关联配置 V${current.draftRevision.version}？发布后工程师将按此版本选配。`, '发布关联配置', { type: 'warning', confirmButtonText: '确认发布' })
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
      <div><span>物料料号</span><strong>{{ mainMaterial.materialCode }}</strong></div>
      <div><span>名称</span><strong>{{ mainMaterial.name || '—' }}</strong></div>
      <div><span>型号</span><strong>{{ mainMaterial.specification || '—' }}</strong></div>
      <div><span>品牌</span><strong>{{ mainMaterial.brand || '—' }}</strong></div>
      <div><span>备注</span><strong>{{ mainMaterial.remark || '—' }}</strong></div>
      <div class="material-relation-summary__status"><span>关联配置</span><el-tag :type="statusType">{{ relationStatus }}</el-tag></div>
    </div>

    <el-empty v-if="!configuring" :description="mainMaterial.approvalStatus === 'Approved' ? '该物料尚未配置关联物料' : '料品批准后方可配置关联物料'">
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
          <div v-for="(option, optionIndex) in group.options" :key="optionIndex" class="material-relation-option-row">
            <el-select v-model="option.materialId" filterable remote :remote-method="searchMaterialOptions" :loading="materialSearchLoading" popper-class="material-relation-material-popper" :disabled="!canManage" placeholder="输入料号或型号搜索">
              <el-option v-for="item in approvedMaterials" :key="item.id" :value="item.id" :label="item.materialCode">
                <span class="material-relation-select-option"><b>{{ item.materialCode }}</b><span>{{ item.name || '—' }}</span><span>{{ item.specification || '—' }}</span><span>{{ item.remark || '—' }}</span></span>
              </el-option>
            </el-select>
            <span :title="relationMaterial(option.materialId)?.name || ''">{{ relationMaterial(option.materialId)?.name || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.specification || ''">{{ relationMaterial(option.materialId)?.specification || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.brand || ''">{{ relationMaterial(option.materialId)?.brand || '—' }}</span>
            <span :title="relationMaterial(option.materialId)?.remark || ''">{{ relationMaterial(option.materialId)?.remark || '—' }}</span>
            <el-input v-model="option.selectionAdvice" :disabled="!canManage" maxlength="500" placeholder="输入建议" aria-label="选型建议" />
            <el-select v-model="option.quantityMode" :disabled="!canManage"><el-option value="PerMainQuantity" label="按主物料数量" /><el-option value="Fixed" label="固定数量" /></el-select>
            <el-input-number v-model="option.quantityPerSet" :disabled="!canManage" :min="1" :step="1" :precision="0" controls-position="right" />
            <el-checkbox v-model="option.isDefault" :disabled="!canManage" aria-label="默认候选" />
            <el-button v-if="canManage" link type="danger" @click="group.options.splice(optionIndex, 1)">移除</el-button>
          </div>
          <div v-if="!group.options.length" class="material-relation-option-empty">暂无候选配件</div>
        </div>
        <el-button v-if="canManage" size="small" @click="addOption(group)">添加候选配件</el-button>
      </section>
      <div class="material-relation-actions">
        <el-button v-if="canManage" @click="addGroup">添加配件组</el-button>
        <span />
        <el-button v-if="canManage" type="primary" :loading="saving" @click="save">保存修改</el-button>
        <el-button v-if="canPublish && relation?.draftRevision" type="success" @click="publish">发布生效</el-button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.material-relation-editor{min-height:420px;max-height:72vh;overflow:auto;padding:1px 3px 2px}.material-relation-summary{display:grid;grid-template-columns:150px minmax(120px,1fr) minmax(120px,1fr) minmax(100px,.8fr) minmax(140px,1.1fr) 150px;overflow:hidden;border:1px solid #dbe4ef;border-radius:7px}.material-relation-summary>div{display:grid;min-width:0;border-right:1px solid #dbe4ef}.material-relation-summary>div:last-child{border-right:0}.material-relation-summary span{padding:6px 9px;background:#f1f5f9;color:#475569;font-size:11px;font-weight:600}.material-relation-summary strong{min-width:0;padding:8px 9px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-summary__status .el-tag{justify-self:start;margin:6px 9px}.material-relation-change-note{display:grid;margin-top:12px;grid-template-columns:80px minmax(0,1fr);align-items:center;gap:8px;color:#475569}.material-relation-group{margin-top:12px;padding:12px;border:1px solid #dbe4ef;border-radius:8px;background:#fafcff}.material-relation-group>header{display:grid;grid-template-columns:minmax(260px,1fr) auto 150px auto;align-items:center;gap:10px}.material-relation-group-name{display:grid;min-width:0;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:8px;color:#475569;font-size:11px;font-weight:600}.material-relation-group-name em{color:#dc2626;font-style:normal}.material-relation-option-table{margin:10px 0;overflow:hidden;border:1px solid #dbe4ef;border-radius:6px}.material-relation-option-head,.material-relation-option-row{display:grid;width:100%;grid-template-columns:minmax(105px,1.1fr) minmax(0,.9fr) minmax(0,.9fr) minmax(0,.75fr) minmax(0,.85fr) minmax(105px,1.2fr) minmax(112px,1fr) 88px 64px 48px;align-items:center}.material-relation-option-head{background:#f1f5f9;color:#475569;font-size:11px;font-weight:600}.material-relation-option-head span{min-width:0;padding:8px 6px;overflow:hidden;border-right:1px solid #dbe4ef;text-overflow:ellipsis;white-space:nowrap}.material-relation-option-row{min-height:43px;border-top:1px solid #e5eaf1}.material-relation-option-row>span{min-width:0;padding:7px 6px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-option-row>.el-select,.material-relation-option-row>.el-input,.material-relation-option-row>.el-input-number{min-width:0;margin:5px 4px;width:calc(100% - 8px)}.material-relation-option-row>.el-checkbox,.material-relation-option-row>.el-button{justify-self:center}.material-relation-option-empty{padding:20px;color:#94a3b8;text-align:center}.material-relation-actions{display:flex;align-items:center;gap:8px;margin-top:12px}.material-relation-actions>span{flex:1}@media(max-width:1000px){.material-relation-summary{grid-template-columns:1fr 1fr}.material-relation-summary>div{border-bottom:1px solid #dbe4ef}.material-relation-group>header{grid-template-columns:1fr}.material-relation-group-name{grid-template-columns:80px minmax(0,1fr)}}
</style>

<style>
.material-relation-material-popper{min-width:760px!important}.material-relation-select-option{display:grid;width:720px;grid-template-columns:155px 180px 180px 1fr;gap:12px}.material-relation-select-option>b,.material-relation-select-option>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-relation-select-option>b{font-weight:600}
</style>
