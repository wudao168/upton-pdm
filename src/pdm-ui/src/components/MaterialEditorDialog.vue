<script setup lang="ts">
import type { MaterialAttachment, MaterialAttachmentKind, MaterialCategory, SaveMaterialInput } from '../types'
import { u9UnitOptions } from '../u9Units'
import { ref } from 'vue'

const props = defineProps<{
  modelValue: boolean
  editingId?: string | null
  form: SaveMaterialInput
  categories: MaterialCategory[]
  materialCodePlaceholder: string
  attachments: MaterialAttachment[]
  saving: boolean
  uploadingKind?: MaterialAttachmentKind | null
  uploadProgress?: number
  coverUrl?: string
  u9FieldsLocked?: boolean
}>()
const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  categoryChange: [code: string]
  attachmentFiles: [kind: MaterialAttachmentKind, event: Event]
  downloadAttachment: [attachment: MaterialAttachment]
  clearCover: []
  save: []
}>()

const model3DAccept = '.sldprt,.sldasm,.step,.stp,.igs,.iges,.x_t,.x_b,.sat'
const documentAccept = '.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png,.zip,.rar,.7z'
const coverAccept = '.jpg,.jpeg,.png,.webp'
const attachmentItems = (kind: MaterialAttachmentKind) => props.attachments.filter(item => item.kind === kind)
const coverInput = ref<HTMLInputElement | null>(null)
const model3DInput = ref<HTMLInputElement | null>(null)
const documentInput = ref<HTMLInputElement | null>(null)
function chooseAttachment(kind: MaterialAttachmentKind) {
  const input = kind === 'CoverImage' ? coverInput.value : kind === 'Model3D' ? model3DInput.value : documentInput.value
  input?.click()
}
</script>

<template>
  <el-dialog :model-value="modelValue" class="material-editor-dialog" :title="editingId ? '编辑或变更料品' : '新增料品草稿'" width="688px" @update:model-value="emit('update:modelValue', $event)">
    <el-alert v-if="u9FieldsLocked" type="warning" :closable="false" title="U9C任务结果尚未确认，名称、分类、单位、规格等U9字段暂不可修改；推荐、选型建议、参考价格、封面和附件仍可维护。" />
    <el-form label-position="top">
      <div class="material-editor-grid">
        <el-form-item label="PLM物料编码"><el-input v-model="form.materialCode" disabled :placeholder="materialCodePlaceholder" /><p class="field-help">保存时从PLM分类当前基线向后预留编号；U9C在后台同步，极少数重复号会自动校准并换号。</p></el-form-item>
        <el-form-item label="物料名称" required><el-input v-model="form.name" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="U9C对应分类" required><el-select v-model="form.categoryCode" :disabled="u9FieldsLocked" filterable placeholder="请选择U9C对应分类" @change="emit('categoryChange', String($event))"><el-option v-for="category in categories" :key="category.code" :label="`${category.code} ${category.name}`" :value="category.code" /></el-select></el-form-item>
        <el-form-item label="PLM业务类型"><el-select v-model="form.kind" disabled><el-option label="电气件" value="Electrical" /><el-option label="机械外购件" value="Standard" /><el-option label="非标机加件" value="NonStandard" /><el-option label="产品/组件" value="Product" /></el-select></el-form-item>
        <el-form-item label="计量单位" required><el-select v-model="form.unitCode" :disabled="u9FieldsLocked" filterable placeholder="请选择U9C计量单位"><el-option v-for="unit in u9UnitOptions" :key="unit.code" :label="`${unit.code} ${unit.name}`" :value="unit.code" /></el-select></el-form-item>
        <el-form-item label="规格/型号"><el-input v-model="form.specification" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="材质"><el-input v-model="form.material" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="品牌"><el-input v-model="form.brand" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="表面处理"><el-input v-model="form.surfaceTreatment" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="重量"><el-input-number v-model="form.weight" :disabled="u9FieldsLocked" :min="0" :precision="6" /></el-form-item>
        <el-form-item label="参考价格"><el-input-number v-model="form.referencePrice" :min="0" :precision="2" :step="10" controls-position="right" /></el-form-item>
        <el-form-item label="推荐属性"><el-button class="material-recommend-button" :type="form.isRecommended ? 'warning' : ''" :aria-pressed="form.isRecommended" @click="form.isRecommended = !form.isRecommended">{{ form.isRecommended ? '已推荐' : '推荐' }}</el-button></el-form-item>
        <el-form-item label="料品采购链接"><el-input v-model="form.purchaseLink" :disabled="u9FieldsLocked" type="url" placeholder="https://..." /></el-form-item>
        <el-form-item label="封面图片">
          <div class="material-attachment-field">
            <img v-if="coverUrl" class="material-cover-preview" :src="coverUrl" alt="当前封面" />
            <input ref="coverInput" class="material-attachment-input" type="file" :accept="coverAccept" @change="emit('attachmentFiles', 'CoverImage', $event)" />
            <el-button :disabled="!editingId || uploadingKind !== null" :loading="uploadingKind === 'CoverImage'" @click="chooseAttachment('CoverImage')">{{ editingId ? '上传/替换' : '保存后上传' }}</el-button>
            <el-button v-if="form && coverUrl" link type="danger" @click="emit('clearCover')">清除当前封面</el-button>
          </div>
        </el-form-item>
        <el-form-item label="3D"><div class="material-attachment-field"><input ref="model3DInput" class="material-attachment-input" type="file" multiple :accept="model3DAccept" @change="emit('attachmentFiles', 'Model3D', $event)" /><el-button :disabled="!editingId || uploadingKind !== null" :loading="uploadingKind === 'Model3D'" @click="chooseAttachment('Model3D')">{{ editingId ? '上传附件' : '保存后上传' }}</el-button><span v-if="uploadingKind === 'Model3D'">{{ uploadProgress }}%</span><div class="material-attachment-list"><el-button v-for="attachment in attachmentItems('Model3D')" :key="attachment.id" link type="primary" :title="attachment.originalFileName" @click="emit('downloadAttachment', attachment)">{{ attachment.originalFileName }}</el-button></div></div></el-form-item>
        <el-form-item label="资料"><div class="material-attachment-field"><input ref="documentInput" class="material-attachment-input" type="file" multiple :accept="documentAccept" @change="emit('attachmentFiles', 'Document', $event)" /><el-button :disabled="!editingId || uploadingKind !== null" :loading="uploadingKind === 'Document'" @click="chooseAttachment('Document')">{{ editingId ? '上传附件' : '保存后上传' }}</el-button><span v-if="uploadingKind === 'Document'">{{ uploadProgress }}%</span><div class="material-attachment-list"><el-button v-for="attachment in attachmentItems('Document')" :key="attachment.id" link type="primary" :title="attachment.originalFileName" @click="emit('downloadAttachment', attachment)">{{ attachment.originalFileName }}</el-button></div></div></el-form-item>
        <el-form-item class="material-editor-grid__wide" label="备注"><el-input v-model="form.remark" :disabled="u9FieldsLocked" /></el-form-item>
        <el-form-item label="选型建议"><el-input v-model="form.selectionAdvice" maxlength="1000" show-word-limit /></el-form-item>
      </div>
    </el-form>
    <template #footer><el-button @click="emit('update:modelValue', false)">取消</el-button><el-button type="primary" :loading="saving" @click="emit('save')">{{ editingId ? '保存修改' : '保存草稿' }}</el-button></template>
  </el-dialog>
</template>

<style scoped>
.material-editor-grid{display:grid;grid-template-columns:repeat(3,minmax(0,200px));gap:0 12px}.material-editor-grid :deep(.el-form-item){margin-bottom:10px}.material-editor-grid__wide{grid-column:span 2}.material-recommend-button{width:100%}.material-attachment-field{display:flex;min-width:0;width:100%;align-items:center;flex-wrap:wrap;gap:4px}.material-attachment-input{position:absolute;width:1px;height:1px;opacity:0}.material-attachment-list{display:flex;max-height:44px;min-width:0;width:100%;overflow:auto;align-items:flex-start;flex-direction:column}.material-cover-preview{width:48px;height:48px;border:1px solid #dbe3ee;border-radius:4px;object-fit:cover}@media(max-width:760px){.material-editor-grid{grid-template-columns:1fr}.material-editor-grid__wide{grid-column:auto}}
</style>
