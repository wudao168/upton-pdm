<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type PDFDocumentLoadingTask, type RenderTask } from 'pdfjs-dist/legacy/build/pdf.mjs'
import workerUrl from 'pdfjs-dist/legacy/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl
const props = defineProps<{ url: string; fillHeight?: boolean }>()
const canvas = ref<HTMLCanvasElement>()
const pageFrame = ref<HTMLDivElement>()
const page = ref(1)
const pages = ref(0)
const busy = ref(false)
const error = ref('')
const zoom = ref(1)
const canvasSize = ref<{ width: string; height: string }>()
let document: PDFDocumentProxy | undefined
let loadingTask: PDFDocumentLoadingTask | undefined
let renderTask: RenderTask | undefined
let generation = 0
let renderGeneration = 0
let resizeObserver: ResizeObserver | undefined
let wheelDelta = 0, wheelZooming = false
let wheelPoint: { x: number; y: number } | undefined

async function renderPage() {
  if (!document || !canvas.value) return
  const current = ++renderGeneration
  const source = document
  renderTask?.cancel()
  await renderTask?.promise.catch(() => {})
  if (current !== renderGeneration) return
  busy.value = true
  error.value = ''
  try {
    const pdfPage = await source.getPage(page.value)
    if (current !== renderGeneration || source !== document || !canvas.value) return
    const base = pdfPage.getViewport({ scale: 1 })
    const frame = pageFrame.value?.getBoundingClientRect()
    const fit = frame ? Math.min(Math.max(1, frame.width - 16) / base.width, Math.max(1, frame.height - 16) / base.height) : 1.5
    const displayScale = (props.fillHeight ? fit : Math.min(1.5, fit)) * zoom.value
    const density = Math.min(window.devicePixelRatio || 1, Math.sqrt(20_000_000 / (base.width * base.height * displayScale ** 2)))
    const viewport = pdfPage.getViewport({ scale: !props.fillHeight && zoom.value === 1 ? 1.5 : displayScale * density })
    canvasSize.value = zoom.value === 1 ? undefined : { width: `${base.width * displayScale}px`, height: `${base.height * displayScale}px` }
    canvas.value.width = viewport.width
    canvas.value.height = viewport.height
    renderTask = pdfPage.render({ canvas: canvas.value, viewport })
    await renderTask.promise
  } catch (cause) {
    if (current === renderGeneration && (cause as Error).name !== 'RenderingCancelledException') error.value = 'PDF渲染失败，请下载文件查看。'
  } finally { if (current === renderGeneration) busy.value = false }
}

watch(() => props.url, async url => {
  const current = ++generation
  wheelDelta = 0
  ++renderGeneration
  renderTask?.cancel()
  await loadingTask?.destroy()
  document = undefined
  pages.value = 0
  page.value = 1
  zoom.value = 1
  canvasSize.value = undefined
  error.value = ''
  busy.value = false
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
  pageFrame.value?.scrollTo(0, 0)
}
async function setZoom(value: number, anchor?: { x: number; y: number }) {
  if (busy.value || !document || !canvas.value || !pageFrame.value) return
  const current = generation
  const next = Math.min(3, Math.max(.5, value))
  if (next === zoom.value) { if (!anchor) pageFrame.value.scrollTo(0, 0); return }
  const frame = pageFrame.value, sheet = canvas.value.getBoundingClientRect(), bounds = frame.getBoundingClientRect()
  const offsetX = anchor ? anchor.x - bounds.left : frame.clientWidth / 2
  const offsetY = anchor ? anchor.y - bounds.top : frame.clientHeight / 2
  const x = (bounds.left + offsetX - sheet.left) / sheet.width
  const y = (bounds.top + offsetY - sheet.top) / sheet.height
  zoom.value = next
  await nextTick()
  await renderPage()
  await nextTick()
  if (current !== generation) return
  if (next === 1) frame.scrollTo(0, 0)
  else if (canvas.value) frame.scrollTo(canvas.value.offsetLeft + canvas.value.clientWidth * x - offsetX, canvas.value.offsetTop + canvas.value.clientHeight * y - offsetY)
}
async function onWheel(event: WheelEvent) {
  if (!event.ctrlKey) return
  event.preventDefault()
  event.stopPropagation()
  if (!document || !event.deltaY || error.value) return
  wheelDelta -= Math.sign(event.deltaY) * .25
  wheelPoint = { x: event.clientX, y: event.clientY }
  if (wheelZooming) return
  wheelZooming = true
  try {
    while (wheelDelta) {
      if (busy.value) { await new Promise(resolve => setTimeout(resolve, 30)); continue }
      const delta = wheelDelta; wheelDelta = 0
      await setZoom(zoom.value + delta, wheelPoint)
    }
  } finally { wheelZooming = false }
}
onMounted(() => {
  resizeObserver = new ResizeObserver(() => { if ((props.fillHeight || zoom.value !== 1) && document) void renderPage() })
  if (pageFrame.value) resizeObserver.observe(pageFrame.value)
})
onBeforeUnmount(() => { generation++; renderGeneration++; wheelDelta = 0; resizeObserver?.disconnect(); renderTask?.cancel(); void loadingTask?.destroy() })
</script>

<template>
  <section class="pdf-drawing-viewer" :class="{ 'is-fill-height': fillHeight }" aria-label="PDF图纸预览">
    <div class="pdf-drawing-viewer__toolbar">
      <slot name="toolbar" />
      <button :disabled="busy || page <= 1" @click="turnPage(-1)">上一页</button>
      <span>{{ pages ? page : 0 }} / {{ pages }} 页</span>
      <button :disabled="busy || page >= pages" @click="turnPage(1)">下一页</button>
      <div class="pdf-drawing-viewer__zoom" aria-label="图纸缩放" title="按住 Ctrl 滚动滚轮缩放">
        <button aria-label="图纸缩小" :disabled="busy || !pages || zoom <= .5" @click="setZoom(zoom - .25)">缩小</button>
        <span aria-label="图纸缩放比例">{{ Math.round(zoom * 100) }}%</span>
        <button aria-label="图纸放大" :disabled="busy || !pages || zoom >= 3" @click="setZoom(zoom + .25)">放大</button>
        <button :disabled="busy || !pages" @click="setZoom(1)">适应窗口</button>
      </div>
      <span v-if="busy" role="status">正在渲染图纸…</span>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <div ref="pageFrame" class="pdf-drawing-viewer__pages" :class="{ 'is-zoomed': zoom !== 1 }" tabindex="0" aria-label="图纸查看区域" title="Ctrl + 滚轮缩放；普通滚轮滚动图纸" @wheel="onWheel"><canvas v-show="pages > 0 && !error" ref="canvas" :style="canvasSize" aria-label="图纸页面" /></div>
  </section>
</template>

<style scoped>
.pdf-drawing-viewer__toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:12px;margin-bottom:8px}.pdf-drawing-viewer__zoom{display:flex;align-items:center;gap:6px}.pdf-drawing-viewer__zoom>span{min-width:40px;text-align:center;font-variant-numeric:tabular-nums}
.pdf-drawing-viewer__pages{position:relative;height:min(72vh,calc(100dvh - 180px));min-height:80px;display:flex;align-items:center;justify-content:center;overflow:hidden;background:var(--pdm-border);padding:8px;box-sizing:border-box}.pdf-drawing-viewer__pages.is-zoomed{overflow:auto;align-items:flex-start;justify-content:flex-start}.is-zoomed canvas{flex:none;max-width:none;max-height:none;margin:auto}
.is-fill-height{display:flex;flex:1;flex-direction:column;min-width:0;min-height:0}.is-fill-height .pdf-drawing-viewer__toolbar{flex-shrink:0;flex-wrap:wrap;gap:8px;margin-bottom:6px}.is-fill-height .pdf-drawing-viewer__pages{flex:1;height:auto;min-height:0}
canvas{display:block;max-width:100%;max-height:100%;width:auto;height:auto;object-fit:contain;background:white}
[role=alert]{color:var(--pdm-danger)}
</style>
