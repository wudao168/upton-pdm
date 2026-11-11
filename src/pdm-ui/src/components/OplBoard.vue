<script setup lang="ts">
import PersonName from './PersonName.vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { OplRow } from '../projectOpl'
import { oplStates, canMoveOpl } from '../oplBoard'
import '../styles/opl-board.css'
const props = defineProps<{ rows: OplRow[]; busy: boolean; movingId: string | null; name: (username: string) => string; overdue: (row: OplRow) => boolean }>()
const emit = defineEmits<{ open: [row: OplRow]; move: [row: OplRow, status: string] }>()
const dragging = ref<string | null>(null), over = ref('')
const columns = computed(() => oplStates.map(status => ({ status, rows: props.rows.filter(row => row.issue.status === status) })))
function start(event: DragEvent, row: OplRow) {
  if (pointer) { event.preventDefault(); return }
  if (props.busy || !oplStates.some(status => canMoveOpl(row, status))) { event.preventDefault(); return }
  dragging.value = row.issue.id
  if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/plain', row.issue.id) }
}
function end() { dragging.value = null; over.value = '' }
function enter(event: DragEvent, status: string) {
  const row = props.rows.find(row => row.issue.id === dragging.value)
  if (!props.busy && row && canMoveOpl(row, status)) { event.preventDefault(); over.value = status; if (event.dataTransfer) event.dataTransfer.dropEffect = 'move' }
}
function drop(event: DragEvent, status: string) {
  event.preventDefault()
  const row = props.rows.find(row => row.issue.id === dragging.value)
  end()
  if (!props.busy && row && canMoveOpl(row, status)) emit('move', row, status)
}
function keyboardMove(event: KeyboardEvent, row: OplRow) {
  if (!event.altKey || !['ArrowLeft','ArrowRight'].includes(event.key) || props.busy) return
  event.preventDefault()
  const index = oplStates.indexOf(row.issue.status as typeof oplStates[number])
  const target = oplStates[index + (event.key === 'ArrowRight' ? 1 : -1)]
  if (target && canMoveOpl(row, target)) emit('move', row, target)
}
// Pointer capture keeps in-page dragging independent of the desktop host's OLE drag loop.
const board = ref<HTMLElement>()
let pointer: { id: number; row: OplRow; card: HTMLElement; x: number; y: number; lastX: number; lastY: number; offsetX: number; offsetY: number; started: boolean } | null = null
let preview: HTMLElement | null = null
let frame = 0, suppressClickUntil = 0
function targetAt(x: number, y: number): HTMLElement | null {
  const column = document.elementFromPoint(x, y)?.closest<HTMLElement>('.opl-board-column')
  return column && board.value?.contains(column) ? column : null
}
function updateTarget() {
  if (!pointer) return
  const status = targetAt(pointer.lastX, pointer.lastY)?.dataset.status ?? ''
  over.value = !props.busy && canMoveOpl(pointer.row, status) ? status : ''
}
function scrollDuringDrag() {
  if (!pointer?.started || !board.value) return
  const rect = board.value.getBoundingClientRect(), { lastX: x, lastY: y } = pointer
  if (y >= rect.top && y <= rect.bottom && x >= rect.left && x <= rect.right) {
    const edge = 36
    board.value.scrollLeft += x < rect.left + edge ? -12 : x > rect.right - edge ? 12 : 0
    const list = targetAt(x, y)?.querySelector<HTMLElement>('.opl-board-column__cards')
    if (list) { const box = list.getBoundingClientRect(); list.scrollTop += y < box.top + edge ? -10 : y > box.bottom - edge ? 10 : 0 }
  }
  updateTarget()
  frame = requestAnimationFrame(scrollDuringDrag)
}
function clearPointer() {
  const previous = pointer
  pointer = null
  cancelAnimationFrame(frame); frame = 0
  preview?.remove(); preview = null
  window.removeEventListener('pointermove', movePointer)
  window.removeEventListener('pointerup', finishPointer)
  window.removeEventListener('pointercancel', cancelPointer)
  window.removeEventListener('blur', cancelPointer)
  window.removeEventListener('keydown', cancelKey)
  if (previous?.card.hasPointerCapture?.(previous.id)) previous.card.releasePointerCapture(previous.id)
  end()
}
function cancelPointer() { if (pointer?.started) suppressClickUntil = Date.now() + 500; clearPointer() }
function cancelKey(event: KeyboardEvent) { if (event.key === 'Escape' && pointer) { event.preventDefault(); cancelPointer() } }
function beginPointer(event: PointerEvent, row: OplRow) {
  if (event.button !== 0 || event.isPrimary === false || props.busy || !oplStates.some(status => canMoveOpl(row, status))) return
  clearPointer(); suppressClickUntil = 0
  const card = event.currentTarget as HTMLElement, box = card.getBoundingClientRect()
  pointer = { id: event.pointerId, row, card, x: event.clientX, y: event.clientY, lastX: event.clientX, lastY: event.clientY, offsetX: event.clientX - box.left, offsetY: event.clientY - box.top, started: false }
  window.addEventListener('pointermove', movePointer, { passive: false })
  window.addEventListener('pointerup', finishPointer)
  window.addEventListener('pointercancel', cancelPointer)
  window.addEventListener('blur', cancelPointer)
  window.addEventListener('keydown', cancelKey)
}
function movePointer(event: PointerEvent) {
  if (!pointer || pointer.id !== event.pointerId) return
  if (props.busy || event.buttons === 0) { cancelPointer(); return }
  pointer.lastX = event.clientX; pointer.lastY = event.clientY
  if (!pointer.started) {
    if (Math.hypot(event.clientX - pointer.x, event.clientY - pointer.y) < 6) return
    pointer.started = true; dragging.value = pointer.row.issue.id
    pointer.card.setPointerCapture?.(pointer.id)
    const box = pointer.card.getBoundingClientRect()
    preview = pointer.card.cloneNode(true) as HTMLElement
    preview.removeAttribute('data-issue-id'); preview.removeAttribute('draggable')
    preview.setAttribute('aria-hidden', 'true'); preview.setAttribute('inert', '')
    preview.classList.remove('is-dragging'); preview.classList.add('opl-board-drag-preview')
    Object.assign(preview.style, { position: 'fixed', left: '0', top: '0', width: `${box.width}px`, height: `${box.height}px`, boxSizing: 'border-box', zIndex: '2147483000', pointerEvents: 'none' })
    document.body.append(preview)
    frame = requestAnimationFrame(scrollDuringDrag)
  }
  event.preventDefault()
  if (preview) preview.style.transform = `translate3d(${event.clientX - pointer.offsetX}px,${event.clientY - pointer.offsetY}px,0)`
  updateTarget()
}
function finishPointer(event: PointerEvent) {
  if (!pointer || pointer.id !== event.pointerId) return
  const { row, started } = pointer
  pointer.lastX = event.clientX; pointer.lastY = event.clientY; updateTarget()
  const status = over.value
  if (started) { event.preventDefault(); suppressClickUntil = Date.now() + 500 }
  clearPointer()
  if (started && !props.busy && canMoveOpl(row, status)) emit('move', row, status)
}
function openCard(row: OplRow) { if (Date.now() >= suppressClickUntil && !pointer?.started) emit('open', row) }
watch(() => props.busy, busy => { if (busy && pointer) cancelPointer() })
onBeforeUnmount(clearPointer)
</script>
<template>
  <div ref="board" class="opl-board" aria-label="OPL实施状态看板" :aria-busy="busy">
    <section v-for="column in columns" :key="column.status" class="opl-board-column" :class="{ 'is-drop-target': over === column.status }" :aria-label="column.status" :data-status="column.status" @dragover="enter($event, column.status)" @dragleave="event => { if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node)) over = '' }" @drop="drop($event, column.status)">
      <header class="opl-board-column__header"><h3>{{ column.status }}</h3><span class="opl-board-count">{{ column.rows.length }}</span></header>
      <div class="opl-board-column__cards">
        <TransitionGroup name="opl-card">
          <article v-for="row in column.rows" :key="row.issue.id" class="opl-board-card" :class="{ 'is-dragging': dragging === row.issue.id, 'is-saving': movingId === row.issue.id }" :data-issue-id="row.issue.id" :draggable="!busy && oplStates.some(status => canMoveOpl(row, status))" @pointerdown="beginPointer($event, row)" @lostpointercapture="cancelPointer" @dragstart="start($event, row)" @dragend="end">
            <button type="button" class="opl-board-card__open" :aria-label="`查看问题 #${row.issue.number} ${row.issue.description}`" :disabled="busy" aria-keyshortcuts="Alt+ArrowLeft Alt+ArrowRight" @keydown="keyboardMove($event, row)" @click="openCard(row)">
              <span class="opl-board-card__meta"><span class="opl-board-priority" :data-priority="row.issue.priority">{{ row.issue.priority }}</span><span class="opl-board-owner" :title="name(row.issue.owner)"><PersonName :name="name(row.issue.owner)" /></span></span>
              <span class="opl-board-card__description">{{ row.issue.description }}</span>
              <span class="opl-board-card__dates"><span><small>提交</small><time>{{ new Date(row.issue.createdAt).toLocaleDateString('sv-SE') }}</time></span><span :class="{ 'opl-overdue': overdue(row) }"><small>需求</small><time>{{ row.issue.dueDate || '未设置' }}</time></span></span>
            </button>
          </article>
        </TransitionGroup>
        <p v-if="!column.rows.length" class="opl-board-empty">{{ busy ? '正在加载…' : '暂无问题' }}</p>
      </div>
    </section>
  </div>
</template>
