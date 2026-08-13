<script setup lang="ts">
import { computed, ref } from 'vue'
import { Box, Boxes, ChevronDown, ChevronRight, Component, FileText, Minus, TriangleAlert } from '@lucide/vue'
import type { DocumentNode } from '../types'

const props = defineProps<{
  node: DocumentNode
  level?: number
  selectedId: string
}>()

const emit = defineEmits<{ select: [node: DocumentNode]; context: [node: DocumentNode, event: MouseEvent] }>()
const expanded = ref((props.level ?? 0) < 2)
const hasChildren = computed(() => props.node.children.length > 0)
const icon = computed(() => {
  if (props.node.status === 'Missing' || props.node.status === 'Unregistered') return TriangleAlert
  if (props.node.kind === 'Assembly') return props.level === 0 ? Boxes : Box
  if (props.node.kind === 'Drawing') return FileText
  return Component
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
      :class="{ 'is-selected': selectedId === node.id, 'has-warning': node.status === 'Missing' || node.status === 'Unregistered' }"
      :style="{ '--tree-level': level ?? 0 }"
      @click="select"
      @dblclick="hasChildren && (expanded = !expanded)"
      @contextmenu.prevent="openContext"
    >
      <span class="pdm-tree-row__content">
        <span class="pdm-tree-row__toggle" @click.stop="hasChildren && (expanded = !expanded)">
          <component :is="hasChildren ? (expanded ? ChevronDown : ChevronRight) : Minus" :size="14" aria-hidden="true" />
        </span>
        <component :is="icon" :size="15" aria-hidden="true" />
        <span class="pdm-tree-row__label">
          <strong>{{ node.drawingNumber }}</strong>
          <small>{{ node.name }}<template v-if="node.quantity > 1"> ×{{ node.quantity }}</template></small>
        </span>
      </span>
      <em>{{ node.status === 'Missing' ? '缺失' : node.status === 'Unregistered' ? '未入库' : node.version }}</em>
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
