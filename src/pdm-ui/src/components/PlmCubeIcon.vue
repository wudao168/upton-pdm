<script setup lang="ts">
import appIcon from '../assets/plm-app-icon.svg'

withDefaults(defineProps<{
  motion?: 'ordered' | 'spatial' | 'axial'
}>(), {
  motion: 'ordered',
})
</script>

<template>
  <span
    class="plm-cube-icon"
    :class="{ 'is-spatial': motion === 'spatial', 'is-axial': motion === 'axial' }"
    aria-hidden="true"
  >
    <img class="plm-cube-icon__image" :src="appIcon" alt="" draggable="false">
  </span>
</template>

<style scoped>
.plm-cube-icon {
  width: var(--plm-cube-box-size, 38px);
  height: var(--plm-cube-box-size, 38px);
  display: grid;
  flex: 0 0 var(--plm-cube-box-size, 38px);
  place-items: center;
}
.plm-cube-icon__image {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
}
</style>
