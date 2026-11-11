<script setup lang="ts">
import PlmCubeIcon from './PlmCubeIcon.vue'

withDefaults(defineProps<{
  label?: string
  overlay?: boolean
  showLabel?: boolean
}>(), {
  label: '加载中',
  overlay: false,
  showLabel: false,
})
</script>

<template>
  <div class="pdm-square-loader" :class="{ 'is-overlay': overlay, 'has-label': overlay && showLabel }" role="status" :aria-label="label" aria-live="polite">
    <PlmCubeIcon class="pdm-square-loader__icon pdm-loading-logo" />
    <span v-if="overlay && showLabel" aria-hidden="true">{{ label }}</span>
  </div>
</template>

<style scoped>
.pdm-square-loader {
  width: var(--pdm-loading-icon-size, 144px);
  height: var(--pdm-loading-icon-size, 144px);
  display: grid;
  flex: 0 0 var(--pdm-loading-icon-size, 144px);
  place-items: center;
}

.pdm-square-loader.is-overlay {
  position: absolute;
  z-index: 20;
  inset: 0;
  width: auto;
  height: auto;
  container-type: size;
}

.pdm-square-loader.is-overlay.has-label {
  place-content: center;
  gap: 8px;
  text-align: center;
  color: var(--pdm-text-soft);
}

.pdm-square-loader__icon {
  --plm-cube-box-size: var(--pdm-loading-icon-size, 144px);
}
.pdm-square-loader.is-overlay > .pdm-square-loader__icon {
  --plm-cube-box-size: min(var(--pdm-loading-icon-size, 144px), 80cqw, 80cqh);
}
</style>
