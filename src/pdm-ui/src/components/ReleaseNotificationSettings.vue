<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { readReleaseNotificationSettings, saveReleaseNotificationSettings } from '../api'
import { createClientUuid } from '../clientId'
import { userErrorMessage } from '../userErrorMessage'
import { notificationEvents, type ReleaseNotificationSettings } from '../releaseDelivery'

const props = defineProps<{ token: string }>()
const settings = ref<ReleaseNotificationSettings | null>(null)
const pending = ref(false), error = ref(''), notice = ref('')
const emailEnabled = computed(() => settings.value?.rules.some(rule => rule.channels.includes('Email')))
let loadVersion = 0
async function load() {
  const version = ++loadVersion
  pending.value = true; error.value = ''
  try { const value = await readReleaseNotificationSettings(props.token); if (version === loadVersion) settings.value = value }
  catch (cause) { if (version === loadVersion) error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知设置加载失败') }
  finally { if (version === loadVersion) pending.value = false }
}
watch(() => props.token, load, { immediate: true })
function addRule() {
  settings.value?.rules.push({ id: createClientUuid(), projectId: null, bomKind: 'All', event: 'Published', departmentIds: [],
    includeChildren: false, channels: ['InApp'], dingTalkGroupId: null, requireExecution: true })
}
async function save() {
  if (!settings.value || pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try { settings.value = await saveReleaseNotificationSettings(settings.value, props.token); notice.value = '发布通知配置已保存，接收部门由这些规则决定；已生成的通知保留原接收范围。' }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '通知配置保存失败') }
  finally { pending.value = false }
}
function departmentLabel(id: string) {
  const item = settings.value?.departments.find(value => value.id === id)
  const parent = settings.value?.departments.find(value => value.id === item?.parentUnitId)
  return parent ? `${parent.name} / ${item?.name}` : item?.name ?? id
}
</script>

<template>
  <section class="release-notification-settings" aria-label="发布与变更通知设置">
    <header><div><h3>发布与变更通知</h3><p>从公司组织中选择接收部门。站内消息保留记录，钉钉和邮件按需启用。部门成员仍需具有项目访问权限。</p></div><button type="button" :disabled="pending" @click="load">刷新</button></header>
    <p v-if="error" role="alert" class="notice-error">{{ error }}</p>
    <p v-if="notice" role="status" class="notice-success">{{ notice }}</p>
    <p v-if="pending && !settings">正在加载通知配置…</p>
    <form v-if="settings" @submit.prevent="save">
      <fieldset :disabled="pending">
        <label class="check"><input v-model="settings.enabled" type="checkbox" aria-label="启用发布通知">启用发布及变更通知</label>
        <p class="hint">启用前的历史发布不补发。普通存档不触发部门通知；项目专用规则优先于该事件的通用规则。</p>
        <label>PLM访问地址<input v-model="settings.publicUrl" type="url" placeholder="http://192.168.2.8:5173" aria-label="通知中的PLM访问地址"></label>
        <div class="rules-header"><h4>接收部门规则</h4><button type="button" @click="addRule">新增通知规则</button></div>
        <p v-if="!settings.rules.length" class="hint">尚未配置接收部门，系统不会自动指定或通知任何部门。</p>
        <article v-for="(rule, index) in settings.rules" :key="rule.id" class="notification-rule">
          <header><strong>规则 {{ index + 1 }}</strong><button type="button" :aria-label="`删除通知规则${index + 1}`" @click="settings.rules.splice(index, 1)">删除规则</button></header>
          <div class="rule-grid">
            <label>项目范围<select v-model="rule.projectId" :aria-label="`规则${index + 1}项目范围`"><option :value="null">当前公司所有项目</option><option v-for="project in settings.projects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label>
            <label>BOM类别<select v-model="rule.bomKind" :aria-label="`规则${index + 1}BOM类别`"><option value="All">所有类别</option><option value="Standard">标准件</option><option value="NonStandard">非标件</option><option value="Electrical">电气件</option></select></label>
            <label>通知时机<select v-model="rule.event" :aria-label="`规则${index + 1}通知时机`"><option v-for="(label, key) in notificationEvents" :key="key" :value="key">{{ label }}</option></select></label>
            <label>接收部门（可多选）<select v-model="rule.departmentIds" multiple size="4" :aria-label="`规则${index + 1}接收部门`"><option v-for="department in settings.departments" :key="department.id" :value="department.id">{{ departmentLabel(department.id) }}</option></select></label>
          </div>
          <div class="rule-options"><label class="check"><input v-model="rule.includeChildren" type="checkbox">包含所选部门的下级组织</label><label class="check"><input v-model="rule.requireExecution" type="checkbox">要求确认执行及填写处理结果</label></div>
          <div class="rule-options"><strong>通知渠道</strong><label class="check"><input type="checkbox" checked disabled>站内消息</label><label class="check"><input v-model="rule.channels" type="checkbox" value="DingTalk">钉钉</label><label class="check"><input v-model="rule.channels" type="checkbox" value="Email">邮件</label></div>
          <label v-if="rule.channels.includes('DingTalk')">钉钉接收群<select v-model="rule.dingTalkGroupId" :aria-label="`规则${index + 1}钉钉群`"><option :value="null">请选择已登记的群</option><option v-for="group in settings.groups" :key="group.id" :value="group.id">{{ group.name }}</option></select></label>
        </article>
        <section v-if="emailEnabled" class="mail-settings" aria-label="邮件发送设置"><h4>邮件发送设置</h4><p class="hint">邮件只发送通知摘要和登录入口，不附带图纸。使用STARTTLS加密连接，接收地址来自部门成员的用户资料。</p><div class="rule-grid">
          <label>SMTP服务器<input v-model="settings.mail.host" aria-label="SMTP服务器" autocomplete="off"></label><label>SMTP端口<select v-model.number="settings.mail.port" aria-label="SMTP端口"><option :value="587">587</option><option :value="25">25</option><option :value="2525">2525</option></select></label>
          <label>发件邮箱<input v-model="settings.mail.from" type="email" aria-label="发件邮箱"></label><label>登录账号<input v-model="settings.mail.username" aria-label="邮件登录账号" autocomplete="off"></label>
          <label>密码或授权码<input v-model="settings.mail.password" type="password" aria-label="邮件授权码" autocomplete="new-password" :placeholder="settings.hasMailPassword ? '已设置，留空保持原值' : '请输入授权码'"></label>
        </div></section>
        <footer><button type="submit" class="pdm-primary-action">{{ pending ? '正在保存…' : '保存通知设置' }}</button></footer>
      </fieldset>
    </form>
  </section>
</template>

<style scoped>
.release-notification-settings{padding:18px;max-width:1150px}.release-notification-settings header,.rules-header{display:flex;align-items:center;justify-content:space-between;gap:16px}.release-notification-settings h3,.release-notification-settings h4{margin:0 0 8px}.release-notification-settings p{line-height:1.65}.release-notification-settings fieldset{border:0;padding:0;min-width:0}.release-notification-settings label{display:flex;flex-direction:column;gap:7px;font-size:13px}.release-notification-settings input:not([type=checkbox]),.release-notification-settings select{width:100%;box-sizing:border-box;min-height:34px;padding:6px 9px;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-panel-bg,#fff);color:inherit}.release-notification-settings .check{display:flex;flex-direction:row;align-items:center;gap:6px}.hint{font-size:12px;color:var(--pdm-text-soft)}.rules-header{margin-top:22px}.notification-rule,.mail-settings{border:1px solid var(--pdm-border);border-radius:6px;padding:15px;margin:12px 0;background:var(--pdm-panel-bg,#fff)}.rule-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:14px;margin:14px 0}.rule-options{display:flex;gap:20px;flex-wrap:wrap;margin:12px 0;font-size:13px}.notice-error{color:var(--pdm-danger)}.notice-success{color:var(--pdm-green)}footer{display:flex;justify-content:flex-end;padding-top:14px}@media(max-width:700px){.rule-grid{grid-template-columns:1fr}.release-notification-settings{padding:10px}.release-notification-settings header{align-items:flex-start}}
</style>
