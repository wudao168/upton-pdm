<script setup lang="ts" generic="Key extends string">
import { computed } from 'vue'
import { orderColumns, type ColumnOption } from '../columnSettings'

const props = defineProps<{ columns: readonly ColumnOption<Key>[] }>()
const visible = defineModel<Key[]>('visible', { required: true })
const order = defineModel<Key[]>('order', { required: true })
const ordered = computed(() => orderColumns(props.columns, order.value))

function toggle(key: Key, checked: boolean) {
  visible.value = checked ? [...visible.value, key] : visible.value.filter(item => item !== key)
}

function move(index: number, offset: number) {
  const keys = ordered.value.map(column => column.key)
  const target = index + offset
  if (target < 0 || target >= keys.length) return
  const [key] = keys.splice(index, 1)
  if (key === undefined) return
  keys.splice(target, 0, key)
  order.value = keys
}
</script>

<template>
  <ul class="pdm-column-settings-list" aria-label="显示列及顺序">
    <li v-for="(column, index) in ordered" :key="column.key">
      <label><input type="checkbox" :value="column.key" :checked="visible.includes(column.key)" @change="toggle(column.key, ($event.target as HTMLInputElement).checked)">{{ column.label }}</label>
      <button type="button" class="pdm-secondary-action" :aria-label="`上移${column.label}`" title="上移" :disabled="index === 0" @click="move(index, -1)">↑</button>
      <button type="button" class="pdm-secondary-action" :aria-label="`下移${column.label}`" title="下移" :disabled="index === ordered.length - 1" @click="move(index, 1)">↓</button>
    </li>
  </ul>
</template>

<style scoped>
.pdm-column-settings-list { max-height: min(55vh, 440px); overflow: auto; margin: 0; padding: 0; list-style: none; }
.pdm-column-settings-list li { display: grid; grid-template-columns: minmax(0, 1fr) 28px 28px; align-items: center; gap: 6px; padding: 3px 6px; border-bottom: 1px solid var(--pdm-border-soft); }
.pdm-column-settings-list label { display: flex; align-items: center; gap: 8px; }
.pdm-column-settings-list button { width: 28px !important; min-width: 28px !important; padding: 0 !important; }
</style>
