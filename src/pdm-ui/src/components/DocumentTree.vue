<script setup lang="ts">
import { RefreshCw, Search } from '@lucide/vue'
import type { DocumentNode } from '../types'
import DocumentTreeNode from './DocumentTreeNode.vue'

defineProps<{
  root: DocumentNode
  selectedId: string
  normalCount: number
  warningCount: number
}>()

const query = defineModel<string>('query', { required: true })
const emit = defineEmits<{ select: [node: DocumentNode]; refresh: [] }>()
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
      <DocumentTreeNode :node="root" :selected-id="selectedId" @select="emit('select', $event)" />
    </ul>
    <footer class="pdm-tree-legend">
      <span><i class="is-green" />已发布</span>
      <span><i class="is-blue" />工作版</span>
      <span><i class="is-orange" />异常</span>
    </footer>
  </section>
</template>
