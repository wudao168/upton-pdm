<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { requestJson } from '../api'
import { dingTalkOfficeAuthCode } from '../dingtalkOffice'
import { userErrorMessage } from '../userErrorMessage'

type ScanContext = { employeeName: string; corpId: string; appKey: string }
const companyId = new URLSearchParams(window.location.search).get('companyId')
const code = new URLSearchParams(window.location.hash.slice(1)).get('ticket')
const path = '/api/integrations/dingtalk/office'
const scan = ref<ScanContext>(), pending = ref(false), confirmed = ref(false), error = ref('')

async function load() {
  if (!companyId || !code || !/^[0-9a-f]{48}$/i.test(code)) {
    error.value = '二维码无效，请在客户端重新生成后扫码。'
    return
  }
  pending.value = true
  try {
    scan.value = await requestJson<ScanContext>(`${path}/binding-scan`, {
      method: 'POST', body: JSON.stringify({ companyId, code }),
    })
  } catch (cause) {
    error.value = userErrorMessage(cause instanceof Error ? cause.message : '无法读取绑定请求，请重新扫码。')
  } finally {
    pending.value = false
  }
}

async function confirm() {
  if (!scan.value || pending.value || confirmed.value) return
  pending.value = true
  error.value = ''
  try {
    const authCode = await dingTalkOfficeAuthCode(scan.value.corpId, scan.value.appKey, true)
    await requestJson<void>(`${path}/binding-proof`, {
      method: 'POST', body: JSON.stringify({ companyId, code, authCode, qr: true }),
    })
    confirmed.value = true
  } catch (cause) {
    error.value = userErrorMessage(cause instanceof Error ? cause.message : '确认绑定失败，请重新扫码。')
  } finally {
    pending.value = false
  }
}

onMounted(() => void load())
</script>

<template>
  <main class="pdm-dingtalk-scan">
    <h1>绑定本人钉钉</h1>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="confirmed" role="status">手机已确认。客户端将自动完成绑定，请在客户端查看结果。</p>
    <template v-else-if="scan">
      <p>请核对 UPLM 姓名：<strong>{{ scan.employeeName }}</strong></p>
      <p>确认后，将当前钉钉账号绑定到该员工账号。请确认是你本人。</p>
      <button type="button" class="pdm-primary-action" :disabled="pending" @click="confirm">
        {{ pending ? '正在验证' : '确认绑定' }}
      </button>
    </template>
    <p v-else-if="pending" role="status">正在读取绑定请求…</p>
  </main>
</template>

<style scoped>
.pdm-dingtalk-scan {
  box-sizing: border-box;
  width: min(100%, 440px);
  margin: 0 auto;
  padding: 32px 16px;
  line-height: 1.65;
}
.pdm-dingtalk-scan h1 { margin: 0 0 16px; font-size: 20px; }
.pdm-dingtalk-scan p { margin: 0 0 12px; overflow-wrap: anywhere; }
</style>
