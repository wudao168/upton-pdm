<script setup lang="ts">
import type { BomItem } from '../types'
defineProps<{ rows: { item: BomItem; quantity: number }[]; netChange?: boolean; label: string }>()
</script>

<template>
  <table class="release-review-bom-table pdm-fit-columns" :aria-label="label">
    <colgroup><col style="width:83.333333%"><col style="width:16.666667%"></colgroup>
    <thead><tr><th>物料 / 型号</th><th>{{ netChange ? '需求净变更' : '总量' }}</th></tr></thead>
    <tbody><tr v-for="(row,index) in rows" :key="row.item.id || index"><td>{{ row.item.name }}<small>{{ row.item.specification || row.item.drawingNumber }}</small></td><td>{{ netChange && row.quantity > 0 ? '+' + row.quantity : row.quantity }}</td></tr></tbody>
  </table>
</template>

<style scoped>
.release-review-bom-table{width:100%;table-layout:fixed}
.release-review-bom-table th,.release-review-bom-table td{padding:6px 4px;text-align:left;vertical-align:middle;overflow-wrap:anywhere}
.release-review-bom-table small{display:block;overflow-wrap:anywhere}
</style>
