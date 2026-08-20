<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { RefreshCw, Search } from '@lucide/vue'
import { postDesktopMessage } from '../api'
import type { DocumentFilter, DocumentNode, SolidWorksOpenMode } from '../types'
import CadDocumentIcon from './CadDocumentIcon.vue'
import DocumentTreeNode from './DocumentTreeNode.vue'

defineProps<{
  root?: DocumentNode
  drawings: DocumentNode[]
  selectedId: string
  allCount: number
  modelCount: number
  drawingCount: number
  warningCount: number
}>()

const query = defineModel<string>('query', { required: true })
const filter = defineModel<DocumentFilter>('filter', { required: true })
const emit = defineEmits<{ select: [node: DocumentNode]; refresh: []; open: [node: DocumentNode, mode: SolidWorksOpenMode] }>()
const contextNode = ref<DocumentNode>()
const contextLeft = ref(0)
const contextTop = ref(0)
const solidWorksAvailable = ref(false)
const filters: Array<{ value: DocumentFilter; label: string }> = [
  { value: 'all', label: '全部' },
  { value: 'model', label: '3D' },
  { value: 'drawing', label: '2D' },
  { value: 'issue', label: '异常' },
]

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
  <section class="pdm-panel pdm-tree-panel" aria-label="项目设计树">
    <header class="pdm-panel-heading">
      <h2>设计树</h2>
      <button type="button" class="pdm-plain-button" aria-label="刷新设计树" @click="emit('refresh')"><RefreshCw :size="15" /></button>
    </header>
    <label class="pdm-tree-search">
      <Search :size="15" aria-hidden="true" />
      <span class="pdm-sr-only">搜索图号或名称</span>
      <input v-model="query" type="search" placeholder="搜索图号或名称">
    </label>
    <div class="pdm-document-filters" role="tablist" aria-label="图档类型筛选">
      <button
        v-for="item in filters"
        :key="item.value"
        type="button"
        role="tab"
        :aria-selected="filter === item.value"
        @click="filter = item.value"
      >
        {{ item.label }}
        <small>{{ item.value === 'all' ? allCount : item.value === 'model' ? modelCount : item.value === 'drawing' ? drawingCount : warningCount }}</small>
      </button>
    </div>
    <ul v-if="filter === 'drawing' && drawings.length" class="pdm-tree pdm-drawing-list" aria-label="2D工程图列表">
      <li v-for="drawing in drawings" :key="drawing.id">
        <button
          type="button"
          class="pdm-tree-row"
          :class="{ 'is-selected': drawing.id === selectedId }"
          @click="emit('select', drawing)"
          @contextmenu.prevent="showContext(drawing, $event)"
        >
          <span class="pdm-tree-row__content"><CadDocumentIcon :kind="drawing.kind" :status="drawing.status" :size="17" /><span class="pdm-tree-row__label"><strong>{{ drawing.drawingNumber }}</strong><small>{{ drawing.name }}</small></span></span>
          <em>{{ drawing.version }}</em>
        </button>
      </li>
    </ul>
    <ul v-else-if="filter !== 'drawing' && root" class="pdm-tree" role="tree">
      <DocumentTreeNode :node="root" :selected-id="selectedId" @select="emit('select', $event)" @context="showContext" />
    </ul>
    <div v-else class="pdm-tree-empty" role="status">
      <strong>没有匹配的图档</strong>
      <span>请调整类型筛选或搜索关键字。</span>
    </div>
    <div
      v-if="contextNode"
      class="pdm-tree-context-menu"
      role="menu"
      :style="{ left: `${contextLeft}px`, top: `${contextTop}px` }"
      @click.stop
    >
      <strong>{{ contextNode.drawingNumber }} · <template v-if="contextNode.snapshotVersion !== undefined">{{ contextNode.snapshotVersion }} / </template>{{ contextNode.version }}</strong>
      <button type="button" role="menuitem" :disabled="!solidWorksAvailable || !contextNode.documentId" @click="open('LatestReadOnly')">在SolidWorks中打开最新受控版</button>
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
