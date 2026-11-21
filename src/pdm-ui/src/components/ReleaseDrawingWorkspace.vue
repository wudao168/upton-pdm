<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { listReleasePackages, listReleaseDrawingReviews, getApprovalEmergencyCapability } from '../api'
import { canSignNode } from '../approvalParticipants'
import { useProjectCenterAccess } from '../projectCenterAccess'
import { userErrorMessage } from '../userErrorMessage'
import { drawingHasActiveReturn, drawingReviewStatus } from '../drawingReviewStatus'
import type { ReleasePackageSummary, DocumentNode, DocumentFilter, DrawingReviewBadge } from '../types'
const props = defineProps<{ projectId: string; token: string; username: string; permissions: string[]; requestedId?: string; requestedDrawingId?: string; visible: boolean }>()
const emit = defineEmits<{ active: [active: boolean]; inline: [visible: boolean]; updated: [release: ReleasePackageSummary] }>()
const packages = ref<ReleasePackageSummary[]>([]), selectedId = ref(''), loading = ref(false), error = ref(''), emergency = ref(false)
const { write } = useProjectCenterAccess()
const selected = computed(() => packages.value.find(p => p.id === selectedId.value))
const history = ref<ReleasePackageSummary[]>()
watch([() => props.projectId, () => props.token, () => props.visible && selected.value?.scope === 'NonStandardSupplement', () => selected.value?.id], async ([projectId, token, needed], _, onCleanup) => {
  let active = true
  onCleanup(() => { active = false })
  history.value = undefined
  if (!needed) return
  try {
    const result = await listReleasePackages(projectId as string, token as string)
    if (active) history.value = result
  } catch {
    // 基线不可读时，审核栏显示缺失提示。
  }
}, { immediate: true })
const current = computed(() => selected.value?.steps.find(s => s.status === 'current' && s.id !== 'production-release'))
const ownTask = computed(() => !!current.value && canSignNode(current.value, props.username) && props.permissions.includes('approval.decide'))
const editable = computed(() => write.value && (ownTask.value || emergency.value))
const drawingId = ref(''), query = ref(''), filter = ref<DocumentFilter>('drawing')
const previewReady = ref(false)
const reviewItem = computed(() => selected.value?.drawingReview?.items.find(i=>i.documentId===drawingId.value))
const nodes = computed<DocumentNode[]>(() => (selected.value?.drawingReview?.items ?? []).map(item => {
  const row=selected.value!.nonStandardBomSnapshot.find(row=>row.id && item.bomItemIds.includes(row.id))
  const binding=selected.value!.versionBindings?.find(b=>b.documentId===item.documentId && b.versionId===item.versionId)
  return {id:item.documentId,documentId:item.documentId,drawingNumber:row?.specification || item.fileName.replace(/\.[^.]+$/,''),name:row?.name || item.fileName,fileName:item.fileName,kind:'Drawing',configuration:'',quantity:1,version:binding?.revision ?? '—',drawingVersion:binding?.revision ?? '已存档',publishedVersion:binding?.previousReleasedRevision || (item.required ? '未发布' : '已发布参照'),status:'Normal',children:[]}
}))
const selectedNode = computed<DocumentNode|undefined>(() => nodes.value.find(n=>n.documentId===drawingId.value) ?? (selected.value ? {id:'empty-review',drawingNumber:'',name:'本次无审核图纸',fileName:'',kind:'Drawing',configuration:'',quantity:0,version:'—',status:'Normal',children:[]} : undefined))
const badges = computed<Record<string,DrawingReviewBadge>>(() => Object.fromEntries((selected.value?.drawingReview?.items ?? []).map(item=>{
  const review=selected.value!.drawingReview!
  const state = drawingReviewStatus(item, review, current.value?.id, props.username)
  const returned = current.value ? drawingHasActiveReturn(item, review, selected.value!.steps) : state.hasIssue
  const confirmed = state.latest === 'Confirmed'
  return [item.documentId, { label: returned ? '待修改' : !item.required ? '已发布参照' : confirmed ? '已确认' : '待确认',
    tone: returned ? 'danger' : confirmed ? 'success' : 'pending' }]
})))
const filteredNodes=computed(()=>nodes.value.filter(n=>filter.value!=='model' && (filter.value!=='issue' || badges.value[n.documentId!]?.tone==='danger') && (!query.value || [n.name,n.fileName,n.drawingNumber].some(t=>t.toLowerCase().includes(query.value.toLowerCase())))))
const root=computed<DocumentNode|undefined>(()=>selected.value ? {id:'release-'+selected.value.id,drawingNumber:'本次审核图纸',name:selected.value.number,fileName:'',kind:'Assembly',configuration:'',quantity:1,version:'—',status:'Normal',isVirtualGroup:true,children:filteredNodes.value} : undefined)
const selectedBom = computed(()=>selected.value?.nonStandardBomSnapshot.find(row=>row.id && reviewItem.value?.bomItemIds.includes(row.id)))
function selectDocument(id:string) { if (nodes.value.some(n=>n.documentId===id) && !filteredNodes.value.some(n=>n.documentId===id)) {query.value='';filter.value='drawing'}; if (drawingId.value!==id) {previewReady.value=false; drawingId.value=id} }
function receiveNativePreview(value: {documentId?:string;versionId?:string;ready:boolean}) { if(value.documentId!==drawingId.value || value.versionId!==reviewItem.value?.versionId)return; previewReady.value=value.ready }
watch(() => selected.value?.id, () => {previewReady.value=false;query.value='';filter.value='drawing';selectDocument(nodes.value[0]?.documentId ?? '')})
watch(nodes, value=>{if(!value.some(n=>n.documentId===drawingId.value))selectDocument(value[0]?.documentId ?? '')})
let generation = 0, capability = 0
async function load(quiet = false) {
  const run = ++generation
  if (!props.visible || !props.permissions.includes('release.view')) { packages.value = []; return }
  if (!quiet) loading.value = true; error.value = ''
  try {
    const values = await listReleaseDrawingReviews(props.projectId, props.token)
    if (run !== generation) return
    packages.value = values
    if (selectedId.value && !values.some(p => p.id === selectedId.value)) selectedId.value = ''
  } catch (e) { if (run === generation) { packages.value = []; error.value = userErrorMessage(e instanceof Error ? e.message : '') } }
  finally { if (run === generation) loading.value = false }
}
watch([() => props.projectId, () => props.token, () => props.visible, () => props.permissions.includes('release.view'),
  () => props.requestedId, () => props.requestedDrawingId], async ([projectId, token, visible, canView, id, documentId], previous) => {
  if (!previous || projectId !== previous[0] || token !== previous[1] || visible !== previous[2] || canView !== previous[3]) {
    packages.value = []
    selectedId.value = id ?? ''
  } else if (id) {
    selectedId.value = id
  }
  await load()
  if (id === selectedId.value && documentId && documentId === props.requestedDrawingId
    && nodes.value.some(node => node.documentId === documentId)) selectDocument(documentId)
}, { immediate: true })
watch(() => packages.value.length>0 && props.visible, value => emit('inline',value), {immediate:true})
watch([() => selected.value?.id, () => reviewItem.value?.token], () => {previewReady.value=false})
watch(() => !!selected.value && props.visible, value => emit('active', value), { immediate: true })
watch(() => [current.value?.id, props.permissions, props.token], async () => {
  const run = ++capability; emergency.value = false
  if (!current.value || !props.permissions.includes('approval.emergency-substitute')) return
  try { const result = await getApprovalEmergencyCapability(current.value.id, props.token); if (run === capability) emergency.value = result.canDecide } catch { /* 服务端未授权时保持只读 */ }
}, { immediate: true })
function receive(value: ReleasePackageSummary) { ++generation; loading.value = false; packages.value = packages.value.map(p => p.id === value.id ? value : p); emit('updated', value) }
const timer = setInterval(() => {
  if (!props.visible || !selected.value) return
  const rechecking = current.value && Object.keys(selected.value.drawingReview?.preservedConfirmations ?? {}).length > 0
  if (selected.value.state === '发布中' || rechecking || Object.values(badges.value).some(b => b.tone === 'danger')) void load(true)
}, 5000)
onBeforeUnmount(() => { clearInterval(timer); ++generation; ++capability; emit('active', false); emit('inline',false) })
</script>
<template>
  <slot :selected="selected" :packages="packages" :history="history" :selected-id="selectedId" :select-package="(id:string)=>selectedId=id" :loading="loading" :error="error" :reload="load"
    :editable="editable" :emergency="!ownTask && emergency" :receive="receive" :select-document="selectDocument" :drawing-id="drawingId"
    :nodes="nodes" :filtered-nodes="filteredNodes" :root="root" :badges="badges" :selected-node="selectedNode" :selected-bom="selectedBom" :review-item="reviewItem"
    :query="query" :set-query="(value:string)=>query=value" :filter="filter" :set-filter="(value:DocumentFilter)=>filter=value"
    :preview-ready="previewReady" :receive-native-preview="receiveNativePreview" />
</template>
