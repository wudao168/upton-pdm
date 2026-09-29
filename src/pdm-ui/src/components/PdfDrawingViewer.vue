<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type PDFDocumentLoadingTask, type RenderTask } from 'pdfjs-dist/legacy/build/pdf.mjs'
import workerUrl from 'pdfjs-dist/legacy/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl
const props = defineProps<{ url: string }>()
const canvas = ref<HTMLCanvasElement>()
const page = ref(1)
const pages = ref(0)
const busy = ref(false)
const error = ref('')
let document: PDFDocumentProxy | undefined
let loadingTask: PDFDocumentLoadingTask | undefined
let renderTask: RenderTask | undefined
let generation = 0

async function renderPage() {
  if (!document || !canvas.value) return
  busy.value = true
  error.value = ''
  try {
    const pdfPage = await document.getPage(page.value)
    const viewport = pdfPage.getViewport({ scale: 1.5 })
    canvas.value.width = viewport.width
    canvas.value.height = viewport.height
    renderTask = pdfPage.render({ canvas: canvas.value, viewport })
    await renderTask.promise
  } catch (cause) {
    if ((cause as Error).name !== 'RenderingCancelledException') error.value = 'PDF渲染失败，请下载文件查看。'
  } finally { busy.value = false }
}

watch(() => props.url, async url => {
  const current = ++generation
  renderTask?.cancel()
  await loadingTask?.destroy()
  document = undefined
  pages.value = 0
  page.value = 1
  error.value = ''
  if (!url) return
  busy.value = true
  const task = getDocument({ url })
  loadingTask = task
  try {
    const loaded = await task.promise
    if (current !== generation) { await task.destroy(); return }
    document = loaded
    pages.value = loaded.numPages
    await nextTick()
    await renderPage()
  } catch {
    if (current === generation) error.value = 'PDF加载失败，请下载文件查看。'
  } finally { if (current === generation) busy.value = false }
}, { immediate: true })

async function turnPage(delta: number) {
  if (busy.value) return
  page.value += delta
  await renderPage()
}
onBeforeUnmount(() => { generation++; renderTask?.cancel(); void loadingTask?.destroy() })
</script>

<template>
  <section class="pdf-drawing-viewer" aria-label="PDF图纸预览">
    <div class="pdf-drawing-viewer__toolbar">
      <button :disabled="busy || page <= 1" @click="turnPage(-1)">上一页</button>
      <span>{{ page }} / {{ pages }} 页</span>
      <button :disabled="busy || page >= pages" @click="turnPage(1)">下一页</button>
      <span v-if="busy" role="status">正在渲染图纸…</span>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <div class="pdf-drawing-viewer__pages"><canvas v-show="pages > 0 && !error" ref="canvas" aria-label="图纸页面" /></div>
  </section>
</template>

<style scoped>
.pdf-drawing-viewer__toolbar{display:flex;align-items:center;gap:12px;margin-bottom:8px}
.pdf-drawing-viewer__pages{height:min(72vh,calc(100dvh - 180px));min-height:80px;display:flex;align-items:center;justify-content:center;overflow:hidden;background:#e5e7eb;padding:8px;box-sizing:border-box}
canvas{display:block;max-width:100%;max-height:100%;width:auto;height:auto;object-fit:contain;background:white}
[role=alert]{color:var(--pdm-danger)}
</style>
