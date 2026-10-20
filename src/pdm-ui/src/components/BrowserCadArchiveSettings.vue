<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { readBrowserCadCapability, setBrowserCadEnabled } from '../api'
import { ElMessage } from '../statusMessage'
const props = defineProps<{ token: string }>()
const enabled = ref(false), canConfigure = ref(false), busy = ref(false), error = ref('')
onMounted(async () => { try { const result = await readBrowserCadCapability(undefined, props.token); enabled.value = result.enabled; canConfigure.value = result.canConfigure } catch { error.value = '网页存档设置暂未读取，请刷新后重试。' } })
async function save() {
  busy.value = true; error.value = ''
  try { await setBrowserCadEnabled(enabled.value, props.token); window.dispatchEvent(new Event('pdm-browser-archive-settings')); ElMessage.success('网页存档入口设置已保存') }
  catch (value) { error.value = value instanceof Error ? value.message : '设置保存失败，请重试。' }
  finally { busy.value = false }
}
</script>
<template><section v-if="canConfigure || error" class="pdm-panel pdm-manager-panel"><header class="pdm-manager-heading"><div><h2>网页存档入口</h2><p>统一控制项目图档和标准结构页面的“存档”按钮；不影响SolidWorks插件存档。</p></div></header><label v-if="canConfigure"><input v-model="enabled" type="checkbox" :disabled="busy">允许网页存档</label><p v-if="error" role="alert">{{ error }}</p><div v-if="canConfigure" class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="busy" @click="save">保存入口设置</button></div></section></template>
