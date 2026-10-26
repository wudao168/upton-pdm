<script setup lang="ts">
import type { MaterialAttachment, MaterialAttachmentKind, MaterialCategory, PdmMaterial, SaveMaterialInput } from '../types'
import { u9UnitOptions } from '../u9Units'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { deleteMaterialAttachment, materialAttachmentObjectUrl } from '../api'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import MaterialRelationEditor from './MaterialRelationEditor.vue'
import MaterialImagePreview from './MaterialImagePreview.vue'
import MaterialImageAdd from './MaterialImageAdd.vue'
import { materialImageAccept, materialImageLimit } from '../materialImages'

const props = withDefaults(defineProps<{
  token: string
  kitMode?: boolean
  modelValue: boolean
  title?: string
  editingId?: string | null
  form: SaveMaterialInput
  categories: MaterialCategory[]
  brands?: string[]
  materialCodePlaceholder: string
  attachments: MaterialAttachment[]
  saving: boolean
  errorMessage?: string
  uploadingKind?: MaterialAttachmentKind | null
  uploadProgress?: number
  coverUrl?: string
  coverAttachmentId?: string | null
  changingCover?: boolean
  u9FieldsLocked?: boolean
  u9ReviewRequired?: boolean
  canEdit?: boolean
  canDeleteAttachments?: boolean
  mainMaterial?: PdmMaterial | null
  initialTab?: 'material' | 'relations'
  canViewRelations?: boolean
  canManageRelations?: boolean
  canPublishRelations?: boolean
}>(), {
  canEdit: true,
  mainMaterial: null,
  initialTab: 'material',
  canViewRelations: false,
  canManageRelations: false,
  canPublishRelations: false,
})
const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  categoryChange: [code: string]
  attachmentFiles: [kind: MaterialAttachmentKind, event: Event]
  downloadAttachment: [attachment: MaterialAttachment]
  attachmentDeleted: [attachment: MaterialAttachment]
  clearCover: []
  setCover: [attachment: MaterialAttachment]
  save: []
  relationsChanged: []
}>()

const activePane = ref<'material' | 'relations'>('material')
const relationPaneOpened = ref(false)
const relationTabVisible = computed(() => Boolean(props.editingId && props.mainMaterial && props.canViewRelations))
watch([() => props.modelValue, () => props.editingId, () => props.initialTab], ([visible]) => {
  if (!visible) return
  relationPaneOpened.value = false
  activePane.value = props.initialTab === 'relations' && relationTabVisible.value ? 'relations' : 'material'
  if (activePane.value === 'relations') relationPaneOpened.value = true
})
watch(activePane, value => { if (value === 'relations') relationPaneOpened.value = true })

const model3DAccept = '.sldprt,.sldasm,.step,.stp,.igs,.iges,.x_t,.x_b,.sat'
const documentAccept = '.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png,.zip,.rar,.7z'
const attachmentItems = (kind: MaterialAttachmentKind) => props.attachments.filter(item => item.kind === kind)
const images = computed(() => attachmentItems('CoverImage'))
const deletingAttachmentId = ref<string | null>(null)
const attachmentBusy = computed(() => props.saving || props.uploadingKind != null || props.changingCover || deletingAttachmentId.value !== null)
const imageError = ref('')
const imageUrls = reactive<Record<string, string>>({})
let imageRequest = 0
function clearImageUrls() {
  ++imageRequest
  for (const [id, url] of Object.entries(imageUrls)) {
    URL.revokeObjectURL(url)
    delete imageUrls[id]
  }
}
watch([() => props.modelValue, () => props.editingId, () => props.token, () => images.value.map(item => item.id).join(',')], async ([visible]) => {
  clearImageUrls()
  imageError.value = ''
  if (!visible || !props.editingId) return
  const request = imageRequest
  const materialId = props.editingId
  await Promise.all(images.value.map(async image => {
    try {
      const url = await materialAttachmentObjectUrl(materialId, image.id, props.token, 120)
      if (request !== imageRequest) URL.revokeObjectURL(url)
      else imageUrls[image.id] = url
    } catch { /* The original image remains available in the preview. */ }
  }))
}, { immediate: true })
onBeforeUnmount(clearImageUrls)

function selectImages(event: Event) {
  const input = event.target as HTMLInputElement
  const count = input.files?.length ?? 0
  if (images.value.length + count > materialImageLimit) {
    imageError.value = `每个${props.kitMode ? '套件' : '料品'}最多上传${materialImageLimit}张图片，当前已有${images.value.length}张，本次选择${count}张。`
    input.value = ''
    return
  }
  imageError.value = ''
  emit('attachmentFiles', 'CoverImage', event)
}
const fileKinds = ['Model3D', 'Document'] as const
const coverInput = ref<HTMLInputElement | null>(null)
const imagePreviewOpen = ref(false)
const previewImageId = ref<string>()
watch(() => props.modelValue, visible => { if (!visible) imagePreviewOpen.value = false })
function previewImage(id?: string) {
  previewImageId.value = id
  imagePreviewOpen.value = true
}
const model3DInput = ref<HTMLInputElement | null>(null)
const documentInput = ref<HTMLInputElement | null>(null)
async function removeAttachment(attachment: MaterialAttachment) {
  if (!props.canEdit || !props.canDeleteAttachments || !props.editingId || attachmentBusy.value) return
  deletingAttachmentId.value = attachment.id
  try {
    try {
      await ElMessageBox.confirm(`确定删除附件“${attachment.originalFileName}”？删除后将从当前料品的附件列表中移除。`, '删除附件', {
        type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消',
      })
    } catch { return }
    await deleteMaterialAttachment(props.editingId, attachment.id, props.token)
    emit('attachmentDeleted', attachment)
    ElMessage.success('附件已删除')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '附件删除失败，请刷新后重试。') }
  finally { deletingAttachmentId.value = null }
}

function chooseAttachment(kind: MaterialAttachmentKind) {
  const input = kind === 'CoverImage' ? coverInput.value : kind === 'Model3D' ? model3DInput.value : documentInput.value
  input?.click()
}
</script>

<template>
  <el-dialog :model-value="modelValue" class="material-editor-dialog" :title="title || (kitMode ? '套件资料维护' : editingId ? '料品明细' : '新增料品草稿')" :width="activePane === 'relations' ? 'min(1480px, 96vw)' : kitMode ? 'min(688px,96vw)' : '688px'" :close-on-click-modal="!attachmentBusy" :close-on-press-escape="!attachmentBusy" :show-close="!attachmentBusy" top="3vh" @update:model-value="emit('update:modelValue', $event)">
    <el-tabs v-if="editingId && !kitMode" v-model="activePane" class="material-editor-tabs">
      <el-tab-pane label="料品信息" name="material" />
      <el-tab-pane v-if="relationTabVisible" label="关联物料" name="relations" />
    </el-tabs>
    <div v-show="activePane === 'material'">
      <slot name="intro" />
      <slot name="images">
      <section class="material-editor-images" aria-label="料品图片">
        <header><strong>图片（{{ images.length }}/{{ materialImageLimit }}）</strong><span v-if="uploadingKind === 'CoverImage'">上传中 {{ uploadProgress }}%</span><el-button v-if="coverAttachmentId || coverUrl" link :disabled="!canEdit || attachmentBusy" @click="emit('clearCover')">清除当前封面</el-button></header>
        <input ref="coverInput" class="material-attachment-input" type="file" multiple :disabled="!canEdit || !editingId || attachmentBusy" :accept="materialImageAccept" :aria-label="kitMode ? '选择套件图片' : '选择料品图片'" @change="selectImages" />
        <div class="material-image-cards">
          <article v-for="image in images" :key="image.id" class="material-image-card" :class="{ 'is-cover': image.id === coverAttachmentId }">
            <button class="material-image-card__preview" type="button" :aria-label="`预览 ${image.originalFileName}`" @click="previewImage(image.id)"><img v-if="imageUrls[image.id]" :src="imageUrls[image.id]" :alt="image.originalFileName" /><span v-else>查看图片</span></button>
            <span class="material-image-card__name" :title="image.originalFileName">{{ image.originalFileName }}</span>
            <span v-if="image.id === coverAttachmentId" class="material-image-card__cover">封面</span><el-button v-else link type="primary" :disabled="!canEdit || attachmentBusy" :aria-label="`将 ${image.originalFileName} 设为封面`" @click="emit('setCover', image)">设为封面</el-button>
          </article>
          <MaterialImageAdd v-if="images.length < materialImageLimit" :disabled="!canEdit || !editingId || attachmentBusy" :label="editingId ? '添加图片' : '保存后上传'" @click="chooseAttachment('CoverImage')" />
        </div>
        <p v-if="images.length > materialImageLimit" class="material-attachment-help">已有{{ images.length }}张历史图片，继续保留；已达到上传上限。</p><p v-else class="material-attachment-help">最多{{ materialImageLimit }}张，点击图片可预览。</p>
        <p v-if="imageError" role="alert" class="material-image-validation">{{ imageError }}</p>
      </section>
      </slot>
    <el-alert v-if="u9ReviewRequired" type="warning" :closable="false" title="U9C同步待复核，可修正PLM字段后重新复核。系统保留同一料号，不会重复创建或覆盖U9C已有料品。" />
    <el-alert v-if="u9FieldsLocked" type="warning" :closable="false" title="U9C任务结果尚未确认，名称、分类、单位、规格等U9字段暂不可修改；推荐、选型建议、参考价格、封面和附件仍可维护。" />
    <fieldset class="material-editor-fieldset" :disabled="!canEdit">
    <el-form label-position="top">
      <div class="material-editor-grid">
        <slot name="fields">
        <el-form-item :label="kitMode ? '料号' : 'PLM物料编码'"><el-input v-stocked-material="form.materialCode" v-model="form.materialCode" disabled :placeholder="materialCodePlaceholder" /></el-form-item>
        <el-form-item :label="kitMode ? '名称' : '物料名称'" :required="!kitMode"><el-input v-model="form.name" :disabled="kitMode || u9FieldsLocked" /></el-form-item>
        <el-form-item v-if="!kitMode" label="U9C对应分类" required><el-select v-model="form.categoryCode" :disabled="u9FieldsLocked" filterable placeholder="请选择U9C对应分类" @change="emit('categoryChange', String($event))"><el-option v-for="category in categories" :key="category.code" :label="`${category.code} ${category.name}`" :value="category.code" /></el-select></el-form-item>
        <el-form-item v-if="!kitMode" label="PLM业务类型"><el-select v-model="form.kind" disabled><el-option label="电气件" value="Electrical" /><el-option label="机械外购件" value="Standard" /><el-option label="非标机加件" value="NonStandard" /><el-option label="产品/组件" value="Product" /></el-select></el-form-item>
        <el-form-item v-if="!kitMode" label="计量单位" required><el-select v-model="form.unitCode" :disabled="u9FieldsLocked" filterable placeholder="请选择U9C计量单位"><el-option v-for="unit in u9UnitOptions" :key="unit.code" :label="`${unit.code} ${unit.name}`" :value="unit.code" /></el-select></el-form-item>
        <el-form-item :label="kitMode ? '型号' : '规格/型号'"><el-input v-model="form.specification" :disabled="kitMode || u9FieldsLocked" /></el-form-item>
        <el-form-item v-if="!kitMode" label="材质"><el-input v-model="form.material" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="品牌"><el-input v-if="kitMode" v-model="form.brand" disabled /><el-select v-else v-model="form.brand" :disabled="u9FieldsLocked" filterable allow-create default-first-option clearable placeholder="可输入或选择品牌"><el-option v-for="brand in brands" :key="brand" :label="brand" :value="brand" /></el-select></el-form-item>
        <el-form-item v-if="!kitMode" label="表面处理"><el-input v-model="form.surfaceTreatment" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item v-if="!kitMode" label="重量"><el-input-number v-model="form.weight" :disabled="u9FieldsLocked" :min="0" :precision="6" /></el-form-item>
        <el-form-item label="参考价格"><el-input-number v-model="form.referencePrice" :min="0" :precision="2" :step="10" controls-position="right" /></el-form-item>
        <el-form-item label="推荐属性"><el-button class="material-recommend-button" :type="form.isRecommended ? 'warning' : ''" :aria-pressed="form.isRecommended" @click="form.isRecommended = !form.isRecommended">{{ form.isRecommended ? '已推荐' : '推荐' }}</el-button></el-form-item>
        <el-form-item v-if="!kitMode" label="料品采购链接"><el-input v-model="form.purchaseLink" :disabled="u9FieldsLocked" type="url" placeholder="https://..." /></el-form-item>
        <el-form-item class="material-editor-grid__wide" label="备注"><el-input v-model="form.remark" :disabled="u9FieldsLocked" :maxlength="kitMode ? 500 : undefined" /></el-form-item>
        <el-form-item label="选型建议"><el-input v-model="form.selectionAdvice" maxlength="1000" show-word-limit /></el-form-item>
        </slot>
      </div>
    </el-form>
    </fieldset>
    <slot name="assets"><section class="material-editor-assets" aria-label="料品附件">
      <input ref="model3DInput" class="material-attachment-input" type="file" multiple :disabled="!canEdit || !editingId || attachmentBusy" :accept="model3DAccept" :aria-label="kitMode ? '上传套件3D附件' : '选择3D附件'" @change="emit('attachmentFiles', 'Model3D', $event)" />
      <input ref="documentInput" class="material-attachment-input" type="file" multiple :disabled="!canEdit || !editingId || attachmentBusy" :accept="documentAccept" :aria-label="kitMode ? '上传套件资料附件' : '选择资料附件'" @change="emit('attachmentFiles', 'Document', $event)" />
      <section v-for="kind in fileKinds" :key="kind" class="material-editor-file-list" :aria-label="`${kind === 'Model3D' ? '3D' : '资料'}附件`">
        <header><strong>{{ kind === 'Model3D' ? '3D' : '资料' }}（{{ attachmentItems(kind).length }}）</strong><span v-if="uploadingKind === kind">上传中 {{ uploadProgress }}%</span><el-button :disabled="!canEdit || !editingId || attachmentBusy" :loading="uploadingKind === kind" @click="chooseAttachment(kind)">{{ editingId ? '上传附件' : '保存后上传' }}</el-button></header>
        <table><thead><tr><th>文件名</th><th>操作</th></tr></thead><tbody><tr v-for="attachment in attachmentItems(kind)" :key="attachment.id"><td :title="attachment.originalFileName">{{ attachment.originalFileName }}</td><td><el-button link type="primary" :aria-label="`下载 ${attachment.originalFileName}`" @click="emit('downloadAttachment', attachment)">下载</el-button><el-button v-if="canEdit && canDeleteAttachments" link type="danger" :disabled="attachmentBusy" :loading="deletingAttachmentId === attachment.id" :aria-label="`删除 ${attachment.originalFileName}`" @click="removeAttachment(attachment)">删除</el-button></td></tr><tr v-if="!attachmentItems(kind).length"><td colspan="2" class="material-attachment-empty">暂无{{ kind === 'Model3D' ? '3D' : '资料' }}附件</td></tr></tbody></table>
      </section>

    </section></slot>
    </div>
    <MaterialRelationEditor v-if="relationPaneOpened && mainMaterial" v-show="activePane === 'relations'" :token="token" :main-material="mainMaterial" :can-manage="canManageRelations" :can-publish="canPublishRelations" @changed="emit('relationsChanged')" />
    <template #footer>
      <slot name="footer">
      <el-alert v-if="errorMessage && activePane === 'material'" class="material-editor-error" type="error" :closable="false" show-icon :title="errorMessage" />
      <slot v-if="activePane === 'material'" name="actions" />
      <el-button :disabled="attachmentBusy" @click="emit('update:modelValue', false)">{{ activePane === 'relations' ? '关闭' : '取消' }}</el-button><el-button v-if="activePane === 'material' && canEdit" type="primary" :disabled="uploadingKind != null || changingCover || deletingAttachmentId !== null" :loading="saving" @click="emit('save')">{{ editingId ? '保存修改' : '保存草稿' }}</el-button>
      </slot>
    </template>
  </el-dialog>
  <MaterialImagePreview v-model="imagePreviewOpen" :material-id="editingId ?? ''" :token="token" :initial-id="previewImageId" :attachments="attachments" />
</template>

<style scoped>
.material-editor-images{margin-bottom:12px}.material-editor-assets{display:flex;flex-direction:column;gap:12px;margin-top:12px}.material-editor-assets section{min-width:0}.material-editor-images header,.material-editor-assets header{display:flex;align-items:center;gap:8px;margin-bottom:8px}.material-editor-images header strong,.material-editor-assets header strong{flex:1}.material-editor-images header>span,.material-editor-assets header>span,.material-attachment-help{font-size:12px;color:var(--pdm-muted)}.material-image-cards{display:flex;flex-wrap:wrap;gap:8px}.material-image-card{box-sizing:border-box;display:flex;width:112px;align-items:center;flex-direction:column;border:1px solid var(--pdm-border);border-radius:4px;padding:4px;gap:3px;overflow:hidden}.material-image-card.is-cover{border-color:var(--pdm-theme-accent)}.material-image-card__preview{display:flex;align-items:center;justify-content:center;width:102px;height:82px;padding:0;border:0;background:var(--pdm-surface-muted);color:var(--pdm-muted);cursor:zoom-in}.material-image-card__preview img{width:100%;height:100%;object-fit:contain}.material-image-card__name{width:100%;overflow:hidden;white-space:nowrap;text-overflow:ellipsis;font-size:11px;text-align:center}.material-image-card__cover{display:flex;align-items:center;justify-content:center;height:24px;color:var(--pdm-theme-accent);font-size:12px}.material-image-card :deep(.el-button){height:24px;min-height:0}.material-attachment-help{margin:6px 0 0}.material-image-validation{color:var(--pdm-danger);font-size:12px}.material-editor-file-list table{width:100%;table-layout:fixed;border-collapse:collapse;font-size:12px}.material-editor-file-list th,.material-editor-file-list td{height:30px;padding:0 8px;border-bottom:1px solid var(--pdm-border-soft);text-align:left;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}.material-editor-file-list th{background:var(--pdm-table-header)}.material-editor-file-list th:last-child,.material-editor-file-list td:last-child{width:128px;text-align:center}.material-attachment-empty{color:var(--pdm-muted)}
.material-editor-error{margin-bottom:10px;text-align:left;overflow-wrap:anywhere;white-space:normal}
.material-editor-tabs{margin-top:-10px}.material-editor-fieldset{min-width:0;margin:0;padding:0;border:0}.material-editor-grid{display:grid;grid-template-columns:repeat(3,minmax(0,200px));gap:0 12px}.material-editor-grid :deep(.el-form-item){margin-bottom:10px}.material-editor-grid__wide{grid-column:span 2}.material-recommend-button{width:100%}.material-attachment-input{position:absolute;width:1px;height:1px;opacity:0}@media(max-width:760px){.material-editor-grid{grid-template-columns:1fr}.material-editor-grid__wide{grid-column:auto}}
</style>
