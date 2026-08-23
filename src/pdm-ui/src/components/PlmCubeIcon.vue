<script setup lang="ts">
const tile = {
  red: '#ff3d00',
  orange: '#ff9800',
  yellow: '#ffdf33',
  green: '#49b653',
  blue: '#2495e8',
  white: '#f8fafc',
}

const faces = [
  { side: 'top', label: 'P', latin: true, colors: [tile.red, tile.green, tile.blue, tile.green, tile.white, tile.yellow, tile.blue, tile.yellow, tile.red] },
  { side: 'front', label: 'L', latin: true, colors: [tile.orange, tile.green, tile.blue, tile.blue, tile.white, tile.red, tile.yellow, tile.orange, tile.green] },
  { side: 'right', label: 'M', latin: true, colors: [tile.yellow, tile.red, tile.blue, tile.red, tile.white, tile.green, tile.blue, tile.green, tile.yellow] },
  { side: 'back', label: '阿', latin: false, colors: [tile.blue, tile.yellow, tile.red, tile.white, tile.green, tile.orange, tile.red, tile.blue, tile.yellow] },
  { side: 'bottom', label: '普', latin: false, colors: [tile.white, tile.green, tile.orange, tile.red, tile.blue, tile.yellow, tile.green, tile.orange, tile.red] },
  { side: 'left', label: '顿', latin: false, colors: [tile.orange, tile.blue, tile.white, tile.green, tile.red, tile.yellow, tile.blue, tile.white, tile.green] },
]
</script>

<template>
  <span class="plm-cube-icon" aria-hidden="true">
    <span class="plm-cube-icon__cube">
      <span v-for="face in faces" :key="face.side" class="plm-cube-icon__face" :class="`is-${face.side}`">
        <span v-for="(color, index) in face.colors" :key="index" class="plm-cube-icon__tile" :style="{ backgroundColor: color }" />
        <strong class="plm-cube-icon__label" :class="{ 'is-latin': face.latin }">{{ face.label }}</strong>
      </span>
    </span>
  </span>
</template>

<style scoped>
.plm-cube-icon{--plm-cube-size:24px;width:38px;height:38px;display:grid;flex:0 0 38px;place-items:center;perspective:120px}.plm-cube-icon__cube{position:relative;width:var(--plm-cube-size);height:var(--plm-cube-size);transform-style:preserve-3d;animation:plm-cube-spin 9s infinite linear}.plm-cube-icon__face{position:absolute;inset:0;box-sizing:border-box;display:grid;grid-template-columns:repeat(3,1fr);grid-template-rows:repeat(3,1fr);gap:.6px;overflow:hidden;border:0;border-radius:0;background:rgba(7,25,54,.58);box-shadow:none;backface-visibility:hidden}.plm-cube-icon__tile{min-width:0;min-height:0;border:0}.plm-cube-icon__label{position:absolute;top:50%;left:50%;z-index:1;width:14px;height:14px;display:grid;place-items:center;background:transparent;color:#fff;font-size:13px;font-weight:800;line-height:1;text-shadow:-1px 0 1px rgba(5,23,54,.9),0 1px 1px rgba(5,23,54,.9),1px 0 1px rgba(5,23,54,.9),0 -1px 1px rgba(5,23,54,.9);transform:translate(-50%,-50%)}.plm-cube-icon__label.is-latin{width:16px;height:16px;font-size:16px;line-height:16px}.plm-cube-icon__face.is-front{transform:translateZ(calc(var(--plm-cube-size)/2))}.plm-cube-icon__face.is-back{transform:rotateY(180deg) translateZ(calc(var(--plm-cube-size)/2))}.plm-cube-icon__face.is-left{transform:rotateY(-90deg) translateZ(calc(var(--plm-cube-size)/2))}.plm-cube-icon__face.is-right{transform:rotateY(90deg) translateZ(calc(var(--plm-cube-size)/2))}.plm-cube-icon__face.is-top{transform:rotateX(90deg) translateZ(calc(var(--plm-cube-size)/2))}.plm-cube-icon__face.is-bottom{transform:rotateX(-90deg) translateZ(calc(var(--plm-cube-size)/2))}@keyframes plm-cube-spin{0%,10%{transform:rotate3d(0,1,0,180deg)}16%,26%{transform:rotate3d(0,.707107,.707107,180deg)}32%,42%{transform:rotate3d(-.57735,.57735,.57735,120deg)}48%,58%{transform:rotate3d(-1,0,0,90deg)}64%,74%{transform:rotate3d(0,1,0,0deg)}80%,90%{transform:rotate3d(0,-1,0,90deg)}100%{transform:rotate3d(0,-1,0,180deg)}}@media(prefers-reduced-motion:reduce){.plm-cube-icon__cube{animation:none;transform:rotateX(-18deg) rotateY(-25deg)}}
.plm-cube-icon__cube{will-change:transform}
.plm-cube-icon__label{font-family:"Microsoft YaHei UI","Microsoft YaHei",sans-serif;font-synthesis:none;text-rendering:geometricPrecision;-webkit-font-smoothing:antialiased}
.plm-cube-icon__face.is-left .plm-cube-icon__label{transform:translate(-50%,-50%) rotate(-90deg)}
.plm-cube-icon__face.is-bottom .plm-cube-icon__label{transform:translate(-50%,-50%) rotate(180deg)}
</style>
