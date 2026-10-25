<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ChevronDown, ChevronRight, FolderTree, Minus } from '@lucide/vue'
import type { DocumentNode, DrawingReviewBadge, WorkspaceLocalFileState } from '../types'
import CadDocumentIcon from './CadDocumentIcon.vue'
import { documentThumbnail, requestDocumentThumbnail } from '../documentThumbnails'
import { documentVersionSummary, documentDrawingVersion, documentPublishedVersion } from '../drawingRevision'

const props = defineProps<{
  node: DocumentNode
  level?: number
  searchQuery?: string
  selectedId: string
  reviewStates?: Record<string, DrawingReviewBadge>
  localStates?: Record<string, WorkspaceLocalFileState>
}>()

const emit = defineEmits<{ select: [node: DocumentNode]; context: [node: DocumentNode, event: MouseEvent] }>()
const expanded = ref((props.level ?? 0) === 0)
watch([() => props.searchQuery, () => props.node], () => {
  if (props.searchQuery?.trim()) expanded.value = true
}, { immediate: true })
const hasChildren = computed(() => props.node.children.length > 0)
const reviewState = computed(() => props.node.documentId ? props.reviewStates?.[props.node.documentId] : undefined)
const localState = computed(() => props.node.documentId ? props.localStates?.[props.node.documentId] : undefined)
const thumbnail = computed(() => documentThumbnail(props.node.documentId))
onMounted(() => requestDocumentThumbnail(props.node.documentId))
const versionText = computed(() => props.node.status === 'Virtual' ? '随父装配' : documentVersionSummary(props.node))
const versionStateText = computed(() => ({
  VersionConflict: '版本关系异常',
  NotSnapshotted: '未进入快照',
} as const)[props.node.versionAlignment as 'VersionConflict' | 'NotSnapshotted'] ?? '')
const versionHint = computed(() => {
  if (props.node.status === 'Virtual') return '内容嵌入父装配，随父文件存档，无需单独登记或升版。'
  const versions = `结构实际版本 ${props.node.snapshotVersion ?? '—'}，最新版本 ${props.node.version}`
  if (props.node.versionAlignment === 'StructureStale') return `${versions}；引用版本较旧：装配体仍引用此前确认的版本，不代表图纸已修改或需要重新审核。发布也可能只改变版本号。需要采用新版时，请在插件中确认更新引用、保存并存档装配体。`
  if (props.node.versionAlignment === 'VersionConflict') return `${versions}；结构版本比最新受控版本更新，版本关系异常。`
  if (props.node.versionAlignment === 'NotSnapshotted') return `${versions}；该图档尚未进入结构快照。`
  if (props.node.versionAlignment === 'Synced') return `${versions}；版本一致。`
  return `最新版本 ${props.node.version}`
})

function select() {
  emit('select', props.node)
}

function openContext(event: MouseEvent) {
  emit('select', props.node)
  emit('context', props.node, event)
}
</script>

<template>
  <li class="pdm-tree-item" :style="{ '--tree-level': level ?? 0 }">
    <button
      type="button"
      role="treeitem"
      :aria-expanded="hasChildren ? expanded : undefined"
      class="pdm-tree-row"
      :class="{ 'is-selected': selectedId === node.id, 'is-assembly': node.kind === 'Assembly' || node.isVirtualGroup, 'has-warning': !node.isVirtualGroup && (node.status === 'Missing' || node.status === 'Unregistered' || node.status === 'Unarchived'), 'has-version-warning': node.versionAlignment === 'StructureStale' || node.versionAlignment === 'NotSnapshotted', 'has-version-conflict': node.versionAlignment === 'VersionConflict' }"
      @click="select"
      @dblclick="hasChildren && (expanded = !expanded)"
      @contextmenu.prevent="openContext"
    >
      <span class="pdm-tree-row__content">
        <span class="pdm-tree-row__toggle" @click.stop="hasChildren && (expanded = !expanded)">
          <component :is="hasChildren ? (expanded ? ChevronDown : ChevronRight) : Minus" :size="14" aria-hidden="true" />
        </span>
        <img v-if="thumbnail" class="pdm-tree-row__thumb" :src="thumbnail" alt="" loading="lazy" />
        <FolderTree v-if="node.isVirtualGroup" :size="17" aria-label="装配体汇总分组" /><CadDocumentIcon v-else :kind="node.kind" :status="node.status" :size="17" />
        <span class="pdm-tree-row__label" :title="node.name === node.drawingNumber ? node.drawingNumber : `${node.drawingNumber} · ${node.name}`">
          <strong>{{ node.drawingNumber }}</strong>
          <small v-if="node.name !== node.drawingNumber || node.quantity > 1">
            <template v-if="node.name !== node.drawingNumber">{{ node.name }}</template><template v-if="node.quantity > 1"> ×{{ node.quantity }}</template>
          </small>
        </span>
      </span>
      <span v-if="!node.isVirtualGroup && node.status !== 'Missing' && node.status !== 'Unregistered' && node.status !== 'Unarchived'" class="pdm-tree-row__version" :title="`${versionText}；${versionHint}`">
        <span class="pdm-tree-row__states">
          <small v-if="reviewState" class="pdm-review-badge" :class="`is-${reviewState.tone}`" :title="reviewState.label">{{ reviewState.label }}</small>
          <small v-if="localState" class="pdm-local-state" :class="`is-${localState.localState}`" :title="localState.message">{{ localState.localStateLabel }}</small>
          <small v-if="node.structureIndexPending" class="pdm-local-state" title="原生文件已保存，结构或关联信息待补全；不会据此删除BOM，补全后再正式发布。">结构待补全</small>
          <small v-if="node.status === 'Virtual'">嵌入存储</small>
          <small v-if="versionStateText" :title="versionHint">{{ versionStateText }}</small>
        </span>
        <span class="pdm-tree-row__versions">
          <em class="pdm-tree-drawing-version" :title="versionHint">{{ node.status === 'Virtual' ? '随父装配' : documentDrawingVersion(node) }}</em>
          <small v-if="node.status !== 'Virtual'" class="pdm-tree-published-version" :title="`发布版本 ${documentPublishedVersion(node)}`">{{ documentPublishedVersion(node) }}</small>
        </span>
      </span>
      <em v-else-if="!node.isVirtualGroup">{{ node.status === 'Missing' ? '缺失' : node.status === 'Unarchived' ? '未存档' : '未入库' }}</em>
    </button>
    <ul v-if="expanded && hasChildren" class="pdm-tree-children" role="group">
      <DocumentTreeNode
        v-for="child in node.children"
        :key="child.id"
        :node="child"
        :level="(level ?? 0) + 1"
        :search-query="searchQuery"
        :selected-id="selectedId"
        :review-states="reviewStates"
        :local-states="localStates"
        @select="emit('select', $event)"
        @context="(node, event) => emit('context', node, event)"
      />
    </ul>
  </li>
</template>
