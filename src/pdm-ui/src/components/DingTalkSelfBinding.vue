<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { requestJson } from '../api'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string }>()
type Binding = { bound: boolean; code?: string; scanUrl?: string; dingTalkName?: string }
const binding = ref<Binding>(), pending = ref(false), error = ref('')
const qrImage = ref('')
const path = '/api/me/dingtalk-binding'
let disposed = false
let timer: ReturnType<typeof setTimeout> | undefined

async function update(action: 'open' | 'read' | 'issue') {
  if (disposed || pending.value) return
  clearTimeout(timer)
  pending.value = true
  error.value = ''
  try {
    const request = (method: 'GET' | 'POST') => requestJson<Binding>(`${path}?qr=true`, { method, cache: 'no-store' }, props.token)
    let next = await request(action === 'issue' ? 'POST' : 'GET')
    if (disposed) return
    if (action === 'open' && !next.bound && !next.code) next = await request('POST')
    if (disposed) return
    if (next.code && next.dingTalkName) {
      await requestJson<void>(`${path}/confirm`, { method: 'POST', body: JSON.stringify({ code: next.code }) }, props.token)
      next = { bound: true }
    }
    if (next.scanUrl && (next.scanUrl !== binding.value?.scanUrl || !qrImage.value)) {
      const { createQr } = await import('../productionDrawingQr')
      qrImage.value = (await createQr(next.scanUrl, '钉钉绑定')).src
    }
    if (!disposed) binding.value = next
  } catch (cause) {
    if (!disposed) error.value = userErrorMessage(cause instanceof Error ? cause.message : '读取钉钉绑定失败，请重试。')
  } finally {
    pending.value = false
    if (!disposed && !error.value && binding.value?.scanUrl) timer = setTimeout(() => void update('read'), 2500)
  }
}

onMounted(() => void update('open'))
onBeforeUnmount(() => { disposed = true; clearTimeout(timer) })
</script>

<template>
  <section class="pdm-dingtalk-self-binding" aria-label="绑定钉钉">
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="binding?.bound" role="status">当前公司已绑定钉钉，可从钉钉工作台进入 UPLM 移动办公。</p>
    <template v-else-if="binding?.scanUrl && qrImage && !error">
      <p>用本人钉钉「扫一扫」，在手机上确认后，保持此页打开，客户端会自动完成绑定。</p>
      <img class="pdm-dingtalk-binding-qr" :src="qrImage" alt="钉钉绑定二维码" width="200" height="200">
      <p role="status">等待手机确认，二维码五分钟内有效。</p>
    </template>
    <p v-else-if="pending" role="status">正在生成绑定二维码…</p>
    <p v-else-if="!error" role="status">二维码已失效，请重新生成。</p>
    <template v-if="!binding?.bound && (binding || error)">
      <div class="pdm-toolbar__group">
        <button v-if="error" type="button" class="pdm-secondary-action" :disabled="pending" @click="update('read')">刷新</button>
        <button type="button" class="pdm-secondary-action" :disabled="pending" @click="update('issue')">重新生成</button>
      </div>
    </template>
  </section>
</template>

<style scoped>
.pdm-dingtalk-binding-qr { display: block; max-width: 100%; height: auto; image-rendering: pixelated; }
</style>
