<script setup lang="ts">
import PlmCubeIcon from './PlmCubeIcon.vue'

withDefaults(defineProps<{
  label?: string
  overlay?: boolean
}>(), {
  label: '加载中',
  overlay: false,
})
</script>

<template>
  <div class="pdm-square-loader" :class="{ 'is-overlay': overlay }" role="status" :aria-label="label" aria-live="polite">
    <PlmCubeIcon class="pdm-square-loader__icon pdm-loading-logo" />
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
  background: rgba(255, 255, 255, .82);
  container-type: size;
}

.pdm-square-loader__icon {
  --plm-cube-box-size: var(--pdm-loading-icon-size, 144px);
}
.pdm-square-loader.is-overlay > .pdm-square-loader__icon {
  --plm-cube-box-size: min(var(--pdm-loading-icon-size, 144px), 80cqw, 80cqh);
}
</style>
