<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { appendOplFiles, oplRecordingFile, oplRecordingLimit, oplSpeechWave } from '../oplSpeech'

const props = defineProps<{
  description: string
  files: File[]
  retained?: number
  disabled?: boolean
  transcribe: (wave: Blob, signal: AbortSignal) => Promise<string>
}>()
const emit = defineEmits<{ 'update:description': [text: string]; 'update:files': [files: File[]]; busy: [busy: boolean] }>()
const stage = ref<'idle' | 'permission' | 'recording' | 'stopping' | 'recognizing'>('idle')
const recording = computed(() => stage.value === 'recording')
const busy = computed(() => stage.value !== 'idle')
const canRecord = window.isSecureContext && typeof navigator.mediaDevices?.getUserMedia === 'function' && typeof window.MediaRecorder === 'function'
const seconds = ref(0)
const status = ref('')
const error = ref('')
const lastFile = ref<File>()
const lastUrl = ref('')
const retry = ref(false)
let recorder: MediaRecorder | undefined
let stream: MediaStream | undefined
let clock: ReturnType<typeof setInterval> | undefined
let stopAt: ReturnType<typeof setTimeout> | undefined
let recognition: AbortController | undefined
let disposed = false

watch(busy, value => emit('busy', value), { immediate: true, flush: 'sync' })
watch(() => lastFile.value && props.files.includes(lastFile.value), present => {
  if (present === false) clearPreview()
})

function clearPreview() {
  if (lastUrl.value) URL.revokeObjectURL(lastUrl.value)
  lastUrl.value = ''
  lastFile.value = undefined
  retry.value = false
}
function releaseMicrophone() {
  if (clock) clearInterval(clock)
  if (stopAt) clearTimeout(stopAt)
  stream?.getTracks().forEach(track => track.stop())
  stream = undefined
  recorder = undefined
}
function stop() {
  if (recorder?.state !== 'recording') return
  stage.value = 'stopping'
  recorder.stop()
}
function cancelRecording() {
  if (!recording.value) return
  recorder!.onstop = null
  recorder!.stop()
  releaseMicrophone()
  stage.value = 'idle'
  status.value = '已取消本次录音。'
}
async function start() {
  if (!canRecord || busy.value || props.disabled) return
  if (props.files.length + (props.retained ?? 0) >= 10) { error.value = '附件已达10个，请先移除一项。'; return }
  stage.value = 'permission'; error.value = ''; status.value = ''; seconds.value = 0
  try {
    const media = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true }, video: false })
    if (disposed) { media.getTracks().forEach(track => track.stop()); return }
    stream = media
    const mimeType = ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus'].find(type => MediaRecorder.isTypeSupported(type))
    const active = new MediaRecorder(media, mimeType ? { mimeType } : undefined)
    recorder = active
    const chunks: Blob[] = []
    active.ondataavailable = event => { if (event.data.size) chunks.push(event.data) }
    active.onstop = () => {
      releaseMicrophone()
      if (!disposed) void addVoice(new Blob(chunks, { type: active.mimeType || chunks[0]?.type || mimeType }))
    }
    active.onerror = () => {
      active.onstop = null
      releaseMicrophone()
      stage.value = 'idle'
      error.value = '录音中断，请重新录音或上传已有录音。'
    }
    active.start()
    stage.value = 'recording'
    const started = performance.now()
    clock = setInterval(() => { seconds.value = Math.min(oplRecordingLimit, Math.floor((performance.now() - started) / 1000)) }, 250)
    stopAt = setTimeout(stop, oplRecordingLimit * 1000)
  } catch {
    releaseMicrophone()
    stage.value = 'idle'
    if (!disposed) error.value = '无法打开麦克风。请核对录音权限或上传已有录音。'
  }
}
async function addVoice(blob: Blob, name = '') {
  try {
    const file = oplRecordingFile(blob, name)
    const files = appendOplFiles(props.files, [file], props.retained)
    if (!files) throw new Error('附件已达上限。最多10个附件，单个不超过10MB，总大小不超过30MB。')
    clearPreview()
    lastFile.value = file
    lastUrl.value = URL.createObjectURL(file)
    emit('update:files', files)
    await recognize(file)
  } catch (cause) {
    stage.value = 'idle'
    error.value = cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '未收到有效录音，请重新录制。'
  }
}
function pick(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (file && !busy.value && !props.disabled) void addVoice(file, file.name)
}
async function recognize(file: File) {
  stage.value = 'recognizing'; error.value = ''; status.value = ''; retry.value = false
  const controller = new AbortController()
  recognition = controller
  try {
    const wave = await oplSpeechWave(file)
    if (disposed || lastFile.value !== file) return
    const text = (await props.transcribe(wave, controller.signal)).trim()
    if (disposed || lastFile.value !== file) return
    if (!text) throw new Error('未识别到文字，可回听录音或手动填写。')
    const separator = props.description.trim() ? '\n' : ''
    const room = Math.max(0, 2000 - props.description.length - separator.length)
    const excerpt = text.slice(0, room).replace(/[\uD800-\uDBFF]$/, '')
    if (excerpt) emit('update:description', props.description + separator + excerpt)
    status.value = room >= text.length ? '识别文字已填入，可修改后提交。' : '描述达到2000字上限，录音已保留，可整理描述后继续。'
  } catch (cause) {
    if (!disposed && lastFile.value === file) {
      error.value = cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message)
        ? cause.message : '识别失败，录音已保留，可重试或手动填写。'
      retry.value = true
    }
  } finally {
    recognition = undefined
    stage.value = 'idle'
  }
}
function remove() {
  if (lastFile.value) emit('update:files', props.files.filter(file => file !== lastFile.value))
  clearPreview()
  error.value = ''; status.value = ''
}
onBeforeUnmount(() => {
  disposed = true
  recognition?.abort()
  if (recorder?.state === 'recording') { recorder.onstop = null; recorder.stop() }
  releaseMicrophone()
  clearPreview()
  emit('busy', false)
})
</script>

<template>
  <section class="opl-speech" aria-label="问题描述语音录入">
    <div class="opl-speech__actions">
      <button v-if="canRecord && !recording" type="button" class="pdm-secondary-action" :disabled="busy || disabled" @click="start">开始录音</button>
      <template v-if="recording">
        <button type="button" class="pdm-primary-action" @click="stop">停止录音</button>
        <button type="button" class="pdm-secondary-action" @click="cancelRecording">取消录音</button>
        <span role="status">录音中 {{ seconds }} / {{ oplRecordingLimit }} 秒</span>
      </template>
      <label v-if="!busy" class="opl-speech__picker">上传录音<input type="file" accept="audio/*,.wav,.mp3,.m4a,.webm,.ogg,.opus,.aac,.flac" aria-label="上传录音" :disabled="disabled" @change="pick"></label>
      <span v-if="stage === 'permission'" role="status">等待麦克风授权…</span>
      <span v-if="stage === 'stopping'" role="status">正在读取录音…</span>
      <span v-if="stage === 'recognizing'" role="status">正在识别录音…</span>
    </div>
    <div v-if="lastUrl" class="opl-speech__actions">
      <audio controls :src="lastUrl" aria-label="回听本次录音"></audio>
      <button v-if="retry && lastFile" type="button" class="pdm-secondary-action" :disabled="busy || disabled" @click="recognize(lastFile)">重试识别</button>
      <button type="button" class="pdm-secondary-action" :disabled="busy || disabled" @click="remove">移除录音</button>
    </div>
    <small v-if="error" role="alert" class="opl-speech__error">{{ error }}</small>
    <small v-else-if="status" role="status">{{ status }}</small>
  </section>
</template>

<style scoped>
.opl-speech{width:100%;display:grid;gap:6px;margin-top:6px}
.opl-speech__actions{display:flex;align-items:center;flex-wrap:wrap;gap:6px}
.opl-speech__actions audio{height:34px;max-width:100%;min-width:min(240px,100%)}
.opl-speech__picker{display:inline-flex;position:relative;align-items:center;justify-content:center;width:80px;height:28px;border-radius:999px;background:var(--pdm-theme-accent);color:white;cursor:pointer;margin:0}
.opl-speech__picker input{position:absolute;inset:0;opacity:0;cursor:pointer;width:100%}
.opl-speech__picker:focus-within{outline:2px solid var(--pdm-theme-accent);outline-offset:2px}
.opl-speech__error{color:var(--pdm-danger)}
</style>
