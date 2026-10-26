<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import PanelActions from './PanelActions.vue'
import { createDingTalkBindingCommand, readDingTalkBinding, saveDingTalkBinding, publishDingTalkEntry } from '../api'
import type { DingTalkBinding } from '../dingtalk'
const props = defineProps<{ projectId: string; token: string }>()
const view = ref<DingTalkBinding | null>(null), pending = ref(false), error = ref(''), notice = ref('')
const entryField = ref<HTMLElement>(), commandField = ref<HTMLElement>()
const registrationCommand = ref(''), commandExpiresAt = ref(''), copying = ref(false)
const group = computed(() => view.value?.groups.find(item => item.id === view.value?.groupId))
const savedEntry = computed(() => group.value ? view.value?.entryUrl || '' : '')
const message = (cause: unknown) => cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '项目群绑定操作失败，请检查网络后重试。'
async function load() {
  pending.value = true; error.value = ''
  try { view.value = await readDingTalkBinding(props.projectId, props.token); notice.value = view.value.bindingMessage || '' }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
function refreshOnFocus() { if (!pending.value && !copying.value) void load() }
async function generateAndCopy() {
  if (!view.value?.canManage || pending.value || copying.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const result = await createDingTalkBindingCommand(props.projectId, props.token)
    registrationCommand.value = result.command; commandExpiresAt.value = result.expiresAt
    await nextTick(); pending.value = false
    await copyText(result.command, commandField.value, '本项目绑定码已复制，请先在目标群的 @ 列表中选中 UPLM，再粘贴指令发送；系统收到后会直接绑定。')
  } catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
async function unbind() {
  if (!view.value?.canManage || !view.value.groupId || pending.value) return
  try {
    await ElMessageBox.confirm('解除当前主项目与钉钉群的绑定？解除后，原群入口将不能再提交本项目问题，未使用的绑定指令也会失效。', '解除群绑定', { type: 'warning', confirmButtonText: '确认解除', cancelButtonText: '取消' })
  } catch { return }
  pending.value = true; error.value = ''
  try {
    view.value = await saveDingTalkBinding(props.projectId, { groupId: null, expectedRowVersion: view.value.rowVersion }, props.token)
    registrationCommand.value = ''; commandExpiresAt.value = ''; notice.value = '已解除当前主项目的群绑定。请在正确项目中生成新指令，到目标群中发送。'
  } catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
async function resend() {
  if (pending.value || !view.value?.canManage || !view.value.groupId) return
  pending.value = true; error.value = ''; notice.value = ''
  try { const result = await publishDingTalkEntry(props.projectId, props.token); if (result.sent) notice.value = result.message; else error.value = result.message }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
async function copyText(text: string, field: HTMLElement | undefined, success: string) {
  if (pending.value || copying.value || !text) return
  copying.value = true; error.value = ''; notice.value = ''
  try {
    let copied = false
    if (navigator.clipboard?.writeText) {
      try { await navigator.clipboard.writeText(text); copied = true } catch { /* 内网HTTP或剪贴板权限受限时使用选中复制。 */ }
    }
    if (!copied) {
      const input = field?.querySelector('input'); input?.focus(); input?.select()
      copied = Boolean(input && document.execCommand('copy'))
    }
    if (copied) notice.value = success
    else error.value = '自动复制未成功，请选中文字后按 Ctrl+C 复制。'
  } catch { error.value = '自动复制未成功，请选中文字后按 Ctrl+C 复制。' }
  finally { copying.value = false }
}
onMounted(() => { void load(); window.addEventListener('focus', refreshOnFocus) })
onBeforeUnmount(() => window.removeEventListener('focus', refreshOnFocus))
</script>
<template>
  <section class="ding-binding" aria-label="钉钉项目群绑定">
    <div v-if="view" class="ding-binding-fields" aria-label="项目群绑定信息">
      <label class="ding-binding-row"><span>当前项目群</span><el-input :model-value="group?.name || ''" aria-label="当前项目群" readonly placeholder="尚未绑定" /></label>
      <label ref="commandField" class="ding-binding-row"><span>项目绑定码</span><div class="ding-entry-copy-row"><el-input :model-value="registrationCommand" aria-label="项目绑定码" readonly placeholder="点击右侧按钮生成本项目绑定码" /><button type="button" class="pdm-secondary-action" aria-label="生成并复制项目绑定码" :disabled="pending || copying || !view.canManage || !view.enabled" @click="generateAndCopy">生成并复制</button></div></label>
      <label ref="entryField" class="ding-binding-row"><span>快捷入口地址</span><div class="ding-entry-copy-row"><el-input :model-value="savedEntry" aria-label="快捷入口地址" readonly placeholder="绑定项目群后生成" /><button type="button" class="pdm-secondary-action" aria-label="复制快捷入口地址" :disabled="pending || copying || !savedEntry" @click="copyText(savedEntry, entryField, '快捷入口地址已复制。')">复制</button></div></label>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" /><el-alert v-if="notice" :title="notice" type="info" :closable="false" />
    <template v-if="view">
      <PanelActions><el-button :disabled="pending" @click="load">刷新群绑定</el-button><el-button v-if="view.canManage" :disabled="pending || !view.groupId" @click="resend">重发提交入口</el-button><el-button v-if="view.canManage" :disabled="pending || !view.groupId" type="danger" plain @click="unbind">解除群绑定</el-button></PanelActions>
      <section class="ding-binding-descriptions" aria-label="绑定使用说明">
        <h3>使用说明</h3>
        <p>绑定关系属于当前主项目，子项目共用；同一群只能关联一个主项目。本页只显示当前项目的绑定，不再列出全部群。</p>
        <el-alert v-if="!view.enabled" title="企业应用尚未启用。请在系统设置中的“钉钉应用”完成配置。" type="info" :closable="false" />
        <section class="ding-binding-help" aria-label="直接绑定步骤">
          <h4>直接绑定本项目</h4>
          <ol>
            <li>将已配置的 UPLM 企业机器人添加到目标钉钉群。</li>
            <li>点击上方“生成并复制”，系统生成包含当前主项目号和一次性绑定码的专用指令。</li>
            <li>在目标群中选中 @UPLM，将本项目指令发送给机器人。系统会直接完成群与项目绑定，无需再选择群或点击保存。</li>
            <li>返回本页会自动刷新并显示绑定结果；也可点击“刷新群绑定”。成功绑定后机器人会尝试发送该群的“提交开口问题”入口。</li>
          </ol>
          <p v-if="commandExpiresAt">本次指令有效至 {{ new Date(commandExpiresAt).toLocaleString('zh-CN', { hour12: false }) }}，仅使用一次。重新生成后旧指令失效；服务重启后需重新生成。</p>
          <p>复制内容不包含 @UPLM。请先在钉钉 @ 列表中选中 UPLM 机器人，再粘贴指令发送。请只在要绑定的目标群中使用这条指令。</p>
          <p>项目或群已有其他绑定时，系统会拒绝直接改绑。绑定错误或需要更换群时，先点击“解除群绑定”，再生成新指令。</p>
        </section>
        <details class="ding-binding-help"><summary>企业机器人怎么设置（管理员）</summary><ol>
          <li>在钉钉企业内部应用中添加并启用“机器人”能力，名称设为 UPLM，消息接收模式选择 Stream。</li>
          <li>开通机器人发送消息、互动卡片、读取提交人姓名所需权限，发布应用版本，并将使用人员纳入可用范围。</li>
          <li>将同一应用的 CorpId、AppKey、AppSecret、RobotCode 填入 PLM“系统设置 → 钉钉应用”，启用并保存。刷新确认群登记连接显示“已连接”。</li>
        </ol></details>
        <section class="ding-binding-help" aria-label="功能说明">
          <h4>入口提交与群通知</h4>
          <p>快捷入口用于在钉钉内识别提交人姓名，填写问题并保存到 PLM；机器人用于发送入口、问题动态和处理提醒。只使用链接提交时，问题仍会保存；不保留机器人将无法发送群通知。</p>
          <p>群管理员可将快捷入口地址添加到钉钉群快捷栏，或将机器人发送的入口消息置顶。成员需有企业应用访问权限，并能访问 PLM；内网地址需连接公司网络或 VPN。</p>
        </section>
        <p v-if="!view.canManage">仅项目经理或管理员可修改绑定。</p>
      </section>
    </template>
  </section>
</template>
<style scoped>.ding-binding{display:flex;flex-direction:column;gap:14px}.ding-binding h3,.ding-binding p{margin:0}.ding-binding p{color:var(--pdm-muted)}.ding-binding-fields{display:grid;gap:12px}.ding-binding-row{display:grid;grid-template-columns:84px minmax(0,1fr);align-items:center;gap:12px}.ding-binding-row>span{white-space:nowrap}.ding-binding-descriptions{display:grid;gap:14px;margin-top:8px;padding-top:14px;border-top:1px solid var(--pdm-border)}.ding-binding-help{display:grid;gap:8px;line-height:1.6}.ding-binding-help h4{margin:0;font-size:inherit}.ding-binding-help ul,.ding-binding-help ol{margin:0;padding-left:20px;color:var(--pdm-muted)}.ding-binding-help li+li{margin-top:4px}.ding-binding-help summary{cursor:pointer;font-weight:600}.ding-entry-copy-row{display:flex;align-items:center;gap:8px;min-width:0}.ding-entry-copy-row>.el-input{flex:1;min-width:0}.ding-entry-copy-row>button{flex:0 0 88px;padding-left:8px;padding-right:8px;white-space:nowrap}</style>
