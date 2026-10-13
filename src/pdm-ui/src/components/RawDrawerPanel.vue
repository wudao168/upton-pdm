<script setup lang="ts">
import { provide, ref, type ComponentPublicInstance } from 'vue'
import { panelHeaderActionsKey } from '../panelHeaderActions'

const actionTarget = ref<HTMLElement>()
provide(panelHeaderActionsKey, actionTarget)
function setActionTarget(element: Element | ComponentPublicInstance | null) {
  actionTarget.value = element instanceof HTMLElement ? element : undefined
}
</script>

<template>
  <section class="pdm-custom-drawer">
    <slot name="header" v-bind="{ setActionTarget }" />
    <slot />
  </section>
</template>
