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
    <PlmCubeIcon class="pdm-square-loader__icon" motion="axial" />
  </div>
</template>

<style scoped>
.pdm-square-loader {
  width: 96px;
  height: 96px;
  display: grid;
  flex: 0 0 96px;
  place-items: center;
}

.pdm-square-loader.is-overlay {
  position: absolute;
  z-index: 20;
  inset: 0;
  width: auto;
  height: auto;
  background: rgba(255, 255, 255, .82);
}

.pdm-square-loader__icon {
  --plm-cube-box-size: 96px;
  --plm-cube-size: 52px;
  --plm-cube-perspective: 260px;
}
</style>
