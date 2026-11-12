<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { aiError, aiJson, aiRequestId, streamAi, type AiBootstrap, type AiConversation, type AiMessage, type AiSource } from '../aiAssistant'
import AiProviderSettings from './AiProviderSettings.vue'
const props = defineProps<{ token: string }>()
const emit = defineEmits<{ openSource: [source: AiSource] }>()
const boot = ref<AiBootstrap>(), conversations = ref<AiConversation[]>([]), current = ref<AiConversation>()
const model = ref(''), business = ref(false), draft = ref(''), search = ref(''), error = ref(''), notice = ref(''), busy = ref(false), loading = ref(false), settingsOpen = ref(false)
const messages = ref<HTMLElement>(); let controller: AbortController | undefined, disposed = false
const filtered = computed(() => conversations.value.filter(c => c.title.includes(search.value)))
const selected = computed(() => boot.value?.models.find(m => m.key === model.value))
const businessReason = computed(() => !boot.value?.canBusiness ? '当前账号未获“使用AI只读业务查询”权限，请在权限中心核对。' : !selected.value ? '请先选择模型。' : !selected.value.tools ? '当前模型未开启工具调用，请在服务配置中通过工具测试并开启业务工具调用。' : '')
const canSend = computed(() => boot.value?.canChat && !!selected.value && !!draft.value.trim() && !busy.value && !loading.value && (!business.value || !businessReason.value))
async function refresh() {
  loading.value = true; error.value = ''
  try {
    boot.value = await aiJson<AiBootstrap>('bootstrap', props.token)
    if (!boot.value.models.some(m => m.key === model.value)) model.value = boot.value.models.find(m => m.key === boot.value?.defaultModel)?.key ?? boot.value.models[0]?.key ?? ''
    conversations.value = boot.value.canChat ? await aiJson<AiConversation[]>('conversations', props.token) : []
    if (!boot.value.canChat && boot.value.canManage) settingsOpen.value = true
  } catch (e) { error.value = aiError(e) } finally { loading.value = false }
}
function fresh() { if (busy.value) return; current.value = undefined; draft.value = ''; error.value = ''; notice.value = '' }
async function open(id: string) {
  if (busy.value) return; loading.value = true; error.value = ''; notice.value = ''
  try { current.value = await aiJson<AiConversation>(`conversations/${id}`, props.token); model.value = current.value.model; business.value = current.value.business; await scroll() }
  catch (e) { current.value = undefined; error.value = aiError(e) } finally { loading.value = false }
}
async function remove() {
  if (!current.value || busy.value || !window.confirm('确定删除本人的这段对话吗？')) return
  try { await aiJson(`conversations/${current.value.id}`, props.token, 'DELETE'); fresh(); await refresh() } catch (e) { error.value = aiError(e) }
}
async function scroll() { await nextTick(); messages.value?.scrollTo({ top: messages.value.scrollHeight }) }
async function send(text = draft.value.trim()) {
  if (!text || busy.value || !model.value) return
  busy.value = true; error.value = ''; notice.value = ''; controller = new AbortController()
  let assistant: AiMessage | undefined
  try {
    if (!current.value) current.value = await aiJson<AiConversation>('conversations', props.token, 'POST', { model: model.value, business: business.value })
    const requestId = aiRequestId(); draft.value = ''
    current.value.messages.push({ id: requestId, role: 'user', text, status: 'complete', createdAt: new Date().toISOString() })
    current.value.messages.push({ id: aiRequestId(), role: 'assistant', text: '', status: 'generating', createdAt: new Date().toISOString() })
    assistant = current.value.messages[current.value.messages.length - 1]!
    await streamAi(current.value.id, { requestId, text, model: model.value, business: business.value }, props.token, controller.signal, (event, data) => {
      if (disposed || !assistant) return
      if (event === 'start' && current.value) { current.value.business = data.business ?? business.value; if (current.value.messages.length === 2) current.value.title = text.slice(0, 32) }
      if (event === 'delta') assistant.text += data.text ?? ''
      if (event === 'start' && data.omittedHistory) notice.value = '较早的对话超出当前模型容量，本次仅使用最近的完整轮次。'
      if (event === 'error') { error.value = data.message ?? '生成失败，请重试。'; if (data.clear) assistant.text = '' }
      if (event === 'done') Object.assign(assistant, { status: data.status, sources: data.sources, inputTokens: data.inputTokens, outputTokens: data.outputTokens })
      void scroll()
    })
  } catch (e) {
    if (assistant) assistant.status = controller.signal.aborted ? 'stopped' : 'failed'
    error.value = controller.signal.aborted ? '已停止生成，稍后刷新可核对服务端保存的内容。' : aiError(e)
    if (!assistant) draft.value = text
  } finally {
    busy.value = false; controller = undefined
    if (!disposed) { try { conversations.value = await aiJson<AiConversation[]>('conversations', props.token) } catch {} }
  }
}
function retry() { const last = current.value?.messages.filter(m => m.role === 'user').at(-1); if (last) void send(last.text) }
function keydown(event: KeyboardEvent) { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); if (canSend.value) void send() } }
onMounted(refresh); onBeforeUnmount(() => { disposed = true; controller?.abort() })
</script>

<template>
  <section class="ai-workspace" aria-label="AI助手">
    <aside class="ai-history"><header><strong>AI助手</strong><button class="ai-primary" :disabled="busy || !boot?.canChat" @click="fresh">新建对话</button></header>
      <input v-model="search" type="search" placeholder="搜索对话" aria-label="搜索对话" />
      <nav aria-label="历史对话"><button v-for="c in filtered" :key="c.id" class="ai-history-item" :class="{ 'is-active': c.id === current?.id }" :title="c.title" :disabled="busy" @click="open(c.id)"><span>{{ c.title }}</span><small>{{ c.business ? '业务查询' : '普通对话' }}</small></button></nav>
      <p v-if="!filtered.length" class="ai-muted">暂无对话</p><button :disabled="busy || loading" @click="refresh">刷新</button>
      <button v-if="boot?.canManage" :disabled="busy" @click="settingsOpen = true">服务配置</button>
    </aside>
    <main class="ai-chat">
      <header class="ai-chat-header"><div><strong>{{ current?.title ?? '新的对话' }}</strong><p class="ai-muted">{{ business ? '只读查询 · 按当前公司和项目权限读取' : '普通对话 · 不读取PLM业务资料' }}</p></div>
        <div class="ai-actions"><select v-model="model" aria-label="选择模型" :disabled="busy || loading"><option value="">请选择模型</option><option v-for="m in boot?.models ?? []" :key="m.key" :value="m.key">{{ m.provider }} · {{ m.name }}</option></select><button v-if="current" :disabled="busy" @click="remove">删除对话</button></div>
      </header>
      <p v-if="error" class="ai-error" role="alert">{{ error }}</p><p v-if="notice" class="ai-notice" role="status">{{ notice }}</p>
      <div v-if="boot && !business" class="ai-mode-hint"><span>{{ businessReason || '普通对话可问日期和基础使用说明。查询项目状态、BOM等实时资料，请启用只读业务查询。' }}</span><button v-if="!businessReason" :disabled="busy || loading" @click="business = true">启用查询</button></div>
      <details class="ai-capabilities"><summary>可查询的内容与范围</summary><p>基础说明：项目、BOM、物料、权限与AI配置。业务查询：项目状态与交期、BOM明细及发布状态、物料主档，均按实际权限读取。</p><p>暂未开放其他业务明细、联网搜索或网页读取；不能执行修改、审批或发布。主项目与子项目分别核对。</p></details>
      <div ref="messages" class="ai-messages" aria-label="对话内容" :aria-busy="busy">
        <div v-if="!current?.messages.length" class="ai-welcome"><span class="ai-avatar ai-avatar-large">AI</span><h2>今天需要了解什么？</h2><p>选择模型开始对话，或开启业务查询获取有权查看的资料。</p>
          <div class="ai-starters"><button @click="draft = '如何在系统中查看项目BOM及发布状态？'">查看使用说明</button><button :disabled="!!businessReason" @click="business = true; draft = '查询我有权限查看的项目，并列出计划交期。'">查询项目交期</button><button :disabled="!!businessReason" @click="business = true; draft = '帮我查找名称包含传感器的物料。'">查找物料</button></div>
          <p v-if="boot && !boot.models.length" class="ai-notice">暂无可用模型。请配置并启用模型；云端模型还需单独授权。</p>
        </div>
        <article v-for="m in current?.messages ?? []" :key="m.id" class="ai-message" :class="{ 'ai-message-user': m.role === 'user' }">
          <header><span class="ai-avatar">{{ m.role === 'user' ? '我' : 'AI' }}</span><strong>{{ m.role === 'user' ? '我的问题' : 'AI助手' }}</strong><small v-if="m.status !== 'complete'">{{ m.status === 'generating' ? '正在生成…' : m.status === 'stopped' ? '已停止' : '未完成' }}</small></header>
          <div class="ai-message-text">{{ m.text || (m.status === 'generating' ? '正在处理，请稍候…' : '未生成回答') }}</div>
          <div v-if="m.sources?.length" class="ai-sources"><span>资料来源</span><button v-for="(source, i) in m.sources" :key="i" :disabled="busy" @click="emit('openSource', source)">{{ source.name }}</button></div>
          <small v-if="m.inputTokens != null" class="ai-muted">输入 {{ m.inputTokens }} · 输出 {{ m.outputTokens ?? 0 }} token</small>
        </article>
      </div>
      <footer class="ai-composer"><div class="ai-actions"><label :title="businessReason || '可在当前会话切换，从下一条消息生效。'"><input v-model="business" type="checkbox" :disabled="busy || loading || (!business && !!businessReason)" />只读业务查询</label><span v-if="selected?.network === 'cloud'" class="ai-muted">云端模型：本次对话与引用资料将发送至该服务</span></div>
        <p v-if="business && businessReason" class="ai-error">{{ businessReason }}也可关闭只读业务查询继续普通对话。</p>
        <textarea v-model="draft" :disabled="!boot?.canChat || busy" rows="3" maxlength="4000" placeholder="输入问题，Enter发送，Shift+Enter换行" aria-label="输入问题" @keydown="keydown"></textarea>
        <div class="ai-composer-bottom"><small class="ai-muted">{{ draft.length }}/4000 · AI回答请结合来源核对</small><div class="ai-actions"><button v-if="current?.messages.length" :disabled="busy || !model" @click="retry">重新回答</button><button v-if="busy" @click="controller?.abort()">停止生成</button><button v-else class="ai-primary" :disabled="!canSend" @click="send()">发送</button></div></div>
      </footer>
    </main>
    <el-drawer v-model="settingsOpen" title="AI服务配置" size="min(1050px, 96vw)" append-to-body><AiProviderSettings v-if="settingsOpen" :token="token" @changed="refresh" /></el-drawer>
  </section>
</template>

<style src="../styles/ai-assistant.css"></style>
