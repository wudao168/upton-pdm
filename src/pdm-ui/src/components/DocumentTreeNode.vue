<script setup lang="ts">
import { computed, ref } from 'vue'
import { ChevronDown, ChevronRight, Minus } from '@lucide/vue'
import type { DocumentNode } from '../types'
import CadDocumentIcon from './CadDocumentIcon.vue'

const props = defineProps<{
  node: DocumentNode
  level?: number
  selectedId: string
}>()

const emit = defineEmits<{ select: [node: DocumentNode]; context: [node: DocumentNode, event: MouseEvent] }>()
const expanded = ref((props.level ?? 0) < 2)
const hasChildren = computed(() => props.node.children.length > 0)
const versionText = computed(() => props.node.snapshotVersion === undefined
  ? props.node.version
  : `${props.node.snapshotVersion} / ${props.node.version}`)
const versionStateText = computed(() => ({
  StructureStale: '结构待更新',
  VersionConflict: '版本关系异常',
  NotSnapshotted: '未进入快照',
} as const)[props.node.versionAlignment as 'StructureStale' | 'VersionConflict' | 'NotSnapshotted'] ?? '')
const versionHint = computed(() => {
  const versions = `结构实际版本 ${props.node.snapshotVersion ?? '—'}，最新版本 ${props.node.version}`
  if (props.node.versionAlignment === 'StructureStale') return `${versions}；存在更新的受控版本，结构待更新。`
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
      :class="{ 'is-selected': selectedId === node.id, 'has-warning': node.status === 'Missing' || node.status === 'Unregistered' || node.status === 'Unarchived', 'has-version-warning': node.versionAlignment === 'StructureStale' || node.versionAlignment === 'NotSnapshotted', 'has-version-conflict': node.versionAlignment === 'VersionConflict' }"
      :style="{ '--tree-level': level ?? 0 }"
      @click="select"
      @dblclick="hasChildren && (expanded = !expanded)"
      @contextmenu.prevent="openContext"
    >
      <span class="pdm-tree-row__content">
        <span class="pdm-tree-row__toggle" @click.stop="hasChildren && (expanded = !expanded)">
          <component :is="hasChildren ? (expanded ? ChevronDown : ChevronRight) : Minus" :size="14" aria-hidden="true" />
        </span>
        <CadDocumentIcon :kind="node.kind" :status="node.status" :size="17" />
        <span class="pdm-tree-row__label">
          <strong>{{ node.drawingNumber }}</strong>
          <small>{{ node.name }}<template v-if="node.quantity > 1"> ×{{ node.quantity }}</template></small>
        </span>
      </span>
      <span v-if="node.status !== 'Missing' && node.status !== 'Unregistered' && node.status !== 'Unarchived'" class="pdm-tree-row__version" :title="versionHint">
        <em>{{ versionText }}</em>
        <small v-if="versionStateText">{{ versionStateText }}</small>
      </span>
      <em v-else>{{ node.status === 'Missing' ? '缺失' : node.status === 'Unarchived' ? '未存档' : '未入库' }}</em>
    </button>
    <ul v-if="expanded && hasChildren" class="pdm-tree-children" role="group">
      <DocumentTreeNode
        v-for="child in node.children"
        :key="child.id"
        :node="child"
        :level="(level ?? 0) + 1"
        :selected-id="selectedId"
        @select="emit('select', $event)"
        @context="(node, event) => emit('context', node, event)"
      />
    </ul>
  </li>
</template>
