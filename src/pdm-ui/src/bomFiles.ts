import type { BomItem } from './types'

export interface BomFileReference {
  id: string
  materialId: string
  kind: string
  fileName: string
  versionNumber: number
  fileLength: number
  sha256: string
  uploadedBy: string
  uploadedAt: string
  source: string
  purpose: string
}
export interface BomFileView { files: BomFileReference[]; libraryUpdateAvailable: boolean; rowVersion: number }
export const fileVersion = (version?: number | null) => version ? `V${String(version).padStart(2, '0')}` : '未启用'
export function bomFilesLabel(row: BomItem) {
  const files = row.files ?? []
  if (!files.length) return '—'
  return `查看（${files.length}）`
}
