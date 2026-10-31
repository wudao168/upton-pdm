<script setup lang="ts">
import { useProjectCenterAccess } from '../projectCenterAccess'
const { write: projectWritable, download: projectDownload } = useProjectCenterAccess()
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { adoptBomLibraryFiles, readBomFileBlob, readBomFiles, readProcurementFiles, readProcurementFileBlob, removeBomFile, uploadBomFile } from '../api'
import { fileVersion } from '../bomFiles'
import type { BomFileReference, BomFileView } from '../bomFiles'
import type { BomItem } from '../types'
const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ projectId: string; row: Pick<BomItem, 'id' | 'drawingNumber' | 'files'>; bomVersionId?: string; token: string; canEdit: boolean; procurement?: boolean }>()
const emit = defineEmits<{ close: []; changed: [files: BomFileReference[]] }>()
const view = ref<BomFileView>({ files: props.row.files ?? [], libraryUpdateAvailable: false, rowVersion: 0 })
const busy = ref(false)
const progress = ref(0)
const purpose = ref('配图')
const previewUrl = ref('')
const previewImage = ref(false)
const editable = computed(() => projectWritable.value && props.canEdit && !props.bomVersionId && !props.procurement)
function update(value: BomFileView) { view.value = value; if (!props.bomVersionId && !props.procurement) emit('changed', value.files) }
async function load() {
  update(props.procurement ? { files: await readProcurementFiles(props.projectId, props.row.drawingNumber, props.token), libraryUpdateAvailable: false, rowVersion: 0 }
    : await readBomFiles(props.projectId, props.row.id!, props.token, props.bomVersionId))
}
onMounted(() => load().catch(() => ElMessage.error('BOM图纸及附件读取失败，请刷新后重试。')))
function clearPreview() { if (previewUrl.value) URL.revokeObjectURL(previewUrl.value); previewUrl.value = '' }
onBeforeUnmount(clearPreview)
async function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? []); input.value = ''
  busy.value = true; progress.value = 0
  try {
    for (const file of files) update(await uploadBomFile(props.projectId, props.row.id!, file, purpose.value, props.token, value => progress.value = value))
    ElMessage.success('项目附件已上传，无需审批')
  } catch { ElMessage.error('附件上传失败，请检查文件格式和项目BOM维护权限后重试。') }
  finally { busy.value = false }
}
async function adopt() {
  try {
    await ElMessageBox.confirm('采用标准物料当前启用的2D、3D版本及资料？仅更新当前工作BOM的引用，已发布BOM保持不变。', '更新标准物料引用', { confirmButtonText: '确认采用', cancelButtonText: '取消' })
    busy.value = true
    update(await adoptBomLibraryFiles(props.projectId, props.row.id!, view.value.rowVersion, props.token))
  } catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error('引用更新失败，请刷新核对后重试。') }
  finally { busy.value = false }
}
async function remove(file: BomFileReference) {
  try {
    await ElMessageBox.confirm(`从当前项目资料中移除“${file.fileName}”？历史发布记录及原文件仍保留。`, '移除项目附件', { confirmButtonText: '确认移除', cancelButtonText: '取消' })
    busy.value = true
    await removeBomFile(props.projectId, props.row.id!, file.id, view.value.rowVersion, props.token); await load()
  } catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error('移除失败，请刷新后重试。') }
  finally { busy.value = false }
}
async function open(file: BomFileReference, preview: boolean) {
  try {
    const blob = props.procurement ? await readProcurementFileBlob(props.projectId, props.row.drawingNumber, file.id, props.token, preview)
      : await readBomFileBlob(props.projectId, props.row.id!, file.id, props.token, props.bomVersionId, preview)
    if (preview) { clearPreview(); previewImage.value = /\.(png|jpe?g|webp)$/i.test(file.fileName); previewUrl.value = URL.createObjectURL(blob); return }
    const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = file.fileName; a.click(); URL.revokeObjectURL(url)
  } catch { ElMessage.error('文件读取失败，请刷新后重试。') }
}
</script>
<template>
  <el-dialog :model-value="true" :title="`${row.drawingNumber} · 图纸附件`" width="min(1150px,96vw)" :close-on-click-modal="!busy" :close-on-press-escape="!busy" :show-close="!busy" @close="emit('close')">
    <p v-if="procurement">当前显示备料对应已发布BOM的图纸附件，按权限提供在线预览及下载；后续标准物料更新不影响此处。</p>
    <p v-else-if="bomVersionId">当前显示该BOM版本记录的文件，后续标准物料更新不影响此处。<span v-if="row.files == null">此历史版本未记录文件快照。</span></p>
    <p v-else>标准物料图纸采用已启用版本；项目附件无需审批。上传的项目资料仅用于当前项目。</p>
    <el-alert v-if="!bomVersionId && view.libraryUpdateAvailable" title="标准物料的启用版本或资料已更新，可核对后采用。" type="info" :closable="false"/>
    <div v-if="editable" class="bom-file-tools"><el-button v-if="view.libraryUpdateAvailable" :disabled="busy" @click="adopt">采用当前启用版本及资料</el-button><el-select v-model="purpose" aria-label="项目附件用途" :disabled="busy" style="width:125px"><el-option v-for="item in ['配图','技术协议','说明书','其他']" :key="item" :label="item" :value="item"/></el-select><label>上传项目附件 <input type="file" multiple :disabled="busy" aria-label="上传项目附件" accept=".pdf,.dwg,.dxf,.slddrw,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.jpg,.jpeg,.png,.zip,.rar,.7z" @change="upload"></label><span v-if="busy">正在处理 {{ progress }}%</span></div>
    <el-table :data="view.files" max-height="420"><el-table-column prop="fileName" label="文件名" min-width="210" show-overflow-tooltip/><el-table-column prop="source" label="来源" width="110"/><el-table-column label="类型" width="100"><template #default="{ row: file }">{{ file.kind === 'Drawing2D' ? '2D图纸' : file.kind === 'Model3D' ? '3D模型' : file.purpose }}</template></el-table-column><el-table-column label="图纸版本" width="100"><template #default="{ row: file }">{{ file.versionNumber ? fileVersion(file.versionNumber) : '—' }}</template></el-table-column><el-table-column prop="uploadedBy" label="上传人" width="95"/><el-table-column label="操作" width="180"><template #default="{ row: file }"><el-button v-if="/\.(pdf|png|jpe?g|webp)$/i.test(file.fileName)" link @click="open(file,true)">预览</el-button><el-button v-if="projectDownload" link @click="open(file,false)">下载</el-button><el-button v-if="editable && file.source === '项目上传'" link type="danger" :disabled="busy" @click="remove(file)">移除</el-button></template></el-table-column></el-table>
    <img v-if="previewUrl && previewImage" :src="previewUrl" alt="附件预览" style="max-width:100%;max-height:60vh;margin-top:12px"><PdfDrawingViewer v-else-if="previewUrl" :url="previewUrl" style="height:55vh;margin-top:12px"/>
    <template #footer><el-button :disabled="busy" @click="emit('close')">关闭</el-button></template>
  </el-dialog>
</template>
<style scoped>.bom-file-tools{display:flex;align-items:center;flex-wrap:wrap;gap:12px;margin:12px 0}.bom-file-tools label{display:flex;align-items:center;gap:8px}</style>
