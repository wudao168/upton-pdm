<script setup lang="ts">
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
    <div class="pdm-square-loader__track" aria-hidden="true">
      <span v-for="index in 8" :key="index" class="pdm-square-loader__square" />
    </div>
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

.pdm-square-loader__track {
  position: relative;
  width: 96px;
  height: 96px;
  transform: rotate(45deg);
}

.pdm-square-loader__square {
  position: absolute;
  top: 0;
  left: 0;
  width: 28px;
  height: 28px;
  margin: 2px;
  border-radius: 0;
  background: #000;
  background-size: cover;
  background-position: center;
  background-attachment: fixed;
  animation: pdm-square-loader-animation 10s ease-in-out infinite both;
}

.pdm-square-loader__square:nth-of-type(1) { animation-delay: 0s; }
.pdm-square-loader__square:nth-of-type(2) { animation-delay: -1.4285714286s; }
.pdm-square-loader__square:nth-of-type(3) { animation-delay: -2.8571428571s; }
.pdm-square-loader__square:nth-of-type(4) { animation-delay: -4.2857142857s; }
.pdm-square-loader__square:nth-of-type(5) { animation-delay: -5.7142857143s; }
.pdm-square-loader__square:nth-of-type(6) { animation-delay: -7.1428571429s; }
.pdm-square-loader__square:nth-of-type(7) { animation-delay: -8.5714285714s; }
.pdm-square-loader__square:nth-of-type(8) { animation-delay: -10s; }

@keyframes pdm-square-loader-animation {
  0%, 10.5% { left: 0; top: 0; }
  12.5%, 23% { left: 32px; top: 0; }
  25%, 35.5% { left: 64px; top: 0; }
  37.5%, 48% { left: 64px; top: 32px; }
  50%, 60.5% { left: 32px; top: 32px; }
  62.5%, 73% { left: 32px; top: 64px; }
  75%, 85.5% { left: 0; top: 64px; }
  87.5%, 98% { left: 0; top: 32px; }
  100% { left: 0; top: 0; }
}
</style>
