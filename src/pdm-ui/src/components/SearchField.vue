<script setup lang="ts">
import { ref } from 'vue'
import '../styles/search-field.css'

withDefaults(defineProps<{ disabled?: boolean; showAction?: boolean }>(), { showAction: true })
const emit = defineEmits<{ search: [] }>()
const root = ref<HTMLElement>()
function search() {
  const input = root.value?.querySelector('input')
  if (!input || input.disabled) return
  // Reapply the current input for live filters; explicit queries use the search event or action slot.
  input.dispatchEvent(new Event('input', { bubbles: true }))
  input.focus({ preventScroll: true })
  emit('search')
}
</script>

<template>
  <span ref="root" class="pdm-search-field" :class="{ 'is-disabled': disabled, 'has-query-action': !!$slots.action, 'is-input-only': !showAction }">
    <svg class="pdm-search-field__icon" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true"><path fill-rule="evenodd" d="M8 4a4 4 0 100 8 4 4 0 000-8zM2 8a6 6 0 1110.89 3.476l4.817 4.817a1 1 0 01-1.414 1.414l-4.816-4.816A6 6 0 012 8z" clip-rule="evenodd" /></svg>
    <span class="pdm-search-field__input"><slot /></span>
    <span v-if="showAction" class="pdm-search-field__action"><slot name="action"><button type="button" :disabled="disabled" @click="search">搜索</button></slot></span>
  </span>
</template>
