<script setup lang="ts">
import type { AuditEntry } from '../types'
withDefaults(defineProps<{ entries: AuditEntry[]; title?: string; description?: string }>(), {
  title: '审计查询',
  description: '查看、下载、恢复、存档、审批和发布操作均保留审计记录。',
})
defineEmits<{ refresh: [] }>()
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="审计查询">
    <header class="pdm-manager-heading"><div><h2>{{ title }}</h2><p>{{ description }}</p></div><button type="button" class="pdm-secondary-action" @click="$emit('refresh')">刷新</button></header>
    <div class="pdm-table-scroll"><table class="pdm-edit-table"><thead><tr><th>时间</th><th>人员</th><th>操作</th><th>对象</th><th>详情</th></tr></thead><tbody><tr v-for="entry in entries" :key="entry.id"><td>{{ new Date(entry.occurredAt).toLocaleString() }}</td><td>{{ entry.actor }}</td><td>{{ entry.action }}</td><td>{{ entry.entityType }} · {{ entry.entityId }}</td><td>{{ entry.detail }}</td></tr><tr v-if="entries.length === 0"><td colspan="5" class="pdm-empty-info">暂无可见审计记录。</td></tr></tbody></table></div>
  </section>
</template>
