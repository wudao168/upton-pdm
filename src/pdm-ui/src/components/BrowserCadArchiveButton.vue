<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { readBrowserCadCapability, readBrowserCadTicket, stageBrowserCadArchive, startBrowserCadArchive } from '../api'
import { createClientUuid } from '../clientId'
import { userErrorMessage } from '../userErrorMessage'
import type { BrowserCadCapability, BrowserCadTarget, BrowserCadTicket } from '../browserCadArchive'
import type { StandardStructure, StructureRevision } from '../standardStructure'
const props = defineProps<{ token: string; projectId?: string; selectedDocumentId?: string; structure?: StandardStructure; revision?: StructureRevision }>()
const emit = defineEmits<{ archived: [] }>()
const capability = ref<BrowserCadCapability>(), ticket = ref<BrowserCadTicket>()
const opened = ref(false), busy = ref(false), error = ref(''), rootPath = ref(''), drawingType = ref(9), updateSelected = ref(false)
const filesInput = ref<HTMLInputElement>(), folderInput = ref<HTMLInputElement>()
const running = computed(() => ticket.value && ['Queued', 'Processing', 'Committing'].includes(ticket.value.state))
const sessionKey = computed(() => `uplm-cad-archive:${props.projectId || 'standard'}`)
let timer: ReturnType<typeof setTimeout> | undefined
const announced = new Set<string>()
async function capabilities() { try { capability.value = await readBrowserCadCapability(props.projectId, props.token) } catch { capability.value = undefined } }
function settingsChanged() { void capabilities() }
onMounted(() => { void capabilities(); window.addEventListener('pdm-browser-archive-settings', settingsChanged) })
onBeforeUnmount(() => { clearTimeout(timer); window.removeEventListener('pdm-browser-archive-settings', settingsChanged) })
watch([() => props.token, () => props.projectId], () => { ticket.value = undefined; clearTimeout(timer); void capabilities() })
function remember(id: string) { try { sessionStorage.setItem(sessionKey.value, id) } catch { /* Server task is durable without browser storage. */ } }
async function poll() {
  if (!ticket.value) return
  try {
    ticket.value = await readBrowserCadTicket(ticket.value.id, props.token)
    if (ticket.value.state === 'Succeeded' && !announced.has(ticket.value.id)) { announced.add(ticket.value.id); emit('archived') }
  } catch (value) { error.value = userErrorMessage(value instanceof Error ? value.message : '任务状态读取失败，文件已保留。') }
  if (running.value) timer = setTimeout(poll, 1800)
}
async function open() {
  await capabilities(); if (!capability.value?.enabled || !capability.value.canArchive) return
  opened.value = true; error.value = ''
  if (!ticket.value) {
    let id: string | null = null; try { id = sessionStorage.getItem(sessionKey.value) } catch { /* optional resume */ }
    if (id) try { ticket.value = await readBrowserCadTicket(id, props.token); rootPath.value = ticket.value.rootPath || '' } catch { /* Other companies/users cannot read this task. */ }
  }
  if (running.value) { clearTimeout(timer); void poll() }
}
function reset() { clearTimeout(timer); ticket.value = undefined; rootPath.value = ''; error.value = ''; try { sessionStorage.removeItem(sessionKey.value) } catch { /* optional resume */ } }
async function choose(event: Event) {
  const input = event.target as HTMLInputElement, files = [...(input.files ?? [])]; input.value = ''; if (!files.length) return
  busy.value = true; error.value = ''; const id = createClientUuid(); remember(id)
  const target: BrowserCadTarget = props.projectId ? { projectId: props.projectId, selectedDocumentId: props.selectedDocumentId }
    : { structureId: updateSelected.value ? props.structure?.id : undefined, expectedStructureRevisionId: updateSelected.value ? props.revision?.id : undefined,
      drawingType: updateSelected.value ? props.structure?.drawingType ?? 9 : drawingType.value, name: updateSelected.value ? props.structure?.name : undefined }
  try {
    ticket.value = await stageBrowserCadArchive(id, target, files, props.token)
    rootPath.value = [...ticket.value.files].sort((a, b) => Number(!a.relativePath.toLowerCase().endsWith('.sldasm')) - Number(!b.relativePath.toLowerCase().endsWith('.sldasm'))
      || a.relativePath.split('/').length - b.relativePath.split('/').length || b.length - a.length)[0]?.relativePath ?? ''
  } catch (value) { error.value = userErrorMessage(value instanceof Error ? value.message : '文件接收未完成，请保留原文件后重试。') }
  finally { busy.value = false }
}
async function start() {
  if (!ticket.value || !rootPath.value) return
  busy.value = true; error.value = ''
  try { ticket.value = await startBrowserCadArchive(ticket.value.id, rootPath.value, props.token); clearTimeout(timer); void poll() }
  catch (value) { error.value = userErrorMessage(value instanceof Error ? value.message : '存档未开始，请稍后重试。'); void capabilities() }
  finally { busy.value = false }
}
</script>
<template>
  <button v-if="capability?.enabled" type="button" class="pdm-primary-action browser-cad-entry" :disabled="!capability.canArchive" :title="capability.reason || '选择已保存图档，由后台解析并存档'" @click="open">存档</button>
  <el-dialog v-model="opened" title="存档" width="620px" append-to-body :close-on-click-modal="false">
    <section class="browser-cad-dialog" aria-label="图档存档">
      <p>选择已保存的图档、完整文件夹或打包ZIP。后台按当前设计树和关联工程图存档，未变化文件沿用原版本。</p>
      <template v-if="!projectId"><label v-if="structure"><input v-model="updateSelected" type="checkbox" :disabled="busy || !!ticket">更新所选标准结构 {{ structure.code }}（不勾选则新增）</label><label>图纸类型<select v-model="drawingType" :disabled="busy || !!ticket || updateSelected" aria-label="存档图纸类型"><option :value="1">标准组件</option><option :value="2">标准零件</option><option :value="3">组件模板</option><option :value="4">零件模板</option><option :value="9">其他图档</option></select></label></template>
      <div v-if="!running && ticket?.state !== 'Succeeded'" class="browser-cad-pickers"><button type="button" :disabled="busy" @click="reset(); filesInput?.click()">选择文件</button><button type="button" :disabled="busy" @click="reset(); folderInput?.click()">选择文件夹</button><input ref="filesInput" type="file" multiple accept=".sldasm,.sldprt,.slddrw,.zip" hidden aria-label="选择存档文件" @change="choose"><input ref="folderInput" type="file" webkitdirectory multiple hidden aria-label="选择存档文件夹" @change="choose"></div>
      <p v-if="busy" role="status">正在接收文件，请保留此窗口…</p>
      <label v-if="ticket && !running && ticket.state !== 'Succeeded'">根图档<select v-model="rootPath" aria-label="存档根图档"><option v-for="file in ticket.files" :key="file.relativePath" :value="file.relativePath">{{ file.relativePath }}</option></select><small>请选择总装；单个零件或工程图可直接作为根图档。</small></label>
      <div v-if="ticket" role="status" class="browser-cad-status"><strong>{{ ticket.stage }}</strong><span v-if="ticket.state === 'Succeeded'">已处理 {{ ticket.archivedFiles }} 个图档，新增 {{ ticket.newVersions }} 个版本{{ ticket.resultCode ? ` · ${ticket.resultCode}` : '' }}。</span><small v-if="running">关闭窗口后后台仍会继续，重新点击“存档”可查看进度。</small></div>
      <p v-if="error || ticket?.error" class="browser-cad-error" role="alert">{{ error || ticket?.error }}</p><ul v-if="ticket?.warnings?.length"><li v-for="warning in ticket.warnings" :key="warning">{{ warning }}</li></ul>
    </section>
    <template #footer><button type="button" @click="opened = false">{{ running ? '后台继续' : '关闭' }}</button><button v-if="ticket && !running && ticket.state !== 'Succeeded'" type="button" class="pdm-primary-action" :disabled="busy || !rootPath" @click="start">存档</button><button v-if="ticket?.state === 'Succeeded'" type="button" @click="reset">继续存档</button></template>
  </el-dialog>
</template>
<style scoped>
.browser-cad-entry{margin-left:auto;white-space:nowrap;padding:3px 12px;flex-shrink:0}.browser-cad-dialog{display:grid;gap:12px;font-size:13px}.browser-cad-dialog p{margin:0;color:var(--pdm-muted)}.browser-cad-dialog label{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.browser-cad-dialog select{max-width:100%;min-width:160px;flex:1;padding:6px;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text)}.browser-cad-dialog small{color:var(--pdm-muted)}.browser-cad-pickers{display:flex;gap:8px}.browser-cad-status{display:grid;gap:6px}.browser-cad-error{color:var(--pdm-danger)!important}
</style>
