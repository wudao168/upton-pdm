import { installButtonTypography } from './buttonTypography'
import { installPanelHeaderActions } from './panelHeaderActions'
import { installColumnResize } from './columnResize'
import './styles/column-resize.css'
import { createApp, defineAsyncComponent } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/app.css'
import './styles/ui-consistency.css'
import App from './App.vue'
import { installClientDiagnostics } from './clientDiagnostics'

const root = new URLSearchParams(window.location.search).has('dingtalkOpl')
  ? defineAsyncComponent(() => import('./components/DingTalkOplPage.vue')) : App
const app = createApp(root).use(ElementPlus, { locale: zhCn })
installClientDiagnostics(app)
installPanelHeaderActions(app)
installColumnResize(app)
installButtonTypography(app)
app.mount('#app')
