import { installGlobalScrollbars } from './globalScrollbars'
import { installContainerBorders } from './containerBorders'
import { installButtonTypography } from './buttonTypography'
import { installButtonStyles } from './buttonStyles'
import { installPanelHeaderActions } from './panelHeaderActions'
import { installColumnResize } from './columnResize'
import { installDatePickers } from './datePickers'
import './styles/column-resize.css'
import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/themes.css'
import './styles/app.css'
import './styles/drawer-motion.css'
import './styles/capsule-fields.css'
import './styles/twenty-appearance.css'
import './styles/global-accent.css'
import './styles/ui-consistency.css'
import ServerConnectionSettings from './components/ServerConnectionSettings.vue'
import { applyPdmTheme, isPdmTheme, watchPdmSystemTheme } from './themes'

const savedTheme = window.localStorage.getItem('pdm_theme')
applyPdmTheme(isPdmTheme(savedTheme) ? savedTheme : 'a')

const app = createApp(ServerConnectionSettings).use(ElementPlus, { locale: zhCn })
app.onUnmount(watchPdmSystemTheme(() => { const theme = document.documentElement.dataset.pdmTheme; return isPdmTheme(theme) ? theme : 'a' }))
installPanelHeaderActions(app)
installColumnResize(app)
installDatePickers(app)
installButtonTypography(app)
installButtonStyles(app)
installGlobalScrollbars(app)
app.onUnmount(installContainerBorders())
app.mount('#app')
