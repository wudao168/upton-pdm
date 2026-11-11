export const oplRecordingLimit = 60

export function appendOplFiles(current: File[], selected: File[], retained = 0) {
  const combined = [...current, ...selected]
  if (combined.length + retained > 10 || combined.some(file => !file.size || file.size > 10 * 1024 * 1024)
    || combined.reduce((size, file) => size + file.size, 0) > 30 * 1024 * 1024) return null
  return combined
}

const audioTypes: Record<string, string> = {
  wav: 'audio/wav', mp3: 'audio/mpeg', m4a: 'audio/mp4', aac: 'audio/aac', ogg: 'audio/ogg',
  opus: 'audio/ogg', flac: 'audio/flac', webm: 'audio/webm',
}

export function oplAudioMime(name: string) {
  const extension = name.split('.').pop()?.toLowerCase() ?? ''
  if (extension === 'webm' && !name.startsWith('OPL录音-')) return ''
  return audioTypes[extension] ?? ''
}

export function oplRecordingFile(blob: Blob, originalName = '') {
  const mime = blob.type.split(';')[0]?.toLowerCase()
  const namedExtension = originalName.split('.').pop()?.toLowerCase() ?? ''
  const extension = Object.keys(audioTypes).find(key => audioTypes[key] === mime)
    ?? (audioTypes[namedExtension] ? namedExtension : undefined)
  if (!extension || !blob.size) throw new Error('请选择可播放的 WAV、MP3、M4A 或 WebM 录音。')
  const timestamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 17)
  return new File([blob], `OPL录音-${timestamp}.${extension}`, { type: audioTypes[extension] })
}

export async function oplSpeechWave(file: File) {
  const browser = window as Window & { webkitAudioContext?: typeof AudioContext; webkitOfflineAudioContext?: typeof OfflineAudioContext }
  const Audio = window.AudioContext ?? browser.webkitAudioContext
  const OfflineAudio = window.OfflineAudioContext ?? browser.webkitOfflineAudioContext
  if (!Audio || !OfflineAudio) throw new Error('此终端暂无法转换录音，可保留附件并手动填写描述。')
  const context = new Audio()
  try {
    const source = await context.decodeAudioData(await file.arrayBuffer()).catch(() => {
      throw new Error('录音无法解码，请选择此终端能播放的音频，或手动填写描述。')
    })
    if (source.duration > oplRecordingLimit + 0.3) throw new Error('每段录音最多60秒，请缩短后重新上传。')
    const frames = Math.min(oplRecordingLimit * 16000, Math.floor(source.duration * 16000))
    if (!frames) throw new Error('未收到有效录音，请重新录制。')
    const renderer = new OfflineAudio(1, frames, 16000)
    const node = renderer.createBufferSource()
    node.buffer = source
    node.connect(renderer.destination)
    node.start()
    return pcmWave((await renderer.startRendering()).getChannelData(0))
  } finally { void context.close().catch(() => undefined) }
}

function pcmWave(samples: Float32Array) {
  const wave = new ArrayBuffer(44 + samples.length * 2)
  const bytes = new DataView(wave)
  const tag = (offset: number, value: string) => {
    for (let index = 0; index < value.length; index++) bytes.setUint8(offset + index, value.charCodeAt(index))
  }
  tag(0, 'RIFF'); bytes.setUint32(4, wave.byteLength - 8, true)
  tag(8, 'WAVE'); tag(12, 'fmt '); bytes.setUint32(16, 16, true)
  bytes.setUint16(20, 1, true); bytes.setUint16(22, 1, true)
  bytes.setUint32(24, 16000, true); bytes.setUint32(28, 32000, true)
  bytes.setUint16(32, 2, true); bytes.setUint16(34, 16, true)
  tag(36, 'data'); bytes.setUint32(40, samples.length * 2, true)
  for (let index = 0; index < samples.length; index++) {
    const sample = Math.max(-1, Math.min(1, samples[index]!))
    bytes.setInt16(44 + index * 2, Math.round(sample * (sample < 0 ? 32768 : 32767)), true)
  }
  return new Blob([wave], { type: 'audio/wav' })
}
