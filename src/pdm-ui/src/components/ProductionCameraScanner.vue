<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { readProductionQrFrame, readProductionQrPhoto } from '../productionQr'
const props = defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ decoded: [code: string] }>()
const photo = ref<HTMLInputElement | null>(null), album = ref<HTMLInputElement | null>(null), video = ref<HTMLVideoElement | null>(null)
const live = Boolean(window.isSecureContext && navigator.mediaDevices?.getUserMedia)
const opened = ref(false), opening = ref(false), reading = ref(false), error = ref(''), notice = ref('')
let stream: MediaStream | undefined, timer: ReturnType<typeof setTimeout> | undefined, generation = 0
function stop() {
  generation++; clearTimeout(timer); timer = undefined
  stream?.getTracks().forEach(track => track.stop()); stream = undefined
  if (video.value) video.value.srcObject = null
  opened.value = false; opening.value = false
}
function accept(code: string) {
  stop(); error.value = ''; notice.value = '二维码已识别，正在定位任务；请核对批次后确认本次数量。'
  emit('decoded', code)
}
function readFrame(current: number) {
  if (current !== generation || !video.value) return
  try {
    const code = readProductionQrFrame(video.value, video.value.videoWidth, video.value.videoHeight)
    if (code) { accept(code); return }
    timer = setTimeout(() => readFrame(current), 250)
  } catch { stop(); error.value = '无法读取相机画面，请改用拍照扫码，或关闭其他相机应用后重试。' }
}
async function start() {
  if (props.disabled || opening.value || reading.value) return
  error.value = ''; notice.value = ''
  if (!live) { photo.value?.click(); return }
  stop(); opened.value = true; opening.value = true
  const current = generation
  try {
    const captured = await navigator.mediaDevices.getUserMedia({ audio: false, video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 }, height: { ideal: 960 } } })
    if (current !== generation) { captured.getTracks().forEach(track => track.stop()); return }
    stream = captured; await nextTick()
    if (current !== generation) return
    if (!video.value) { stop(); return }
    video.value.srcObject = captured; await video.value.play()
    if (current !== generation) return
    opening.value = false; readFrame(current)
  } catch (cause) {
    if (current !== generation) return
    stop()
    const name = cause && typeof cause === 'object' && 'name' in cause ? String(cause.name) : ''
    error.value = name === 'NotAllowedError' ? '未获得相机权限，请在浏览器设置中允许相机，或使用拍照扫码。'
      : name === 'NotFoundError' ? '未找到可用相机，请连接相机，或选择已有照片识别。'
        : '相机暂时无法打开，请关闭其他相机应用后重试，或使用拍照扫码。'
  }
}
function pickPhoto(input: HTMLInputElement | null) { stop(); error.value = ''; notice.value = ''; input?.click() }
async function readPhoto(event: Event) {
  const input = event.target as HTMLInputElement, file = input.files?.[0]
  input.value = ''
  if (!file || props.disabled || reading.value) return
  stop(); const current = generation; reading.value = true; error.value = ''; notice.value = '正在本机识别照片中的二维码…'
  try { const code = await readProductionQrPhoto(file); if (current === generation) accept(code) }
  catch (cause) { if (current === generation) { notice.value = ''; error.value = cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '照片识别失败，请重新拍摄清晰的二维码后重试。' } }
  finally { reading.value = false }
}
function hide() { if (document.hidden) stop() }
watch(() => props.disabled, value => { if (value) stop() })
onMounted(() => { document.addEventListener('visibilitychange', hide); window.addEventListener('pagehide', stop) })
onBeforeUnmount(() => { stop(); document.removeEventListener('visibilitychange', hide); window.removeEventListener('pagehide', stop) })
</script>
<template>
  <div class="production-camera" aria-label="相机识别二维码">
    <div class="production-camera__actions">
      <button type="button" :disabled="disabled || reading || opened" @click="start">{{ live ? '摄像头扫码' : '拍照扫码' }}</button>
      <button v-if="live" type="button" :disabled="disabled || reading" @click="pickPhoto(photo)">拍照扫码</button>
      <button type="button" :disabled="disabled || reading" @click="pickPhoto(album)">识别相册图片</button>
    </div>
    <input ref="photo" class="production-camera__file" type="file" accept="image/*" capture="environment" aria-label="拍摄图纸或批次流转卡二维码" @change="readPhoto">
    <input ref="album" class="production-camera__file" type="file" accept="image/*" aria-label="选择图纸或批次流转卡照片" @change="readPhoto">
    <p class="production-camera__hint">{{ live ? '将图纸或批次流转卡的二维码放入画面，识别后自动关闭相机。' : '当前内网地址使用拍照扫码：拍摄清晰完整的二维码后即可定位任务。' }} 照片仅在本机识别。</p>
    <p v-if="notice" role="status">{{ notice }}</p><p v-if="error" role="alert" class="production-camera__error">{{ error }}</p>
    <div v-if="opened" class="production-camera__preview" role="region" aria-label="实时扫码画面">
      <video ref="video" autoplay muted playsinline aria-label="后置相机画面"></video>
      <p v-if="opening" role="status">正在打开相机，请允许浏览器使用相机…</p>
      <button type="button" @click="stop">关闭相机</button>
    </div>
  </div>
</template>
<style scoped>
.production-camera{margin-top:14px;min-width:0}.production-camera__actions{display:flex;flex-wrap:wrap;gap:8px}.production-camera__file{display:none}.production-camera__hint{font-size:12px;color:var(--pdm-text-soft);line-height:1.6}.production-camera__error{color:var(--pdm-danger)}.production-camera p{overflow-wrap:anywhere}.production-camera__preview{background:var(--pdm-bg);border-radius:6px;padding:8px}.production-camera video{display:block;width:100%;max-height:360px;object-fit:contain;background:#111;border-radius:5px}.production-camera__preview button{margin-top:8px}.production-camera button{min-height:44px;border:1px solid var(--pdm-theme-accent-border);border-radius:5px;padding:8px 12px;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font:inherit;cursor:pointer}.production-camera__actions button:first-child{background:var(--pdm-theme-accent);color:#fff;border-color:var(--pdm-theme-accent)}.production-camera button:disabled{opacity:.6;cursor:not-allowed}
</style>
