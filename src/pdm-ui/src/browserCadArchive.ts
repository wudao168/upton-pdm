export const browserCadUploadAccept = '.sldasm,.sldprt,.slddrw,.step,.stp,.txt,.xls,.xlsx,.zip'
export const isBrowserCadRootPath = (path: string) => /\.(sldasm|sldprt|slddrw)$/i.test(path)
export const isBrowserCadUploadPath = (path: string) => isBrowserCadRootPath(path) || /\.(step|stp|txt|xls|xlsx|zip)$/i.test(path)

export interface BrowserCadTarget { projectId?: string; structureId?: string; expectedStructureRevisionId?: string; drawingType?: number; name?: string; selectedDocumentId?: string }
export interface BrowserCadCapability { enabled: boolean; canArchive: boolean; canConfigure: boolean; reason?: string }
export interface BrowserCadTicket {
  id: string; state: 'Uploading' | 'Ready' | 'Queued' | 'Processing' | 'Committing' | 'Succeeded' | 'Failed'
  stage: string; files: Array<{ relativePath: string; length: number }>; rootPath?: string; error?: string
  archivedFiles: number; newVersions: number; resultCode?: string; warnings?: string[]; previewsPending?: boolean; previewRetryable?: boolean
}
