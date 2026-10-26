<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { readDingTalkSettings, saveDingTalkSettings } from '../api'
import type { DingTalkSettings } from '../dingtalk'
const props = defineProps<{ token: string }>()
const settings = ref<DingTalkSettings | null>(null), secret = ref(''), pending = ref(false), error = ref(''), notice = ref('')
const previewValues: Record<string, string> = { 所属项目: 'P700014 · TEST 5', 子项目: '项目整体', 问题编号: '5', 问题描述: '测试', 优先级: '普通', 实施状态: '待处理', 负责人: '白红梅', 提交人: '王勇煌', 要求完成日期: '2026-10-17', 解决措施: '处理结果示例', 提交日期: '2026-10-16 14:25', 完成日期: '', 更新人: '王勇煌', 变更内容: '', 附件: '现场.png' }
const preview = computed(() => (settings.value?.notificationTemplate || settings.value?.defaultNotificationTemplate || '').split('\n')
  .filter(line => { const fields = [...line.matchAll(/\{([^{}\r\n]+)\}/g)]; return !fields.length || fields.some(match => previewValues[match[1]!] !== '') })
  .map(line => line.replace(/\{([^{}\r\n]+)\}/g, (placeholder, field: string) => previewValues[field] ?? placeholder)).join('\n'))
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
    <header class="pdm-manager-heading"><div><h2>钉钉应用</h2><p>配置当前公司的企业应用及现有机器人；每个项目群绑定一个主项目，子项目共用。</p></div><div><el-button :disabled="pending" @click="load">刷新</el-button><el-button type="primary" :loading="pending" :disabled="!settings" @click="save">保存配置</el-button></div></header>
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
      <p class="pdm-counter-note">项目群内使用现有机器人发送的“提交开口问题”卡片；卡片携带群绑定信息，自动选择本群项目。项目设置可重发入口，请勿用统一应用首页地址替换群内卡片地址。</p>
      <el-alert :title="settings.stream?.message || '保存应用配置后，点击刷新检查群登记连接。'" :type="settings.stream?.state === 'connected' ? 'success' : 'info'" :closable="false" />
      <p class="pdm-counter-note">只使用当前企业应用内的一个机器人。在钉钉开发后台启用机器人并选择Stream模式；将同一个机器人加入目标项目群。在PLM项目设置中生成并复制本项目专用绑定指令，在目标群中 @UPLM 发送后直接绑定，无需从全部群中查找，也无需填写群ID或Webhook。</p>
      <p class="pdm-counter-note">负责人选项来自有项目访问权限的PLM在职用户，通知中显示负责人名称。通过项目专用指令绑定群后，即可提交问题并由现有机器人发送群通知。</p>
      <section aria-label="群通知消息模板" class="ding-template">
        <div class="ding-toolbar"><h3>群通知消息模板</h3><el-button :disabled="pending" @click="settings.notificationTemplate = settings.defaultNotificationTemplate">恢复默认模板</el-button></div>
        <p class="pdm-counter-note">适用于新增、更新、完成和处理提醒。可修改分类名称、字段顺序、文字和换行；不需要的字段直接删除。留空使用默认模板，保存配置后生效。</p>
        <label class="pdm-dialog-field">消息内容<el-input v-model="settings.notificationTemplate" aria-label="群通知消息内容" type="textarea" :rows="9" maxlength="6000" show-word-limit :disabled="pending" /></label>
        <p class="pdm-counter-note">可用字段：<span v-for="field in settings.notificationFields" :key="field">{{ '{' + field + '}' }} </span>。字段均为空的行自动省略，图片仍随卡片发送。@负责人由系统发送，不受模板影响。</p>
        <strong>内容预览（新增问题示例）</strong><pre class="ding-message-preview">{{ preview }}</pre>
        <p class="pdm-counter-note">编辑问题时可勾选“保存后通知钉钉群”；在清单勾选“更新或完成后通知钉钉群”后，点击完成也会通知。更新通知标题会显示问题已更新或已完成，{变更内容}显示本次变化。</p>
      </section>
    </template>
  </section>
</template>
<style scoped>
.ding-settings { flex:1;display:flex;flex-direction:column;gap:14px;overflow:auto;min-height:0;height:auto; }.ding-settings > * { flex-shrink:0; }.ding-fields { display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px; }.ding-toolbar { display:flex;justify-content:space-between;align-items:center; } .ding-settings h3 { margin:8px 0; } @media(max-width:700px){.ding-fields{grid-template-columns:1fr}}
.ding-template{display:flex;flex-direction:column;gap:10px}.ding-message-preview{white-space:pre-wrap;overflow-wrap:anywhere;font:inherit;background:var(--pdm-surface-muted,var(--pdm-bg));border:1px solid var(--pdm-border);border-radius:6px;padding:14px;margin:0}
</style>
