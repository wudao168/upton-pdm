<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import ProductionOperationPanel from './ProductionOperationPanel.vue'
import ProductionCameraScanner from './ProductionCameraScanner.vue'
import { login, readProductionTrackingRelease, resolveProductionScan, listMaterialStockPlans } from '../api'
import { type ProductionReleaseRecord, type ProductionScanResult } from '../productionTracking'
import { refreshMaterialStockPlans, setMaterialStockContext } from '../materialStockPlans'
import { userErrorMessage } from '../userErrorMessage'
const username = ref(''), password = ref(''), token = ref('')
const scan = ref(''), scanInput = ref<HTMLInputElement | null>(null)
const cameraScanner = ref<InstanceType<typeof ProductionCameraScanner>>()
const candidates = ref<ProductionScanResult[]>([]), selected = ref<ProductionScanResult | null>(null)
const record = ref<ProductionReleaseRecord | null>(null), pending = ref(false), error = ref('')
const line = computed(() => record.value?.state.lines.find(item => item.id === selected.value?.lineId))
function expired() { setMaterialStockContext(''); token.value = ''; selected.value = null; record.value = null; candidates.value = []; scan.value = ''; error.value = '登录已过期，请重新登录。' }
onMounted(() => window.addEventListener('pdm-session-expired', expired))
onBeforeUnmount(() => window.removeEventListener('pdm-session-expired', expired))
async function signIn() {
  if (pending.value) return
  pending.value = true; error.value = ''
  try {
    const session = await login(username.value, password.value)
    token.value = session.accessToken; username.value = session.username; password.value = ''
    if (session.activeCompanyId) window.localStorage.setItem('pdm_active_organization', session.activeCompanyId)
    setMaterialStockContext(JSON.stringify([session.username, session.activeCompanyId]))
    void refreshMaterialStockPlans(() => listMaterialStockPlans(token.value))
    await nextTick(); scanInput.value?.focus()
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
async function lookup() {
  if (!scan.value.trim() || pending.value) return
  pending.value = true; error.value = ''; selected.value = null; record.value = null
  try {
    candidates.value = await resolveProductionScan(scan.value.trim(), token.value)
    if (!candidates.value.length) error.value = '该图纸没有可查看的生产任务。'
    else if (candidates.value.length === 1) await choose(candidates.value[0])
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
async function choose(value: ProductionScanResult) {
  pending.value = true; error.value = ''
  try { record.value = await readProductionTrackingRelease(value.releasePackageId, token.value); selected.value = value; candidates.value = [] }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
function cameraDecoded(code: string) { scan.value = code; void lookup() }
</script>

<template>
  <main class="production-scan-page" aria-label="生产现场扫码">
    <header><strong>UPLM · 生产现场扫码</strong><div v-if="token" class="production-scan-page__header-actions"><button type="button" :disabled="pending" @click="cameraScanner?.openSettings()">扫码设置</button><button type="button" @click="expired">退出</button></div></header>
    <p v-if="error" role="alert" class="production-scan-page__error">{{ error }}</p>

    <form v-if="!token" class="production-scan-page__login" @submit.prevent="signIn"><h1>登录生产现场</h1><label>账号<input v-model="username" autocomplete="username" required></label><label>密码<input v-model="password" type="password" autocomplete="current-password" required></label><button type="submit" :disabled="pending">登录</button></form>
    <template v-else><form class="production-scan-page__scan" @submit.prevent="lookup"><label for="production-scan-code">扫描图纸／批次流转卡</label><div class="production-scan-page__code"><input id="production-scan-code" ref="scanInput" v-model="scan" placeholder="请扫码或输入二维码内容" autocomplete="off"><button type="submit" :disabled="pending">{{ pending ? '定位中…' : '定位' }}</button></div><ProductionCameraScanner ref="cameraScanner" :disabled="pending" @decoded="cameraDecoded" /></form>
      <section v-if="candidates.length > 1" class="production-scan-page__choices"><h2>请选择发布包与批次</h2><button v-for="item in candidates" :key="`${item.releasePackageId}-${item.lineId}-${item.lotId}`" type="button" @click="choose(item)">{{ item.packageNumber }} · {{ item.drawingNumber }} · {{ item.name }} · {{ item.revision }}</button></section>
      <section v-if="selected && record && line" class="production-scan-page__task"><h1 v-stocked-material="line.materialCode">{{ line.name }} <small>{{ line.revision }}</small></h1><p>{{ record.state.projectCode }} · {{ record.state.packageNumber }} · {{ line.drawingNumber }}</p>
        <ProductionOperationPanel compact :token="token" :username="username" :record="record" :line-id="line.id" :preferred-lot-id="selected.lotId || undefined" @updated="record = $event" />
      </section>
    </template>
  </main>
</template>

<style scoped>
.production-scan-page{min-height:100dvh;box-sizing:border-box;background:var(--pdm-bg);color:var(--pdm-text);font:15px/1.45 "Microsoft YaHei",sans-serif;padding:0 14px 24px}.production-scan-page>header{height:54px;margin:0 -14px 16px;padding:0 16px;background:var(--pdm-sidebar);color:#fff;display:flex;align-items:center;justify-content:space-between}.production-scan-page>header button{color:#fff;border:1px solid var(--pdm-sidebar-text-soft);background:transparent;border-radius:5px;padding:6px 10px}.production-scan-page h1{font-size:20px;margin:0 0 8px}.production-scan-page h2{font-size:16px}.production-scan-page h1 small{font-size:14px;color:var(--pdm-text-soft)}.production-scan-page form,.production-scan-page section{background:#fff;border:1px solid var(--pdm-border);border-radius:8px;padding:16px;margin:12px auto;max-width:620px}.production-scan-page label{display:grid;gap:7px;margin:12px 0;font-weight:600}.production-scan-page input,.production-scan-page select{height:44px;box-sizing:border-box;border:1px solid var(--pdm-text-soft);border-radius:5px;padding:0 10px;font:inherit;background:#fff;color:var(--pdm-text);min-width:0;width:100%}.production-scan-page button{min-height:44px;border:0;border-radius:5px;padding:8px 15px;background:var(--pdm-theme-accent);color:#fff;font:inherit;font-weight:700}.production-scan-page button:disabled{opacity:.6}.production-scan-page__login button,.production-scan-page__actions>button{width:100%}.production-scan-page__code{display:flex;gap:8px}.production-scan-page__scan button{flex:none}.production-scan-page__choices{display:grid;gap:8px}.production-scan-page__choices button{text-align:left;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);border:1px solid var(--pdm-theme-accent-border)}.production-scan-page__task>p{margin:4px 0 14px;color:var(--pdm-text-soft)}.production-scan-page__facts{display:grid;grid-template-columns:repeat(3,1fr);gap:6px}.production-scan-page__facts span{display:grid;gap:3px;background:var(--pdm-theme-accent-soft);padding:8px;border-radius:5px;font-size:12px}.production-scan-page__facts strong{font-size:16px}.production-scan-page__error,.production-scan-page__notice{max-width:620px;margin:8px auto;padding:10px;border-radius:6px}.production-scan-page__error{background:var(--pdm-danger-soft);color:var(--pdm-danger)}.production-scan-page__notice{background:var(--pdm-green-soft);color:var(--pdm-green)}
.production-scan-page{padding:0 10px 10px;font-size:14px}.production-scan-page>header{height:46px;margin:0 -10px 8px;padding:0 12px;gap:8px}.production-scan-page>header strong{font-size:14px;white-space:nowrap;min-width:0}.production-scan-page__header-actions{display:flex;gap:6px;flex:none}.production-scan-page>header button{min-height:32px;padding:4px 7px;font-size:13px}.production-scan-page form,.production-scan-page section{padding:12px;margin:8px auto}.production-scan-page label{gap:4px;margin:4px 0 6px;font-size:13px}.production-scan-page input,.production-scan-page select{height:38px;font-size:16px}.production-scan-page button{min-height:36px;padding:6px 10px;font-size:14px}.production-scan-page__task h1{font-size:18px;margin:0}.production-scan-page__task>p{margin:4px 0 8px;font-size:12px;overflow-wrap:anywhere}.production-scan-page :deep(.production-operation){font-size:13px;padding:0;margin:8px 0 0;border:0;border-radius:0;background:transparent}.production-scan-page :deep(.production-operation p){margin:6px 0}.production-scan-page :deep(.production-operation header button){min-height:28px;padding:4px 7px}.production-scan-page :deep(.production-operation fieldset){display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:4px 8px}.production-scan-page :deep(.production-operation label){margin:4px 0;gap:3px;font-weight:400}.production-scan-page :deep(.production-operation fieldset>p){grid-column:1/-1;margin:3px 0}.production-scan-page :deep(.production-operation input),.production-scan-page :deep(.production-operation select){height:36px;min-height:36px;font-size:16px}.production-scan-page :deep(.production-operation button){min-height:36px}.production-scan-page :deep(.production-operation form>button){width:100%;min-height:40px}.production-scan-page :deep(.production-operation__history),.production-scan-page :deep(.production-operation__controls){margin-top:6px;padding-top:6px}
.production-scan-page{height:100vh;height:100dvh;min-height:0;overflow-x:hidden;overflow-y:auto;-webkit-overflow-scrolling:touch}
</style>
