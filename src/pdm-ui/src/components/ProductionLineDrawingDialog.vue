<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import PdfDrawingViewer from './PdfDrawingViewer.vue'
import { downloadProductionDrawingFile, listProductionDrawings, readProductionDrawingPreview } from '../api'
import type { ProductionDrawingItem } from '../types'
import type { ProductionLine, ProductionReleaseState } from '../productionTracking'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'
import { drawingsForProductionLine } from '../productionDrawingSelection'

const props = defineProps<{ token: string; state: ProductionReleaseState; line: ProductionLine; action: 'preview' | 'download' }>()
const emit = defineEmits<{ close: [] }>()
const drawings = ref<ProductionDrawingItem[]>([]), loading = ref(true), pending = ref(false), error = ref(''), url = ref('')
const selected = ref<ProductionDrawingItem>()
let disposed = false, generation = 0
onBeforeUnmount(() => { disposed = true; generation++; if (url.value) URL.revokeObjectURL(url.value) })
function failure(cause: unknown) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '图纸读取失败，请刷新后重试。') }
async function preview(item: ProductionDrawingItem) {
  const current = ++generation
  pending.value = true; error.value = ''; selected.value = item
  if (url.value) { URL.revokeObjectURL(url.value); url.value = '' }
  try {
    const blob = await readProductionDrawingPreview(item, props.token)
    if (!disposed && current === generation) url.value = URL.createObjectURL(blob)
  } catch (cause) { if (!disposed && current === generation) failure(cause) }
  finally { if (!disposed && current === generation) pending.value = false }
}
async function download(item: ProductionDrawingItem, closeAfter = false) {
  pending.value = true; error.value = ''
  try {
    await downloadProductionDrawingFile(item, 'Pdf', props.token)
    if (!disposed) { ElMessage.success('正式图纸已交给浏览器保存'); if (closeAfter) emit('close') }
  } catch (cause) { if (!disposed && !(cause instanceof DOMException && cause.name === 'AbortError')) failure(cause) }
  finally { if (!disposed) pending.value = false }
}
onMounted(async () => {
  try {
    const items = await listProductionDrawings(props.token, true, props.state.projectId)
    if (disposed) return
    drawings.value = drawingsForProductionLine(items, props.state, props.line)
    if (!drawings.value.length) error.value = '该物料在此发布包中没有可用的正式图纸，请到图纸资料核对。'
    else if (drawings.value.length === 1 && drawings.value[0].pdfReady) {
      if (props.action === 'download') await download(drawings.value[0], true)
      else await preview(drawings.value[0])
    }
  } catch (cause) { if (!disposed) failure(cause) }
  finally { if (!disposed) loading.value = false }
})
</script>

<template>
  <el-dialog :model-value="true" title="正式图纸" width="min(1120px, calc(100vw - 32px))" @update:model-value="!$event && emit('close')">
    <section class="production-line-drawings" aria-label="物料正式图纸">
      <p>{{ state.packageNumber }} · {{ line.drawingNumber }} · {{ line.name }}</p>
      <p v-if="loading" role="status">正在读取发布包图纸…</p><p v-if="error" role="alert">{{ error }}</p>
      <div class="production-line-drawings__files"><div v-for="item in drawings" :key="item.versionId"><strong>{{ item.drawingNumber }} · {{ item.revision }}</strong><span v-if="!item.isCurrent">历史正式版</span><span v-if="!item.pdfReady">PDF 待转换</span><template v-else><el-button :disabled="pending" @click="preview(item)">预览</el-button><el-button :disabled="pending" @click="download(item)">下载 PDF</el-button></template></div></div>
      <p v-if="pending" role="status">正在读取正式图纸，请稍候…</p><PdfDrawingViewer v-if="url" :key="url" :url="url" />
      <small v-if="selected?.legacyUnverified">历史图面未校验，图面标记可能与系统记录不同。</small>
    </section>
    <template #footer><el-button @click="emit('close')">关闭</el-button></template>
  </el-dialog>
</template>

<style scoped>
.production-line-drawings{min-width:0;font-size:12px}.production-line-drawings>p{margin:0 0 12px;color:var(--pdm-muted)}.production-line-drawings [role=alert]{color:var(--pdm-danger)}.production-line-drawings__files{display:grid;gap:8px;margin-bottom:12px}.production-line-drawings__files>div{display:flex;flex-wrap:wrap;align-items:center;gap:10px}.production-line-drawings__files span,.production-line-drawings>small{color:var(--pdm-muted)}.production-line-drawings :deep(.pdf-drawing-viewer__pages){max-height:60vh;overflow:auto}
</style>
