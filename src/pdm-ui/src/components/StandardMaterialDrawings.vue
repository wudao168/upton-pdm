<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { downloadMaterialAttachment, enableMaterialDrawing, listMaterialAttachments, materialAttachmentObjectUrl, uploadMaterialAttachment } from '../api'
import type { MaterialAttachment } from '../types'
import { fileVersion } from '../bomFiles'
const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ materialId: string; materialCode: string; kind: 'Drawing2D' | 'Model3D'; token: string; canManage: boolean }>()
const emit = defineEmits<{ close: []; changed: [] }>()
const files = ref<MaterialAttachment[]>([])
const busy = ref(false)
const progress = ref(0)
const preview = ref('')
const label = computed(() => props.kind === 'Drawing2D' ? '2D图纸' : '3D模型')
const enabled = computed(() => files.value.find(file => file.isEnabled))
async function load() { files.value = await listMaterialAttachments(props.materialId, props.token, props.kind) }
onMounted(() => load().catch(() => ElMessage.error('图纸版本读取失败，请刷新后重试。')))
onBeforeUnmount(() => { if (preview.value) URL.revokeObjectURL(preview.value) })
async function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; input.value = ''
  if (!file) return
  busy.value = true; progress.value = 0
  try {
    const saved = await uploadMaterialAttachment(props.materialId, props.kind, file, props.token, value => progress.value = value)
    await load(); emit('changed')
    ElMessage.success(`已上传${fileVersion(saved.versionNumber)}，请核对后选择启用版本。`)
  } catch { ElMessage.error('图纸上传失败，请检查文件格式和维护权限后重试。') }
  finally { busy.value = false }
}
async function enable(file: MaterialAttachment) {
  try {
    await ElMessageBox.confirm(`将${label.value}启用版本从${enabled.value ? fileVersion(enabled.value.versionNumber) : '未启用'}切换为${fileVersion(file.versionNumber)}？请核对2D与3D是否对应。已有项目引用及已发布BOM保持不变。`, '选择启用版本', { confirmButtonText: '确认启用', cancelButtonText: '取消' })
    busy.value = true
    await enableMaterialDrawing(props.materialId, file.id, enabled.value?.id ?? null, props.token)
    await load(); emit('changed'); ElMessage.success('启用版本已更新')
  } catch (error) { if (error !== 'cancel' && error !== 'close') { ElMessage.error('启用失败，版本可能已变化或当前账号无维护权限，请刷新核对后重试。'); await load() } }
  finally { busy.value = false }
}
async function view(file: MaterialAttachment) {
  try {
    if (preview.value) URL.revokeObjectURL(preview.value)
    preview.value = await materialAttachmentObjectUrl(props.materialId, file.id, props.token)
  } catch { ElMessage.error('图纸预览失败，请重试或下载查看。') }
}
</script>
<template>
  <el-dialog :model-value="true" :title="`${materialCode} · ${label}版本`" width="min(1050px, 96vw)" :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy" @close="emit('close')">
    <p>当前启用：<strong>{{ enabled ? fileVersion(enabled.versionNumber) : '未启用' }}</strong>。上传自动升版；由标准化人员选择启用版本，全部历史文件保留。</p>
    <label v-if="canManage" class="drawing-upload">上传新版本 <input type="file" :disabled="busy" :accept="kind === 'Drawing2D' ? '.pdf,.dwg,.dxf,.slddrw' : '.sldprt,.sldasm,.step,.stp,.igs,.iges,.x_t,.x_b,.sat'" :aria-label="`上传${label}新版本`" @change="upload"></label>
    <span v-if="busy">正在处理 {{ progress }}%</span>
    <el-table :data="files" max-height="420"><el-table-column label="版本" width="85"><template #default="{ row }">{{ fileVersion(row.versionNumber) }}</template></el-table-column><el-table-column prop="originalFileName" label="文件名" min-width="200" show-overflow-tooltip/><el-table-column label="启用" width="85"><template #default="{ row }">{{ row.isEnabled ? '当前启用' : '未启用' }}</template></el-table-column><el-table-column prop="uploadedBy" label="上传人" width="95"/><el-table-column label="上传时间" min-width="165"><template #default="{ row }">{{ new Date(row.uploadedAt).toLocaleString('zh-CN') }}</template></el-table-column><el-table-column label="操作" width="180"><template #default="{ row }"><el-button v-if="/\.pdf$/i.test(row.originalFileName)" link @click="view(row)">预览</el-button><el-button link @click="downloadMaterialAttachment(materialId, row, token).catch(() => ElMessage.error('文件下载失败，请重试。'))">下载</el-button><el-button v-if="canManage && !row.isEnabled" link type="primary" :disabled="busy" @click="enable(row)">启用</el-button></template></el-table-column></el-table>
    <PdfDrawingViewer v-if="preview" :url="preview" style="height:55vh;margin-top:12px"/>
    <template #footer><el-button :disabled="busy" @click="emit('close')">关闭</el-button></template>
  </el-dialog>
</template>
<style scoped>.drawing-upload{display:inline-flex;align-items:center;gap:12px;margin-bottom:12px}</style>
