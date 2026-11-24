<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { PackageCheck } from '@lucide/vue'
import { readReleaseChangeOverview } from '../api'
import { reportChartColors } from '../reportCharts'
import type { ReleaseChangeCategory, ReleaseChangeOverviewGroup } from '../types'

const props = defineProps<{ projectId: string; token: string; active: boolean; refreshVersion: number }>()
const categories = [
  ['All', '全部'],
  ['Standard', '标准件'],
  ['NonStandard', '机加件'],
  ['Electrical', '电气件'],
] as const
const category = ref<ReleaseChangeCategory>('All')
const groups = ref<ReleaseChangeOverviewGroup[]>([])
const pending = ref(false)
const failed = ref(false)
const retry = ref(0)
const current = computed(() => groups.value.find(group => group.category === category.value))
const reasons = computed(() => current.value?.reasons ?? [])
const allReasons = computed(() => groups.value.find(group => group.category === 'All')?.reasons ?? [])
const maximum = computed(() => Math.max(1, ...allReasons.value.map(row => row.count)))
const colorsByReason = computed(() => new Map(allReasons.value.map((row, index) =>
  [row.reason, reportChartColors[index % reportChartColors.length]])))

watch([() => props.projectId, () => props.token, () => props.active, () => props.refreshVersion, retry],
  async ([projectId, token, active], previous, onCleanup) => {
    if (!previous || previous[0] !== projectId || previous[1] !== token) {
      groups.value = []
      category.value = 'All'
    }
    pending.value = false
    failed.value = false
    if (!active || !projectId || !token) return
    const request = new AbortController()
    onCleanup(() => request.abort())
    pending.value = true
    try {
      const result = await readReleaseChangeOverview(projectId, token, request.signal)
      if (!request.signal.aborted) groups.value = result
    } catch {
      if (!request.signal.aborted) {
        groups.value = []
        failed.value = true
      }
    } finally {
      if (!request.signal.aborted) pending.value = false
    }
  }, { immediate: true })
</script>

<template>
  <article class="pdm-panel project-release-change-overview" aria-label="发布详情" :aria-busy="pending">
    <header>
      <span class="release-overview-icon"><PackageCheck :size="18" aria-hidden="true" /></span>
      <h2>发布详情</h2>
      <select v-model="category" aria-label="发布物料类别">
        <option v-for="[value, label] in categories" :key="value" :value="value">{{ label }}</option>
      </select>
    </header>
    <div class="release-overview-content">
      <div class="pdm-stat-card release-count" aria-label="增补／变更总次数">
        <span class="pdm-stat-card__label"><span class="pdm-stat-card__icon"><PackageCheck :size="15" aria-hidden="true" /></span>增补／变更次数</span>
        <strong aria-live="polite">{{ current?.count ?? '—' }}<small v-if="current">次</small></strong>
      </div>
      <section class="release-reasons" aria-label="变更原因">
        <h3>变更原因</h3>
        <div v-if="failed" class="release-status" role="status">
          <span>发布统计读取失败</span>
          <button type="button" class="pdm-secondary-action" @click="retry++">重试</button>
        </div>
        <p v-else-if="!current && pending" class="release-status" role="status">正在读取发布统计…</p>
        <p v-else-if="current && !reasons.length" class="release-status" role="status">暂无具体变更原因</p>
        <ol v-if="reasons.length" class="release-reason-chart" aria-label="按次数排列的具体变更原因">
          <li v-for="row in reasons" :key="row.reason">
            <span class="release-reason-label">{{ row.reason }}</span>
            <span class="release-reason-track" aria-hidden="true">
              <span :style="{ width: (row.count / maximum * 100) + '%', backgroundColor: colorsByReason.get(row.reason) ?? reportChartColors[0] }"></span>
            </span>
            <strong>{{ row.count }} 次</strong>
          </li>
        </ol>
      </section>
    </div>
  </article>
</template>

<style scoped>
.project-release-change-overview { min-width: 0; min-height: 0; display: flex; flex-direction: column; }
.project-release-change-overview > header {
  min-height: 42px; flex: none; display: flex; align-items: center; flex-wrap: wrap; gap: 8px;
  padding: 8px 13px; border-bottom: 1px solid var(--pdm-border-soft);
}
.release-overview-icon {
  display: grid; place-items: center; width: 30px; height: 30px; border-radius: 7px;
  color: var(--pdm-green); background: var(--pdm-green-soft);
}
.project-release-change-overview h2, .release-reasons h3 { margin: 0; font-size: 12px; font-weight: 600; }
.project-release-change-overview select {
  margin-inline-start: auto; width: 105px; height: 28px; border-radius: 999px;
  border: 1px solid var(--pdm-container-border, var(--pdm-border));
}
.release-overview-content { min-height: 0; padding: 12px 13px 14px; overflow: auto; }
.project-release-change-overview .release-count.pdm-stat-card { max-width: 260px; min-height: 0; }
.release-reasons { margin-top: 16px; }
.release-reason-chart { display: grid; gap: 8px; list-style: none; padding: 0; margin: 8px 0 0; }
.release-reason-chart li {
  display: grid; grid-template-columns: min(36%, 150px) minmax(0, 1fr) max-content;
  align-items: center; gap: 8px; min-height: 20px; font-size: 12px;
}
.release-reason-label { overflow-wrap: anywhere; }
.release-reason-track { display: block; height: 12px; background: var(--pdm-surface-muted); border-radius: 3px; overflow: hidden; }
.release-reason-track span { display: block; height: 100%; border-radius: inherit; }
.release-reason-chart strong { font-weight: 600; font-variant-numeric: tabular-nums; }
.release-status { display: flex; gap: 8px; align-items: center; margin: 8px 0; color: var(--pdm-muted); font-size: 12px; }
</style>
