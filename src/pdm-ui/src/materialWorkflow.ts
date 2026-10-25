import type { MaterialCodeApplication, MaterialSyncTask } from './types'

export function currentMaterialSyncTasks(tasks: MaterialSyncTask[], applications: MaterialCodeApplication[]) {
  const headerMaterials = new Set(applications.filter(item => item.applicationType === 'BomHeader').map(item => item.materialId))
  const awaitingSync = new Set(applications.filter(item => item.status === 'Approved' && item.workflowState !== 'Completed').map(item => item.materialId))
  const seenMaterials = new Set<string>()
  return tasks.filter(task => {
    if (task.bomHeaderKind || headerMaterials.has(task.materialId) || task.status === 'Superseded' || seenMaterials.has(task.materialId)) return false
    seenMaterials.add(task.materialId)
    return task.status !== 'Succeeded' || awaitingSync.has(task.materialId)
  })
}
