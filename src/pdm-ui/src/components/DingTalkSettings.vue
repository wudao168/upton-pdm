<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref } from 'vue'
import { readDingTalkSettings, saveDingTalkSettings } from '../api'
import type { DingTalkSettings } from '../dingtalk'
const props = defineProps<{ token: string }>()
const settings = ref<DingTalkSettings | null>(null), secret = ref(''), pending = ref(false), error = ref(''), notice = ref('')
const DingTalkEmployeeBindings = defineAsyncComponent(() => import('./DingTalkEmployeeBindings.vue'))
const MobileOfficeLayoutEditor = defineAsyncComponent(() => import('./MobileOfficeLayoutEditor.vue'))
const layoutsOpen = ref(false), layoutEditor = ref<{ canLeave: () => boolean }>()
function toggleLayouts() { if (!layoutsOpen.value || layoutEditor.value?.canLeave()) layoutsOpen.value = !layoutsOpen.value }
const employeesOpen = ref(false)
const previewValues: Record<string, string> = { 所属项目: 'P700014 · TEST 5', 子项目: '项目整体', 问题编号: '5', 问题描述: '测试', 优先级: '普通', 实施状态: '待处理', 负责人: '白红梅', 提交人: '王勇煌', 要求完成日期: '2026-10-17', 解决措施: '处理结果示例', 提交日期: '2026-10-16 14:25', 完成日期: '', 更新人: '王勇煌', 变更内容: '', 附件: '现场.png' }
const preview = computed(() => (settings.value?.notificationTemplate || settings.value?.defaultNotificationTemplate || '').split('\n')
  .filter(line => { const fields = [...line.matchAll(/\{([^{}\r\n]+)\}/g)]; return !fields.length || fields.some(match => previewValues[match[1]!] !== '') })
  .map(line => line.replace(/\{([^{}\r\n]+)\}/g, (placeholder, field: string) => previewValues[field] ?? placeholder)).join('\n'))
const message = (cause: unknown) => cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '钉钉配置操作失败，请检查网络后重试。'
async function load() { pending.value = true; error.value = ''; try { settings.value = await readDingTalkSettings(props.token) } catch (cause) { error.value = message(cause) } finally { pending.value = false } }
async function save() {
  if (!settings.value || pending.value) return
  if (employeesOpen.value) { error.value = '请先完成员工绑定保存并收起绑定设置，再保存应用配置。'; return }
  pending.value = true; error.value = ''; notice.value = ''
  try { settings.value = await saveDingTalkSettings({ ...settings.value, appSecret: secret.value, expectedRowVersion: settings.value.rowVersion }, props.token); secret.value = ''; notice.value = '钉钉企业应用配置已保存。' }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
onMounted(load)
</script>
<template>
  <section class="pdm-panel pdm-manager-panel ding-settings" aria-label="钉钉应用设置">
    <header class="pdm-manager-heading"><div><h2>钉钉应用</h2><p>配置当前公司的企业应用及现有机器人；每个项目群绑定一个主项目，子项目共用。</p></div><div class="ding-heading-actions"><el-button class="ding-action" :disabled="pending" @click="load">刷新</el-button><el-button class="ding-action" type="primary" :loading="pending" :disabled="!settings" @click="save">保存配置</el-button></div></header>
    <el-alert v-if="error" :title="error" type="error" :closable="false" /><el-alert v-if="notice" :title="notice" type="success" :closable="false" />
    <template v-if="settings">
      <div class="ding-fields pdm-settings-grid--four">
        <label class="pdm-dialog-field">启用应用<el-switch v-model="settings.enabled" :disabled="pending" /></label>
        <label class="pdm-dialog-field">CorpId<el-input v-model="settings.corpId" :disabled="pending" /></label>
        <label class="pdm-dialog-field">AppKey<el-input v-model="settings.appKey" :disabled="pending" /></label>
        <label class="pdm-dialog-field">AppSecret<el-input v-model="secret" type="password" show-password autocomplete="new-password" :disabled="pending" :placeholder="settings.hasAppSecret ? '已配置；留空保留原密钥' : '请输入应用密钥'" /></label>
        <label class="pdm-dialog-field">企业机器人RobotCode<el-input v-model="settings.robotCode" :disabled="pending" placeholder="从企业应用的机器人能力页面复制" /></label>
        <label class="pdm-dialog-field">PLM访问地址<el-input v-model="settings.publicUrl" :disabled="pending" placeholder="http://192.168.2.8:5173" /></label>
        <label class="pdm-dialog-field">统一应用首页地址<el-input :model-value="settings.entryUrl" readonly placeholder="保存访问地址后生成" /></label>
        <label class="pdm-dialog-field">员工移动办公首页地址<el-input :model-value="settings.officeEntryUrl" aria-label="员工移动办公首页地址" readonly placeholder="保存访问地址后生成" /></label>
      </div>
      <el-button class="ding-action" :disabled="pending" @click="toggleLayouts">{{ layoutsOpen ? '收起移动端页面维护' : '移动端页面维护' }}</el-button>
      <MobileOfficeLayoutEditor v-if="layoutsOpen" ref="layoutEditor" :token="token" />
      <el-button class="ding-action" :disabled="pending" @click="employeesOpen = !employeesOpen">{{ employeesOpen ? '收起员工绑定设置' : '员工账号绑定' }}</el-button>
      <DingTalkEmployeeBindings v-if="employeesOpen" :token="token" @saved="version => { if (settings) settings.rowVersion = version }" />
      <el-alert :title="settings.stream?.message || '保存应用配置后，点击刷新检查群登记连接。'" :type="settings.stream?.state === 'connected' ? 'success' : 'info'" :closable="false" />
      <section aria-label="群通知消息模板" class="ding-template">
        <div class="ding-toolbar"><h3>群通知消息模板</h3><el-button class="ding-action" :disabled="pending" @click="settings.notificationTemplate = settings.defaultNotificationTemplate">恢复默认模板</el-button></div>
        <div class="ding-template-columns">
          <label class="pdm-dialog-field ding-template-pane"><span>消息内容</span><el-input v-model="settings.notificationTemplate" aria-label="群通知消息内容" type="textarea" :rows="9" maxlength="6000" show-word-limit :disabled="pending" /></label>
          <div class="pdm-dialog-field ding-template-pane"><span>内容预览（新增问题示例）</span><pre class="ding-message-preview">{{ preview }}</pre></div>
        </div>
      </section>
    </template>
  </section>
</template>
<style scoped>
.ding-settings { flex:1;display:flex;flex-direction:column;gap:14px;overflow:auto;min-height:0;height:auto; }.ding-settings > * { flex-shrink:0; }.ding-fields { display:grid;max-width:1440px; }.ding-toolbar { display:flex;justify-content:space-between;align-items:center; } .ding-settings h3 { margin:8px 0; }
.ding-heading-actions { display:flex; flex-wrap:wrap; gap:8px; }
.ding-toolbar { flex-wrap:wrap; gap:8px; }
.ding-settings :deep(.ding-action) { margin-inline-start:0; }
@layer pdm-button-sizing {
  :root body .ding-settings :deep(button.el-button.ding-action[data-pdm-button-size]) {
    justify-content:flex-start !important;
    text-align:start !important;
  }
}
.ding-template { display:flex; flex-direction:column; gap:10px; container-type:inline-size; }
.ding-template-columns { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
.ding-template-pane { grid-template-rows:auto minmax(0,1fr); min-inline-size:0; }
.ding-message-preview {
  contain:size; overflow:auto; white-space:pre-wrap; overflow-wrap:anywhere;
  font:inherit; font-size:var(--pdm-font-size-body); color:var(--pdm-text);
  background:var(--pdm-surface-muted,var(--pdm-bg)); border:1px solid var(--pdm-border);
  border-radius:16px; padding:10px 12px; margin:0;
}
@container (max-width:640px) {
  .ding-template-columns { grid-template-columns:minmax(0,1fr); }
  .ding-message-preview { contain:none; min-block-size:140px; max-block-size:320px; }
}
</style>
