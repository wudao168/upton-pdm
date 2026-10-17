<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PanelActions from './PanelActions.vue'
import { readDingTalkBinding, saveDingTalkBinding, publishDingTalkEntry } from '../api'
import type { DingTalkBinding, DingTalkGroup } from '../dingtalk'
const props = defineProps<{ projectId: string; token: string }>()
const view = ref<DingTalkBinding | null>(null), selected = ref(''), pending = ref(false), error = ref(''), notice = ref('')
const entryField = ref<HTMLElement>()
const copying = ref(false)
const savedEntry = computed(() => selected.value && selected.value === view.value?.groupId ? view.value.entryUrl : '')
const group = computed(() => view.value?.groups.find(group => group.id === selected.value))
function unavailable(item: DingTalkGroup) {
  return !item.registered || (item.id !== view.value?.groupId && (item.boundProjects
    ? item.boundProjects.some(project => project.id !== view.value?.rootProjectId) : item.projectCount > 0))
}
function groupLabel(item: DingTalkGroup) {
  const projects = item.boundProjects?.map(project => `${project.code} · ${project.name}`).join('、')
  return `${item.name}（${projects ? `已关联：${projects}` : item.projectCount ? '已关联项目' : '未关联项目'}）${!item.registered ? ' · 待登记' : ''}`
}
const message = (cause: unknown) => cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '项目群绑定操作失败，请检查网络后重试。'
async function load() { pending.value = true; error.value = ''; try { view.value = await readDingTalkBinding(props.projectId, props.token); selected.value = view.value.groupId || '' } catch (cause) { error.value = message(cause) } finally { pending.value = false } }
async function save() {
  if (!view.value?.canManage || pending.value) return
  if (selected.value && group.value && unavailable(group.value)) { error.value = '该群已关联其他主项目或尚未登记，请选择可用的群。'; return }
  pending.value = true; error.value = ''; notice.value = ''
  try { view.value = await saveDingTalkBinding(props.projectId, { groupId: selected.value || null, expectedRowVersion: view.value.rowVersion }, props.token); selected.value = view.value.groupId || ''; notice.value = selected.value ? '项目群绑定已保存。' + (view.value.entryMessage || '') : '已解除当前主项目的群绑定。' }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
async function resend() {
  if (pending.value || !view.value?.canManage || !view.value.groupId) return
  pending.value = true; error.value = ''; notice.value = ''
  try { const result = await publishDingTalkEntry(props.projectId, props.token); if (result.sent) notice.value = result.message; else error.value = result.message }
  catch (cause) { error.value = message(cause) } finally { pending.value = false }
}
async function copyEntry() {
  if (!savedEntry.value || pending.value || copying.value) return
  copying.value = true; error.value = ''; notice.value = ''
  try {
    let copied = false
    if (navigator.clipboard?.writeText) {
      try { await navigator.clipboard.writeText(savedEntry.value); copied = true } catch { /* 内网HTTP或剪贴板权限受限时使用选中复制。 */ }
    }
    if (!copied) {
      const input = entryField.value?.querySelector('input')
      input?.focus(); input?.select()
      copied = Boolean(input && document.execCommand('copy'))
    }
    if (copied) notice.value = '群快捷入口地址已复制。'
    else error.value = '自动复制未成功，请选中地址后按 Ctrl+C 复制。'
  } catch { error.value = '自动复制未成功，请选中地址后按 Ctrl+C 复制。' }
  finally { copying.value = false }
}
onMounted(load)
</script>
<template>
  <section class="ding-binding" aria-label="钉钉项目群绑定">
    <h3>钉钉群绑定</h3><p>绑定关系属于主项目，子项目共用；同一群只能关联一个主项目。</p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" /><el-alert v-if="notice" :title="notice" type="info" :closable="false" />
    <p>在钉钉项目群中添加同一个企业机器人，@机器人发送“登记项目群”，然后刷新并选择该群。不需要填写群ID。</p>
    <template v-if="view">
      <el-alert v-if="!view.enabled" title="企业应用尚未启用。请在系统设置中的“钉钉应用”完成配置。" type="info" :closable="false" />
      <label class="pdm-dialog-field">选择已发现的群<el-select v-model="selected" clearable :disabled="pending || !view.canManage" placeholder="请选择机器人已登记的群"><el-option v-for="item in view.groups" :key="item.id" :label="groupLabel(item)" :value="item.id" :disabled="unavailable(item)" /></el-select></label>
      <label v-if="group" class="pdm-dialog-field">群快捷入口地址<div ref="entryField" class="ding-entry-copy-row"><el-input :model-value="savedEntry || group.entryUrl" aria-label="群快捷入口地址" readonly /><button type="button" class="pdm-secondary-action" :disabled="pending || copying || !savedEntry" @click="copyEntry">{{ copying ? '复制中…' : '复制' }}</button></div><small v-if="!savedEntry">当前为预览地址，保存群绑定后生效并可复制。</small></label>
      <p>保存后机器人会向绑定群发送“提交开口问题”入口，群管理员可将消息置顶。已关联其他主项目的群不可再次选择。</p><p v-if="!view.canManage">仅项目经理或管理员可修改绑定。</p>
      <PanelActions><el-button :disabled="pending" @click="load">刷新群绑定</el-button><el-button v-if="view.canManage" :disabled="pending || !view.groupId || selected !== view.groupId" @click="resend">重发提交入口</el-button><el-button v-if="view.canManage" type="primary" :loading="pending" @click="save">保存群绑定</el-button></PanelActions>
    </template>
  </section>
</template>
<style scoped>.ding-binding{display:flex;flex-direction:column;gap:14px}.ding-binding h3,.ding-binding p{margin:0}.ding-binding p{color:var(--pdm-muted)}.ding-entry-copy-row{display:flex;align-items:center;gap:8px;min-width:0}.ding-entry-copy-row>.el-input{flex:1;min-width:0}.ding-entry-copy-row>button{flex-shrink:0}.pdm-dialog-field>small{color:var(--pdm-muted);font-size:11px}</style>
