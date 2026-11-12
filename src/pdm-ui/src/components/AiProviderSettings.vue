<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { aiError, aiJson, aiRequestId, defaultAiWeb, type AiSettings, type AiProvider } from '../aiAssistant'
const props = defineProps<{ token: string }>()
const emit = defineEmits<{ changed: [] }>()
const settings = ref<AiSettings>({ revision: 0, providers: [], defaultModel: null })
const loading = ref(false), dirty = ref(false), error = ref(''), notice = ref(''), editing = ref<AiProvider>(), testing = ref('')
const choices = computed(() => settings.value.providers.filter(p => p.enabled).flatMap(p => p.models.filter(m => m.enabled).map(m => ({ key: `${p.id}:${m.id}`, name: `${p.name} · ${m.name}` }))))
const webProviders = computed(() => settings.value.providers.filter(p => p.enabled && p.network === 'cloud' && p.adapter === 'deepseek' && (p.hasKey || p.apiKey) && /^https:\/\/api\.deepseek\.com(?::443)?(?:\/v1)?\/?$/i.test(p.baseUrl)))
async function load() { loading.value = true; error.value = ''; try { settings.value = await aiJson<AiSettings>('settings', props.token); settings.value.web ??= defaultAiWeb(); dirty.value = false } catch (e) { error.value = aiError(e) } finally { loading.value = false } }
function add(preset: 'intranet' | 'deepseek' | 'qwen') {
  const p: AiProvider = { id: aiRequestId(), name: preset === 'deepseek' ? 'DeepSeek' : preset === 'qwen' ? '千问' : '内网模型',
    baseUrl: preset === 'deepseek' ? 'https://api.deepseek.com' : preset === 'qwen' ? 'https://dashscope.aliyuncs.com/compatible-mode/v1' : '',
    network: preset === 'intranet' ? 'intranet' : 'cloud', adapter: preset === 'deepseek' ? 'deepseek' : 'compatible', enabled: false,
    models: [{ id: preset === 'deepseek' ? 'deepseek-flash' : '', name: preset === 'deepseek' ? 'DeepSeek Flash' : '', enabled: true, tools: false, contextTokens: 32768, outputTokens: 2048, thinking: false, reasoningEffort: 'high' }] }
  settings.value.providers.push(p); editing.value = p; dirty.value = true
}
function edit(p: AiProvider) { p.models.forEach(m => { m.thinking ??= false; m.reasoningEffort ??= 'high' }); editing.value = p }
async function save() {
  loading.value = true; error.value = ''; notice.value = ''
  try {
    settings.value = await aiJson<AiSettings>('settings', props.token, 'PUT', settings.value)
    dirty.value = false; editing.value = undefined; notice.value = 'AI服务配置已保存。'; emit('changed')
  } catch (e) { error.value = aiError(e) } finally { loading.value = false }
}
async function test(p: AiProvider, modelId: string, tools: boolean) {
  testing.value = `${p.id}:${modelId}`; error.value = ''; notice.value = ''
  try { const result = await aiJson<{ message: string }>(`providers/${p.id}/test`, props.token, 'POST', { modelId, tools }); notice.value = result.message }
  catch (e) { error.value = aiError(e) } finally { testing.value = '' }
}
function remove(p: AiProvider) {
  if (!window.confirm(`确定移除“${p.name}”吗？保存后生效，历史会话保留。`)) return
  settings.value.providers = settings.value.providers.filter(x => x.id !== p.id)
  if (settings.value.defaultModel?.startsWith(`${p.id}:`)) settings.value.defaultModel = null
  editing.value = undefined; dirty.value = true
}
onMounted(load)
</script>

<template>
  <section class="ai-settings" aria-label="AI服务配置">
    <header class="ai-settings-toolbar"><div><strong>AI服务与模型</strong><p>手动添加服务地址和模型，配置仅对当前公司生效。</p></div>
      <div class="ai-actions"><button :disabled="loading || dirty" @click="load">刷新</button><button :disabled="loading || !dirty" class="ai-primary" @click="save">保存</button></div>
    </header>
    <div class="ai-actions"><button :disabled="loading" @click="add('deepseek')">DeepSeek</button><button :disabled="loading" @click="add('qwen')">千问</button><button :disabled="loading" @click="add('intranet')">内网接口</button>
      <label class="ai-default">默认模型 <select v-model="settings.defaultModel" :disabled="loading" @change="dirty = true"><option :value="null">不指定</option><option v-for="m in choices" :key="m.key" :value="m.key">{{ m.name }}</option></select></label>
    </div>
    <p v-if="error" class="ai-error" role="alert">{{ error }}</p><p v-if="notice" class="ai-notice" role="status">{{ notice }}</p>
    <p v-if="dirty" class="ai-muted">有未保存的修改。连接测试使用保存后的配置。</p>
    <section v-if="settings.web" class="ai-provider-card ai-web-settings" aria-label="联网搜索配置" @input="dirty = true" @change="dirty = true">
      <header><span class="ai-avatar">网</span><strong>联网搜索与网页读取</strong><label class="ai-actions"><input v-model="settings.web.enabled" type="checkbox" :disabled="loading" />启用联网服务</label></header>
      <p class="ai-muted">使用DeepSeek官方搜索服务与已有密钥。搜索及网页读取单独计费，模型回复另计费；可随时关闭。</p>
      <label class="ai-web-provider">联网服务<select v-model="settings.web.providerId" :disabled="loading"><option :value="null">请选择DeepSeek官方服务</option><option v-for="p in webProviders" :key="p.id" :value="p.id">{{ p.name }}</option></select></label>
      <div class="ai-web-limits"><label>每日上限<input v-model.number="settings.web.dailyLimit" type="number" min="1" max="10000" step="1" :disabled="loading" /></label><label>每月上限<input v-model.number="settings.web.monthlyLimit" type="number" min="1" max="100000" step="1" :disabled="loading" /></label><label>每轮上限<input v-model.number="settings.web.perTurnLimit" type="number" min="1" max="5" step="1" :disabled="loading" /></label></div>
      <p>当前公司：今日已尝试 {{ settings.webUsage?.today ?? 0 }} 次，本月已尝试 {{ settings.webUsage?.month ?? 0 }} 次。</p>
      <p class="ai-muted">搜索与读取共用额度，失败尝试也计入本地上限，以控制重复请求；实际费用以DeepSeek账单为准。每日、每月按北京时间重置。账号还需“使用AI联网搜索与网页读取”和“使用云端AI模型”权限。</p>
      <a href="https://api-docs.deepseek.com/zh-cn/guides/search/" target="_blank" rel="noopener noreferrer">查看官方接口与计费说明</a>
    </section>
    <div v-if="!settings.providers.length" class="ai-empty"><strong>尚未添加AI服务</strong><p>先选择 DeepSeek、千问或内网接口，填写模型信息后保存。</p></div>
    <div class="ai-provider-grid">
      <article v-for="p in settings.providers" :key="p.id" class="ai-provider-card">
        <header><span class="ai-avatar">AI</span><strong>{{ p.name }}</strong><span class="ai-badge">{{ p.enabled ? '已启用' : '已停用' }}</span></header>
        <p class="ai-muted">{{ p.network === 'cloud' ? '云端服务 · 使用需单独授权' : '内网服务' }}</p><p class="ai-endpoint">{{ p.baseUrl || '地址待填写' }}</p>
        <p>{{ p.models.length }} 个模型 · {{ p.hasKey ? '密钥已保存' : p.apiKey ? '密钥待保存' : '未配置密钥' }}</p>
        <div class="ai-actions"><button @click="edit(p)">配置</button><button @click="remove(p)">移除</button></div>
        <div v-for="m in p.models" :key="m.id" class="ai-model-test"><span>{{ m.name || '模型待填写' }}</span><button :disabled="dirty || loading || !!testing || !p.enabled || !m.enabled" @click="test(p, m.id, false)">连接测试</button><button :disabled="dirty || loading || !!testing || !p.enabled || !m.enabled" @click="test(p, m.id, true)">工具测试</button></div>
      </article>
    </div>
    <el-drawer :model-value="!!editing" title="配置AI服务" size="min(700px, 96vw)" append-to-body @close="editing = undefined">
      <form v-if="editing" class="ai-provider-form" @input="dirty = true" @change="dirty = true" @submit.prevent="save">
        <label>服务名称<input v-model="editing.name" maxlength="60" required /></label>
        <div class="ai-field-row"><label>网络范围<select v-model="editing.network"><option value="intranet">内网</option><option value="cloud">云端</option></select></label>
          <label>接口类型<select v-model="editing.adapter" @change="editing.models.forEach(m => { if (editing?.adapter !== 'deepseek') m.thinking = false })"><option value="deepseek">DeepSeek兼容</option><option value="compatible">通用兼容接口</option></select></label></div>
        <label>API基础地址<input v-model="editing.baseUrl" placeholder="例如 http://内网服务器:11434/v1" maxlength="300" required /></label>
        <label>API密钥<input v-model="editing.apiKey" type="password" autocomplete="new-password" :placeholder="editing.hasKey ? '已保存，留空保留原密钥' : '本地服务可不填'" maxlength="4096" /></label>
        <div class="ai-actions"><label><input v-model="editing.clearKey" type="checkbox" />清除原密钥</label><label><input v-model="editing.enabled" type="checkbox" />启用服务</label></div>
        <p class="ai-muted">云端模型会接收本次对话及引用资料，使用账号须获得“使用云端AI模型”权限。切换地址时须重新填写或清除密钥。</p>
        <div v-for="(m, index) in editing.models" :key="index" class="ai-model-form">
          <div class="ai-field-row"><label>模型ID<input v-model="m.id" maxlength="150" required placeholder="填写服务提供的准确模型ID" /></label><label>显示名称<input v-model="m.name" maxlength="80" required /></label></div>
          <div class="ai-field-row"><label>上下文容量<input v-model.number="m.contextTokens" type="number" min="4096" max="2000000" required /></label><label>输出上限<input v-model.number="m.outputTokens" type="number" min="128" max="8192" required /></label></div>
          <div v-if="editing.adapter === 'deepseek'" class="ai-field-row">
            <label>思考模式<select v-model="m.thinking" :disabled="loading"><option :value="false">关闭（非思考）</option><option :value="true">开启思考</option></select></label>
            <label>思考强度<select v-model="m.reasoningEffort" :disabled="loading || !m.thinking"><option value="low">低</option><option value="high">高</option><option value="max">最高</option></select></label>
          </div>
          <p v-if="editing.adapter === 'deepseek'" class="ai-muted">由管理员统一设置，保存后所有使用此模型的对话生效。关闭时强度不生效，通常更快、更省用量；开启后，思考与回答共用输出上限，强度越高通常耗时和用量越多。</p>
          <div class="ai-actions"><label><input v-model="m.enabled" type="checkbox" />启用模型</label><label><input v-model="m.tools" type="checkbox" />允许工具调用</label><button type="button" :disabled="editing.models.length <= 1" @click="editing.models.splice(index, 1); dirty = true">移除模型</button></div>
        </div>
        <p class="ai-muted">容量按服务实际配置填写。先保存并通过工具测试，再开启工具调用，用于业务查询和联网查询；普通对话无需开启。</p>
        <div class="ai-actions"><button type="button" :disabled="editing.models.length >= 20" @click="editing.models.push({ id: '', name: '', enabled: true, tools: false, contextTokens: 32768, outputTokens: 2048, thinking: false, reasoningEffort: 'high' }); dirty = true">添加模型</button><button class="ai-primary" type="submit" :disabled="loading">保存</button></div>
        <p v-if="error" class="ai-error" role="alert">{{ error }}</p>
      </form>
    </el-drawer>
  </section>
</template>

<style src="../styles/ai-assistant.css"></style>
