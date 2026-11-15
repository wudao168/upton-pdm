<script setup lang="ts">
import galaxyImage from '../assets/hubble-m106.jpg'
</script>

<template>
  <div class="galaxy-scene">
    <div class="galaxy-scene__visual" aria-hidden="true">
      <img class="pdm-login-artwork__image galaxy-scene__image" :src="galaxyImage" alt="" fetchpriority="high">
      <div class="galaxy-scene__shade"></div>
    </div>
    <div class="galaxy-scene__info">
      <span>M106 <span class="galaxy-scene__label">· 浩瀚星河，共创未来</span></span>
    </div>
  </div>
</template>

<style scoped>
.galaxy-scene { position: absolute; inset: 0; overflow: hidden; background: #050810; }
.galaxy-scene__visual { position: absolute; inset: 0; overflow: hidden; }
.galaxy-scene__image { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: 52% 50%; }
.galaxy-scene__shade { position: absolute; inset: 0; background: linear-gradient(180deg, rgba(3,8,19,.58), transparent 45%, rgba(3,8,19,.1) 70%, #030813 100%), linear-gradient(90deg, rgba(3,8,19,.32), transparent 60%); }
.galaxy-scene__info { position: absolute; z-index: 2; bottom: 30px; left: clamp(22px,4vw,72px); right: clamp(22px,4vw,72px); color: #d6dfef; font-size: 14px; letter-spacing: .06em; }
.galaxy-scene__label { font-size: 12px; }
@media (max-width: 760px) {
  .galaxy-scene__image { object-position: 61% 50%; }
  .galaxy-scene__info { bottom: 22px; left: 22px; right: 22px; }
  .galaxy-scene__label { font-size: 11px; letter-spacing: 0; }
}
</style>
