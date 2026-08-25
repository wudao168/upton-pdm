<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive } from 'vue'
import type { AddDrawingReviewMarkupInput, DrawingReviewCandidate, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'
import DrawingReviewPanel from './DrawingReviewPanel.vue'

type OverlayTheme = 'a' | 'c' | 'o'

interface ReviewOverlayState {
  visible: boolean
  packageId: string
  packages: DrawingReviewPackage[]
  candidates: DrawingReviewCandidate[]
  selectedDocumentId?: string
  currentUsername: string
  pending: boolean
  canSubmit: boolean
  canManageWithdraw: boolean
  canAnnotate: boolean
  canDecide: boolean
  allowSelfReview: boolean
  theme: OverlayTheme
}

const state = reactive<ReviewOverlayState>({
  visible: false,
  packageId: '',
  packages: [],
  candidates: [],
  selectedDocumentId: undefined,
  currentUsername: '',
  pending: false,
  canSubmit: false,
  canManageWithdraw: false,
  canAnnotate: false,
  canDecide: false,
  allowSelfReview: false,
  theme: 'a',
})

function send(action: string, payload?: Record<string, unknown>) {
  window.chrome?.webview?.postMessage({ type: 'review-overlay-action', payload: { action, ...payload } })
}

function updateState(event: MessageEvent) {
  const message = event.data as { type?: string; payload?: Partial<ReviewOverlayState> } | undefined
  if (message?.type !== 'review-overlay-state' || !message.payload) return
  Object.assign(state, message.payload)
}

function addMarkup(packageId: string, input: AddDrawingReviewMarkupInput) {
  send('add-markup', { packageId, input })
}

function decide(packageId: string, itemId: string, target: DrawingReviewTarget, decision: DrawingReviewDecision, comment: string) {
  send('decide', { packageId, itemId, target, decision, comment })
}

onMounted(() => window.chrome?.webview?.addEventListener('message', updateState))
onBeforeUnmount(() => window.chrome?.webview?.removeEventListener?.('message', updateState))
</script>

<template>
  <main class="pdm-review-overlay pdm-app-shell" :class="`theme-${state.theme}`">
    <DrawingReviewPanel
      v-if="state.visible"
      :package-id="state.packageId"
      :packages="state.packages"
      :candidates="state.candidates"
      :selected-document-id="state.selectedDocumentId"
      :current-username="state.currentUsername"
      :pending="state.pending"
      :can-submit="state.canSubmit"
      :can-manage-withdraw="state.canManageWithdraw"
      :can-annotate="state.canAnnotate"
      :can-decide="state.canDecide"
      :allow-self-review="state.allowSelfReview"
      desktop-available
      overlay-hosted
      @update:package-id="packageId => send('update-package', { packageId })"
      @close="send('close')"
      @create="modelDocumentIds => send('create', { modelDocumentIds })"
      @refresh="send('refresh')"
      @refresh-candidates="send('refresh-candidates')"
      @withdraw="(packageId, reason) => send('withdraw', { packageId, reason })"
      @select-document="documentId => send('select-document', { documentId })"
      @add-markup="addMarkup"
      @resolve-markup="(packageId, markupId) => send('resolve-markup', { packageId, markupId })"
      @decide="decide"
    />
  </main>
</template>
