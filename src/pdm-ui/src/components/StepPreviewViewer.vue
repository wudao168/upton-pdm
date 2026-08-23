<script setup lang="ts">
import * as THREE from 'three'
import { OrbitControls } from 'three/addons/controls/OrbitControls.js'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'

interface PreviewMesh {
  name: string
  color?: [number, number, number]
  position: Float32Array
  normal?: Float32Array
  index: Uint32Array
}

const props = defineProps<{ buffer: ArrayBuffer }>()
const emit = defineEmits<{ ready: []; error: [message: string] }>()
const canvas = ref<HTMLCanvasElement>()
let worker: Worker | undefined
let renderer: THREE.WebGLRenderer | undefined
let scene: THREE.Scene | undefined
let camera: THREE.PerspectiveCamera | undefined
let controls: OrbitControls | undefined
let model: THREE.Group | undefined
let resizeObserver: ResizeObserver | undefined
let frame = 0
let requestId = 0

function disposeModel() {
  if (!model) return
  model.traverse(object => {
    if (!(object instanceof THREE.Mesh)) return
    object.geometry.dispose()
    const materials = Array.isArray(object.material) ? object.material : [object.material]
    materials.forEach(material => material.dispose())
  })
  scene?.remove(model)
  model = undefined
}

function resize() {
  if (!canvas.value || !renderer || !camera) return
  const width = Math.max(1, canvas.value.clientWidth)
  const height = Math.max(1, canvas.value.clientHeight)
  renderer.setSize(width, height, false)
  camera.aspect = width / height
  camera.updateProjectionMatrix()
}

function showMeshes(meshes: PreviewMesh[]) {
  if (!scene || !camera || !controls || meshes.length === 0) {
    emit('error', 'STEP文件中没有可显示的几何体。')
    return
  }
  disposeModel()
  model = new THREE.Group()
  for (const mesh of meshes) {
    const geometry = new THREE.BufferGeometry()
    geometry.setAttribute('position', new THREE.BufferAttribute(mesh.position, 3))
    if (mesh.normal?.length) geometry.setAttribute('normal', new THREE.BufferAttribute(mesh.normal, 3))
    geometry.setIndex(new THREE.BufferAttribute(mesh.index, 1))
    if (!mesh.normal?.length) geometry.computeVertexNormals()
    geometry.computeBoundingBox()
    const color = mesh.color ? new THREE.Color(...mesh.color) : new THREE.Color(0x4f8fba)
    model.add(new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({ color, metalness: 0.12, roughness: 0.62, side: THREE.DoubleSide })))
  }
  scene.add(model)
  const bounds = new THREE.Box3().setFromObject(model)
  const center = bounds.getCenter(new THREE.Vector3())
  const size = bounds.getSize(new THREE.Vector3())
  const radius = Math.max(size.length() / 2, 1)
  camera.near = Math.max(radius / 1000, 0.01)
  camera.far = Math.max(radius * 100, 1000)
  camera.position.copy(center).add(new THREE.Vector3(radius * 1.55, radius * 1.15, radius * 1.55))
  camera.updateProjectionMatrix()
  controls.target.copy(center)
  controls.update()
  emit('ready')
}

function loadStep() {
  if (!worker || props.buffer.byteLength === 0) return
  const id = ++requestId
  const buffer = props.buffer.slice(0)
  worker.postMessage({ id, buffer }, [buffer])
}

onMounted(() => {
  if (!canvas.value) return
  scene = new THREE.Scene()
  scene.background = new THREE.Color(0xeaf1f7)
  camera = new THREE.PerspectiveCamera(42, 1, 0.01, 1000)
  renderer = new THREE.WebGLRenderer({ canvas: canvas.value, antialias: true, alpha: false })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
  renderer.outputColorSpace = THREE.SRGBColorSpace
  scene.add(new THREE.HemisphereLight(0xffffff, 0x526577, 2.25))
  const keyLight = new THREE.DirectionalLight(0xffffff, 2.6)
  keyLight.position.set(4, 5, 3)
  scene.add(keyLight)
  controls = new OrbitControls(camera, canvas.value)
  controls.enableDamping = true
  const render = () => {
    frame = window.requestAnimationFrame(render)
    controls?.update()
    if (scene && camera) renderer?.render(scene, camera)
  }
  render()
  resize()
  if (typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(resize)
    resizeObserver.observe(canvas.value)
  }
  worker = new Worker(new URL('../workers/stepPreview.worker.ts', import.meta.url), { type: 'module' })
  worker.onmessage = (event: MessageEvent<{ id: number; meshes?: PreviewMesh[]; error?: string }>) => {
    if (event.data.id !== requestId) return
    if (event.data.error || !event.data.meshes) {
      emit('error', event.data.error ?? 'STEP文件解析失败。')
      return
    }
    showMeshes(event.data.meshes)
  }
  worker.onerror = () => emit('error', 'STEP预览组件加载失败。')
  loadStep()
})

watch(() => props.buffer, loadStep)

onBeforeUnmount(() => {
  worker?.terminate()
  resizeObserver?.disconnect()
  if (frame) window.cancelAnimationFrame(frame)
  controls?.dispose()
  disposeModel()
  renderer?.dispose()
})
</script>

<template>
  <canvas ref="canvas" class="pdm-step-preview-canvas" aria-label="STEP三维预览" />
</template>
