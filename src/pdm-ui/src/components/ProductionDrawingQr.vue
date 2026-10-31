<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { listProductionDrawings } from '../api'
import { drawingsForProductionLine } from '../productionDrawingSelection'
import type { ProductionLine, ProductionReleaseState } from '../productionTracking'
import type { ProductionDrawingItem } from '../types'
import { userErrorMessage } from '../userErrorMessage'
import NameplateQr from './NameplateQr.vue'

const props = defineProps<{ token: string; state: ProductionReleaseState; line: ProductionLine }>()
const drawings = ref<ProductionDrawingItem[]>([]), versionId = ref('')
const qr = ref<{ content: string; src: string }>(), loading = ref(false), error = ref('')
let generation = 0
onBeforeUnmount(() => { generation++ })
async function show(item: ProductionDrawingItem) {
  const current = ++generation
  qr.value = undefined; error.value = ''; loading.value = true
  try {
    const { createProductionDrawingQr } = await import('../productionDrawingQr')
    const value = await createProductionDrawingQr(item.qrContent)
    if (current === generation) qr.value = value
  } catch (cause) { if (current === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { if (current === generation) loading.value = false }
}
function choose() {
  const item = drawings.value.find(item => item.versionId === versionId.value)
  if (item) void show(item)
  else { generation++; qr.value = undefined; error.value = ''; loading.value = false }
}
async function refresh() {
  const current = ++generation
  qr.value = undefined; drawings.value = []; versionId.value = ''; error.value = ''; loading.value = true
  if (props.line.nameplateContent) { loading.value = false; return }
  try {
    const items = await listProductionDrawings(props.token, true, props.state.projectId)
    if (current !== generation) return
    drawings.value = drawingsForProductionLine(items, props.state, props.line)
    if (drawings.value.length === 1) { versionId.value = drawings.value[0].versionId; await show(drawings.value[0]) }
  } catch (cause) { if (current === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { if (current === generation) loading.value = false }
}
watch(() => [props.state.releasePackageId, props.state.formalReleasePackageId, props.line.id, props.line.nameplateContent?.serialNumber, props.token], refresh, { immediate: true })
</script>

<template>
  <NameplateQr v-if="line.nameplateContent" :serial-number="line.nameplateContent.serialNumber" />
  <div v-else class="production-drawing-qr" aria-label="正式图纸二维码">
    <select v-if="drawings.length > 1" v-model="versionId" aria-label="选择二维码对应的正式图纸" @change="choose"><option value="">请选择正式图纸</option><option v-for="item in drawings" :key="item.versionId" :value="item.versionId">{{ item.drawingNumber }} · {{ item.revision }}</option></select>
    <p v-if="loading" role="status">正在生成二维码…</p>
    <img v-else-if="qr" :src="qr.src" :title="qr.content" alt="与PDF内容一致的图纸二维码" width="96" height="96">
    <p v-else-if="error" role="alert">{{ error }} <button type="button" @click="refresh">重试</button></p>
    <p v-else-if="!drawings.length">暂无对应的正式图纸</p>
  </div>
</template>

<style scoped>
.production-drawing-qr{display:grid;width:96px;justify-items:center;gap:4px;font-size:11px;line-height:1.4}.production-drawing-qr img{display:block;max-width:100%;background:#fff}.production-drawing-qr select{width:100%;min-width:0;height:28px;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text)}.production-drawing-qr p{margin:0;overflow-wrap:anywhere}.production-drawing-qr [role=alert]{color:var(--pdm-danger)}.production-drawing-qr button{border:0;background:transparent;color:var(--pdm-theme-accent);font:inherit;cursor:pointer}
</style>
