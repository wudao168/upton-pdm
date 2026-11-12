<script setup lang="ts">
import { computed } from 'vue'
import DepartmentName from './DepartmentName.vue'
import { isDepartmentName } from '../departmentLabels'

const props = defineProps<{ name?: string | null; names?: readonly string[]; label?: string }>()
const suffix = computed(() => props.name && props.label?.startsWith(props.name) ? props.label.slice(props.name.length) : '')
const showName = computed(() => props.names?.some(isDepartmentName) || isDepartmentName(props.name))
</script>

<template>
  <span class="pdm-department-select" :class="{ 'has-department': showName }">
    <slot />
    <span v-if="showName" class="pdm-department-select__name" aria-hidden="true"><DepartmentName :name="name" :names="names" separator=" / " />{{ suffix }}</span>
  </span>
</template>

<style scoped>
.pdm-department-select { position: relative; display: grid; min-width: 0; max-width: 100%; }
.pdm-department-select > :slotted(select) { width: 100%; min-width: 0; }
.pdm-department-select.has-department > :slotted(select) { color: transparent !important; }
.pdm-department-select.has-department :deep(option) { color: var(--pdm-text); background: var(--pdm-surface); }
.pdm-department-select__name { position: absolute; inset-inline: 8px 24px; align-self: center; justify-self: start; max-inline-size: calc(100% - 32px); pointer-events: none; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font: inherit; }
.pdm-department-select:has(select:disabled) .pdm-department-select__name { opacity: .65; }
@media (forced-colors: active) {
  .pdm-department-select.has-department > :slotted(select) { color: CanvasText !important; }
  .pdm-department-select__name { display: none; }
}
</style>
