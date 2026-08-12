<script setup lang="ts">
import type { ProjectSummary } from '../types'
defineProps<{ project: ProjectSummary; status: { vaultAvailable: boolean; releaseAvailable: boolean } | null }>()
defineEmits<{ refresh: [] }>()
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="项目存储设置">
    <header class="pdm-manager-heading"><div><h2>项目存储位置</h2><p>图档库和生产发布目录按项目隔离，路径由系统配置维护。</p></div><button type="button" class="pdm-secondary-action" @click="$emit('refresh')">检查可用性</button></header>
    <div class="pdm-setting-list"><article><div><small>图档库存放地点</small><strong>{{ project.vaultLocation }}</strong></div><span :class="status?.vaultAvailable ? 'is-ok' : 'is-warn'">{{ status ? (status.vaultAvailable ? '可用' : '不可用') : '未检查' }}</span></article><article><div><small>生产发包地点</small><strong>{{ project.releaseLocation }}</strong></div><span :class="status?.releaseAvailable ? 'is-ok' : 'is-warn'">{{ status ? (status.releaseAvailable ? '可用' : '不可用') : '未检查' }}</span></article></div>
  </section>
</template>
