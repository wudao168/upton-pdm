<script setup lang="ts">
import { FileClock } from '@lucide/vue'
import type { ProjectVersionItem } from '../types'

defineProps<{ versions: ProjectVersionItem[]; pending: boolean }>()
defineEmits<{ open: [documentId: string]; refresh: [] }>()

function statusLabel(status: ProjectVersionItem['status']) {
  return status === 'Released' || status === 1 ? '正式版' : '工作版'
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="项目版本">
    <div v-if="versions.length" class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>图号</th><th>文件</th><th>版本</th><th>状态</th><th>创建人</th><th>创建时间</th><th>变更说明</th><th>操作</th></tr></thead><tbody><tr v-for="version in versions" :key="version.id"><td><strong>{{ version.drawingNumber }}</strong><small>{{ version.documentName }}</small></td><td>{{ version.fileName }}</td><td>{{ version.revision.display }}</td><td><span class="pdm-status" :class="statusLabel(version.status) === '正式版' ? 'is-ok' : 'is-warn'">{{ statusLabel(version.status) }}</span></td><td>{{ version.createdBy }}</td><td>{{ new Date(version.createdAt).toLocaleString() }}</td><td>{{ version.changeNote || '—' }}</td><td><button type="button" class="pdm-text-action" @click="$emit('open', version.documentId)">查看与对比</button></td></tr></tbody></table></div>
    <div v-else class="pdm-project-empty"><FileClock :size="42" /><h2>当前项目还没有版本记录</h2><p>图档首次存档后，版本记录会显示在这里。</p></div>
  </section>
</template>
