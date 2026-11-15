<script setup lang="ts">
import type { ReleasePackageSummary } from '../types'
defineProps<{ rows: ReleasePackageSummary['nonStandardBomSnapshot']; multiplier?: number; label: string }>()
</script>

<template>
  <table class="release-review-bom-table pdm-fit-columns" :aria-label="label">
    <colgroup><col style="width:83.333333%"><col style="width:16.666667%"></colgroup>
    <thead><tr><th>物料 / 型号</th><th>总量</th></tr></thead>
    <tbody><tr v-for="(row,index) in rows" :key="row.id || index"><td>{{ row.name }}<small>{{ row.specification || row.drawingNumber }}</small></td><td>{{ Number(row.quantity) * (multiplier ?? 1) }}</td></tr></tbody>
  </table>
</template>

<style scoped>
.release-review-bom-table{width:100%;table-layout:fixed}
.release-review-bom-table th,.release-review-bom-table td{padding:6px 4px;text-align:left;vertical-align:middle;overflow-wrap:anywhere}
.release-review-bom-table small{display:block;overflow-wrap:anywhere}
</style>
