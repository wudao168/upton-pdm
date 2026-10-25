<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { readProductionQrFrame, readProductionQrPhoto } from '../productionQr'
import { readProductionCameraAccess, type ProductionCameraAccess } from '../productionCameraAccess'
const props = defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ decoded: [code: string] }>()
const photo = ref<HTMLInputElement | null>(null), album = ref<HTMLInputElement | null>(null), video = ref<HTMLVideoElement | null>(null)
const setupClose = ref<HTMLButtonElement>(), cameraClose = ref<HTMLButtonElement>()
const secure = Boolean(window.isSecureContext), live = Boolean(secure && navigator.mediaDevices?.getUserMedia)
const showSetup = ref(false), loadingSetup = ref(false), cameraAccess = ref<ProductionCameraAccess>()
const opened = ref(false), opening = ref(false), reading = ref(false), error = ref(''), notice = ref('')
const zoom = ref(1), zoomRange = ref<{ min: number; max: number; step: number }>()
type CameraCapabilities = MediaTrackCapabilities & { zoom?: { min: number; max: number; step?: number }; focusMode?: string[] }
let stream: MediaStream | undefined, timer: ReturnType<typeof setTimeout> | undefined, generation = 0
let startedAt = 0
function stop() {
  generation++; clearTimeout(timer); timer = undefined
  stream?.getTracks().forEach(track => track.stop()); stream = undefined
  if (video.value) video.value.srcObject = null
  opened.value = false; opening.value = false
  zoomRange.value = undefined
}
function accept(code: string) {
  stop(); error.value = ''; notice.value = ''
  emit('decoded', code)
}
async function readFrame(current: number) {
  if (current !== generation || !video.value) return
  try {
    const code = await readProductionQrFrame(video.value, video.value.videoWidth, video.value.videoHeight)
    if (current !== generation) return
    if (code) { accept(code); return }
    if (Date.now() - startedAt > 6000) notice.value = '未识别，请调整距离或放大画面。'
    timer = setTimeout(() => readFrame(current), 250)
  } catch { if (current === generation) { stop(); error.value = '扫码组件或相机画面暂时无法读取，请刷新页面后重试，或改用拍照扫码。' } }
}
async function changeZoom() {
  const current = generation, track = stream?.getVideoTracks()[0]
  if (!track) return
  try { await track.applyConstraints({ advanced: [{ zoom: zoom.value } as MediaTrackConstraintSet] }) }
  catch { if (current === generation) notice.value = '当前相机无法调整放大倍率，请靠近二维码或改用拍照扫码。' }
}
async function start() {
  if (props.disabled || opening.value || reading.value || loadingSetup.value) return
  error.value = ''; notice.value = ''
  if (!live) {
    if (secure) { error.value = '当前浏览器未提供相机接口，请使用最新版Safari或Chrome，并允许此网站使用相机。'; return }
    await openSettings()
    return
  }
  showSetup.value = false
  stop(); opened.value = true; opening.value = true
  const current = generation
  try {
    const captured = await navigator.mediaDevices.getUserMedia({ audio: false, video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 }, height: { ideal: 1080 } } })
    if (current !== generation) { captured.getTracks().forEach(track => track.stop()); return }
    stream = captured; await nextTick(); cameraClose.value?.focus()
    if (current !== generation) return
    if (!video.value) { stop(); return }
    video.value.srcObject = captured; await video.value.play()
    if (current !== generation) return
    const track = captured.getVideoTracks?.()[0], capabilities = track?.getCapabilities?.() as CameraCapabilities | undefined
    if (capabilities?.focusMode?.includes('continuous')) {
      try { await track?.applyConstraints({ advanced: [{ focusMode: 'continuous' } as MediaTrackConstraintSet] }) } catch { /* 不支持对焦调整时仍可继续识别。 */ }
    }
    if (current !== generation) return
    if (capabilities?.zoom && capabilities.zoom.max > capabilities.zoom.min) {
      zoomRange.value = { ...capabilities.zoom, step: capabilities.zoom.step || .1 }
      zoom.value = (track?.getSettings() as MediaTrackSettings & { zoom?: number })?.zoom ?? capabilities.zoom.min
    }
    opening.value = false; startedAt = Date.now(); void readFrame(current)
  } catch (cause) {
    if (current !== generation) return
    stop()
    const name = cause && typeof cause === 'object' && 'name' in cause ? String(cause.name) : ''
    error.value = name === 'NotAllowedError' ? '未获得相机权限，请在浏览器设置中允许相机，或使用拍照扫码。'
      : name === 'NotFoundError' ? '未找到可用相机，请连接相机，或选择已有照片识别。'
        : '相机暂时无法打开，请关闭其他相机应用后重试，或使用拍照扫码。'
  }
}
async function openSettings() {
  if (loadingSetup.value) return
  stop(); error.value = ''; notice.value = ''; showSetup.value = true; loadingSetup.value = true
  await nextTick(); setupClose.value?.focus()
  try { cameraAccess.value = await readProductionCameraAccess() }
  catch (cause) { error.value = cause instanceof Error && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : '暂时无法读取安全扫码配置，请刷新后重试。' }
  finally { loadingSetup.value = false }
}
defineExpose({ openSettings })
function pickPhoto(input: HTMLInputElement | null) { stop(); error.value = ''; notice.value = ''; input?.click() }
async function readPhoto(event: Event) {
  const input = event.target as HTMLInputElement, file = input.files?.[0]
  input.value = ''
  if (!file || props.disabled || reading.value) return
  stop(); const current = generation; reading.value = true; error.value = ''; notice.value = '正在识别…'
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
      <button type="button" :disabled="disabled || reading || opened || loadingSetup" @click="start">摄像头扫码</button>
      <button type="button" :disabled="disabled || reading" @click="pickPhoto(photo)">拍照扫码</button>
      <button type="button" :disabled="disabled || reading" @click="pickPhoto(album)">识别相册图片</button>
    </div>
    <input ref="photo" class="production-camera__file" type="file" accept="image/*" capture="environment" aria-label="拍摄图纸或批次流转卡二维码" @change="readPhoto">
    <input ref="album" class="production-camera__file" type="file" accept="image/*" aria-label="选择图纸或批次流转卡照片" @change="readPhoto">
    <div v-if="showSetup" class="production-camera__backdrop" @click.self="showSetup = false">
    <section class="production-camera__setup" role="dialog" aria-modal="true" aria-label="开启浏览器摄像头扫码" @keydown.esc="showSetup = false">
      <header><h3>扫码设置</h3><button ref="setupClose" type="button" @click="showSetup = false">关闭</button></header>
      <p>首次使用：安装证书 → 开启信任 → 进入安全扫码。</p>
      <p v-if="loadingSetup" role="status">正在读取安全扫码入口…</p>
      <template v-if="cameraAccess">
        <a class="production-camera__secure-link" :href="cameraAccess.httpsScanUrl">进入HTTPS摄像头扫码</a>
        <details><summary>iPhone／iPad首次配置</summary><ol><li>使用Safari打开本页，<a :href="cameraAccess.certificateUrl">下载iPhone／iPad证书</a>，允许下载描述文件。</li><li>在“设置 → 通用 → VPN与设备管理”中，安装“{{ cameraAccess.certificateName }}”。</li><li>在“设置 → 通用 → 关于本机 → 证书信任设置”中，为该证书开启完全信任。</li><li>返回本页进入HTTPS扫码，登录后允许相机权限。</li></ol></details>
        <details><summary>Android首次配置</summary><ol><li><a :href="cameraAccess.androidCertificateUrl">下载Android证书</a>。</li><li>在手机“设置 → 安全（或安全与隐私）→ 加密与凭据”中安装CA证书“{{ cameraAccess.certificateName }}”；具体菜单以手机系统为准。</li><li>返回本页进入HTTPS扫码，允许相机权限。</li></ol></details>
        <p>已安装或由公司统一下发证书的手机，可直接进入。切换地址需重新登录。</p>
        <details><summary>证书核对信息</summary><p>{{ cameraAccess.certificateName }}</p><code>{{ cameraAccess.certificateSha256 }}</code></details>
      </template>
      <p v-if="error" role="alert" class="production-camera__error">{{ error }}</p>
    </section>
    </div>
    <p v-if="notice && !opened" role="status">{{ notice }}</p><p v-if="error && !showSetup" role="alert" class="production-camera__error">{{ error }}</p>
    <div v-if="opened" class="production-camera__backdrop">
    <div class="production-camera__preview" role="region" aria-label="实时扫码画面" @keydown.esc="stop">
      <header><strong>摄像头扫码</strong><button ref="cameraClose" type="button" @click="stop">关闭相机</button></header>
      <label v-if="zoomRange" class="production-camera__zoom">放大倍率 {{ Number(zoom).toFixed(1) }} 倍<input v-model.number="zoom" type="range" :min="zoomRange.min" :max="zoomRange.max" :step="zoomRange.step" aria-label="相机放大倍率" @input="changeZoom"></label>
      <div class="production-camera__view"><video ref="video" autoplay muted playsinline aria-label="后置相机画面"></video><span class="production-camera__target" aria-hidden="true"></span></div>
      <p v-if="opening" role="status">正在打开相机…</p><p v-else-if="notice" role="status">{{ notice }}</p>
    </div>
    </div>
  </div>
</template>
<style scoped>
.production-camera{margin-top:14px;min-width:0}.production-camera__actions{display:flex;flex-wrap:wrap;gap:8px}.production-camera__file{display:none}.production-camera__error{color:var(--pdm-danger)}.production-camera p{overflow-wrap:anywhere}.production-camera__preview{background:var(--pdm-bg);border-radius:6px;padding:8px}.production-camera video{display:block;width:100%;max-height:360px;object-fit:contain;background:#111;border-radius:5px}.production-camera__preview button{margin-top:8px}.production-camera button{min-height:44px;border:1px solid var(--pdm-theme-accent-border);border-radius:5px;padding:8px 12px;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font:inherit;cursor:pointer}.production-camera__actions button:first-child{background:var(--pdm-theme-accent);color:#fff;border-color:var(--pdm-theme-accent)}.production-camera button:disabled{opacity:.6;cursor:not-allowed}
.production-camera__view{position:relative;height:360px}.production-camera__view video{height:100%}.production-camera__target{position:absolute;pointer-events:none;left:50%;top:50%;transform:translate(-50%,-50%);width:min(60%,160px);aspect-ratio:1;border:2px solid #fff;border-radius:8px;box-shadow:0 0 0 1px #0006}.production-camera__zoom{display:grid;gap:6px;margin:12px 0;font-size:13px}.production-camera__zoom input{width:100%;min-height:32px}
.production-camera__setup{padding:12px;border:1px solid var(--pdm-border);border-radius:6px;font-size:13px;line-height:1.6}.production-camera__setup h3{font-size:15px;margin:0 0 8px}.production-camera__setup details{margin-top:12px}.production-camera__setup ol{padding-left:20px}.production-camera__setup li{margin:7px 0}.production-camera__setup a{color:var(--pdm-theme-accent)}.production-camera__setup code{overflow-wrap:anywhere}.production-camera__setup .production-camera__secure-link{display:block;padding:10px;text-align:center;background:var(--pdm-theme-accent);color:#fff;border-radius:5px;text-decoration:none}
.production-camera{margin-top:8px}.production-camera__actions{gap:6px}.production-camera__actions button{min-height:34px;padding:6px 10px;font-size:13px}.production-camera__backdrop{position:fixed;inset:0;z-index:2600;display:flex;align-items:flex-start;justify-content:center;box-sizing:border-box;overflow:auto;padding:12px;background:var(--pdm-overlay)}.production-camera__setup,.production-camera__preview{width:min(520px,100%);max-height:calc(100dvh - 24px);overflow:auto;box-sizing:border-box;background:var(--pdm-surface);padding:12px}.production-camera__setup header,.production-camera__preview header{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px}.production-camera__setup header button,.production-camera__preview header button{margin:0;min-height:30px;padding:4px 8px}.production-camera__setup h3{margin:0}.production-camera__setup li{margin:5px 0}.production-camera__view{height:min(58dvh,360px)}
</style>
