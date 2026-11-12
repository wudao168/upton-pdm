<script setup lang="ts">
import { computed } from 'vue'
import { isDepartmentName } from '../departmentLabels'
import { nameColorHue } from '../nameColorHue'

const props = withDefaults(defineProps<{ name?: string | null; names?: readonly string[]; fallback?: string; separator?: string; plain?: boolean }>(), { fallback: '—', separator: '、' })
const labels = computed(() => (props.names?.length ? props.names : [props.name || props.fallback]).map(text => ({
  text,
  hue: !props.plain && isDepartmentName(text) ? nameColorHue(text.trim().normalize('NFC').toLowerCase()) : null,
})))
</script>

<template>
  <span class="pdm-department-names" :title="labels.map(item => item.text).join(separator)">
    <template v-for="(item, index) in labels" :key="index">
      <template v-if="index">{{ separator }}</template>
      <span v-if="item.hue !== null" class="pdm-department-name" :style="{ '--pdm-department-hue': item.hue }">{{ item.text }}</span>
      <template v-else>{{ item.text }}</template>
    </template>
  </span>
</template>

<style scoped>
.pdm-department-names { display: inline-block; min-inline-size: 0; max-inline-size: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle; }
.pdm-department-name { box-sizing: border-box; display: inline-block; max-inline-size: 100%; overflow: hidden; text-overflow: ellipsis; vertical-align: middle; padding: 0 6px; border-radius: 5px; line-height: 18px; background: hsl(var(--pdm-department-hue) 62% 88%); color: hsl(var(--pdm-department-hue) 60% 22%); }
:global(:root[data-pdm-color-scheme="dark"] .pdm-department-name) { background: hsl(var(--pdm-department-hue) 40% 28%); color: hsl(var(--pdm-department-hue) 74% 89%); }
</style>
