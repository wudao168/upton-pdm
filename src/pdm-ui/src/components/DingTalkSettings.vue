<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { readDingTalkSettings, saveDingTalkSettings } from '../api'
import type { DingTalkSettings } from '../dingtalk'
const props = defineProps<{ token: string }>()
const settings = ref<DingTalkSettings | null>(null), secret = ref(''), pending = ref(false), error = ref(''), notice = ref('')
const message = (cause: unknown) => cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '钉钉配置操作失败，请检查网络后重试。'
async function load() { pending.value = true; error.value = ''; try { settings.value = await readDingTalkSettings(props.token) } catch (cause) { error.value = message(cause) } finally { pending.value = false } }
async function save() {
  if (!settings.value || pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try { settings.value = await saveDingTalkSettings({ ...settings.value, appSecret: secret.value, expectedRowVersion: settings.value.rowVersion }, props.token); secret.value = ''; notice.value = '钉钉企业应用配置已保存。' }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
onMounted(load)
</script>
<template>
  <section class="pdm-panel pdm-manager-panel ding-settings" aria-label="钉钉应用设置">
    <header class="pdm-manager-heading"><div><h2>钉钉应用</h2><p>配置当前公司的企业内部H5应用；项目设置中绑定群，群可关联多个主项目。</p></div><div><el-button :disabled="pending" @click="load">刷新</el-button><el-button type="primary" :loading="pending" :disabled="!settings" @click="save">保存配置</el-button></div></header>
    <el-alert v-if="error" :title="error" type="error" :closable="false" /><el-alert v-if="notice" :title="notice" type="success" :closable="false" />
    <template v-if="settings">
      <div class="ding-fields">
        <label class="pdm-dialog-field">启用应用<el-switch v-model="settings.enabled" :disabled="pending" /></label>
        <label class="pdm-dialog-field">CorpId<el-input v-model="settings.corpId" :disabled="pending" /></label>
        <label class="pdm-dialog-field">AppKey<el-input v-model="settings.appKey" :disabled="pending" /></label>
        <label class="pdm-dialog-field">AppSecret<el-input v-model="secret" type="password" show-password autocomplete="new-password" :disabled="pending" :placeholder="settings.hasAppSecret ? '已配置；留空保留原密钥' : '请输入应用密钥'" /></label>
        <label class="pdm-dialog-field">企业机器人RobotCode<el-input v-model="settings.robotCode" :disabled="pending" placeholder="从企业应用的机器人能力页面复制" /></label>
        <label class="pdm-dialog-field">PLM访问地址<el-input v-model="settings.publicUrl" :disabled="pending" placeholder="http://192.168.2.8:5173" /></label>
        <label class="pdm-dialog-field">统一应用首页地址<el-input :model-value="settings.entryUrl" readonly placeholder="保存访问地址后生成" /></label>
      </div>
      <p class="pdm-counter-note">将生成的统一首页地址填写到钉钉应用首页及PC端首页。访客仅提交问题，自动记录钉钉姓名，无需PLM账号或账号绑定，也不校验群成员名单。</p>
      <el-alert :title="settings.stream?.message || '保存应用配置后，点击刷新检查群登记连接。'" :type="settings.stream?.state === 'connected' ? 'success' : 'info'" :closable="false" />
      <p class="pdm-counter-note">只使用当前企业应用内的一个机器人。在钉钉开发后台启用机器人并选择Stream模式；将同一个机器人加入各项目群，@机器人发送“登记项目群”。然后进入PLM项目设置选择已发现的群，无需填写群ID或Webhook。</p>
      <p class="pdm-counter-note">机器人发送图文通知并@负责人。负责人的PLM个人资料需填写钉钉对应手机号，应用需开通按手机号查询用户的权限；提交人无需绑定PLM账号。</p>
    </template>
  </section>
</template>
<style scoped>
.ding-settings { flex:1;display:flex;flex-direction:column;gap:14px;overflow:auto;min-height:0;height:auto; }.ding-settings > * { flex-shrink:0; }.ding-fields { display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px; }.ding-toolbar { display:flex;justify-content:space-between;align-items:center; } .ding-settings h3 { margin:8px 0; } @media(max-width:700px){.ding-fields{grid-template-columns:1fr}}
</style>
