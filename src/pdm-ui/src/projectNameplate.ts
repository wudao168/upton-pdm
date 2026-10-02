export interface ProjectNameplate {
  projectId: string
  enabled: boolean
  powerSupply: string
  dimensions: string
  airPressure: string
  weight: string
  factoryDate: string | null
  rowVersion: number
  updatedBy?: string
  updatedAt?: string
}
export interface NameplateTemplate { title: string; labels: Record<string, string>; rowVersion: number }
export interface ProjectNameplateView {
  nameplate: ProjectNameplate
  name: string
  model: string
  serialNumbers: string[]
  template: NameplateTemplate
  canEdit: boolean
  canManageTemplate: boolean
}
