<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import PlmCubeIcon from './PlmCubeIcon.vue'
import { readMobileOfficeLayout, saveMobileOfficeLayoutDraft, publishMobileOfficeLayout, rollbackMobileOfficeLayout } from '../api'
import { defaultMobileOfficeLayout, mobileOfficeBlockLabels, mobileOfficePageLabels, type MobileOfficeBlockKey, type MobileOfficeLayout, type MobileOfficeLayoutView, type MobileOfficePage } from '../mobileOfficeLayout'
import { userErrorMessage } from '../userErrorMessage'
import { chinaToday } from '../projectCalendar'
import type { MyApprovalTask, ProjectSummary } from '../types'
import MobileOfficeHome from './MobileOfficeHome.vue'
import MobileOfficeNavigation from './MobileOfficeNavigation.vue'
import '../styles/mobile-office.css'
import '../styles/mobile-office-layout.css'

const props = defineProps<{ token: string }>()
const view = ref<MobileOfficeLayoutView>(), draft = ref<MobileOfficeLayout>(defaultMobileOfficeLayout())
const pending = ref(false), error = ref(''), notice = ref(''), selected = ref<MobileOfficeBlockKey>('welcome')
const previewPage = ref<MobileOfficePage>('home'), previewWidth = ref(390), previewProjects = ref(true), historyPreview = ref<number | null>(null)
const confirmation = ref<{ kind: 'reload' | 'reset' | 'publish' | 'rollback'; version?: number }>()
const dragging = ref<{ kind: 'blocks' | 'navigation'; key: string }>()
const clone = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T
const dirty = computed(() => !!view.value && JSON.stringify(draft.value) !== JSON.stringify(view.value.draft))
const block = computed(() => draft.value.blocks.find(item => item.key === selected.value)!)
const previewLayout = computed(() => historyPreview.value === null ? draft.value : view.value?.history.find(item => item.version === historyPreview.value)?.layout ?? draft.value)
const editableBlocks = computed(() => draft.value.blocks.map((item, index) => ({ item, index })).filter(({ item }) => item.key !== 'shortcuts'))
const previewTasks = Array.from({ length: 3 }, (_, index) => ({ id: `preview-task-${index}`, projectId: 'preview-project', projectCode: 'P700014', projectName: '移动办公示例项目', releasePackageNumber: `REL-00${index + 1}`, stepName: '工程审核', stage: '工程审核', packageState: 'PendingApproval', createdAt: chinaToday() })) satisfies MyApprovalTask[]
const previewProjectRows = Array.from({ length: 3 }, (_, index) => ({ id: `preview-project-${index}`, code: `P70001${index + 4}`, name: ['装配线改造', '输送设备开发', '工装设计'][index], stage: '设计中', customerName: '示例客户', owner: 'preview', vaultName: '', vaultLocation: '', releaseLocation: '' })) as ProjectSummary[]
const confirmationText = computed(() => {
  if (confirmation.value?.kind === 'reload') return '重新读取将放弃本页尚未保存的修改，是否继续？'
  if (confirmation.value?.kind === 'reset') return '将当前编辑内容恢复为系统默认布局；保存并发布后才会影响员工。是否继续？'
  if (confirmation.value?.kind === 'publish') return '将已保存的草稿发布给当前公司的员工，重新进入移动办公后生效。是否继续？'
  return `将历史版本 V${confirmation.value?.version} 发布为一个新版本，保留当前草稿和全部历史。是否继续？`
})
function edited() { historyPreview.value = null; notice.value = ''; confirmation.value = undefined }
function move(kind: 'blocks' | 'navigation', key: string, target: number) {
  const list = draft.value[kind]
  const from = list.findIndex(item => item.key === key)
  if (from < 0 || target < 0 || target >= list.length || from === target) return
  // Both lists retain their own item type; moving only changes the order.
  if (kind === 'blocks') { const [item] = draft.value.blocks.splice(from, 1); draft.value.blocks.splice(target, 0, item!) }
  else { const [item] = draft.value.navigation.splice(from, 1); draft.value.navigation.splice(target, 0, item!) }
  edited()
}
function drop(kind: 'blocks' | 'navigation', index: number) {
  if (dragging.value?.kind === kind) move(kind, dragging.value.key, index)
  dragging.value = undefined
}
async function load() {
  if (pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try { view.value = await readMobileOfficeLayout(props.token); draft.value = clone(view.value.draft); historyPreview.value = null }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '页面配置读取失败，请检查网络后重新读取。') }
  finally { pending.value = false }
}
async function save() {
  if (!view.value || pending.value) return
  pending.value = true; error.value = ''; notice.value = ''; confirmation.value = undefined
  try { view.value = await saveMobileOfficeLayoutDraft(clone(draft.value), view.value.rowVersion, props.token); draft.value = clone(view.value.draft); notice.value = '草稿已保存，员工仍使用当前发布版本。' }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '草稿保存失败，当前修改已保留，请重试。') }
  finally { pending.value = false }
}
async function confirm() {
  const action = confirmation.value
  confirmation.value = undefined
  if (!action || pending.value) return
  if (action.kind === 'reload') { await load(); return }
  if (action.kind === 'reset') { draft.value = defaultMobileOfficeLayout(); edited(); return }
  if (!view.value || (action.kind === 'publish' && dirty.value)) return
  pending.value = true; error.value = ''; notice.value = ''
  try {
    view.value = action.kind === 'publish' ? await publishMobileOfficeLayout(view.value.rowVersion, props.token)
      : await rollbackMobileOfficeLayout(action.version!, view.value.rowVersion, props.token)
    notice.value = action.kind === 'publish' ? `已发布 V${view.value.publishedVersion}，员工重新进入或刷新后生效。`
      : `已回滚并发布为 V${view.value.publishedVersion}，草稿保持不变。`
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '页面发布操作失败，请重新读取发布记录核对后重试。') }
  finally { pending.value = false }
}
function canLeave() {
  if (pending.value || dirty.value) { error.value = '页面维护仍有未保存修改或操作正在进行，请先保存草稿或重新读取。'; return false }
  return true
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value || pending.value) { event.preventDefault(); event.returnValue = '' } }
onMounted(() => { window.addEventListener('beforeunload', beforeUnload); void load() })
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
defineExpose({ canLeave })
</script>
<template>
  <section class="office-layout-editor" aria-label="移动端页面维护">
    <header class="layout-toolbar"><div><h3>移动端页面维护</h3><p>仅维护员工移动办公首页及底部导航。手机预览使用示例数据。</p></div><div class="layout-actions"><el-button :disabled="pending" @click="dirty ? confirmation = { kind: 'reload' } : load()">重新读取</el-button><el-button :disabled="pending || !view" @click="confirmation = { kind: 'reset' }">恢复默认布局</el-button><el-button :disabled="pending || !view || !dirty" @click="save">保存草稿</el-button><el-button type="primary" :disabled="pending || !view || dirty" @click="confirmation = { kind: 'publish' }">发布草稿</el-button></div></header>
    <el-alert v-if="error" :title="error" type="error" :closable="false" /><el-alert v-if="notice" :title="notice" type="success" :closable="false" />
    <div v-if="confirmation" class="layout-confirm" role="alert"><p>{{ confirmationText }}</p><el-button :disabled="pending" @click="confirmation = undefined">取消操作</el-button><el-button type="primary" :disabled="pending" @click="confirm">确认{{ confirmation.kind === 'publish' ? '发布' : confirmation.kind === 'rollback' ? '回滚' : '继续' }}</el-button></div>
    <div v-if="pending" class="layout-loading" role="status" aria-label="正在处理页面配置"><span /><span /><span /></div>
    <template v-if="view">
      <p class="layout-status" role="status">当前发布：{{ view.publishedVersion ? `V${view.publishedVersion}` : '系统默认 V0' }} · {{ dirty ? '有未保存修改，请先保存草稿再发布' : '草稿已保存，可发布' }}</p>
      <fieldset :disabled="pending" class="layout-workspace">
        <div class="layout-outline">
          <h4>首页模块</h4><p>拖拽调整顺序，也可使用上移和下移。</p>
          <div v-for="({ item, index }, position) in editableBlocks" :key="item.key" class="layout-item" :data-selected="selected === item.key" :draggable="!pending" @dragstart="dragging = { kind: 'blocks', key: item.key }" @dragend="dragging = undefined" @dragover.prevent @drop.prevent="drop('blocks', index)">
            <button class="layout-select" :aria-pressed="selected === item.key" @click="selected = item.key; historyPreview = null">{{ mobileOfficeBlockLabels[item.key] }}</button><label><input v-model="item.enabled" type="checkbox" :aria-label="`显示${mobileOfficeBlockLabels[item.key]}`" @change="edited">显示</label>
            <div class="layout-actions">
              <button :disabled="position === 0" :aria-label="`上移${mobileOfficeBlockLabels[item.key]}`" @click="move('blocks', item.key, editableBlocks[position - 1]!.index)">上移</button>
              <button :disabled="position === editableBlocks.length - 1" :aria-label="`下移${mobileOfficeBlockLabels[item.key]}`" @click="move('blocks', item.key, editableBlocks[position + 1]!.index)">下移</button>
            </div>
          </div>
          <h4>底部导航</h4>
          <div v-for="(item, index) in draft.navigation" :key="item.key" class="layout-nav-item" :draggable="!pending" @dragstart="dragging = { kind: 'navigation', key: item.key }" @dragend="dragging = undefined" @dragover.prevent @drop.prevent="drop('navigation', index)">
            <strong>{{ mobileOfficePageLabels[item.key] }}</strong><label><input v-model="item.enabled" type="checkbox" :disabled="item.key === 'home' || item.key === 'mine'" :aria-label="`显示${mobileOfficePageLabels[item.key]}导航`" @change="edited">显示</label>
            <label>名称<input v-model="item.label" maxlength="8" :aria-label="`${mobileOfficePageLabels[item.key]}导航名称`" @input="edited"></label><label>图标<select v-model="item.icon" :aria-label="`${mobileOfficePageLabels[item.key]}导航图标`" @change="edited"><option v-for="(name, key) in mobileOfficePageLabels" :key="key" :value="key">{{ name }}</option></select></label>
            <div class="layout-actions"><button :disabled="index === 0" :aria-label="`上移${mobileOfficePageLabels[item.key]}导航`" @click="move('navigation', item.key, index - 1)">上移</button><button :disabled="index === draft.navigation.length - 1" :aria-label="`下移${mobileOfficePageLabels[item.key]}导航`" @click="move('navigation', item.key, index + 1)">下移</button></div>
          </div>
        </div>
        <div class="layout-preview-column">
          <div class="layout-preview-tools"><label>手机宽度<select v-model="previewWidth" aria-label="手机预览宽度"><option :value="320">320</option><option :value="390">390</option><option :value="430">430</option></select></label><label><input v-model="previewProjects" type="checkbox">预览项目权限</label><button @click="previewPage = 'home'; historyPreview = null">预览当前草稿</button></div>
          <p>{{ historyPreview === null ? '当前草稿预览' : `历史版本 V${historyPreview} 预览` }} · 示例数据，不执行真实业务操作</p>
          <div class="layout-phone-scroll"><div class="mobile-office layout-phone" :data-layout-theme="previewLayout.theme" :style="{ width: `${previewWidth}px` }" aria-label="手机页面预览"><header class="mobile-office-header"><div class="mobile-office-brand"><PlmCubeIcon /><strong>UPLM</strong></div><small class="mobile-office-user">员工张工</small></header><main class="mobile-office-main"><MobileOfficeHome v-if="previewPage === 'home'" :layout="previewLayout" display-name="员工张工" :today="chinaToday()" :tasks="previewTasks" :projects="previewProjects ? previewProjectRows : []" :unread="2" :today-hours="4.5" :can-view-projects="previewProjects" :selected-block="historyPreview === null ? selected : ''" @select="selected = $event" @navigate="previewPage = $event" @open-task="previewPage = 'tasks'" @open-project="previewPage = 'projects'" /><section v-else><h1>{{ mobileOfficePageLabels[previewPage] }}</h1><p>此处展示导航选中效果，业务页面沿用现有功能。</p><button @click="previewPage = 'home'">返回首页预览</button></section></main><MobileOfficeNavigation :layout="previewLayout" :page="previewPage" :can-view-projects="previewProjects" :task-count="5" :reminder-count="2" @navigate="previewPage = $event" /></div></div>
        </div>
        <div class="layout-properties" @input="edited" @change="edited">
          <h4>{{ mobileOfficeBlockLabels[selected] }}属性</h4><label>模块标题<input v-model="block.title" aria-label="模块标题" maxlength="20"></label>
          <label>模块宽度<select v-model="block.width" aria-label="模块宽度"><option :value="2">整行</option><option :value="1">半行</option></select></label>
          <label v-if="selected !== 'welcome'">内容列数<select v-model="block.columns" aria-label="内容列数"><option :value="1">单列</option><option :value="2">双列</option></select></label>
          <label v-if="selected === 'approvals' || selected === 'projects'">列表显示条数<input v-model.number="block.limit" aria-label="列表显示条数" type="number" min="1" max="20" step="1"></label>
          <h4>页面设置</h4><label>默认入口<select v-model="draft.defaultPage" aria-label="默认入口"><option v-for="item in draft.navigation.filter(item => item.enabled)" :key="item.key" :value="item.key">{{ item.label }}</option></select></label>
          <label>主题<select v-model="draft.theme" aria-label="移动端主题"><option value="green">绿色</option><option value="blue">蓝色</option><option value="purple">紫色</option></select></label><label>模块间距<select v-model="draft.spacing" aria-label="模块间距"><option value="comfortable">舒适</option><option value="compact">紧凑</option></select></label>
          <p>首页和我的入口始终保留。页面配置不会授予业务权限。没有项目权限的员工看不到项目模块和导航。</p>
        </div>
      </fieldset>
      <section class="layout-history" aria-label="页面发布历史"><h4>发布历史</h4><p>回滚会创建新版本，保留草稿及历史记录。</p><div v-for="entry in view.history" :key="entry.version" class="layout-history-row"><span><strong>V{{ entry.version }}</strong> {{ entry.version === view.publishedVersion ? '当前发布' : '' }} · {{ entry.action === 'default' ? '系统默认' : entry.action === 'rollback' ? `回滚自 V${entry.sourceVersion}` : '发布草稿' }} · {{ entry.publishedBy }} {{ entry.publishedAt ? new Date(entry.publishedAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai' }) : '' }}</span><div class="layout-actions"><button :disabled="pending" :aria-label="`预览版本V${entry.version}`" @click="historyPreview = entry.version; previewPage = 'home'">预览</button><button :disabled="pending || entry.version === view.publishedVersion" :aria-label="`回滚到V${entry.version}`" @click="confirmation = { kind: 'rollback', version: entry.version }">回滚到此版本</button></div></div></section>
    </template>
  </section>
</template>
<style scoped>
.office-layout-editor{display:grid;gap:16px;min-width:0}.office-layout-editor h3,.office-layout-editor h4{margin:0;font-weight:600}.office-layout-editor h3{font-size:20px;line-height:28px}.office-layout-editor h4{font-size:16px;line-height:24px}.office-layout-editor p{margin:4px 0;color:var(--pdm-text-muted,#64748b);font-size:14px;line-height:20px}.layout-toolbar,.layout-actions,.layout-history-row{display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap}.layout-toolbar{align-items:flex-start}.layout-status{padding:12px;background:var(--pdm-surface-muted,#f5f5f7);border-radius:8px}.layout-workspace{display:grid;grid-template-columns:minmax(200px,1fr) minmax(340px,2fr) minmax(180px,1fr);gap:24px;min-width:0;border:0;padding:0;margin:0}.layout-outline,.layout-properties{display:flex;flex-direction:column;gap:12px;min-width:0}.layout-properties>label,.layout-nav-item>label{display:grid;gap:4px;font-size:14px}.layout-item,.layout-nav-item{display:flex;align-items:center;gap:8px;flex-wrap:wrap;border:1px solid var(--pdm-border,#e2e8f0);border-radius:8px;padding:12px;background:var(--pdm-surface,#fff)}.layout-item[data-selected=true]{outline:2px solid var(--pdm-primary,#15803d);outline-offset:0}.layout-item .layout-select{flex:1;text-align:left}.layout-nav-item>label{flex:1;min-width:64px}.layout-nav-item>label:has(input[type=checkbox]){display:flex;align-items:center}.layout-nav-item>.layout-actions{width:100%;justify-content:flex-end}.office-layout-editor input:not([type=checkbox]),.office-layout-editor select{width:100%;min-width:0;line-height:20px;border:1px solid var(--pdm-border,#cbd5e1);border-radius:4px;padding:2px 12px;background:var(--pdm-surface,#fff);color:var(--pdm-text,#1e293b);font:inherit}.office-layout-editor button:not(.el-button):not(.layout-phone button){font:inherit;color:inherit;background:var(--pdm-surface,#fff);border:1px solid var(--pdm-border,#cbd5e1);border-radius:8px;padding:8px 12px;cursor:pointer}.office-layout-editor button:disabled{opacity:.5;cursor:default}.office-layout-editor :focus-visible{outline:2px solid var(--pdm-primary,#15803d);outline-offset:2px}.layout-preview-column{min-width:0}.layout-preview-tools{display:flex;gap:12px;flex-wrap:wrap;align-items:center}.layout-preview-tools label{display:flex;gap:4px;align-items:center;font-size:14px}.layout-preview-tools select{width:auto}.layout-phone-scroll{overflow:auto;max-width:100%;padding:8px}.layout-phone{margin:0 auto;max-width:none;min-height:0;height:740px;overflow:auto;position:relative;border:1px solid var(--pdm-border,#cbd5e1);border-radius:16px;background:#f5f5f7; color:var(--pdm-text);display:flex;flex-direction:column}.layout-phone .mobile-office-header{position:static;flex-shrink:0}.layout-phone .mobile-office-main{padding:16px;min-height:0;width:100%;flex:1;overflow:auto}.layout-phone .mobile-bottom-nav{position:static;transform:none;max-width:none;width:100%;flex-shrink:0;padding-bottom:8px}.layout-confirm{padding:16px;border:1px solid var(--pdm-primary,#15803d);border-radius:8px}.layout-history{display:grid;gap:12px}.layout-history-row{padding:12px;border:1px solid var(--pdm-border,#cbd5e1);border-radius:8px;font-size:14px}.layout-loading{display:grid;gap:8px}.layout-loading span{height:16px;border-radius:4px;background:var(--pdm-surface-muted,#e2e8f0)}.layout-loading span:nth-child(2){width:75%}.layout-loading span:nth-child(3){width:50%}@media(max-width:1100px){.layout-workspace{grid-template-columns:minmax(200px,1fr) minmax(340px,2fr)}.layout-properties{grid-column:1/-1;display:grid;grid-template-columns:repeat(2,minmax(0,1fr))}.layout-properties h4,.layout-properties p{grid-column:1/-1}}@media(max-width:700px){.layout-workspace{display:flex;flex-direction:column}.layout-properties{display:flex}.layout-preview-column{order:-1}.layout-toolbar .layout-actions{justify-content:flex-start}}
</style>
