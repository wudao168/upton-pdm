import createImporter from 'occt-import-js'
import occtWasmUrl from 'occt-import-js/dist/occt-import-js.wasm?url'

type WorkerRequest = { id: number; buffer: ArrayBuffer }
type WorkerScope = {
  onmessage: ((event: MessageEvent<WorkerRequest>) => void) | null
  postMessage: (message: unknown, transfer?: Transferable[]) => void
}

const workerScope = self as unknown as WorkerScope
const importer = createImporter({
  locateFile: fileName => fileName.endsWith('.wasm') ? occtWasmUrl : fileName,
})

workerScope.onmessage = async event => {
  const { id, buffer } = event.data
  try {
    const occt = await importer
    const result = occt.ReadStepFile(new Uint8Array(buffer), null)
    if (!result.success) throw new Error('STEP文件解析失败。')
    const meshes = result.meshes.map(mesh => ({
      name: mesh.name,
      color: mesh.color,
      position: new Float32Array(mesh.attributes.position.array),
      normal: mesh.attributes.normal ? new Float32Array(mesh.attributes.normal.array) : undefined,
      index: new Uint32Array(mesh.index.array),
    }))
    const transfer = meshes.flatMap(mesh => [
      mesh.position.buffer,
      mesh.index.buffer,
      ...(mesh.normal ? [mesh.normal.buffer] : []),
    ])
    workerScope.postMessage({ id, meshes }, transfer)
  } catch (error) {
    workerScope.postMessage({ id, error: error instanceof Error ? error.message : 'STEP文件解析失败。' })
  }
}
