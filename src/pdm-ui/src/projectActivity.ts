export interface ProjectActivity {
  id: string
  projectId: string
  projectCode: string
  releasePackageId: string | null
  title: string
  content: string
  createdAt: string
  expiresAt: string
  important: boolean
  target: 'release' | 'project-plan' | 'overview'
  kind: string
}
export function currentActivities(items: ProjectActivity[], now = Date.now()): ProjectActivity[] {
  const latest = new Map<string, ProjectActivity>()
  for (const item of items) {
    if (!(Date.parse(item.expiresAt) > now) || !(Date.parse(item.createdAt) <= now)) continue
    const key = item.releasePackageId ? 'release:' + item.releasePackageId : item.projectId + ':' + item.kind
    const previous = latest.get(key)
    if (!previous || Date.parse(previous.createdAt) < Date.parse(item.createdAt)) latest.set(key, item)
  }
  return [...latest.values()].sort((a, b) => Number(b.important) - Number(a.important) || Date.parse(b.createdAt) - Date.parse(a.createdAt))
}
