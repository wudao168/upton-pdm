<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'

const root = ref<HTMLDetailsElement>()
function close(restoreFocus = false) {
  if (!root.value?.open) return
  root.value.open = false
  if (restoreFocus) root.value.querySelector('summary')?.focus()
}
function outside(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) close()
}
function toggle() {
  document.removeEventListener('pointerdown', outside)
  if (root.value?.open) document.addEventListener('pointerdown', outside)
}
function activate(event: MouseEvent) {
  const button = (event.target as Element).closest('button')
  if (button && !button.disabled && button.getAttribute('aria-disabled') !== 'true') close()
}
onBeforeUnmount(() => document.removeEventListener('pointerdown', outside))
</script>

<template>
  <details ref="root" class="pdm-more-actions" @toggle="toggle" @keydown.esc.stop.prevent="close(true)">
    <summary>更多操作</summary>
    <div @click="activate"><slot /></div>
  </details>
</template>
