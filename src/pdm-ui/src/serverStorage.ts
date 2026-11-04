export interface ServerStorageSettings {
  version: number
  projectFileRoot: string
  oplAttachmentRoot: string
  legacyOplAttachmentRoot: string
  programTemplateRoot: string
  uploadTempRoot: string
  browserCadArchiveRoot: string
  conversionWorkRoot: string
  clientLogRoot: string
  backupRoot: string
  databaseDataTarget: string
  secretsTarget: string
  previewAgentWorkTarget: string
  historicalRoots: Record<string, string[]>
}

export interface ServerStorageView {
  settings: ServerStorageSettings
  databaseDataRoot: string
  secretsRoot: string
  databaseBackupRoot: string
  filesBackupRoot: string
  previewAgentUrl: string
  historyPolicy: string
}

export interface StorageBackupPlan {
  backupRoot: string
  referenceCount: number
  sources: Array<{ path: string; categories: string[]; isDirectory: boolean; required: boolean; exists?: boolean }>
  issues?: Array<{ category: string; path: string; message: string }>
  warnings: string[]
  message: string
}
