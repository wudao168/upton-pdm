<script setup lang="ts">
import { ChevronRight, FolderLock, FolderOpen, RefreshCw } from '@lucide/vue'
import type { DocumentNode, ProjectSummary, WorkspaceLocalFileState } from '../types'

const props = withDefaults(defineProps<{
  project: ProjectSummary
  selected: DocumentNode
  localState?: WorkspaceLocalFileState
  desktopAvailable?: boolean
  refreshing?: boolean
}>(), { desktopAvailable: false, refreshing: false })

const emit = defineEmits<{
  refresh: []
  openFolder: [node: DocumentNode]
}>()
</script>

<template>
  <header class="pdm-workspace-explorer" aria-label="工作区命令栏">
    <nav class="pdm-workspace-address" aria-label="工作区位置">
      <FolderLock :size="15" aria-hidden="true" />
      <span>UPLM</span><ChevronRight :size="13" aria-hidden="true" />
      <span :title="`${project.code} · ${project.name}`">{{ project.code }}</span><ChevronRight :size="13" aria-hidden="true" />
      <strong>工作区</strong>
      <template v-if="selected.documentId">
        <ChevronRight :size="13" aria-hidden="true" /><span class="pdm-workspace-address__file" :title="selected.fileName">{{ selected.fileName }}</span>
      </template>
      <span v-if="desktopAvailable && localState" class="pdm-workspace-address__state" :class="`is-${localState.localState}`" :title="localState.message">{{ localState.localStateLabel }}</span>
    </nav>
    <div class="pdm-workspace-explorer__commands">
      <button type="button" class="pdm-workspace-command" :disabled="refreshing" :aria-busy="refreshing" @click="emit('refresh')">
        <RefreshCw :size="15" /><span>{{ refreshing ? '刷新中' : '刷新' }}</span>
      </button>
      <button type="button" class="pdm-workspace-command" :disabled="!desktopAvailable" @click="emit('openFolder', selected)">
        <FolderOpen :size="15" /><span>打开文件夹</span>
      </button>
      <span class="pdm-workspace-explorer__trust"><FolderLock :size="14" /><span>PLM受控工作区</span></span>
    </div>
  </header>
</template>
