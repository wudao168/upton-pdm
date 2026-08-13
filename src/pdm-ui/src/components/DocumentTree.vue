<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { RefreshCw, Search } from '@lucide/vue'
import { postDesktopMessage } from '../api'
import type { DocumentNode, SolidWorksOpenMode } from '../types'
import DocumentTreeNode from './DocumentTreeNode.vue'

defineProps<{
  root: DocumentNode
  selectedId: string
  normalCount: number
  warningCount: number
}>()

const query = defineModel<string>('query', { required: true })
const emit = defineEmits<{ select: [node: DocumentNode]; refresh: []; open: [node: DocumentNode, mode: SolidWorksOpenMode] }>()
const contextNode = ref<DocumentNode>()
const contextLeft = ref(0)
const contextTop = ref(0)
const solidWorksAvailable = ref(false)

function showContext(node: DocumentNode, event: MouseEvent) {
  contextNode.value = node
  contextLeft.value = Math.max(8, Math.min(event.clientX, window.innerWidth - 310))
  contextTop.value = Math.max(8, Math.min(event.clientY, window.innerHeight - 210))
}

function closeContext() {
  contextNode.value = undefined
}

function open(mode: SolidWorksOpenMode) {
  if (!contextNode.value?.documentId || !solidWorksAvailable.value) return
  emit('open', contextNode.value, mode)
  closeContext()
}

function onCapability(event: Event) {
  solidWorksAvailable.value = Boolean((event as CustomEvent<{ available?: boolean }>).detail?.available)
}

onMounted(() => {
  window.addEventListener('click', closeContext)
  window.addEventListener('blur', closeContext)
  window.addEventListener('pdm-solidworks-capability', onCapability)
  postDesktopMessage('solidworks-capability-request')
})

onBeforeUnmount(() => {
  window.removeEventListener('click', closeContext)
  window.removeEventListener('blur', closeContext)
  window.removeEventListener('pdm-solidworks-capability', onCapability)
})
</script>

<template>
  <section class="pdm-panel pdm-tree-panel" aria-label="项目图档结构">
    <header class="pdm-panel-heading">
      <h2>图档结构</h2>
      <button type="button" class="pdm-plain-button" aria-label="刷新结构树" @click="emit('refresh')"><RefreshCw :size="15" /></button>
    </header>
    <label class="pdm-tree-search">
      <Search :size="15" aria-hidden="true" />
      <span class="pdm-sr-only">搜索图号或名称</span>
      <input v-model="query" type="search" placeholder="搜索图号或名称">
    </label>
    <div class="pdm-tree-summary">
      <span>共 {{ normalCount + warningCount }} 个文件</span>
      <span class="is-ok">{{ normalCount }} 正常</span>
      <span class="is-warning">{{ warningCount }} 待补充</span>
    </div>
    <ul class="pdm-tree" role="tree">
      <DocumentTreeNode :node="root" :selected-id="selectedId" @select="emit('select', $event)" @context="showContext" />
    </ul>
    <div
      v-if="contextNode"
      class="pdm-tree-context-menu"
      role="menu"
      :style="{ left: `${contextLeft}px`, top: `${contextTop}px` }"
      @click.stop
    >
      <strong>{{ contextNode.drawingNumber }} · {{ contextNode.version }}</strong>
      <button type="button" role="menuitem" :disabled="!solidWorksAvailable || !contextNode.documentId" @click="open('LatestReadOnly')">在SolidWorks中打开最新受控版（只读）</button>
      <button type="button" role="menuitem" :disabled="!solidWorksAvailable || !contextNode.documentId" @click="open('LatestEdit')">获取最新版本并编辑</button>
      <button type="button" role="menuitem" :disabled="!solidWorksAvailable || !contextNode.documentId" @click="open('LatestReleased')">打开最新正式发布版（只读）</button>
      <small v-if="!contextNode.documentId">该引用尚未入库，请先在SolidWorks插件中提交整套存档</small>
      <small v-if="!solidWorksAvailable">当前电脑未安装SolidWorks或UPTON PDM插件</small>
    </div>
    <footer class="pdm-tree-legend">
      <span><i class="is-green" />已发布</span>
      <span><i class="is-blue" />工作版</span>
      <span><i class="is-orange" />异常</span>
    </footer>
  </section>
</template>
