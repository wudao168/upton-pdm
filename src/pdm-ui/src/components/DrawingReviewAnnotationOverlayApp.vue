<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive } from 'vue'
import type { AddDrawingReviewMarkupInput, DrawingReviewDecision, DrawingReviewPackage, DrawingReviewTarget } from '../types'
import DrawingReviewAnnotationCard from './DrawingReviewAnnotationCard.vue'

type OverlayTheme = 'a' | 'c' | 'o'

interface ReviewAnnotationState {
  visible: boolean
  packageId: string
  packages: DrawingReviewPackage[]
  selectedDocumentId?: string
  currentUsername: string
  pending: boolean
  canAnnotate: boolean
  canDecide: boolean
  allowSelfReview: boolean
  theme: OverlayTheme
}

const state = reactive<ReviewAnnotationState>({
  visible: false,
  packageId: '',
  packages: [],
  selectedDocumentId: undefined,
  currentUsername: '',
  pending: false,
  canAnnotate: false,
  canDecide: false,
  allowSelfReview: false,
  theme: 'a',
})

const activePackage = computed(() => state.packages.find(item => item.id === state.packageId) ?? state.packages[0])

function send(action: string, payload?: Record<string, unknown>) {
  window.chrome?.webview?.postMessage({ type: 'review-overlay-action', payload: { action, ...payload } })
}

function updateState(event: MessageEvent) {
  const message = event.data as { type?: string; payload?: Partial<ReviewAnnotationState> } | undefined
  if (message?.type !== 'review-annotation-state' || !message.payload) return
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
  <main class="pdm-review-annotation pdm-app-shell" :class="`theme-${state.theme}`">
    <DrawingReviewAnnotationCard
      v-if="state.visible"
      :package="activePackage"
      :selected-document-id="state.selectedDocumentId"
      :current-username="state.currentUsername"
      :pending="state.pending"
      :can-annotate="state.canAnnotate"
      :can-decide="state.canDecide"
      :allow-self-review="state.allowSelfReview"
      desktop-available
      overlay-hosted
      @add-markup="addMarkup"
      @resolve-markup="(packageId, markupId) => send('resolve-markup', { packageId, markupId })"
      @decide="decide"
      @decide-supervisor="(packageId, decision, comment) => send('decide-supervisor', { packageId, decision, comment })"
    />
  </main>
</template>
