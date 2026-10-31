import { computed, inject, type ComputedRef, type InjectionKey } from 'vue'

export const projectCenterSchema = 'system.project-center-permissions.v1'
export const projectCenterAccessKey: InjectionKey<ComputedRef<readonly string[]>> = Symbol('project-center-permissions')

export function canViewProjectTab(permissions: readonly string[], tab: string) {
  return !permissions.includes(projectCenterSchema) || permissions.includes(`project.tab.${tab}.view`)
}

export function useProjectCenterAccess() {
  const permissions = inject(projectCenterAccessKey, computed<readonly string[]>(() => []))
  const allows = (action: string) => !permissions.value.includes(projectCenterSchema) || permissions.value.includes(`project.content.${action}`)
  return {
    canViewTab: (tab: string) => canViewProjectTab(permissions.value, tab),
    write: computed(() => allows('write')), download: computed(() => allows('download')),
    export: computed(() => allows('export')), print: computed(() => allows('print')),
  }
}
