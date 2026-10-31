import { installGlobalScrollbars } from './globalScrollbars'
import { installContainerBorders } from './containerBorders'
import { installButtonTypography } from './buttonTypography'
import { installButtonStyles } from './buttonStyles'
import { installPanelHeaderActions } from './panelHeaderActions'
import { installColumnResize } from './columnResize'
import { installDatePickers } from './datePickers'
import './styles/column-resize.css'
import { createApp, defineAsyncComponent } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/themes.css'
import './styles/app.css'
import './styles/ui-consistency.css'
import './styles/tabs.css'
import './styles/business.css'
import './styles/material-stock-plans.css'
import { stockedMaterial } from './materialStockPlans'
import App from './App.vue'
import { installClientDiagnostics } from './clientDiagnostics'
import { isPdmTheme } from './themes'

const query = new URLSearchParams(window.location.search)
const savedTheme = window.localStorage.getItem('pdm_theme')
document.documentElement.dataset.pdmTheme = isPdmTheme(savedTheme) ? savedTheme : 'a'
const root = query.has('dingtalkOpl')
  ? defineAsyncComponent(() => import('./components/DingTalkOplPage.vue'))
  : query.has('productionScan')
    ? defineAsyncComponent(() => import('./components/ProductionScanPage.vue')) : App
const app = createApp(root).use(ElementPlus, { locale: zhCn })
app.directive('stocked-material', stockedMaterial)
installClientDiagnostics(app)
installPanelHeaderActions(app)
installColumnResize(app)
installDatePickers(app)
installButtonTypography(app)
installButtonStyles(app)
installGlobalScrollbars(app)
app.onUnmount(installContainerBorders())
app.mount('#app')
