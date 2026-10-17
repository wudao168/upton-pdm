<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'

type DingSdk = { env?: { platform?: string }; ready: (callback: () => void) => void; runtime: { permission: { requestAuthCode: (input: { corpId: string; onSuccess: (result: { code: string }) => void; onFail: () => void }) => void } } }
type ProjectOption = { id: string; code: string; name: string; subprojects: Array<{ id: string; code: string; name: string }> }
const query = new URLSearchParams(window.location.search)
const requestedProject = /^[\da-f-]{36}$/i.test(query.get('dingtalkOpl') || '') ? query.get('dingtalkOpl')! : ''
const projectId = ref(''), previousProject = ref(''), projects = ref<ProjectOption[]>([])
const selected = computed(() => projects.value.find(project => project.id === projectId.value))
const token = ref(''), user = ref(''), error = ref(''), notice = ref(''), pending = ref(false)
const description = ref(''), relatedProjectId = ref<string | null>(null), files = ref<File[]>([])
const priority = ref('普通'), status = ref('待处理'), owner = ref(''), dueDate = ref(''), solution = ref('')
const people = ref<Array<{ username: string; displayName: string }>>([])
const message = (cause: unknown, fallback: string) => cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : fallback
async function withTimeout<T>(promise: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  try { return await Promise.race([promise, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('钉钉授权响应超时，请重新打开群内入口。')), 20000) })]) }
  finally { if (timer) clearTimeout(timer) }
}
async function request(path: string, init: RequestInit = {}) {
  const headers = new Headers(init.headers)
  if (token.value) headers.set('X-DingTalk-Session', token.value)
  const response = await fetch(path, { ...init, headers })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}))
    throw new Error(typeof problem.title === 'string' && /[\u3400-\u9fff]/.test(problem.title) ? problem.title : '操作失败，请重新打开群入口后重试。')
  }
  return response.json()
}
function resetInput() {
  description.value = ''; files.value = []; priority.value = '普通'; status.value = '待处理'; owner.value = ''; dueDate.value = ''; solution.value = ''
  const input = document.querySelector<HTMLInputElement>('#dingtalk-opl-files'); if (input) input.value = ''
}
async function loadPeople() { people.value = projectId.value ? await request(`/api/integrations/dingtalk/opl/guest/projects/${projectId.value}/people`) : [] }
async function changeProject() {
  if ((description.value.trim() || solution.value.trim() || files.value.length) && !window.confirm('切换项目会清空尚未提交的问题，是否继续？')) { projectId.value = previousProject.value; return }
  resetInput(); relatedProjectId.value = null; error.value = ''; notice.value = ''; previousProject.value = projectId.value
  people.value = []; pending.value = true
  try { await loadPeople() } catch (cause) { error.value = message(cause, '负责人列表加载失败，可先保持待分派。') } finally { pending.value = false }
}
async function login() {
  pending.value = true
  try {
    const companyQuery = query.get('companyId') ? `?companyId=${encodeURIComponent(query.get('companyId')!)}` : ''
    const bootstrap = await request(`/api/integrations/dingtalk/opl/bootstrap${companyQuery}`)
    if (!bootstrap.enabled) throw new Error('钉钉提交尚未启用，请联系项目人员完成应用配置。')
    await withTimeout(new Promise<void>((resolve, reject) => {
      const script = document.createElement('script')
      // 多端SDK通过电脑钉钉容器识别PC，不依赖User-Agent中的DingTalk标记。
      script.src = 'https://g.alicdn.com/dingding/dingtalk-jsapi/3.0.25/dingtalk.open.js'
      script.onload = () => resolve(); script.onerror = () => reject(new Error('钉钉组件加载失败，请检查网络后重新打开。'))
      document.head.appendChild(script)
    }))
    const sdk = (window as unknown as { dd: DingSdk }).dd
    const inDingTalk = sdk?.env?.platform ? sdk.env.platform !== 'notInDingTalk' : /DingTalk/i.test(navigator.userAgent)
    if (!inDingTalk) throw new Error('请在钉钉内打开此入口，以记录提交姓名。')
    const code = await withTimeout(new Promise<string>((resolve, reject) => sdk.ready(() => sdk.runtime.permission.requestAuthCode({ corpId: bootstrap.corpId, onSuccess: result => resolve(result.code), onFail: () => reject(new Error('钉钉身份识别失败，请从已发布的钉钉应用或群入口重新打开。')) }))))
    const session = await request('/api/integrations/dingtalk/opl/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: requestedProject || null, companyId: bootstrap.companyId || null, groupId: query.get('groupId') || null, code }) })
    token.value = session.guestToken; user.value = session.displayName
    projects.value = await request('/api/integrations/dingtalk/opl/guest/projects')
    if (requestedProject && !projects.value.some(project => project.id === requestedProject)) throw new Error('项目入口已变更，请从群快捷入口重新打开。')
    projectId.value = requestedProject || (projects.value.length === 1 ? projects.value[0]!.id : '')
    previousProject.value = projectId.value
    await loadPeople()
  } catch (cause) { error.value = message(cause, '身份识别失败，请检查网络后重新打开。') }
  finally { pending.value = false }
}
async function submit() {
  if (pending.value || !selected.value) return
  error.value = ''; notice.value = ''
  if (!description.value.trim()) { error.value = '请填写问题描述。'; return }
  if (files.value.length > 10 || files.value.some(file => !file.size || file.size > 10 * 1024 * 1024) || files.value.reduce((sum, file) => sum + file.size, 0) > 30 * 1024 * 1024) { error.value = '最多10个附件，单个不超过10MB，总大小不超过30MB。'; return }
  pending.value = true
  try {
    const body = new FormData(); body.append('data', JSON.stringify({ description: description.value, relatedProjectId: relatedProjectId.value, priority: priority.value, status: status.value, owner: owner.value, dueDate: dueDate.value || null, solution: solution.value })); files.value.forEach(file => body.append('files', file))
    const result = await request(`/api/integrations/dingtalk/opl/guest/projects/${projectId.value}/submit`, { method: 'POST', body })
    notice.value = `问题 #${result.number} 已提交，提交人：${result.submittedBy}。${result.notificationMessage || (result.notificationSent ? '群通知已发送。' : '群通知暂未送达，问题已保存，无需重复提交。')}可以继续提交其他问题。`
    resetInput()
    await nextTick(); document.querySelector<HTMLElement>('.ding-opl')?.scrollTo?.({ top: 0, behavior: 'smooth' })
  } catch (cause) { error.value = message(cause, '问题提交失败，请检查网络后重试。') }
  finally { pending.value = false }
}
function selectFiles(event: Event) { files.value = Array.from((event.target as HTMLInputElement).files || []) }
onMounted(login)
</script>

<template>
  <main class="ding-opl">
    <header><h1>OPL 开口问题反馈</h1><p v-if="user">提交人：{{ user }}</p><p>描述现场问题并上传附件，即可提交给项目人员处理。</p></header>
    <p v-if="error" role="alert" class="ding-error">{{ error }}</p><p v-if="notice" role="status" class="ding-success">{{ notice }}</p>
    <p v-if="pending && !token">正在识别钉钉姓名…</p>
    <label v-if="token">所属项目<select v-model="projectId" :disabled="pending || projects.length === 1" @change="changeProject"><option v-if="projects.length !== 1" value="">请选择项目</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label>
    <p v-if="token && selected && query.get('groupId') && projects.length === 1">已自动关联当前群绑定的项目。</p>
    <p v-if="token && !pending && !projects.length">当前入口尚未关联项目，请联系项目人员设置群绑定。</p>
    <form v-if="token && selected" @submit.prevent="submit">
      <fieldset :disabled="pending">
        <label>子项目（非必填）<select v-model="relatedProjectId"><option :value="null">项目整体</option><option v-for="project in selected.subprojects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label>
        <label>问题描述<textarea v-model="description" required maxlength="2000" placeholder="请描述现场问题" /></label>
        <div class="ding-fields">
          <label>优先级<select v-model="priority"><option v-for="value in ['紧急','高','普通','低']" :key="value">{{ value }}</option></select></label>
          <label>实施状态<select v-model="status"><option v-for="value in ['待处理','处理中','待确认','已完成','已作废']" :key="value">{{ value }}</option></select></label>
          <label>负责人<select v-model="owner"><option value="">待分派</option><option v-for="person in people" :key="person.username" :value="person.username">{{ person.displayName }}</option></select></label>
          <label>需求完成日期<input v-model="dueDate" type="date"></label>
        </div>
        <label>解决措施<textarea v-model="solution" maxlength="4000" :required="status === '待确认'" placeholder="请填写处理过程和解决结果" /></label>
        <label>图片和附件<input id="dingtalk-opl-files" type="file" multiple @change="selectFiles"></label>
        <small>最多10个附件，单个不超过10MB，总大小不超过30MB。</small>
        <button type="submit">{{ pending ? '正在提交…' : '提交问题' }}</button>
      </fieldset>
    </form>
  </main>
</template>
<style scoped>
.ding-opl { max-width:720px;margin:0 auto;padding:20px;color:var(--pdm-text,#22324b);overflow:auto;height:100vh;box-sizing:border-box;background:white; }
.ding-fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 12px}
h1{font-size:20px}header p{color:#667085}fieldset{border:0;margin:0;padding:0;min-width:0}label{display:flex;flex-direction:column;gap:6px;margin:14px 0}input,select,textarea{box-sizing:border-box;width:100%;padding:10px;border:1px solid #cad5e1;border-radius:5px;font:inherit}textarea{height:100px;resize:vertical}button{padding:10px 16px;margin-top:12px;background:#087f79;border:0;border-radius:5px;color:white;cursor:pointer}fieldset:disabled{opacity:.65}.ding-error{color:#b42318}.ding-success{background:#effaf3;padding:12px;border-radius:5px;line-height:1.7}small{display:block;color:#667085}
</style>
