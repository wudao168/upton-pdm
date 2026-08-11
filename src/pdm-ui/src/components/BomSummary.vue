<script setup lang="ts">
import { CircleAlert, CircleCheck } from '@lucide/vue'
import { computed } from 'vue'
import type { BomItem } from '../types'

const props = defineProps<{ mechanical: BomItem[]; electrical: BomItem[] }>()
const emit = defineEmits<{ open: [] }>()

const rows = computed(() => [
  { name: '机械BOM', source: '服务端机械BOM', items: props.mechanical },
  { name: '电气BOM', source: '服务端电气BOM', items: props.electrical },
].map((row) => ({
  ...row,
  incomplete: row.items.filter((item) => !item.complete).length,
})))
</script>

<template>
  <section class="pdm-panel pdm-info-panel" aria-label="BOM完整性">
    <header class="pdm-panel-heading"><h2>BOM完整性</h2><button type="button" class="pdm-text-action" @click="emit('open')">进入BOM</button></header>
    <div v-for="row in rows" :key="row.name" class="pdm-check-row" :class="row.incomplete ? 'is-warning' : 'is-ok'">
      <span>
        <CircleAlert v-if="row.incomplete" :size="18" />
        <CircleCheck v-else :size="18" />
        <span><strong>{{ row.name }}</strong><small>{{ row.source }}</small></span>
      </span>
      <em>{{ row.incomplete ? `${row.incomplete} 项待确认` : `${row.items.length} 项完整` }}</em>
    </div>
  </section>
</template>
