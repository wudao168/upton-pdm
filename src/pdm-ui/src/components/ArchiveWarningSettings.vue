<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { getArchiveWarningPolicy, getArchiveWarningCatalog, saveArchiveWarningPolicy, type ArchiveWarningPolicy, type ArchiveWarningDefinition } from '../api'

const props = defineProps<{ token: string; canEdit: boolean }>()
const policy = ref<ArchiveWarningPolicy>({ revision: 0, blockAll: false, blockingCodes: [] })
const catalog = ref<ArchiveWarningDefinition[]>([])
const loaded = ref(false)
const pending = ref(false)
const error = ref('')
async function load() {
  loaded.value = false; pending.value = true; error.value = ''
  try {
    const [settings, definitions] = await Promise.all([getArchiveWarningPolicy(props.token), getArchiveWarningCatalog(props.token)])
    policy.value = settings; catalog.value = definitions; loaded.value = true
  } catch { error.value = '无法读取存档警告策略，请检查连接后刷新。' }
  finally { pending.value = false }
}
function setBlocked(code: string, blocked: boolean) {
  policy.value.blockingCodes = blocked ? [...new Set([...policy.value.blockingCodes, code])] : policy.value.blockingCodes.filter(value => value !== code)
}
async function save() {
  if (!loaded.value || !props.canEdit || pending.value) return
  pending.value = true; error.value = ''
  try {
    policy.value = await saveArchiveWarningPolicy(policy.value, props.token)
    ElMessage.success('存档警告策略已保存，下次存档读取新策略')
  } catch { error.value = '策略未保存，服务器状态未确认。请刷新后核对，避免覆盖其他管理员的修改。' }
  finally { pending.value = false }
}
onMounted(load)
</script>

<template>
  <section class="pdm-panel pdm-manager-panel archive-warnings" aria-label="存档警告策略">
    <header class="pdm-manager-heading"><h2>存档警告策略</h2><span>策略版本 {{ policy.revision }}</span></header>
    <p>仅控制存档警告，不改变获取、放弃、审核或发布规则。一般警告默认仅提示；原文件、全部配置和原引用保留。</p>
    <p>权限、实际文件读写、上传失败和版本冲突不允许忽略。确认的循环引用始终阻止；上下文关联须先正确分类。</p>
    <div class="archive-warning-actions">
      <label><input v-model="policy.blockAll" type="checkbox" :disabled="!loaded || !canEdit || pending" />全部已支持的警告阻止存档</label>
      <button type="button" :disabled="pending" @click="load">刷新</button>
      <button type="button" :disabled="!loaded || !canEdit || pending" @click="save">保存策略</button>
    </div>
    <p v-if="error" role="alert">{{ error }}</p>
    <p>当前已接入 {{ catalog.filter(item => item.supported).length }} 项；官方清单及补充项中还有 {{ catalog.filter(item => !item.supported).length }} 项尚未接入，不列为可配置规则。关闭拦截不会自动补齐、换配置或扩大提交范围。</p>
    <div class="pdm-table-scroll"><table class="pdm-edit-table" aria-label="存档警告清单">
      <thead><tr><th>警告</th><th>依据</th><th>处理方式</th></tr></thead>
      <tbody><tr v-for="item in catalog.filter(item => item.supported)" :key="item.code">
        <td>{{ item.name }}</td><td>{{ item.source }}</td>
        <td><span v-if="item.alwaysBlocks">始终阻止</span><span v-else-if="!item.supported">暂不支持检测</span>
          <select v-else :aria-label="item.name" :value="policy.blockAll || policy.blockingCodes.includes(item.code) ? 'block' : 'warn'"
            :disabled="!loaded || !canEdit || pending || policy.blockAll" @change="setBlocked(item.code, ($event.target as HTMLSelectElement).value === 'block')">
            <option value="warn">仅提示并继续</option><option value="block">阻止存档</option>
          </select></td>
      </tr></tbody>
    </table></div>
    <p>规则依据：<a href="https://help.solidworks.com/2026/english/EnterprisePDM/admin/IDD_USER_GROUP_WARNING.htm?id=2.27.15.13" target="_blank" rel="noopener noreferrer">SOLIDWORKS PDM 官方警告清单</a>。本系统的默认选项与补充项按业务要求制定。</p>
  </section>
</template>

<style scoped>
.archive-warnings { min-width:0; min-height:0; gap:12px; }
.archive-warnings p { margin:0; line-height:1.6; }
.archive-warning-actions { display:flex; align-items:center; flex-wrap:wrap; gap:16px; }
.pdm-table-scroll { overflow:auto; min-height:0; flex:1; }
.pdm-edit-table { width:100%; }
select { padding:6px; color:inherit; background:var(--pdm-bg-panel, white); border:1px solid var(--pdm-border-color, #ccc); }
</style>
