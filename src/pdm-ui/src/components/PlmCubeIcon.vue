<script setup lang="ts">
withDefaults(defineProps<{
  motion?: 'ordered' | 'spatial' | 'axial'
}>(), {
  motion: 'ordered',
})

const tile = {
  coral: 'linear-gradient(135deg, #ec949d 0%, #c94f60 100%)',
  peach: 'linear-gradient(135deg, #efad7d 0%, #cc713c 100%)',
  yellow: 'linear-gradient(135deg, #e8df6a 0%, #b9ad2e 100%)',
  mint: 'linear-gradient(135deg, #8ed69a 0%, #389b52 100%)',
  sky: 'linear-gradient(135deg, #76c5e8 0%, #1e85b8 100%)',
  lavender: 'linear-gradient(135deg, #b49de3 0%, #6f54b6 100%)',
}

const faces = [
  { side: 'top', label: 'P', latin: true, colors: [tile.yellow, tile.sky, tile.lavender, tile.coral] },
  { side: 'front', label: 'L', latin: true, colors: [tile.peach, tile.mint, tile.sky, tile.yellow] },
  { side: 'right', label: 'M', latin: true, colors: [tile.lavender, tile.peach, tile.coral, tile.mint] },
  { side: 'back', label: '阿', latin: false, colors: [tile.coral, tile.lavender, tile.sky, tile.yellow] },
  { side: 'bottom', label: '普', latin: false, colors: [tile.mint, tile.lavender, tile.sky, tile.peach] },
  { side: 'left', label: '顿', latin: false, colors: [tile.mint, tile.yellow, tile.coral, tile.peach] },
]
</script>

<template>
  <span
    class="plm-cube-icon"
    :class="{
      'is-spatial': motion === 'spatial',
      'is-axial': motion === 'axial',
    }"
    aria-hidden="true"
  >
    <span class="plm-cube-icon__cube">
      <span v-for="face in faces" :key="face.side" class="plm-cube-icon__face" :class="`is-${face.side}`">
        <span v-for="(color, index) in face.colors" :key="index" class="plm-cube-icon__tile" :style="{ background: color }" />
        <strong class="plm-cube-icon__label" :class="{ 'is-latin': face.latin }">{{ face.label }}</strong>
      </span>
    </span>
  </span>
</template>

<style scoped>
.plm-cube-icon {
  --plm-cube-box-size: 38px;
  --plm-cube-size: 24px;
  --plm-cube-perspective: 120px;
  width: var(--plm-cube-box-size);
  height: var(--plm-cube-box-size);
  display: grid;
  flex: 0 0 var(--plm-cube-box-size);
  place-items: center;
  perspective: var(--plm-cube-perspective);
}
.plm-cube-icon__cube {
  position: relative;
  width: var(--plm-cube-size);
  height: var(--plm-cube-size);
  transform-style: preserve-3d;
  animation: plm-cube-spin 9s infinite linear;
  will-change: transform;
}
.plm-cube-icon.is-spatial .plm-cube-icon__cube {
  animation: plm-cube-spatial-spin 6.8s infinite linear;
}
.plm-cube-icon.is-axial .plm-cube-icon__cube {
  animation: plm-cube-axial-spin 1.5s infinite linear;
}
.plm-cube-icon__face {
  position: absolute;
  inset: 0;
  box-sizing: border-box;
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  grid-template-rows: repeat(2, 1fr);
  gap: calc(var(--plm-cube-size) / 40);
  overflow: hidden;
  border: 0;
  border-radius: 0;
  background: rgba(7, 25, 54, .58);
  box-shadow: none;
  backface-visibility: hidden;
}
.plm-cube-icon__tile { min-width: 0; min-height: 0; border: 0; }
.plm-cube-icon__label {
  position: absolute;
  top: 50%;
  left: 50%;
  z-index: 1;
  width: 58%;
  height: 58%;
  display: grid;
  place-items: center;
  background: transparent;
  color: #fff;
  font-family: "Microsoft YaHei UI", "Microsoft YaHei", sans-serif;
  font-size: calc(var(--plm-cube-size) * .54);
  font-synthesis: none;
  font-weight: 800;
  line-height: 1;
  text-rendering: geometricPrecision;
  text-shadow: 0 0 1px rgba(5,23,54,.9);
  transform: translate(-50%, -50%);
  -webkit-font-smoothing: antialiased;
}
.plm-cube-icon__label.is-latin {
  width: 72%;
  height: 72%;
  font-size: calc(var(--plm-cube-size) * .67);
}
.plm-cube-icon__face.is-front { transform: translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-back { transform: rotateY(180deg) translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-left { transform: rotateY(-90deg) translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-right { transform: rotateY(90deg) translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-top { transform: rotateX(90deg) translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-bottom { transform: rotateX(-90deg) translateZ(calc(var(--plm-cube-size) / 2)); }
.plm-cube-icon__face.is-left .plm-cube-icon__label { transform: translate(-50%, -50%) rotate(-90deg); }
.plm-cube-icon__face.is-bottom .plm-cube-icon__label { transform: translate(-50%, -50%) rotate(180deg); }

@keyframes plm-cube-spin {
  0%, 10% { transform: rotate3d(0, 1, 0, 180deg); }
  16%, 26% { transform: rotate3d(0, .707107, .707107, 180deg); }
  32%, 42% { transform: rotate3d(-.57735, .57735, .57735, 120deg); }
  48%, 58% { transform: rotate3d(-1, 0, 0, 90deg); }
  64%, 74% { transform: rotate3d(0, 1, 0, 0deg); }
  80%, 90% { transform: rotate3d(0, -1, 0, 90deg); }
  100% { transform: rotate3d(0, -1, 0, 180deg); }
}
@keyframes plm-cube-spatial-spin {
  0% { transform: rotateX(-18deg) rotateY(24deg) rotateZ(0deg); }
  19% { transform: rotateX(123deg) rotateY(-71deg) rotateZ(38deg); }
  41% { transform: rotateX(281deg) rotateY(147deg) rotateZ(-29deg); }
  63% { transform: rotateX(437deg) rotateY(326deg) rotateZ(74deg); }
  82% { transform: rotateX(591deg) rotateY(509deg) rotateZ(196deg); }
  100% { transform: rotateX(702deg) rotateY(744deg) rotateZ(360deg); }
}
@keyframes plm-cube-axial-spin {
  from { transform: rotateY(0deg) rotateX(-54.736deg) rotateY(-45deg); }
  to { transform: rotateY(360deg) rotateX(-54.736deg) rotateY(-45deg); }
}
@media (prefers-reduced-motion: reduce) {
  .plm-cube-icon__cube,
  .plm-cube-icon.is-spatial .plm-cube-icon__cube,
  .plm-cube-icon.is-axial .plm-cube-icon__cube {
    animation: none;
    transform: rotateY(0deg) rotateX(-54.736deg) rotateY(-45deg);
  }
}
</style>
