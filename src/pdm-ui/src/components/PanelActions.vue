<script setup lang="ts">
import { inject } from 'vue'
import { panelHeaderActionsKey } from '../panelHeaderActions'

const target = inject(panelHeaderActionsKey, undefined)
</script>

<template>
  <Teleport :to="target || 'body'" :disabled="!target"><slot /></Teleport>
</template>
