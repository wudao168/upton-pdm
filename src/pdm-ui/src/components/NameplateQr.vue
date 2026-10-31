<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
const props = defineProps<{ serialNumber: string }>()
const qr = ref<{ content: string; src: string }>(), error = ref(''), loading = ref(false)
let generation = 0
onBeforeUnmount(() => { generation++ })
async function refresh() {
  const current = ++generation
  qr.value = undefined; error.value = ''; loading.value = true
  try {
    const { createNameplateQr } = await import('../productionDrawingQr')
    const result = await createNameplateQr(props.serialNumber)
    if (current === generation) qr.value = result
  } catch { if (current === generation) error.value = '铭牌二维码生成失败，请核对序列号后重试。' }
  finally { if (current === generation) loading.value = false }
}
watch(() => props.serialNumber, refresh, { immediate: true })
</script>

<template>
  <div class="nameplate-qr" aria-label="铭牌序列号二维码">
    <span>铭牌序列号</span>
    <p v-if="loading" role="status">正在生成二维码…</p>
    <template v-else-if="qr"><img :src="qr.src" :title="qr.content" alt="铭牌序列号二维码" width="96" height="96"><span>{{ qr.content }}</span></template>
    <p v-else-if="error" role="alert">{{ error }} <button type="button" @click="refresh">重试</button></p>
  </div>
</template>

<style scoped>
.nameplate-qr{display:grid;flex:0 0 96px;width:96px;justify-items:center;gap:4px;font-size:11px;line-height:1.4;text-align:center;overflow-wrap:anywhere}.nameplate-qr img{display:block;background:#fff;max-width:100%}.nameplate-qr p{margin:0}.nameplate-qr [role=alert]{color:var(--pdm-danger)}.nameplate-qr button{border:0;background:transparent;color:var(--pdm-theme-accent);font:inherit;cursor:pointer}
</style>
