<script setup lang="ts">
import CompanyName from './CompanyName.vue'
import SearchField from './SearchField.vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { listMaterials } from '../api'
import type { PdmMaterial } from '../types'
import ObjectInspector from './ObjectInspector.vue'
const props = defineProps<{ token: string; companyId: string; canSearchMaterials: boolean; items: Array<{ id: string; label: string; detail?: string; kind: string }> }>()
const emit = defineEmits<{ choose: [item: { id: string; kind: string }] }>()
const opened = ref(false), query = ref(''), pending = ref(false), failure = ref(''), materials = ref<PdmMaterial[]>([]), input = ref<HTMLInputElement>(), material = ref<PdmMaterial>(), detailOpen = ref(false)
const results = ref<HTMLUListElement>()
function moveResult(direction: number) {
  const buttons = [...(results.value?.querySelectorAll('button') ?? [])]
  const current = buttons.indexOf(document.activeElement as HTMLButtonElement)
  if (current === 0 && direction < 0) input.value?.focus()
  else buttons[Math.min(buttons.length - 1, Math.max(0, current + direction))]?.focus()
}
let timer: ReturnType<typeof setTimeout> | undefined, controller: AbortController | undefined, generation = 0
const searchTerm = computed(() => query.value.trim())
const canSearch = computed(() => [...searchTerm.value].length >= 3)
const matches = computed(() => canSearch.value ? props.items.filter(item => `${item.label} ${item.detail ?? ''}`.toLowerCase().includes(searchTerm.value.toLowerCase())).slice(0, 15) : [])
const fields = computed(() => material.value ? [{ label: '料号', value: material.value.materialCode },{ label: '名称', value: material.value.name },{ label: '型号', value: material.value.specification },{ label: '品牌', value: material.value.brand },{ label: '材质', value: material.value.material },{ label: '备注', value: material.value.remark }] : [])
function open() { opened.value = true; void nextTick(() => input.value?.focus({ preventScroll: true })) }
function keyboard(event: KeyboardEvent) { if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); open() } }
window.addEventListener('keydown', keyboard)
watch([searchTerm, opened], () => {
  clearTimeout(timer); controller?.abort(); const run = ++generation; materials.value = []; failure.value = ''; pending.value = false
  if (!opened.value || !props.canSearchMaterials || !canSearch.value) return
  pending.value = true
  timer = setTimeout(async () => { controller = new AbortController(); try { const result = await listMaterials(props.token, searchTerm.value, false, 20, '', controller.signal); if (run === generation) materials.value = result } catch { if (run === generation) failure.value = '料品查询暂未完成，请重试。' } finally { if (run === generation) pending.value = false } }, 300)
})
watch(() => [props.token, props.companyId], () => { generation++; controller?.abort(); clearTimeout(timer); opened.value = false; detailOpen.value = false; material.value = undefined; materials.value = []; query.value = '' })
watch(() => props.canSearchMaterials, allowed => { if (!allowed) { generation++; controller?.abort(); clearTimeout(timer); materials.value = []; material.value = undefined; detailOpen.value = false; pending.value = false } })
onBeforeUnmount(() => { generation++; clearTimeout(timer); controller?.abort(); window.removeEventListener('keydown', keyboard) })
function choose(item: { id: string; kind: string }) { opened.value = false; emit('choose', item) }
function chooseFirst() {
  if (!canSearch.value) return
  if (matches.value[0]) choose(matches.value[0])
  else if (materials.value[0]) {
    material.value = materials.value[0]
    detailOpen.value = true
  }
}
</script>
<template>
  <button type="button" class="pdm-command-trigger pdm-header-control" aria-label="快速查找" aria-keyshortcuts="Control+k Meta+k" @click="open">快速查找 <kbd>Ctrl K</kbd></button>
  <el-drawer v-model="opened" title="快速查找" class="pdm-command-drawer" direction="rtl" size="min(640px, 100vw)" append-to-body @opened="input?.focus()">
    <SearchField @search="chooseFirst()"><input ref="input" v-model="query" class="pdm-command-input" aria-label="查找项目、图档、发布包或料品" placeholder="输入项目号、名称、图号或料号" @keydown.down.prevent="moveResult(1)" @keydown.enter.prevent="chooseFirst()"></SearchField>
    <p class="pdm-muted">项目按当前权限检索；图档与发布包限定当前项目。</p>
    <ul v-if="canSearch && (matches.length || materials.length)" ref="results" class="pdm-command-results" @keydown.down.prevent="moveResult(1)" @keydown.up.prevent="moveResult(-1)"><li v-for="item in matches" :key="item.kind + item.id"><button type="button" class="pdm-text-action" @click="choose(item)"><span><strong>{{ item.label }}</strong><small><CompanyName v-if="item.kind === '项目'" :name="item.detail" /><template v-else>{{ item.detail }}</template></small></span><em>{{ item.kind }}</em></button></li>
      <li v-for="item in materials" :key="item.id"><button type="button" class="pdm-text-action" @click="material = item; detailOpen = true"><span><strong>{{ item.materialCode }} · {{ item.name }}</strong><small>{{ item.specification }}</small></span><em>料品</em></button></li>
    </ul><p v-if="!canSearch" class="pdm-muted">请输入至少 3 个字符。</p><p v-else-if="pending" role="status">正在查找料品…</p><p v-else-if="failure" role="alert">{{ failure }}</p><p v-else-if="!matches.length && !materials.length">没有符合条件的对象。</p>
  </el-drawer>
  <ObjectInspector v-model="detailOpen" :title="material?.name || '料品详情'" :fields="fields" />
</template>
