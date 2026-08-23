declare module 'occt-import-js' {
  export interface OcctImportOptions {
    locateFile?: (fileName: string) => string
  }

  export interface OcctMesh {
    name: string
    color?: [number, number, number]
    attributes: {
      position: { array: number[] }
      normal?: { array: number[] }
    }
    index: { array: number[] }
  }

  export interface OcctImportResult {
    success: boolean
    meshes: OcctMesh[]
  }

  export interface OcctImporter {
    ReadStepFile(content: Uint8Array, options: unknown): OcctImportResult
  }

  export default function createImporter(options?: OcctImportOptions): Promise<OcctImporter>
}
