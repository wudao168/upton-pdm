<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { listMaterialAttachments, materialAttachmentObjectUrl } from '../api'
import type { MaterialAttachment } from '../types'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{
  modelValue: boolean
  materialId: string
  token: string
  initialId?: string
  attachments?: MaterialAttachment[]
}>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const images = ref<MaterialAttachment[]>([])
const index = ref(0)
const url = ref('')
const originalOpen = ref(false)
const loading = ref(false)
const errorMessage = ref('')
const current = computed(() => images.value[index.value])
let request = 0

function releaseImage() {
  originalOpen.value = false
  if (url.value.startsWith('blob:')) URL.revokeObjectURL(url.value)
  url.value = ''
}

async function loadImage() {
  const id = ++request
  releaseImage()
  errorMessage.value = ''
  loading.value = true
  try {
    if (!current.value) return
    const next = await materialAttachmentObjectUrl(props.materialId, current.value.id, props.token)
    if (id !== request) { URL.revokeObjectURL(next); return }
    url.value = next
  } catch (error) {
    if (id === request) errorMessage.value = userErrorMessage(error instanceof Error ? error.message : '图片加载失败，请重试。')
  } finally { if (id === request) loading.value = false }
}

async function openImages() {
  const id = ++request
  releaseImage()
  images.value = []
  errorMessage.value = ''
  loading.value = true
  try {
    const items = props.attachments ?? await listMaterialAttachments(props.materialId, props.token, 'CoverImage')
    if (id !== request) return
    images.value = items.filter(item => item.kind === 'CoverImage')
    index.value = Math.max(0, images.value.findIndex(item => item.id === props.initialId))
    await loadImage()
  } catch (error) {
    if (id === request) errorMessage.value = userErrorMessage(error instanceof Error ? error.message : '图片列表加载失败，请重试。')
  } finally { if (id === request) loading.value = false }
}

function move(offset: number) {
  index.value += offset
  void loadImage()
}

watch([() => props.modelValue, () => props.materialId, () => props.initialId], ([visible]) => {
  if (visible && props.materialId) void openImages()
  else { ++request; releaseImage(); loading.value = false }
}, { immediate: true })
onBeforeUnmount(() => { ++request; releaseImage() })
</script>

<template>
  <el-dialog :model-value="modelValue" class="material-image-dialog" modal-class="material-image-overlay" title="料品图片" width="532px" align-center append-to-body @update:model-value="emit('update:modelValue', $event)">
    <div v-loading="loading" class="material-image-stage">
      <img v-if="url" :src="url" :alt="current?.originalFileName" title="点击查看原图，可缩放和旋转" role="button" tabindex="0" @click="originalOpen = true" @keydown.enter.prevent="originalOpen = true" @keydown.space.prevent="originalOpen = true" @error="errorMessage = '图片无法显示，请重新加载或检查图片文件。'" />
      <div v-if="errorMessage" role="alert" class="material-image-error"><span>{{ errorMessage }}</span><el-button @click="images.length ? loadImage() : openImages()">重新加载</el-button></div>
      <span v-else-if="!loading && !images.length">暂无图片</span>
    </div>
    <div class="material-image-navigation">
      <el-button :disabled="loading || index <= 0" @click="move(-1)">上一张</el-button>
      <span :title="current?.originalFileName">{{ images.length ? `${index + 1} / ${images.length} · ${current?.originalFileName}` : '0 / 0' }}</span>
      <el-button :disabled="loading || index >= images.length - 1" @click="move(1)">下一张</el-button>
    </div>
  </el-dialog>
  <el-image-viewer v-if="originalOpen && url" :url-list="[url]" teleported @close="originalOpen = false" />
</template>

<style scoped>
:global(.material-image-overlay .el-overlay-dialog){align-items:center;justify-content:center;padding:12px}
:global(.material-image-overlay .el-overlay-dialog>.material-image-dialog){max-width:calc(100vw - 24px);height:auto;max-height:calc(100dvh - 24px);padding:0;border-radius:6px}
:global(.material-image-dialog .el-dialog__body){flex:0 1 auto}
.material-image-stage{position:relative;display:flex;width:500px;max-width:100%;aspect-ratio:1;max-height:calc(100dvh - 180px);align-items:center;justify-content:center;overflow:hidden;background:var(--pdm-surface-muted);color:var(--pdm-muted)}
.material-image-stage>img{width:100%;height:100%;object-fit:contain;cursor:zoom-in}
.material-image-error{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:12px;padding:20px;background:var(--pdm-surface);text-align:center}
.material-image-navigation{display:flex;align-items:center;gap:8px;margin-top:10px}
.material-image-navigation>span{min-width:0;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;text-align:center}
</style>
