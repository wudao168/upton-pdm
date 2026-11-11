<script setup lang="ts">
import type { RelationOption } from '../authorizationRelations'

defineProps<{ options: RelationOption[]; disabled: boolean }>()
const selected = defineModel<string[]>({ required: true })
</script>

<template>
  <div class="authorization-scope-options">
    <label v-for="option in options" :key="option.key" class="authorization-scope-option">
      <input v-model="selected" type="checkbox" :value="option.key" :disabled="disabled" :aria-label="option.label">
      <span><strong>{{ option.label }}</strong><small v-if="option.description">{{ option.description }}</small></span>
    </label>
  </div>
</template>

<style scoped>
.authorization-scope-options { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px 12px; }
.authorization-scope-option { display: flex; gap: 8px; align-items: flex-start; padding: 6px; border-radius: 6px; cursor: pointer; }
.authorization-scope-option:hover { background: var(--pdm-theme-accent-soft); }
.authorization-scope-option input { flex: 0 0 auto; margin-top: 3px; }
.authorization-scope-option strong, .authorization-scope-option small { display: block; }
.authorization-scope-option small { color: var(--pdm-muted); margin-top: 3px; line-height: 1.5; }
@media (max-width: 700px) { .authorization-scope-options { grid-template-columns: minmax(0, 1fr); } }
</style>
