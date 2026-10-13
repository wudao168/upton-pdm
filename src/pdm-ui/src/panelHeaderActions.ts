import { defineComponent, h, provide, ref, type App, type Component, type InjectionKey, type Ref } from 'vue'
import { ElDialog, ElDrawer } from 'element-plus'

// 系统弹窗按侧面抽屉展示，统一把主操作放到标题与关闭按钮之间。
export const panelHeaderActionsKey: InjectionKey<Ref<HTMLElement | undefined>> = Symbol('panel-header-actions')

function withHeaderActions(panel: Component, name: string) {
  return defineComponent({
    name,
    inheritAttrs: false,
    setup(_, { attrs, slots, expose }) {
      const instance = ref<{ handleClose: () => void }>()
      const actionTarget = ref<HTMLElement>()
      provide(panelHeaderActionsKey, actionTarget)
      expose({ handleClose: () => instance.value?.handleClose() })
      return () => {
        const { footer, header, title, ...content } = slots
        return h(panel, { ...attrs, ref: instance }, {
          ...content,
          header: (scope: { titleId: string; titleClass: string; close: () => void }) => h('div', { class: 'pdm-panel-topbar' }, [
            h('div', { class: 'pdm-panel-topbar-title' }, header ? header(scope) : title ? title() : h('span', {
              id: scope.titleId,
              class: scope.titleClass,
              role: 'heading',
              'aria-level': attrs.headerAriaLevel ?? attrs['header-aria-level'] ?? 4,
              title: String(attrs.title ?? ''),
            }, String(attrs.title ?? ''))),
            h('div', { class: 'pdm-panel-header-actions', ref: actionTarget }, footer?.()),
          ]),
        })
      }
    },
  })
}

export function installPanelHeaderActions(app: App) {
  app.component('ElDrawer', withHeaderActions(ElDrawer, 'PdmHeaderDrawer'))
  app.component('ElDialog', withHeaderActions(ElDialog, 'PdmHeaderDialog'))
}
