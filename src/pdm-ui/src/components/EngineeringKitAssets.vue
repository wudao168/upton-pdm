<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import type { EngineeringKit, MaterialAttachment, MaterialAttachmentKind, SaveMaterialInput } from '../types'
import { downloadMaterialAttachment, listMaterialAttachments, saveEngineeringKitLibraryMetadata, uploadMaterialAttachment } from '../api'
import { userErrorMessage } from '../userErrorMessage'
import { ElMessage } from '../statusMessage'
import EngineeringKitActivityButton from './EngineeringKitActivityButton.vue'
import MaterialEditorDialog from './MaterialEditorDialog.vue'
import { materialImageLimit } from '../materialImages'

const props = defineProps<{ kit: EngineeringKit; token: string; canEdit: boolean; canManage: boolean }>()
const emit = defineEmits<{ close: []; changed: []; details: []; category: []; activityChanged: [kit: EngineeringKit] }>()
const currentKit = ref(props.kit)
const form = reactive<SaveMaterialInput>({ materialCode: props.kit.code ?? '', name: props.kit.name, kind: 'Standard', supplyMode: 'Purchase',
  unitCode: '001', specification: props.kit.model, brand: props.kit.brand, remark: props.kit.description,
  selectionAdvice: props.kit.selectionAdvice, referencePrice: props.kit.referencePrice, isRecommended: props.kit.isRecommended ?? false })
const attachments = ref<MaterialAttachment[]>([]), loading = ref(false), saving = ref(false), error = ref('')
const uploading = ref<MaterialAttachmentKind | null>(null), progress = ref(0), changingCover = ref(false)
const editable = computed(() => props.canEdit && currentKit.value.isActive !== false)
const busy = computed(() => loading.value || saving.value || !!uploading.value || changingCover.value)

async function load() {
  loading.value = true
  try { attachments.value = await listMaterialAttachments(currentKit.value.id, props.token) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '套件附件读取失败，请刷新重试。') }
  finally { loading.value = false }
}
async function persistMetadata(fromDraft: boolean, coverId: string | null | undefined = currentKit.value.coverImageAttachmentId) {
  const kit = currentKit.value
  const saved = await saveEngineeringKitLibraryMetadata(kit.id, {
    remark: fromDraft ? form.remark : kit.description, selectionAdvice: fromDraft ? form.selectionAdvice : kit.selectionAdvice,
    referencePrice: fromDraft ? form.referencePrice : kit.referencePrice, isRecommended: fromDraft ? !!form.isRecommended : !!kit.isRecommended,
    coverImageAttachmentId: coverId ?? null, expectedRowVersion: kit.rowVersion,
  }, props.token)
  currentKit.value = saved
}
async function save() {
  if (!editable.value || busy.value) return
  saving.value = true; error.value = ''
  try { await persistMetadata(true); emit('changed'); ElMessage.success('套件资料已保存'); emit('close') }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '套件资料保存失败，请重试。') }
  finally { saving.value = false }
}
async function upload(kind: MaterialAttachmentKind, event: Event) {
  const input = event.target as HTMLInputElement, files = [...(input.files ?? [])]; input.value = ''
  if (!editable.value || busy.value || !files.length) return
  if (kind === 'CoverImage' && attachments.value.filter(file => file.kind === kind).length + files.length > materialImageLimit) {
    error.value = `每个套件最多上传${materialImageLimit}张图片，请减少本次选择的图片数量。`; return
  }
  uploading.value = kind; error.value = ''; let completed = false
  try {
    for (const file of files) {
      progress.value = 0
      const attachment = await uploadMaterialAttachment(currentKit.value.id, kind, file, props.token, value => { progress.value = value })
      completed = true
      if (kind === 'CoverImage' && !currentKit.value.coverImageAttachmentId) await persistMetadata(false, attachment.id)
    }
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '套件附件上传失败，请重试。') }
  finally { uploading.value = null; progress.value = 0; if (completed) { await load(); emit('changed') } }
}
async function changeCover(attachment: MaterialAttachment | null) {
  if (!editable.value || busy.value) return
  changingCover.value = true; error.value = ''
  try { await persistMetadata(false, attachment?.id ?? null); emit('changed'); ElMessage.success(attachment ? '已设为封面' : '当前封面已清除，历史图片仍保留') }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '套件封面设置失败，请重试。') }
  finally { changingCover.value = false }
}
async function download(attachment: MaterialAttachment) {
  error.value = ''
  try { await downloadMaterialAttachment(currentKit.value.id, attachment, props.token) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '套件附件下载失败，请重试。') }
}
function attachmentDeleted(attachment: MaterialAttachment) {
  attachments.value = attachments.value.filter(item => item.id !== attachment.id)
  emit('changed')
}
function activityChanged(kit: EngineeringKit) { currentKit.value = kit; emit('activityChanged', kit) }
onMounted(load)
</script>

<template>
  <MaterialEditorDialog :model-value="true" kit-mode :token="token" :editing-id="currentKit.id" :form="form" :categories="[]" material-code-placeholder=""
    :attachments="attachments" :saving="saving || loading" :error-message="error" :uploading-kind="uploading" :upload-progress="progress"
    :cover-attachment-id="currentKit.coverImageAttachmentId" :changing-cover="changingCover" :can-edit="editable && !loading" :can-delete-attachments="editable && !loading" @attachment-deleted="attachmentDeleted"
    @update:model-value="emit('close')" @save="save" @attachment-files="upload" @download-attachment="download" @clear-cover="changeCover(null)" @set-cover="changeCover">
    <template #actions>
      <el-tag v-if="currentKit.isActive === false" type="info">已停用</el-tag>
      <el-button :disabled="busy" @click="emit('details')">组成明细</el-button>
      <el-button v-if="canManage" :disabled="busy" @click="emit('category')">调整分类</el-button>
      <EngineeringKitActivityButton :kit="currentKit" :token="token" :can-manage="canManage" :disabled="busy" @changed="activityChanged" />
    </template>
  </MaterialEditorDialog>
</template>
