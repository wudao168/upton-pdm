<script setup lang="ts">
defineProps<{ tone: 'error' | 'new' }>()
</script>

<template>
  <span class="pdm-release-attention-border" :class="`is-${tone}`" aria-hidden="true" />
</template>

<style scoped>
.pdm-release-attention-border { position: absolute; inset: 0; padding: 2px; overflow: hidden; border-radius: inherit; pointer-events: none; --release-border-color: #dc2626; mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0); mask-composite: exclude; background: var(--release-border-color); animation: pdm-release-border-breathe 2.4s ease-in-out infinite; }
.pdm-release-attention-border.is-new { --release-border-color: #16a34a; }
@keyframes pdm-release-border-breathe { 0%, 100% { opacity: .25; } 50% { opacity: 1; } }
@media (prefers-reduced-motion: reduce) { .pdm-release-attention-border { animation: none; opacity: 1; } }
</style>
