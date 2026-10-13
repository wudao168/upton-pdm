<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ChevronDown, ChevronRight, FolderTree, Minus } from '@lucide/vue'
import type { DocumentNode, DrawingReviewBadge, WorkspaceLocalFileState } from '../types'
import CadDocumentIcon from './CadDocumentIcon.vue'
import { documentThumbnail, requestDocumentThumbnail } from '../documentThumbnails'

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
const versionText = computed(() => props.node.snapshotVersion === undefined
  ? props.node.version
  : `${props.node.snapshotVersion} / ${props.node.version}`)
const versionStateText = computed(() => ({
  StructureStale: '引用版本较旧',
  VersionConflict: '版本关系异常',
  NotSnapshotted: '未进入快照',
} as const)[props.node.versionAlignment as 'StructureStale' | 'VersionConflict' | 'NotSnapshotted'] ?? '')
const versionHint = computed(() => {
  const versions = `结构实际版本 ${props.node.snapshotVersion ?? '—'}，最新版本 ${props.node.version}`
  if (props.node.versionAlignment === 'StructureStale') return `${versions}；装配体仍引用此前确认的版本，不代表图纸已修改或需要重新审核。发布也可能只改变版本号。需要采用新版时，请在插件中确认更新引用、保存并存档装配体。`
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
  <li class="pdm-tree-item">
    <button
      type="button"
      role="treeitem"
      :aria-expanded="hasChildren ? expanded : undefined"
      class="pdm-tree-row"
      :class="{ 'is-selected': selectedId === node.id, 'has-warning': !node.isVirtualGroup && (node.status === 'Missing' || node.status === 'Unregistered' || node.status === 'Unarchived'), 'has-version-warning': node.versionAlignment === 'StructureStale' || node.versionAlignment === 'NotSnapshotted', 'has-version-conflict': node.versionAlignment === 'VersionConflict' }"
      :style="{ '--tree-level': level ?? 0 }"
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
        <span class="pdm-tree-row__label">
          <strong>{{ node.drawingNumber }}</strong>
          <small v-if="node.name !== node.drawingNumber || node.quantity > 1">
            <template v-if="node.name !== node.drawingNumber">{{ node.name }}</template><template v-if="node.quantity > 1"> ×{{ node.quantity }}</template>
          </small>
        </span>
      </span>
      <span v-if="!node.isVirtualGroup && node.status !== 'Missing' && node.status !== 'Unregistered' && node.status !== 'Unarchived'" class="pdm-tree-row__version" :title="versionHint">
        <em>{{ versionText }}</em>
        <small v-if="localState" class="pdm-local-state" :class="`is-${localState.localState}`" :title="localState.message">{{ localState.localStateLabel }}</small>
        <small v-if="reviewState" class="pdm-review-badge" :class="`is-${reviewState.tone}`">{{ reviewState.label }}</small>
        <small v-if="versionStateText">{{ versionStateText }}</small>
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
