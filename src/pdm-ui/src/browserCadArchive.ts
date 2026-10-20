export interface BrowserCadTarget { projectId?: string; structureId?: string; expectedStructureRevisionId?: string; drawingType?: number; name?: string; selectedDocumentId?: string }
export interface BrowserCadCapability { enabled: boolean; canArchive: boolean; canConfigure: boolean; reason?: string }
export interface BrowserCadTicket {
  id: string; state: 'Uploading' | 'Ready' | 'Queued' | 'Processing' | 'Committing' | 'Succeeded' | 'Failed'
  stage: string; files: Array<{ relativePath: string; length: number }>; rootPath?: string; error?: string
  archivedFiles: number; newVersions: number; resultCode?: string; warnings?: string[]
}
