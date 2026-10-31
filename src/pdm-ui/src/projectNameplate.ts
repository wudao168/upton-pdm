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
  executionSavedBy?: string | null
  executionSavedAt?: string | null
  hardwareSavedBy?: string | null
  hardwareSavedAt?: string | null
  releasePackageId?: string | null
  releasePackageNumber?: string | null
  confirmedBy?: string | null
  confirmedAt?: string | null
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
  canEditExecution: boolean
  canEditHardware: boolean
  canConfirm: boolean
}

export interface NameplateContent {
  name: string
  model: string
  serialNumber: string
  powerSupply: string
  ratedPower: number
  dimensions: string
  airPressure: string
  weight: string
  factoryDate: string
  confirmedBy: string
  confirmedAt: string
}

export function nameplateStatus(plate: ProjectNameplate) {
  const published = !!plate.releasePackageId
  const executionComplete = published || !!(plate.executionSavedAt && plate.dimensions?.trim() && plate.weight?.trim() && plate.airPressure?.trim() && plate.factoryDate)
  const hardwareComplete = published || !!plate.hardwareSavedAt && ['V', 'Hz', 'A'].every(unit => {
    const value = plate.powerSupply?.match(new RegExp(`(\\d+(?:\\.\\d+)?)\\s*${unit}\\b`, 'i'))?.[1]
    return !!value && Number(value) > 0
  })
  const label = published ? '已发布' : !plate.enabled ? '未启用'
    : executionComplete && hardwareComplete ? '待硬件确认'
      : executionComplete ? '执行已填写' : hardwareComplete ? '硬件已填写'
        : plate.executionSavedAt && plate.hardwareSavedAt ? '填写中'
          : plate.executionSavedAt ? '执行填写中' : plate.hardwareSavedAt ? '硬件填写中' : '待填写'
  return { published, executionComplete, hardwareComplete, label }
}
